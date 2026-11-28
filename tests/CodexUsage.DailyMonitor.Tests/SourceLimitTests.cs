using System.Text.Json;
using CodexUsage.Daily;
using CodexUsage.DailyPanel;

internal static class SourceLimitTests
{
    // This is the diagnostic contract exposed in snapshot JSON.
    private const string LimitCode = "source.limit_reached";

    public static async Task RunAsync()
    {
        const int limit = 32;
        var home = Path.Combine(Path.GetTempPath(), "CodexDaily-source-limit-" + Guid.NewGuid().ToString("N"));
        var sessions = Path.Combine(home, "sessions");
        var archive = Path.Combine(home, "archived_sessions");
        var now = new DateTimeOffset(2026, 10, 23, 12, 0, 0, TimeSpan.Zero);
        Directory.CreateDirectory(sessions);
        Directory.CreateDirectory(archive);
        try
        {
            var paths = Enumerable.Range(1, limit).Select(id => Write(sessions, id, now)).ToArray();
            using var monitor = new SessionLogMonitor(home, TimeZoneInfo.Utc);

            Check(await monitor.RefreshAsync(now), readableSources: 32, capped: false, unreadable: false);
            var extra = Write(archive, limit + 1, now.AddDays(-1));
            Check(await monitor.RefreshAsync(now), readableSources: 32, capped: false, unreadable: false);
            Console.WriteLine("PASS: exactly 32 eligible sources clear the limit even with an older archive");

            File.SetLastWriteTimeUtc(extra, now.UtcDateTime);
            Check(await monitor.RefreshAsync(now), readableSources: 32, capped: true, unreadable: false);
            Console.WriteLine("PASS: the 33rd eligible source marks the subtotal partial without a read failure");

            using (new FileStream(paths[0], FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                Check(await monitor.RefreshAsync(now), readableSources: 31, capped: true, unreadable: true);
                File.Delete(extra); // The skipped source never contributed to the retained subtotal.
                Check(await monitor.RefreshAsync(now), readableSources: 31, capped: false, unreadable: true);
            }
            Console.WriteLine("PASS: lock and cap coexist, then clearing the cap leaves the real read failure");

            Check(await monitor.RefreshAsync(now), readableSources: 32, capped: false, unreadable: false);
            Console.WriteLine("PASS: unlock restores all 32 sources and clears their partial reasons");

            string Write(string directory, int id, DateTimeOffset modifiedAt)
            {
                var thread = "synthetic-root-" + id;
                var path = Path.Combine(directory, thread + ".jsonl");
                var totals = new { input_tokens = 100, cached_input_tokens = 60, output_tokens = 25, reasoning_output_tokens = 10, total_tokens = 125 };
                var metadata = JsonSerializer.Serialize(new
                {
                    type = "session_meta", timestamp = now.AddMinutes(-1), payload = new { id = thread, source = "cli" }
                });
                var usage = JsonSerializer.Serialize(new
                {
                    type = "token_usage_record", timestamp = now.AddMinutes(-1),
                    payload = new
                    {
                        thread_id = thread, session_id = "run", turn_id = "turn", root_turn_id = "turn", response_id = "request-" + id,
                        usage = totals, turn_token_usage = totals, thread_token_usage = totals
                    }
                });
                File.WriteAllText(path, metadata + "\n" + usage + "\n");
                File.SetLastWriteTimeUtc(path, modifiedAt.UtcDateTime);
                return path;
            }
        }
        finally { Directory.Delete(home, recursive: true); }
    }

    private static void Check(DailySnapshot snapshot, int readableSources, bool capped, bool unreadable)
    {
        bool Has(string code) => snapshot.Issues.Any(issue => issue.Code == code);
        var label = $"readable={readableSources}, capped={capped}, unreadable={unreadable}";
        Need(snapshot.SourceCount == 32 && snapshot.InputTokens == 100 * readableSources
            && snapshot.OutputTokens == 25 * readableSources && snapshot.CachedInputTokens == 60 * readableSources
            && snapshot.ReasoningTokens == 10 * readableSources, "Incorrect source count or verifiable subtotal: " + label);
        Need(snapshot.IsPartial == (capped || unreadable), "Incorrect Partial state: " + label);
        Need(Has(LimitCode) == capped, "Missing or spurious source-limit reason: " + label);
        if (capped)
            Need(snapshot.Issues.Single(issue => issue.Code == LimitCode).Parameters["limit"] == "32", "Incorrect limit parameter");
        Need(Has(DailyIssueCodes.SourceUnreadable) == unreadable, "Incorrect unreadable classification: " + label);
        Need(Has(DailyIssueCodes.SourcePartial) == unreadable, "Cap alone must not imply a loading/read failure: " + label);
        Need(!Has(DailyIssueCodes.CoverageLost), "Skipping an uncached archive must not introduce history loss: " + label);
    }

    private static void Need(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
