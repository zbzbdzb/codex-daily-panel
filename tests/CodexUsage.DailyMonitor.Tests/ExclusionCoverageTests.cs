using System.Text;
using System.Text.Json;
using CodexUsage.Daily;
using CodexUsage.DailyPanel;

internal static class ExclusionCoverageTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 21, 12, 0, 0, TimeSpan.FromHours(8));
    private static readonly DateTimeOffset Now = At.AddMinutes(10);
    private static readonly TimeZoneInfo Zone = TimeZoneInfo.CreateCustomTimeZone(
        "SyntheticExclusionUTC8", TimeSpan.FromHours(8), "UTC+8", "UTC+8");
    private static readonly UTF8Encoding Utf8 = new(false, true);

    public static async Task RunAsync()
    {
        var passed = 0;
        var failures = new List<string>();
        async Task Case(string name, Func<Task> action)
        {
            try
            {
                await action();
                passed++;
                Console.WriteLine("PASS: " + name);
            }
            catch (Exception exception)
            {
                var failure = name + ": " + exception.Message;
                failures.Add(failure);
                Console.WriteLine("FAIL: " + failure);
            }
        }

        await Case("EXC-01 losing yesterday's first occurrence cannot promote today's replay to complete coverage", async () =>
        {
            using var fixture = new Fixture("first-occurrence-loss");
            var oldRows = Metadata("A", At.AddDays(-1).AddHours(-1))
                + Modern("A", "shared-request", "shared-turn", 100, 60, 40, 10, At.AddDays(-1));
            var yesterday = fixture.WriteSession("yesterday", oldRows);
            fixture.WriteSession("today", Metadata("A", At.AddHours(-1))
                + Modern("A", "shared-request", "shared-turn", 100, 60, 40, 10, At)
                + Metadata("B", At.AddHours(-1)) + Modern("B", "today-B", "turn-B", 200, 80, 70, 20, At));
            using var monitor = new SessionLogMonitor(fixture.Home, Zone);
            Expect(await monitor.RefreshAsync(Now), 200, 80, 70, 20, false, "yesterday's request is excluded from today");
            Expect(await monitor.RefreshAsync(Now), 200, 80, 70, 20, false, "idle baseline keeps the original allocation");
            File.Delete(yesterday);
            Expect(await monitor.RefreshAsync(Now), 200, 80, 70, 20, true, "remembered occurrence date prevents replay from becoming today's usage");
            Expect(await monitor.RefreshAsync(Now), 200, 80, 70, 20, true, "idle cannot erase lost first-occurrence evidence");
            fixture.WriteSession("yesterday", oldRows);
            Expect(await monitor.RefreshAsync(Now), 200, 80, 70, 20, false, "restored first occurrence excludes A again");
            Expect(await monitor.RefreshAsync(Now), 200, 80, 70, 20, false, "restored allocation remains complete on idle");
        });

        await Case("EXC-02 losing a newer child marker cannot silently promote its usage into root coverage", async () =>
        {
            using var fixture = new Fixture("child-marker-loss");
            fixture.WriteSession("old-root-and-usage", Metadata("A", At.AddHours(-1))
                + Modern("A", "request-A", "turn-A", 100, 60, 40, 10, At));
            fixture.WriteSession("independent-root", Metadata("B", At.AddHours(-1))
                + Modern("B", "request-B", "turn-B", 200, 80, 70, 20, At));
            var markerRows = Row("session_meta", At.AddSeconds(1), new
            {
                id = Session("A"),
                source = new { subagent = new { thread_spawn = new { parent_thread_id = Session("B") } } }
            });
            var marker = fixture.WriteSession("newer-child-marker", markerRows);
            using var monitor = new SessionLogMonitor(fixture.Home, Zone);
            Expect(await monitor.RefreshAsync(Now), 200, 80, 70, 20, false, "newer metadata confirms A is a child");
            Expect(await monitor.RefreshAsync(Now), 200, 80, 70, 20, false, "idle keeps A outside root scope");
            File.Delete(marker);
            Expect(await monitor.RefreshAsync(Now), 200, 80, 70, 20, true, "remembered child relation prevents older root metadata from promoting usage");
            Expect(await monitor.RefreshAsync(Now), 200, 80, 70, 20, true, "lost child exclusion remains visible on idle");
            fixture.WriteSession("newer-child-marker", markerRows);
            Expect(await monitor.RefreshAsync(Now), 200, 80, 70, 20, false, "restored child marker restores the original scope");
            Expect(await monitor.RefreshAsync(Now), 200, 80, 70, 20, false, "restored scope remains complete on idle");
        });

        await Case("EXC-03 losing historical cross-turn call identity conflict cannot manufacture trusted TPS", async () =>
        {
            using var fixture = new Fixture("historical-tool-conflict");
            var oldAt = At.AddDays(-1);
            var oldRows = Metadata("timing", oldAt.AddHours(-1)) + Start("old-turn", oldAt)
                + Tool("function_call", "old-turn", oldAt.AddSeconds(2))
                + Tool("function_call_output", "old-turn", oldAt.AddSeconds(5))
                + Row("event_msg", oldAt.AddSeconds(10), new { type = "task_aborted", turn_id = Turn("old-turn") });
            var historical = fixture.WriteSession("historical-tool-owner", oldRows);
            fixture.WriteSession("completed-today", CompletedTimingRows());
            using var monitor = new SessionLogMonitor(fixture.Home, Zone);
            var baseline = await monitor.RefreshAsync(Now);
            Expect(baseline, 100, 60, 40, 10, false, "only today's completed request contributes tokens");
            ExpectUnavailableTiming(baseline, "same call identity belongs to different turns");
            var idleBefore = await monitor.RefreshAsync(Now);
            Expect(idleBefore, 100, 60, 40, 10, false, "tool conflict baseline is stable on idle");
            ExpectUnavailableTiming(idleBefore, "idle retains the existing timing exclusion");
            File.Delete(historical);
            var lost = await monitor.RefreshAsync(Now);
            Expect(lost, 100, 60, 40, 10, true, "token subtotal survives lost timing exclusion evidence");
            ExpectUnavailableTiming(lost, "removing a conflict cannot create trusted adjusted timing");
            var idleAfter = await monitor.RefreshAsync(Now);
            Expect(idleAfter, 100, 60, 40, 10, true, "timing exclusion debt persists on idle");
            ExpectUnavailableTiming(idleAfter, "idle cannot turn unavailable TPS into a value");
            fixture.WriteSession("historical-tool-owner", oldRows);
            var restored = await monitor.RefreshAsync(Now);
            Expect(restored, 100, 60, 40, 10, false, "restored historical evidence recovers coverage");
            ExpectUnavailableTiming(restored, "original cross-turn conflict still excludes today's timing");
        });

        await Case("EXC-04 a read failure before an appended tail cannot be discharged by an old prefix mirror", async () =>
        {
            using var fixture = new Fixture("unread-tail-loss");
            var oldRows = Metadata("A", At.AddHours(-1))
                + Modern("A", "request-A", "turn-A", 100, 60, 40, 10, At);
            var tail = Modern("A", "request-B", "turn-B", 200, 80, 70, 20, At.AddSeconds(1));
            var source = fixture.WriteSession("growing-source", oldRows);
            fixture.WriteArchive("old-prefix-mirror", oldRows);
            using var monitor = new SessionLogMonitor(fixture.Home, Zone);
            Expect(await monitor.RefreshAsync(Now), 100, 60, 40, 10, false, "original prefix and mirror deduplicate");
            Expect(await monitor.RefreshAsync(Now), 100, 60, 40, 10, false, "idle before new tail");
            File.AppendAllText(source, tail, Utf8);
            File.SetLastWriteTimeUtc(source, Now.UtcDateTime);
            using (var held = new FileStream(source, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                Expect(await monitor.RefreshAsync(Now), 100, 60, 40, 10, true, "old mirror remains readable while new source tail cannot be opened");
            File.Delete(source);
            Expect(await monitor.RefreshAsync(Now), 100, 60, 40, 10, true, "prefix equality does not account for the unread tail");
            Expect(await monitor.RefreshAsync(Now), 100, 60, 40, 10, true, "unread-tail uncertainty cannot clear on idle");
            fixture.WriteArchive("full-replay", oldRows + tail);
            var restored = await monitor.RefreshAsync(Now);
            ExpectTokens(restored, 300, 140, 110, 30, "full replay supplies current A and B counters once");
            // The old reader never observed the tail. A full current replay may still
            // leave an unprovable historical-coverage reason; do not require complete.
            ExpectTokens(await monitor.RefreshAsync(Now), 300, 140, 110, 30, "full current replay remains deduplicated on idle");
        });

        await Case("EXC-05 an initially unreadable source can recover while the original source remains present", async () =>
        {
            using var fixture = new Fixture("unread-source-recovery-control");
            var source = fixture.WriteSession("initially-unreadable", Metadata("A", At.AddHours(-1))
                + Modern("A", "request-A", "turn-A", 100, 60, 40, 10, At));
            fixture.WriteSession("readable-B", Metadata("B", At.AddHours(-1))
                + Modern("B", "request-B", "turn-B", 200, 80, 70, 20, At));
            using var monitor = new SessionLogMonitor(fixture.Home, Zone);
            using (var held = new FileStream(source, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                Expect(await monitor.RefreshAsync(Now), 200, 80, 70, 20, true, "first read of A is blocked");
            Expect(await monitor.RefreshAsync(Now), 300, 140, 110, 30, false, "original source is read to EOF after the lock ends");
            Expect(await monitor.RefreshAsync(Now), 300, 140, 110, 30, false, "successful source recovery remains complete on idle");
        });

        await Case("EXC-06 today's tool fixture has reliable timing when no conflicting historical owner exists", async () =>
        {
            using var fixture = new Fixture("tool-timing-control");
            fixture.WriteSession("completed-today", CompletedTimingRows());
            using var monitor = new SessionLogMonitor(fixture.Home, Zone);
            var snapshot = await monitor.RefreshAsync(Now);
            Expect(snapshot, 100, 60, 40, 10, false, "healthy timing control request");
            Require(snapshot.ProcessingSeconds is { } processing && Math.Abs(processing - 6) < 0.000001,
                "10 seconds minus 1 second TTFT and 3 seconds tool wait yields 6 processing seconds");
            Require(snapshot.AverageTps is { } tps && Math.Abs(tps - 6.666666666666667) < 0.000001
                && snapshot.TimedTurnCount == 1 && snapshot.ExcludedTimingTurnCount == 0,
                "healthy control includes one completed turn at 40 output tokens / 6 processing seconds");
        });

        await Case("EXC-07 an interrupted longer mirror settles EOF before a proof replay and recovers within two refreshes", async () =>
        {
            using var fixture = new Fixture("interrupted-mirror-replay");
            var clean = Metadata("A", At.AddHours(-1))
                + Modern("A", "request-A", "turn-A", 100, 60, 40, 10, At);
            var second = Modern("A", "request-B", "turn-B", 200, 80, 70, 20, At.AddSeconds(1));
            var shortPath = fixture.WriteSession("short-half-line", clean + second[..(second.Length / 2)]);
            var mirrorPath = fixture.WriteArchive("long-complete-mirror", clean + second);
            using var monitor = new SessionLogMonitor(fixture.Home, Zone);
            Expect(await monitor.RefreshAsync(Now), 300, 140, 110, 30, true, "long mirror was fully read while short file has a half-line");
            using (var held = new FileStream(mirrorPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                Expect(await monitor.RefreshAsync(Now), 100, 60, 40, 10, true, "mirror read failure leaves only short file's complete request");
            File.Delete(shortPath);
            ExpectTokens(await monitor.RefreshAsync(Now), 300, 140, 110, 30,
                "first recovery refresh reads the original mirror through EOF");
            Expect(await monitor.RefreshAsync(Now), 300, 140, 110, 30, false,
                "second refresh at latest proves the short prefix without creating permanent uncertainty");
            Expect(await monitor.RefreshAsync(Now), 300, 140, 110, 30, false, "settled replay stays complete on idle");
        });

        await Case("EXC-08 lost timing exclusion is scoped to its root while an independent completed root keeps TPS", async () =>
        {
            using var fixture = new Fixture("scoped-timing-exclusion");
            var oldAt = At.AddDays(-1);
            var historicalRows = Metadata("timing", oldAt.AddHours(-1)) + Start("old-turn", oldAt)
                + Tool("function_call", "old-turn", oldAt.AddSeconds(2))
                + Tool("function_call_output", "old-turn", oldAt.AddSeconds(5))
                + Row("event_msg", oldAt.AddSeconds(10), new { type = "task_aborted", turn_id = Turn("old-turn") });
            var historical = fixture.WriteSession("historical-tool-owner", historicalRows);
            fixture.WriteSession("affected-root-A", CompletedTimingRows());
            fixture.WriteSession("independent-root-B", CompletedTimingRows()
                .Replace(Session("timing"), Session("unrelated"), StringComparison.Ordinal)
                .Replace("synthetic-exclusion-request-timed-request", "synthetic-exclusion-request-unrelated-request", StringComparison.Ordinal));
            using var monitor = new SessionLogMonitor(fixture.Home, Zone);
            void ExpectOnlyIndependentTiming(DailySnapshot snapshot, string context) => Require(
                snapshot.ProcessingSeconds is { } processing && Math.Abs(processing - 6) < 0.000001
                && snapshot.AverageTps is { } tps && Math.Abs(tps - 6.666666666666667) < 0.000001
                && snapshot.TimedTurnCount == 1 && snapshot.ExcludedTimingTurnCount == 1,
                $"{context}: only B should have 6 processing seconds and TPS=6.666666666666667; "
                + $"actual processing={snapshot.ProcessingSeconds}, TPS={snapshot.AverageTps}, "
                + $"timed={snapshot.TimedTurnCount}, excluded={snapshot.ExcludedTimingTurnCount}; Status={snapshot.Status}");
            var baseline = await monitor.RefreshAsync(Now);
            Expect(baseline, 200, 120, 80, 20, false, "both current requests contribute tokens");
            ExpectOnlyIndependentTiming(baseline, "historical conflict excludes A but not B");
            File.Delete(historical);
            var lost = await monitor.RefreshAsync(Now);
            Expect(lost, 200, 120, 80, 20, true, "lost A timing exclusion leaves current token subtotal partial");
            ExpectOnlyIndependentTiming(lost, "lost evidence cannot suppress unrelated B timing or admit A");
            var idle = await monitor.RefreshAsync(Now);
            Expect(idle, 200, 120, 80, 20, true, "scoped timing debt persists on idle");
            ExpectOnlyIndependentTiming(idle, "independent timing survives idle debt reconciliation");
            fixture.WriteSession("historical-tool-owner", historicalRows);
            var restored = await monitor.RefreshAsync(Now);
            Expect(restored, 200, 120, 80, 20, false, "restored historical conflict recovers coverage");
            ExpectOnlyIndependentTiming(restored, "restoration preserves the original timing qualifications");
        });

        await Case("EXC-09 a dormant lost origin becomes relevant when a later source replays that request today", async () =>
        {
            using var fixture = new Fixture("dormant-origin");
            var yesterdayRows = Metadata("A", At.AddDays(-1).AddHours(-1))
                + Modern("A", "shared-request", "shared-turn", 100, 60, 40, 10, At.AddDays(-1));
            var yesterday = fixture.WriteSession("yesterday-only", yesterdayRows);
            fixture.WriteSession("independent-B", Metadata("B", At.AddHours(-1))
                + Modern("B", "today-B", "turn-B", 200, 80, 70, 20, At));
            using var monitor = new SessionLogMonitor(fixture.Home, Zone);
            Expect(await monitor.RefreshAsync(Now), 200, 80, 70, 20, false, "yesterday A does not contribute today");
            File.Delete(yesterday);
            Expect(await monitor.RefreshAsync(Now), 200, 80, 70, 20, false, "lost origin stays dormant while A has no current candidates");
            Expect(await monitor.RefreshAsync(Now), 200, 80, 70, 20, false, "dormant guard does not taint independent B on idle");
            fixture.WriteSession("later-replay", Metadata("A", At.AddHours(-1))
                + Modern("A", "shared-request", "shared-turn", 100, 60, 40, 10, At));
            Expect(await monitor.RefreshAsync(Now), 200, 80, 70, 20, true, "remembered origin excludes later A replay but its lost proof is now relevant");
            Expect(await monitor.RefreshAsync(Now), 200, 80, 70, 20, true, "origin guard stays active on idle");
            fixture.WriteSession("yesterday-only", yesterdayRows);
            Expect(await monitor.RefreshAsync(Now), 200, 80, 70, 20, false, "restored yesterday origin discharges the active guard");
        });

        await Case("EXC-10 an explicitly newer root relation can supersede a lost child marker", async () =>
        {
            using var fixture = new Fixture("new-root-after-child-loss");
            fixture.WriteSession("old-root-and-A", Metadata("A", At.AddHours(-1))
                + Modern("A", "request-A", "turn-A", 100, 60, 40, 10, At));
            fixture.WriteSession("independent-B", Metadata("B", At.AddHours(-1))
                + Modern("B", "request-B", "turn-B", 200, 80, 70, 20, At));
            var child = fixture.WriteSession("child-marker", Row("session_meta", At.AddSeconds(1), new
            {
                id = Session("A"),
                source = new { subagent = new { thread_spawn = new { parent_thread_id = Session("B") } } }
            }));
            using var monitor = new SessionLogMonitor(fixture.Home, Zone);
            Expect(await monitor.RefreshAsync(Now), 200, 80, 70, 20, false, "latest confirmed relation makes A a child");
            File.Delete(child);
            Expect(await monitor.RefreshAsync(Now), 200, 80, 70, 20, true, "lost child fact still excludes A while its proof is missing");
            fixture.WriteSession("explicit-newer-root", Metadata("A", At.AddSeconds(2)));
            Expect(await monitor.RefreshAsync(Now), 300, 140, 110, 30, false, "a newer valid root marker supersedes the lost child relation");
            Expect(await monitor.RefreshAsync(Now), 300, 140, 110, 30, false, "superseded child guard does not remain permanently partial");
        });

        await Case("EXC-11 truncating an observed unread tail exactly to the cursor still resets the reader", async () =>
        {
            using var fixture = new Fixture("observed-tail-truncated-to-cursor");
            var path = fixture.WriteSession("large-observed-source", Row("response_item", At,
                new { type = "message", content = new string('x', 5 * 1024 * 1024) }));
            using var reader = new DailyLogReader(path);
            var resets = 0;
            reader.ResetDetected += () => resets++;
            var first = await reader.ReadAvailableAsync(_ => { });
            Require(first.BytesRead == 4194304 && reader.Offset == 4194304 && first.HasMore,
                "first read consumes only the 4 MiB cursor budget from a file larger than 5 MiB");
            Require(reader.Coverage.ObservedLength > reader.Coverage.Stamp.Bytes,
                "the reader observed file content beyond its consumed prefix");
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
                stream.SetLength(reader.Offset);
            var after = await reader.ReadAvailableAsync(_ => { });
            Require(after.ResetDetected && resets == 1,
                "a shorter observed length must reset even when new length equals the cursor and both probes still match");
            Require(reader.CoverageBeforeReset.ObservedLength > reader.CoverageBeforeReset.Stamp.Bytes,
                "pre-reset coverage preserves the formerly observed unread tail instead of overwriting its length");
        });

        await Case("EXC-12 the explicit access-denied retry resumes after a configured directory becomes a file", async () =>
        {
            using var fixture = new Fixture("directory-denied-retry");
            var path = Path.Combine(fixture.Home, "sessions", "configured-directory.jsonl");
            Directory.CreateDirectory(path);
            using var reader = new DailyLogReader(path);
            var delivered = new List<string>();
            var denied = await reader.ReadAvailableAsync(delivered.Add);
            Require(denied.AccessDenied && reader.IsStopped && delivered.Count == 0,
                "Windows denies opening the explicitly configured directory as a log file and stops the reader");
            Directory.Delete(path);
            File.WriteAllText(path, Metadata("A", At.AddHours(-1))
                + Modern("A", "request-A", "turn-A", 100, 60, 40, 10, At), Utf8);
            File.SetLastWriteTimeUtc(path, Now.UtcDateTime);
            reader.RetryAccessDenied();
            var recovered = await reader.ReadAvailableAsync(delivered.Add);
            Require(!recovered.AccessDenied && !recovered.Missing && !reader.IsStopped
                && !recovered.HasMore && !recovered.WaitingForCompleteLine
                && recovered.LinesRead == 2 && delivered.Count == 2,
                "explicit retry reads both complete rows from the replacement regular file through EOF");
            var idle = await reader.ReadAvailableAsync(delivered.Add);
            Require(idle.BytesRead == 0 && idle.LinesRead == 0 && !idle.AccessDenied && delivered.Count == 2,
                "idle retry poll neither repeats delivered rows nor restores the access-denied state");
        });

        await Case("EXC-13 losing a child marker remains relevant when the excluded source contains only legacy usage", async () =>
        {
            using var fixture = new Fixture("legacy-child-marker-loss");
            fixture.WriteSession("legacy-child-candidate", Metadata("A", At.AddHours(-1))
                + Row("event_msg", At, new
                {
                    type = "token_count", info = new
                    {
                        last_token_usage = Vector(900, 500, 100, 20),
                        total_token_usage = Vector(900, 500, 100, 20)
                    }
                }));
            fixture.WriteSession("independent-modern-B", Metadata("B", At.AddHours(-1))
                + Modern("B", "request-B", "turn-B", 200, 80, 70, 20, At));
            var markerRows = Row("session_meta", At.AddSeconds(1), new
            {
                id = Session("A"),
                source = new { subagent = new { thread_spawn = new { parent_thread_id = Session("B") } } }
            });
            var marker = fixture.WriteSession("child-marker", markerRows);
            using var monitor = new SessionLogMonitor(fixture.Home, Zone);
            Expect(await monitor.RefreshAsync(Now), 200, 80, 70, 20, false, "confirmed legacy child cannot taint root B's subtotal");
            File.Delete(marker);
            Expect(await monitor.RefreshAsync(Now), 200, 80, 70, 20, true,
                "legacy's missing request and turn identities do not make its lost child exclusion irrelevant");
            Expect(await monitor.RefreshAsync(Now), 200, 80, 70, 20, true,
                "legacy child exclusion debt persists on idle without adding legacy counters");
            fixture.WriteSession("child-marker", markerRows);
            Expect(await monitor.RefreshAsync(Now), 200, 80, 70, 20, false,
                "restored child marker discharges legacy scope uncertainty");
        });

        await Case("EXC-14 midnight expires daily gaps without forgetting a lost request's original day", async () =>
        {
            using var fixture = new Fixture("lost-origin-across-midnight");
            var oldRows = Metadata("A", At.AddDays(-1).AddHours(-1))
                + Modern("A", "old-request", "old-turn", 100, 60, 40, 10, At.AddDays(-1));
            var oldPath = fixture.WriteSession("old-origin", oldRows);
            fixture.WriteSession("day-one-B", Metadata("B", At.AddHours(-1))
                + Modern("B", "day-one-B", "day-one-B", 200, 80, 70, 20, At));
            using var monitor = new SessionLogMonitor(fixture.Home, Zone);
            Expect(await monitor.RefreshAsync(Now), 200, 80, 70, 20, false, "day one baseline");
            File.Delete(oldPath);
            Expect(await monitor.RefreshAsync(Now), 200, 80, 70, 20, false,
                "lost old origin is dormant while there is no current replay");
            var next = At.AddDays(1);
            var nextPath = fixture.WriteSession("day-two", Metadata("A", next.AddHours(-1))
                + Modern("A", "old-request", "old-turn", 100, 60, 40, 10, next)
                + Metadata("B", next.AddHours(-1))
                + Modern("B", "day-two-B", "day-two-B", 200, 80, 70, 20, next));
            File.SetLastWriteTimeUtc(nextPath, next.AddMinutes(10).UtcDateTime);
            Expect(await monitor.RefreshAsync(Now.AddDays(1)), 200, 80, 70, 20, true,
                "midnight must not promote the known old request into the new day");
            Expect(await monitor.RefreshAsync(Now.AddDays(1)), 200, 80, 70, 20, true,
                "new-day idle keeps the conditional origin debt");
            var restored = fixture.WriteSession("old-origin", oldRows);
            File.SetLastWriteTimeUtc(restored, next.AddMinutes(10).UtcDateTime);
            Expect(await monitor.RefreshAsync(Now.AddDays(1)), 200, 80, 70, 20, false,
                "restored raw origin proves the new-day replay remains excluded");
        });

        await Case("EXC-15 a first occurrence read yesterday survives rollover before its source loss is observed", async () =>
        {
            using var fixture = new Fixture("rollover-unobserved-origin-loss");
            var yesterdayNow = new DateTimeOffset(2026, 10, 20, 23, 59, 0, TimeSpan.FromHours(8));
            var originalRows = Metadata("A", At.AddDays(-1).AddHours(-1))
                + Modern("A", "shared-request", "shared-turn", 100, 60, 40, 10, At.AddDays(-1));
            var original = fixture.WriteSession("yesterday-origin", originalRows);
            File.SetLastWriteTimeUtc(original, yesterdayNow.UtcDateTime);
            using var monitor = new SessionLogMonitor(fixture.Home, Zone);
            Expect(await monitor.RefreshAsync(yesterdayNow), 100, 60, 40, 10, false,
                "yesterday's first occurrence is fully read before midnight");
            Expect(await monitor.RefreshAsync(yesterdayNow.AddSeconds(10)), 100, 60, 40, 10, false,
                "the original source still exists at the last refresh of yesterday and has not generated a loss");
            File.Delete(original);
            fixture.WriteSession("today-replay", Metadata("A", At.AddHours(-1))
                + Modern("A", "shared-request", "shared-turn", 100, 60, 40, 10, At));
            fixture.WriteSession("today-independent-B", Metadata("B", At.AddHours(-1))
                + Modern("B", "today-B", "turn-B", 200, 80, 70, 20, At));
            Expect(await monitor.RefreshAsync(Now), 200, 80, 70, 20, true,
                "rollover preserves the known original date without counting A's today replay");
            Expect(await monitor.RefreshAsync(Now), 200, 80, 70, 20, true,
                "missing yesterday origin proof remains partial after rollover idle");
            fixture.WriteSession("yesterday-origin", originalRows);
            Expect(await monitor.RefreshAsync(Now), 200, 80, 70, 20, false,
                "restoring the original yesterday evidence proves A's allocation and completes B's subtotal");
            Expect(await monitor.RefreshAsync(Now), 200, 80, 70, 20, false,
                "restored cross-day allocation remains complete on idle");
        });

        await Case("EXC-16 a queued prefix replay interrupted before open can settle EOF and recover within three refreshes", async () =>
        {
            using var fixture = new Fixture("queued-replay-open-failure");
            var clean = Metadata("A", At.AddHours(-1))
                + Modern("A", "request-A", "turn-A", 100, 60, 40, 10, At);
            var second = Modern("A", "request-B", "turn-B", 200, 80, 70, 20, At.AddSeconds(1));
            var shortPath = fixture.WriteSession("short-half-line", clean + second[..(second.Length / 2)]);
            var mirrorPath = fixture.WriteArchive("long-complete-mirror", clean + second);
            using var monitor = new SessionLogMonitor(fixture.Home, Zone);
            Expect(await monitor.RefreshAsync(Now), 300, 140, 110, 30, true,
                "the long mirror is drained before any prefix challenge exists");
            File.Delete(shortPath);
            using (var held = new FileStream(mirrorPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                Expect(await monitor.RefreshAsync(Now), null, null, null, null, true,
                    "short-source deletion queues a prefix replay but opening its only mirror fails");
            ExpectTokens(await monitor.RefreshAsync(Now), 300, 140, 110, 30,
                "first unlocked refresh recovers the original mirror's current complete counters");
            ExpectTokens(await monitor.RefreshAsync(Now), 300, 140, 110, 30,
                "second unlocked refresh can verify the challenged prefix without repeating counters");
            Expect(await monitor.RefreshAsync(Now), 300, 140, 110, 30, false,
                "third unlocked refresh at latest must settle the original EOF and prefix challenge");
            Expect(await monitor.RefreshAsync(Now), 300, 140, 110, 30, false,
                "a replay queued before an open failure must not create permanent partiality");
        });

        await Case("EXC-17 two missing exclusion facts cannot make each other's coverage guard dormant", async () =>
        {
            using var fixture = new Fixture("composed-origin-and-child-loss");
            var yesterdayRows = Metadata("A", At.AddDays(-1).AddHours(-1))
                + Modern("A", "shared-request", "shared-turn", 100, 60, 40, 10, At.AddDays(-1));
            var yesterday = fixture.WriteSession("yesterday-origin", yesterdayRows);
            fixture.WriteSession("today-replay", Metadata("A", At.AddHours(-1))
                + Modern("A", "shared-request", "shared-turn", 100, 60, 40, 10, At));
            fixture.WriteSession("independent-B", Metadata("B", At.AddHours(-1))
                + Modern("B", "today-B", "turn-B", 200, 80, 70, 20, At));
            using var monitor = new SessionLogMonitor(fixture.Home, Zone);
            Expect(await monitor.RefreshAsync(Now), 200, 80, 70, 20, false,
                "available yesterday origin excludes A's today replay");
            File.Delete(yesterday);
            Expect(await monitor.RefreshAsync(Now), 200, 80, 70, 20, true,
                "missing original-date proof activates A's origin guard without adding A counters");
            var child = fixture.WriteSession("newer-child-marker", Row("session_meta", At.AddSeconds(1), new
            {
                id = Session("A"),
                source = new { subagent = new { thread_spawn = new { parent_thread_id = Session("B") } } }
            }));
            Expect(await monitor.RefreshAsync(Now), 200, 80, 70, 20, false,
                "an available newer child marker independently excludes A and makes its missing origin proof dormant");
            Expect(await monitor.RefreshAsync(Now), 200, 80, 70, 20, false,
                "available child proof keeps independent B complete on idle");
            File.Delete(child);
            Expect(await monitor.RefreshAsync(Now), 200, 80, 70, 20, true,
                "missing child proof and missing origin proof cannot mutually certify A's exclusion");
            Expect(await monitor.RefreshAsync(Now), 200, 80, 70, 20, true,
                "combined exclusion uncertainty remains partial on idle without resurrecting A counters");
        });

        Console.WriteLine($"RESULT EXCLUSION COVERAGE: {passed} passed, {failures.Count} failed");
        if (failures.Count > 0)
            throw new InvalidOperationException("Exclusion coverage regressions:\n" + string.Join("\n", failures));
    }

    private static void Expect(DailySnapshot actual, long? input, long? cached, long? output,
        long? reasoning, bool partial, string context)
    {
        ExpectTokens(actual, input, cached, output, reasoning, context);
        Require(actual.IsPartial == partial,
            $"{context}: expected Partial={partial}, actual Partial={actual.IsPartial}; Status={actual.Status}");
    }

    private static void ExpectTokens(DailySnapshot actual, long? input, long? cached, long? output,
        long? reasoning, string context) => Require(actual.InputTokens == input && actual.CachedInputTokens == cached
            && actual.OutputTokens == output && actual.ReasoningTokens == reasoning,
            $"{context}: expected input={input}, cached={cached}, output={output}, reasoning={reasoning}; "
            + $"actual input={actual.InputTokens}, cached={actual.CachedInputTokens}, output={actual.OutputTokens}, "
            + $"reasoning={actual.ReasoningTokens}; Status={actual.Status}");

    private static void ExpectUnavailableTiming(DailySnapshot actual, string context) =>
        Require(actual.AverageTps is null && actual.ProcessingSeconds is null && actual.TimedTurnCount == 0,
            $"{context}: expected unavailable TPS and processing seconds, actual TPS={actual.AverageTps}, "
            + $"processing={actual.ProcessingSeconds}, timed turns={actual.TimedTurnCount}; Status={actual.Status}");

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static string Session(string name) => "synthetic-exclusion-session-" + name;
    private static string Turn(string name) => "synthetic-exclusion-turn-" + name;

    private static string Metadata(string session, DateTimeOffset time) =>
        Row("session_meta", time, new { id = Session(session), source = "cli" });

    private static string Modern(string session, string request, string turn, long input, long cached,
        long output, long reasoning, DateTimeOffset time) => Row("token_usage_record", time, new
        {
            thread_id = Session(session), session_id = "synthetic-exclusion-run",
            turn_id = Turn(turn), root_turn_id = Turn(turn), response_id = "synthetic-exclusion-request-" + request,
            usage = Vector(input, cached, output, reasoning),
            turn_token_usage = Vector(input, cached, output, reasoning),
            thread_token_usage = Vector(input, cached, output, reasoning)
        });

    private static object Vector(long input, long cached, long output, long reasoning) => new
    {
        input_tokens = input, cached_input_tokens = cached, output_tokens = output,
        reasoning_output_tokens = reasoning, total_tokens = input + output
    };

    private static string Start(string turn, DateTimeOffset time) =>
        Row("event_msg", time, new { type = "task_started", turn_id = Turn(turn) });

    private static string Tool(string kind, string turn, DateTimeOffset time) => Row("response_item", time, new
    {
        type = kind, call_id = "synthetic-exclusion-shared-call", turn_id = Turn(turn),
        arguments = "{}", output = "synthetic tool result"
    });

    private static string CompletedTimingRows() => Metadata("timing", At.AddHours(-1)) + Start("today-turn", At)
        + Tool("function_call", "today-turn", At.AddSeconds(2))
        + Tool("function_call_output", "today-turn", At.AddSeconds(5))
        + Modern("timing", "timed-request", "today-turn", 100, 60, 40, 10, At.AddSeconds(9))
        + Row("event_msg", At.AddSeconds(10), new
        {
            type = "task_complete", turn_id = Turn("today-turn"), duration_ms = 10000, time_to_first_token_ms = 1000
        });

    private static string Row(string type, DateTimeOffset timestamp, object payload) =>
        JsonSerializer.Serialize(new { type, timestamp, payload }) + "\n";

    private sealed class Fixture : IDisposable
    {
        public string Home { get; }

        public Fixture(string name)
        {
            Home = Path.Combine(Path.GetTempPath(), "CodexDaily-exclusion-" + name + "-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(Home, "sessions"));
            Directory.CreateDirectory(Path.Combine(Home, "archived_sessions"));
        }

        public string WriteSession(string name, string contents) => Write("sessions", name, contents);
        public string WriteArchive(string name, string contents) => Write("archived_sessions", name, contents);

        private string Write(string folder, string name, string contents)
        {
            var path = Path.Combine(Home, folder, name + ".jsonl");
            File.WriteAllText(path, contents, Utf8);
            File.SetLastWriteTimeUtc(path, Now.UtcDateTime);
            return path;
        }

        public void Dispose() => Directory.Delete(Home, recursive: true);
    }
}
