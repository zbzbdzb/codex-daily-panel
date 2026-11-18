using System.Text.Json;
using CodexUsage.Daily;
using CodexUsage.DailyPanel;

internal static class DiscoveryCancellationTests
{
    public static async Task RunAsync()
    {
        foreach (var atEndOfDirectory in new[] { false, true })
        {
            var home = Path.Combine(Path.GetTempPath(), "CodexDaily-discovery-" + Guid.NewGuid().ToString("N"));
            var sessions = Path.Combine(home, "sessions");
            var nested = Path.Combine(sessions, "old-date");
            var archive = Path.Combine(home, "archived_sessions");
            var now = new DateTimeOffset(2026, 10, 23, 12, 0, 0, TimeSpan.Zero);
            Directory.CreateDirectory(nested);
            Directory.CreateDirectory(archive);
            try
            {
                foreach (var directory in new[] { sessions, nested, archive })
                {
                    var path = Path.Combine(directory, "synthetic.jsonl");
                    File.WriteAllText(path, "");
                    File.SetLastWriteTimeUtc(path, now.UtcDateTime);
                }

                using var cancel = new CancellationTokenSource();
                var visited = new List<string>();
                var advancedAfterCancel = false;
                var injectCancellation = true;
                using var monitor = new SessionLogMonitor(home, TimeZoneInfo.Utc, Enumerate);

                try
                {
                    await monitor.RefreshAsync(now, cancel.Token);
                    throw new InvalidOperationException("Discovery returned a snapshot after cancellation");
                }
                catch (OperationCanceledException error) when (error.CancellationToken == cancel.Token) { }

                Need(cancel.IsCancellationRequested, "The test cancellation boundary was not reached");
                Need(!advancedAfterCancel, "Discovery requested the next file after cancellation");
                Need(visited.SequenceEqual(new[] { sessions }), "Discovery entered a queued directory or archive root after cancellation");

                injectCancellation = false;
                var snapshot = await monitor.RefreshAsync(now);
                Need(snapshot.SourceCount == 3, "An uncancelled refresh must still discover nested and archived logs");
                Console.WriteLine(atEndOfDirectory
                    ? "PASS: cancellation at directory completion skips queued directories and archive"
                    : "PASS: cancellation during a file step stops further enumeration");

                IEnumerable<FileSystemInfo> Enumerate(DirectoryInfo directory)
                {
                    visited.Add(directory.FullName);
                    foreach (var entry in SessionLogMonitor.EnumerateDirectory(directory))
                    {
                        if (injectCancellation && !atEndOfDirectory && directory.FullName == sessions && entry is FileInfo)
                            cancel.Cancel();
                        yield return entry;
                        if (injectCancellation && cancel.IsCancellationRequested)
                            advancedAfterCancel = true;
                    }
                    if (injectCancellation && atEndOfDirectory && directory.FullName == sessions)
                        cancel.Cancel();
                }
            }
            finally { Directory.Delete(home, recursive: true); }
        }
        var failures = new List<InvalidOperationException>();
        foreach (var change in Enum.GetValues<QueuedDirectoryChange>())
        {
            try { await PreservesSiblingWhenQueuedDirectoryChangesAsync(change); }
            catch (InvalidOperationException error)
            {
                Console.Error.WriteLine($"FAIL: {change}: {error.Message}");
                failures.Add(error);
            }
        }
        if (failures.Count > 0) throw new AggregateException("Directory discovery regressions failed", failures);
    }

    private enum QueuedDirectoryChange
    {
        None,
        Removed,
        ReplacedByFile,
        ReplacedWhileOpening,
        IoFailureWhileOpening,
        AccessDeniedDuringEnumeration,
        LockedReplacementWhileOpening
    }

