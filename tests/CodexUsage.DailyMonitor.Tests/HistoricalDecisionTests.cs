using System.Text;
using System.Text.Json;
using CodexUsage.Daily;
using CodexUsage.DailyPanel;

internal static class HistoricalDecisionTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 21, 12, 0, 0, TimeSpan.FromHours(8));
    private static readonly DateTimeOffset Now = At.AddMinutes(10);
    private static readonly DateTimeOffset AfterMidnight = new(2026, 10, 21, 0, 1, 0, TimeSpan.FromHours(8));
    private static readonly TimeZoneInfo Zone = TimeZoneInfo.CreateCustomTimeZone(
        "SyntheticHistoricalUTC8", TimeSpan.FromHours(8), "UTC+8", "UTC+8");
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

        await Case("HIS-01 a request first observed while child keeps its original day after history loss and a newer root marker", async () =>
        {
            using var fixture = new Fixture("child-origin-before-root", Now);
            var yesterdayRows = ChildMetadata("A", "B", At.AddDays(-1).AddHours(-1))
                + Modern("A", "shared-request", "shared-turn", 100, 60, 40, 10, At.AddDays(-1));
            var yesterday = fixture.WriteSession("child-yesterday-origin", yesterdayRows);
            fixture.WriteSession("child-today-replay", ChildMetadata("A", "B", At.AddHours(-1))
                + Modern("A", "shared-request", "shared-turn", 100, 60, 40, 10, At));
            fixture.WriteSession("independent-root-B", RootMetadata("B", At.AddHours(-1))
                + Modern("B", "today-B", "turn-B", 200, 80, 70, 20, At));
            using var monitor = new SessionLogMonitor(fixture.Home, Zone);
            Expect(await monitor.RefreshAsync(Now), 200, 80, 70, 20, false,
                "initial child A is excluded while root B is complete");
            File.Delete(yesterday);
            Expect(await monitor.RefreshAsync(Now), 200, 80, 70, 20, false,
                "missing child history is dormant while current child metadata independently excludes A");
            Expect(await monitor.RefreshAsync(Now), 200, 80, 70, 20, false,
                "idle child scope does not manufacture daily coverage debt");
            fixture.WriteSession("newer-root-A", RootMetadata("A", At.AddSeconds(20)));
            Expect(await monitor.RefreshAsync(Now), 200, 80, 70, 20, true,
                "new root relation cannot turn A's remembered yesterday request into today's usage");
            Expect(await monitor.RefreshAsync(Now), 200, 80, 70, 20, true,
                "lost original-date proof stays partial without repeating B or adding A");
            fixture.WriteSession("child-yesterday-origin", yesterdayRows);
            Expect(await monitor.RefreshAsync(Now), 200, 80, 70, 20, false,
                "restored historical occurrence proves the original date under the newer root relation");
            Expect(await monitor.RefreshAsync(Now), 200, 80, 70, 20, false,
                "restored history remains complete and deduplicated on idle");
        });

        await Case("HIS-02 historical child tool conflicts survive history loss before the session becomes root", async () =>
        {
            using var fixture = new Fixture("child-tool-history-before-root", Now);
            var oldAt = At.AddDays(-1);
            var historicalRows = ChildMetadata("A", "parent", oldAt.AddHours(-1)) + Start("old-turn", oldAt)
                + Tool("function_call", "old-turn", oldAt.AddSeconds(2))
                + Tool("function_call_output", "old-turn", oldAt.AddSeconds(5))
                + Row("event_msg", oldAt.AddSeconds(10), new { type = "task_aborted", turn_id = Turn("old-turn") });
            var historical = fixture.WriteSession("child-old-tool-owner", historicalRows);
            fixture.WriteSession("child-completed-today", ChildMetadata("A", "parent", At.AddHours(-1))
                + Start("today-turn", At)
                + Tool("function_call", "today-turn", At.AddSeconds(2))
                + Tool("function_call_output", "today-turn", At.AddSeconds(5))
                + Modern("A", "timed-request", "today-turn", 100, 60, 40, 10, At.AddSeconds(9))
                + Complete("today-turn", At.AddSeconds(10)));
            using var monitor = new SessionLogMonitor(fixture.Home, Zone);
            var child = await monitor.RefreshAsync(Now);
            Expect(child, null, null, null, null, false, "initial child contributes no root token totals");
            ExpectUnavailableTiming(child, "initial child contributes no root timing");
            File.Delete(historical);
            var dormant = await monitor.RefreshAsync(Now);
            Expect(dormant, null, null, null, null, false, "lost child tool history is dormant while A remains child");
            ExpectUnavailableTiming(dormant, "dormant child history cannot expose root TPS");
            fixture.WriteSession("newer-root-A", RootMetadata("A", At.AddSeconds(20)));
            var root = await monitor.RefreshAsync(Now);
            Expect(root, 100, 60, 40, 10, true, "current readable A counters become root counters with incomplete historical coverage");
            ExpectUnavailableTiming(root, "remembered shared call identity conflict still excludes A's adjusted timing");
            var idle = await monitor.RefreshAsync(Now);
            Expect(idle, 100, 60, 40, 10, true, "historical tool exclusion stays partial on idle");
            ExpectUnavailableTiming(idle, "idle does not convert missing child history into trusted TPS");
            fixture.WriteSession("child-old-tool-owner", historicalRows);
            var restored = await monitor.RefreshAsync(Now);
            Expect(restored, 100, 60, 40, 10, false, "restored historical tool evidence completes root coverage");
            ExpectUnavailableTiming(restored, "the restored cross-turn conflict still excludes today's timing");
            Require(restored.ExcludedTimingTurnCount == 1,
                "restored original qualifications exclude exactly the completed today turn");
            var restoredIdle = await monitor.RefreshAsync(Now);
            Expect(restoredIdle, 100, 60, 40, 10, false, "restored tool history is deduplicated on idle");
            ExpectUnavailableTiming(restoredIdle, "restored timing exclusion remains stable on idle");
        });

        await Case("HIS-03A a successor read today keeps a prior-day unfinished turn inactive after successor loss", async () =>
        {
            using var fixture = new Fixture("successor-lost-after-today-read", AfterMidnight);
            var oldStart = new DateTimeOffset(2026, 10, 20, 23, 55, 0, TimeSpan.FromHours(8));
            var successorStart = new DateTimeOffset(2026, 10, 20, 23, 58, 0, TimeSpan.FromHours(8));
            fixture.WriteSession("old-unfinished-turn", RootMetadata("activity", oldStart.AddHours(-1))
                + Start("old-turn", oldStart));
            var successorRows = SuccessorRows(successorStart);
            var successor = fixture.WriteSession("completed-successor", successorRows);
            using var monitor = new SessionLogMonitor(fixture.Home, Zone);
            ExpectInactiveToday(await monitor.RefreshAsync(AfterMidnight), false,
                "both historical sources prove the old turn was superseded before midnight");
            ExpectInactiveToday(await monitor.RefreshAsync(AfterMidnight), false,
                "idle today does not restart the old unfinished turn");
            File.Delete(successor);
            ExpectInactiveToday(await monitor.RefreshAsync(AfterMidnight), true,
                "losing the already observed successor cannot restart the old turn or invent today's active seconds");
            ExpectInactiveToday(await monitor.RefreshAsync(AfterMidnight.AddMinutes(1)), true,
                "00:02 idle retains the known supersession without accruing active seconds");
            fixture.WriteSession("completed-successor", successorRows);
            ExpectInactiveToday(await monitor.RefreshAsync(AfterMidnight.AddMinutes(1)), false,
                "restored successor proves complete non-active history");
            ExpectInactiveToday(await monitor.RefreshAsync(AfterMidnight.AddMinutes(1)), false,
                "restored successor remains non-active on idle");
        });

        await Case("HIS-03B successor history read before midnight still suppresses the old turn after rollover loss", async () =>
        {
            using var fixture = new Fixture("successor-lost-before-rollover", AfterMidnight);
            var oldStart = new DateTimeOffset(2026, 10, 20, 23, 55, 0, TimeSpan.FromHours(8));
            var successorStart = new DateTimeOffset(2026, 10, 20, 23, 58, 0, TimeSpan.FromHours(8));
            var beforeMidnight = new DateTimeOffset(2026, 10, 20, 23, 59, 0, TimeSpan.FromHours(8));
            fixture.WriteSession("old-unfinished-turn", RootMetadata("activity", oldStart.AddHours(-1))
                + Start("old-turn", oldStart));
            var successorRows = SuccessorRows(successorStart);
            var successor = fixture.WriteSession("completed-successor", successorRows);
            using var monitor = new SessionLogMonitor(fixture.Home, Zone);
            var yesterday = await monitor.RefreshAsync(beforeMidnight);
            Expect(yesterday, 100, 60, 40, 10, false, "yesterday's completed successor request is counted once");
            Require(yesterday.ActiveTurns == 0 && yesterday.ActiveSeconds is { } active && Math.Abs(active - 10) < 0.000001,
                "only the known completed successor interval supplies yesterday's 10 active seconds");
            var yesterdayIdle = await monitor.RefreshAsync(beforeMidnight.AddSeconds(10));
            Expect(yesterdayIdle, 100, 60, 40, 10, false, "the last pre-midnight refresh still has complete successor evidence");
            Require(yesterdayIdle.ActiveTurns == 0 && yesterdayIdle.ActiveSeconds is { } idleActive
                && Math.Abs(idleActive - 10) < 0.000001, "pre-midnight idle does not restart the superseded turn");
            File.Delete(successor);
            ExpectInactiveToday(await monitor.RefreshAsync(AfterMidnight), true,
                "rollover cannot forget the observed successor and restart the old turn");
            ExpectInactiveToday(await monitor.RefreshAsync(AfterMidnight.AddMinutes(1)), true,
                "00:02 idle does not accumulate activity from the superseded old start");
            fixture.WriteSession("completed-successor", successorRows);
            ExpectInactiveToday(await monitor.RefreshAsync(AfterMidnight.AddMinutes(1)), false,
                "restored prior-day successor resolves rollover uncertainty without today's duration");
            ExpectInactiveToday(await monitor.RefreshAsync(AfterMidnight.AddMinutes(1)), false,
                "restored rollover history is stable on idle");
        });

        await Case("HIS-04 a retired child origin with the same raw response id cannot change another root's date", async () =>
        {
            using var fixture = new Fixture("origin-session-scope-control", Now);
            fixture.WriteSession("independent-root-A", RootMetadata("A", At.AddHours(-1))
                + Modern("A", "shared-raw-response", "root-today-turn", 100, 60, 40, 10, At));
            var childRows = ChildMetadata("B", "A", At.AddDays(-1).AddHours(-1))
                + Modern("B", "shared-raw-response", "child-yesterday-turn", 200, 80, 70, 20, At.AddDays(-1));
            var child = fixture.WriteSession("child-B-yesterday-origin", childRows);
            using var monitor = new SessionLogMonitor(fixture.Home, Zone);
            Expect(await monitor.RefreshAsync(Now), 100, 60, 40, 10, false,
                "same raw response id in a different child session does not reclassify root A's current request");
            File.Delete(child);
            Expect(await monitor.RefreshAsync(Now), 100, 60, 40, 10, false,
                "retiring B's origin must not move independent root A to yesterday or make its coverage partial");
            Expect(await monitor.RefreshAsync(Now), 100, 60, 40, 10, false,
                "cross-session origin scope remains independent on idle");
            fixture.WriteSession("child-B-yesterday-origin", childRows);
            Expect(await monitor.RefreshAsync(Now), 100, 60, 40, 10, false,
                "restoring child B's historical origin does not change A's counters or daily allocation");
        });

        await Case("HIS-05 a genuinely newer today start can supersede a lost historical successor guard", async () =>
        {
            using var fixture = new Fixture("new-start-supersedes-history-control", AfterMidnight);
            var oldStart = new DateTimeOffset(2026, 10, 20, 23, 55, 0, TimeSpan.FromHours(8));
            var successorStart = new DateTimeOffset(2026, 10, 20, 23, 58, 0, TimeSpan.FromHours(8));
            fixture.WriteSession("old-unfinished-turn", RootMetadata("activity", oldStart.AddHours(-1))
                + Start("old-turn", oldStart));
            var successorRows = SuccessorRows(successorStart);
            var successor = fixture.WriteSession("completed-successor", successorRows);
            using var monitor = new SessionLogMonitor(fixture.Home, Zone);
            ExpectInactiveToday(await monitor.RefreshAsync(AfterMidnight), false,
                "known completed successor initially suppresses the old unfinished turn");
            File.Delete(successor);
            ExpectInactiveToday(await monitor.RefreshAsync(AfterMidnight), true,
                "missing historical successor is partial before a new independent start arrives");
            fixture.WriteSession("new-raw-today-start", RootMetadata("activity", AfterMidnight.AddMinutes(-1))
                + Start("new-today-turn", AfterMidnight.AddSeconds(-30)));
            void ExpectNewActive(DailySnapshot snapshot, double seconds, string context)
            {
                Expect(snapshot, null, null, null, null, false, context);
                Require(snapshot.ActiveTurns == 1 && snapshot.ActiveSeconds is { } active
                    && Math.Abs(active - seconds) < 0.000001,
                    $"{context}: expected one new active turn and {seconds} active seconds; actual active turns="
                    + $"{snapshot.ActiveTurns}, active seconds={snapshot.ActiveSeconds}; Status={snapshot.Status}");
                ExpectUnavailableTiming(snapshot, context);
            }
            ExpectNewActive(await monitor.RefreshAsync(AfterMidnight), 30,
                "explicit 00:00:30 start legitimately contributes 30 active seconds at 00:01");
            ExpectNewActive(await monitor.RefreshAsync(AfterMidnight.AddSeconds(30)), 60,
                "only the new turn accrues 60 active seconds by 00:01:30");
            fixture.WriteSession("completed-successor", successorRows);
            ExpectNewActive(await monitor.RefreshAsync(AfterMidnight.AddSeconds(30)), 60,
                "restored old successor evidence cannot suppress the genuinely later current start");
        });

        await Case("HIS-06 lost supersession proof becomes dormant when the old start is older than fifteen minutes", async () =>
        {
            using var fixture = new Fixture("old-start-expiry-control", AfterMidnight);
            var oldStart = new DateTimeOffset(2026, 10, 20, 23, 55, 0, TimeSpan.FromHours(8));
            var successorStart = new DateTimeOffset(2026, 10, 20, 23, 58, 0, TimeSpan.FromHours(8));
            fixture.WriteSession("old-unfinished-turn", RootMetadata("activity", oldStart.AddHours(-1))
                + Start("old-turn", oldStart));
            var successorRows = SuccessorRows(successorStart);
            var successor = fixture.WriteSession("completed-successor", successorRows);
            using var monitor = new SessionLogMonitor(fixture.Home, Zone);
            ExpectInactiveToday(await monitor.RefreshAsync(AfterMidnight), false,
                "available successor history suppresses the six-minute-old prior-day start");
            File.Delete(successor);
            ExpectInactiveToday(await monitor.RefreshAsync(AfterMidnight), true,
                "lost supersession evidence matters while the old start remains inside the activity window");
            ExpectInactiveToday(await monitor.RefreshAsync(AfterMidnight.AddMinutes(10)), false,
                "at 00:11 the old 23:55 start is sixteen minutes old and its lost exclusion proof is dormant");
            ExpectInactiveToday(await monitor.RefreshAsync(AfterMidnight.AddMinutes(11)), false,
                "00:12 idle remains complete without restarting or accumulating duration for the expired start");
            fixture.WriteSession("completed-successor", successorRows);
            ExpectInactiveToday(await monitor.RefreshAsync(AfterMidnight.AddMinutes(11)), false,
                "restoring dormant historical evidence does not introduce today's activity or timing");
        });

        Console.WriteLine($"RESULT HISTORICAL DECISIONS: {passed} passed, {failures.Count} failed");
        if (failures.Count > 0)
            throw new InvalidOperationException("Historical decision regressions:\n" + string.Join("\n", failures));
    }

    private static void Expect(DailySnapshot actual, long? input, long? cached, long? output,
        long? reasoning, bool partial, string context) => Require(actual.InputTokens == input
            && actual.CachedInputTokens == cached && actual.OutputTokens == output && actual.ReasoningTokens == reasoning
            && actual.IsPartial == partial,
            $"{context}: expected input={input}, cached={cached}, output={output}, reasoning={reasoning}, Partial={partial}; "
            + $"actual input={actual.InputTokens}, cached={actual.CachedInputTokens}, output={actual.OutputTokens}, "
            + $"reasoning={actual.ReasoningTokens}, Partial={actual.IsPartial}, ActiveTurns={actual.ActiveTurns}, "
            + $"ActiveSeconds={actual.ActiveSeconds}, ProcessingSeconds={actual.ProcessingSeconds}, TPS={actual.AverageTps}; Status={actual.Status}");

    private static void ExpectUnavailableTiming(DailySnapshot actual, string context) => Require(
        actual.AverageTps is null && actual.ProcessingSeconds is null && actual.TimedTurnCount == 0,
        $"{context}: expected unavailable TPS and processing seconds; actual TPS={actual.AverageTps}, "
        + $"processing={actual.ProcessingSeconds}, timed turns={actual.TimedTurnCount}; Status={actual.Status}");

    private static void ExpectInactiveToday(DailySnapshot actual, bool partial, string context)
    {
        Expect(actual, null, null, null, null, partial, context);
        Require(actual.Day == new DateOnly(2026, 10, 21) && actual.ActiveTurns == 0 && actual.ActiveSeconds is null,
            $"{context}: expected day=2026-10-21, active turns=0 and active seconds unavailable; actual day={actual.Day}, "
            + $"active turns={actual.ActiveTurns}, active seconds={actual.ActiveSeconds}; Status={actual.Status}");
        ExpectUnavailableTiming(actual, context);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static string Session(string name) => "synthetic-history-session-" + name;
    private static string Turn(string name) => "synthetic-history-turn-" + name;

    private static string RootMetadata(string session, DateTimeOffset time) =>
        Row("session_meta", time, new { id = Session(session), source = "cli" });

    private static string ChildMetadata(string session, string parent, DateTimeOffset time) => Row("session_meta", time, new
    {
        id = Session(session),
        source = new { subagent = new { thread_spawn = new { parent_thread_id = Session(parent) } } }
    });

    private static string Modern(string session, string request, string turn, long input, long cached,
        long output, long reasoning, DateTimeOffset time) => Row("token_usage_record", time, new
        {
            thread_id = Session(session), session_id = "synthetic-history-run", turn_id = Turn(turn), root_turn_id = Turn(turn),
            response_id = "synthetic-history-request-" + request,
            usage = Vector(input, cached, output, reasoning), turn_token_usage = Vector(input, cached, output, reasoning),
            thread_token_usage = Vector(input, cached, output, reasoning)
        });

    private static object Vector(long input, long cached, long output, long reasoning) => new
    {
        input_tokens = input, cached_input_tokens = cached, output_tokens = output,
        reasoning_output_tokens = reasoning, total_tokens = input + output
    };

    private static string Start(string turn, DateTimeOffset time) =>
        Row("event_msg", time, new { type = "task_started", turn_id = Turn(turn) });

    private static string Complete(string turn, DateTimeOffset time) => Row("event_msg", time, new
    {
        type = "task_complete", turn_id = Turn(turn), duration_ms = 10000, time_to_first_token_ms = 1000
    });

    private static string Tool(string kind, string turn, DateTimeOffset time) => Row("response_item", time, new
    {
        type = kind, call_id = "synthetic-history-shared-call", turn_id = Turn(turn),
        arguments = "{}", output = "synthetic tool result"
    });

    private static string SuccessorRows(DateTimeOffset start) => RootMetadata("activity", start.AddMinutes(-1))
        + Start("successor-turn", start)
        + Modern("activity", "successor-request", "successor-turn", 100, 60, 40, 10, start.AddSeconds(9))
        + Complete("successor-turn", start.AddSeconds(10));

    private static string Row(string type, DateTimeOffset timestamp, object payload) =>
        JsonSerializer.Serialize(new { type, timestamp, payload }) + "\n";

    private sealed class Fixture : IDisposable
    {
        private readonly DateTimeOffset _modifiedAt;
        public string Home { get; }

        public Fixture(string name, DateTimeOffset modifiedAt)
        {
            _modifiedAt = modifiedAt;
            Home = Path.Combine(Path.GetTempPath(), "CodexDaily-history-" + name + "-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(Home, "sessions"));
            Directory.CreateDirectory(Path.Combine(Home, "archived_sessions"));
        }

        public string WriteSession(string name, string contents)
        {
            var path = Path.Combine(Home, "sessions", name + ".jsonl");
            File.WriteAllText(path, contents, Utf8);
            File.SetLastWriteTimeUtc(path, _modifiedAt.UtcDateTime);
            return path;
        }

        public void Dispose() => Directory.Delete(Home, recursive: true);
    }
}