    private static async Task PreservesSiblingWhenQueuedDirectoryChangesAsync(QueuedDirectoryChange change)
    {
        var home = Path.Combine(Path.GetTempPath(), "CodexDaily-discovery-sibling-" + Guid.NewGuid().ToString("N"));
        var sessions = Path.Combine(home, "sessions");
        var changing = Path.Combine(sessions, "A");
        var healthy = Path.Combine(sessions, "B");
        var now = new DateTimeOffset(2026, 10, 23, 12, 0, 0, TimeSpan.Zero);
        Directory.CreateDirectory(changing);
        Directory.CreateDirectory(healthy);
        FileStream? replacementLock = null;
        try
        {
            WriteLog(healthy, "healthy-sibling", now);
            var changeQueuedDirectory = false;
            var openedChangedDirectory = false;
            var visitedHealthyAfterChange = false;
            using var monitor = new SessionLogMonitor(home, TimeZoneInfo.Utc, OpenDirectory);
            var before = await monitor.RefreshAsync(now);
            Need(before.SourceCount == 1 && before.InputTokens == 100 && before.OutputTokens == 25 && !before.IsPartial,
                "The healthy sibling must supply the initial subtotal");

            changeQueuedDirectory = true;
            var after = await monitor.RefreshAsync(now.AddSeconds(1));
            Console.WriteLine($"DISCOVERY {change}: sources={after.SourceCount}, input={after.InputTokens?.ToString() ?? "unknown"}, "
                + $"output={after.OutputTokens?.ToString() ?? "unknown"}, partial={after.IsPartial}, "
                + $"issues=[{string.Join(", ", after.Issues.Select(issue => issue.Code))}]");
            var replaced = change is QueuedDirectoryChange.ReplacedByFile or QueuedDirectoryChange.ReplacedWhileOpening
                or QueuedDirectoryChange.LockedReplacementWhileOpening;
            Need(Directory.Exists(changing) == (!replaced && change != QueuedDirectoryChange.Removed)
                && File.Exists(changing) == replaced,
                "Directory A did not change as configured: " + change);
            Need(openedChangedDirectory == (change is not (QueuedDirectoryChange.Removed or QueuedDirectoryChange.ReplacedByFile)),
                "Directory A was opened at the wrong stage: " + change);
            if (change == QueuedDirectoryChange.LockedReplacementWhileOpening)
                Need(replacementLock is { CanWrite: true }, "The replacement must remain exclusively locked through the refresh");
            // The move-next failure occurs after one eligible A log was already discovered.
            var sourceCount = change == QueuedDirectoryChange.AccessDeniedDuringEnumeration ? 2 : 1;
            Need(after.SourceCount == sourceCount && after.InputTokens == 100 * sourceCount && after.OutputTokens == 25 * sourceCount,
                "The healthy sibling subtotal was lost: " + change);
            Need(visitedHealthyAfterChange, "The monitor did not enumerate the healthy sibling");
            var shouldBePartial = change != QueuedDirectoryChange.None;
            Need(after.IsPartial == shouldBePartial && after.Issues.Any(issue => issue.Code == DailyIssueCodes.SourceUnreadable) == shouldBePartial,
                "Discovery completeness did not match the directory outcome");
            Need(after.Issues.All(issue => issue.Code != DailyIssueCodes.SourceLimitReached),
                "A changed directory was misclassified as a source limit");
            Console.WriteLine($"PASS: {change} preserves the healthy sibling subtotal and discovery status");

            void ChangeDirectory()
            {
                Directory.Delete(changing);
                if (change == QueuedDirectoryChange.LockedReplacementWhileOpening)
                {
                    replacementLock = new FileStream(changing, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
                    replacementLock.WriteByte(0x41);
                    replacementLock.Flush();
                }
                else if (change != QueuedDirectoryChange.Removed)
                    File.WriteAllText(changing, "ordinary synthetic file");
            }

            IEnumerable<FileSystemInfo> OpenDirectory(DirectoryInfo directory)
            {
                if (changeQueuedDirectory && directory.FullName == changing)
                {
                    openedChangedDirectory = true;
                    if (change == QueuedDirectoryChange.IoFailureWhileOpening)
                        throw new IOException("Synthetic directory open failure");
                }
                return Enumerate(directory);
            }

            IEnumerable<FileSystemInfo> Enumerate(DirectoryInfo directory)
            {
                if (directory.FullName == sessions)
                {
                    // Both children are queued before discovery opens either of them.
                    yield return new DirectoryInfo(changing);
                    yield return new DirectoryInfo(healthy);
                    if (changeQueuedDirectory && change is QueuedDirectoryChange.Removed or QueuedDirectoryChange.ReplacedByFile)
                        ChangeDirectory();
                    yield break;
                }

                // This iterator starts when discovery advances its enumerator, after the attribute check.
                if (changeQueuedDirectory && directory.FullName == changing)
                {
                    if (change is QueuedDirectoryChange.ReplacedWhileOpening or QueuedDirectoryChange.LockedReplacementWhileOpening)
                        ChangeDirectory();
                    if (change == QueuedDirectoryChange.AccessDeniedDuringEnumeration)
                    {
                        yield return new FileInfo(WriteLog(changing, "readable-before-denial", now));
                        throw new UnauthorizedAccessException("Synthetic denial while enumerating directory");
                    }
                }
                if (changeQueuedDirectory && directory.FullName == healthy)
                    visitedHealthyAfterChange = true;
                foreach (var entry in SessionLogMonitor.EnumerateDirectory(directory))
                    yield return entry;
            }
        }
        finally
        {
            replacementLock?.Dispose();
            Directory.Delete(home, recursive: true);
        }
    }

    private static string WriteLog(string directory, string thread, DateTimeOffset now)
    {
        var log = Path.Combine(directory, "synthetic.jsonl");
        var totals = new { input_tokens = 100L, cached_input_tokens = 60L, output_tokens = 25L, reasoning_output_tokens = 0L, total_tokens = 125L };
        var metadata = JsonSerializer.Serialize(new { type = "session_meta", timestamp = now,
            payload = new { id = thread, source = "cli" } });
        var usage = JsonSerializer.Serialize(new { type = "token_usage_record", timestamp = now, payload = new
        {
            thread_id = thread, session_id = "run", turn_id = "turn", root_turn_id = "turn", response_id = "one",
            usage = totals, turn_token_usage = totals, thread_token_usage = totals
        } });
        File.WriteAllText(log, metadata + "\n" + usage + "\n");
        File.SetLastWriteTimeUtc(log, now.UtcDateTime);
        return log;
    }

    private static void Need(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
