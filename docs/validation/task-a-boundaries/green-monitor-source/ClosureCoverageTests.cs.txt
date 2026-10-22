using System.Text;
using System.Text.Json;
using CodexUsage.Daily;
using CodexUsage.DailyPanel;

internal static class ClosureCoverageTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 21, 12, 0, 0, TimeSpan.FromHours(8));
    private static readonly DateTimeOffset Now = At.AddMinutes(10);
    private static readonly DateTimeOffset AfterMidnight = new(2026, 10, 21, 0, 1, 0, TimeSpan.FromHours(8));
    private static readonly DateTimeOffset OldStart = new(2026, 10, 20, 23, 55, 0, TimeSpan.FromHours(8));
    private static readonly DateTimeOffset SuccessorStart = new(2026, 10, 20, 23, 58, 0, TimeSpan.FromHours(8));
    private static readonly TimeZoneInfo Zone = TimeZoneInfo.CreateCustomTimeZone(
        "SyntheticClosureUTC8", TimeSpan.FromHours(8), "UTC+8", "UTC+8");
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

        await Case("CON-01 an available same-day occurrence before the turn start excludes adjusted TPS after becoming root", async () =>
        {
            using var fixture = new Fixture("same-day-origin-present-control", Now);
            fixture.WriteSession("early-child-occurrence", EarlyChildRows());
            fixture.WriteSession("current-child-completed-turn", CurrentChildRows());
            using var monitor = new SessionLogMonitor(fixture.Home, Zone);
            var child = await monitor.RefreshAsync(Now);
            Expect(child, null, null, null, null, false, "initial child A contributes no root counters");
            ExpectUnavailableTiming(child, "initial child A contributes no root TPS");
            fixture.WriteSession("newer-root-A", RootMetadata("A", At.AddSeconds(20)));
            var root = await monitor.RefreshAsync(Now);
            Expect(root, 100, 60, 40, 10, false, "retained 11:59:30 occurrence keeps the current request counted once today");
            ExpectUnavailableTiming(root, "a request first observed before 12:00:00 does not qualify for the completed turn's TPS");
            Check(root, root.ExcludedTimingTurnCount == 1, "exactly the completed root turn is excluded from timing");
            var idle = await monitor.RefreshAsync(Now);
            Expect(idle, 100, 60, 40, 10, false, "available original occurrence remains complete and deduplicated on idle");
            ExpectUnavailableTiming(idle, "idle does not move the first occurrence into the later turn interval");
        });

        await Case("CON-02 losing a same-day child occurrence before becoming root cannot manufacture nine processing seconds", async () =>
        {
            using var fixture = new Fixture("same-day-origin-lost-before-root", Now);
            var early = fixture.WriteSession("early-child-occurrence", EarlyChildRows());
            fixture.WriteSession("current-child-completed-turn", CurrentChildRows());
            using var monitor = new SessionLogMonitor(fixture.Home, Zone);
            Expect(await monitor.RefreshAsync(Now), null, null, null, null, false,
                "both same-day occurrences are initially in confirmed child scope");
            File.Delete(early);
            Expect(await monitor.RefreshAsync(Now), null, null, null, null, false,
                "missing early occurrence stays dormant while current child metadata excludes the session");
            fixture.WriteSession("newer-root-A", RootMetadata("A", At.AddSeconds(20)));
            var root = await monitor.RefreshAsync(Now);
            Expect(root, 100, 60, 40, 10, true, "current root counters are known but the 11:59:30 occurrence proof is missing");
            ExpectUnavailableTiming(root, "missing same-day pre-start evidence cannot turn this request into a nine-second TPS sample");
            var idle = await monitor.RefreshAsync(Now);
            Expect(idle, 100, 60, 40, 10, true, "same-day occurrence uncertainty persists on idle");
            ExpectUnavailableTiming(idle, "idle cannot admit the 12:00:09 copy as the first occurrence");
            fixture.WriteSession("early-child-occurrence", EarlyChildRows());
            var restored = await monitor.RefreshAsync(Now);
            Expect(restored, 100, 60, 40, 10, false, "restored original occurrence recovers complete current root coverage");
            ExpectUnavailableTiming(restored, "available earlier occurrence still prevents TPS qualification");
            var restoredIdle = await monitor.RefreshAsync(Now);
            Expect(restoredIdle, 100, 60, 40, 10, false, "restored same-day history does not duplicate the request");
            ExpectUnavailableTiming(restoredIdle, "restored exclusion remains stable on idle");
        });

        await Case("CON-03 removing only a known completion cannot restart its retained historical start", async () =>
        {
            using var fixture = new Fixture("completion-truncated-after-today-read", AfterMidnight);
            fixture.WriteSession("old-unfinished-turn", OldOpenRows());
            var completionOnlyRows = OpenSuccessorRows() + Complete("successor-turn", SuccessorStart.AddSeconds(10));
            fixture.WriteSession("successor", completionOnlyRows);
            using var monitor = new SessionLogMonitor(fixture.Home, Zone);
            ExpectInactiveToday(await monitor.RefreshAsync(AfterMidnight), false,
                "today already observed the successor start and its prior-day completion");
            fixture.WriteSession("successor", OpenSuccessorRows());
            ExpectInactiveToday(await monitor.RefreshAsync(AfterMidnight), true,
                "retaining the exact metadata and start bytes while removing only completion cannot restart the successor");
            ExpectInactiveToday(await monitor.RefreshAsync(AfterMidnight.AddMinutes(1)), true,
                "00:02 idle cannot accrue active seconds for a turn already known completed");
            fixture.WriteSession("successor", completionOnlyRows);
            ExpectInactiveToday(await monitor.RefreshAsync(AfterMidnight.AddMinutes(1)), false,
                "restored completion proves complete non-active history");
            ExpectInactiveToday(await monitor.RefreshAsync(AfterMidnight.AddMinutes(1)), false,
                "restored closure stays non-active and complete on idle");
        });

        await Case("CON-04 a completion read before midnight remains binding after start-only truncation and rollover", async () =>
        {
            using var fixture = new Fixture("completion-truncated-before-rollover", AfterMidnight);
            var beforeMidnight = new DateTimeOffset(2026, 10, 20, 23, 59, 0, TimeSpan.FromHours(8));
            fixture.WriteSession("old-unfinished-turn", OldOpenRows());
            fixture.WriteSession("successor", CompletedSuccessorRows());
            using var monitor = new SessionLogMonitor(fixture.Home, Zone);
            var yesterday = await monitor.RefreshAsync(beforeMidnight);
            Expect(yesterday, 100, 60, 40, 10, false, "complete successor request belongs to yesterday");
            Check(yesterday, yesterday.ActiveTurns == 0 && yesterday.ActiveSeconds is { } active
                && Math.Abs(active - 10) < 0.000001, "only the completed successor supplies yesterday's ten active seconds");
            Check(yesterday, yesterday.ProcessingSeconds is { } processing && Math.Abs(processing - 9) < 0.000001
                && yesterday.AverageTps is { } tps && Math.Abs(tps - 4.444444444444445) < 0.000001,
                "baseline uses the unchanged ten-second duration minus one-second TTFT formula");
            var yesterdayIdle = await monitor.RefreshAsync(beforeMidnight.AddSeconds(10));
            Expect(yesterdayIdle, 100, 60, 40, 10, false, "last pre-midnight refresh still contains the completion");
            Check(yesterdayIdle, yesterdayIdle.ActiveTurns == 0, "pre-midnight idle does not restart either historical turn");
            fixture.WriteSession("successor", OpenSuccessorRows());
            ExpectInactiveToday(await monitor.RefreshAsync(AfterMidnight), true,
                "rollover cannot forget a completion when its exact metadata and start remain readable");
            ExpectInactiveToday(await monitor.RefreshAsync(AfterMidnight.AddMinutes(1)), true,
                "00:02 idle preserves the known closed state without today's duration");
            fixture.WriteSession("successor", CompletedSuccessorRows());
            ExpectInactiveToday(await monitor.RefreshAsync(AfterMidnight.AddMinutes(1)), false,
                "restored prior-day completion settles rollover coverage");
        });

        await Case("CON-05 a start with no observed terminal event remains a legitimate recent active turn", async () =>
        {
            using var fixture = new Fixture("never-closed-start-control", AfterMidnight);
            fixture.WriteSession("open-only", RootMetadata("activity", SuccessorStart.AddMinutes(-1))
                + Start("successor-turn", SuccessorStart));
            using var monitor = new SessionLogMonitor(fixture.Home, Zone);
            var active = await monitor.RefreshAsync(AfterMidnight);
            Expect(active, null, null, null, null, false, "a genuinely unfinished recent turn has complete observed coverage");
            Check(active, active.ActiveTurns == 1 && active.ActiveSeconds is { } seconds
                && Math.Abs(seconds - 60) < 0.000001, "a never-closed start contributes sixty clipped active seconds at 00:01");
            ExpectUnavailableTiming(active, "an active turn does not invent completed-turn processing or TPS");
            var idle = await monitor.RefreshAsync(AfterMidnight.AddMinutes(1));
            Expect(idle, null, null, null, null, false, "start-only control remains complete at 00:02");
            Check(idle, idle.ActiveTurns == 1 && idle.ActiveSeconds is { } secondsAtTwo
                && Math.Abs(secondsAtTwo - 120) < 0.000001, "the genuinely open turn may accrue 120 clipped active seconds");
            ExpectUnavailableTiming(idle, "start-only idle never qualifies as completed timing");
        });

        await Case("CON-06 losing an observed abort cannot restart its retained start and its guard may expire", async () =>
        {
            using var fixture = new Fixture("abort-truncated-and-expired", AfterMidnight);
            fixture.WriteSession("old-unfinished-turn", OldOpenRows());
            fixture.WriteSession("successor", AbortedSuccessorRows());
            using var monitor = new SessionLogMonitor(fixture.Home, Zone);
            ExpectInactiveToday(await monitor.RefreshAsync(AfterMidnight), false,
                "a known prior-day abort closes the successor before today's interval");
            fixture.WriteSession("successor", OpenSuccessorRows());
            ExpectInactiveToday(await monitor.RefreshAsync(AfterMidnight), true,
                "an aborted successor cannot become active merely because its abort row disappeared");
            ExpectInactiveToday(await monitor.RefreshAsync(AfterMidnight.AddMinutes(1)), true,
                "00:02 idle preserves the observed abort decision");
            fixture.WriteSession("successor", AbortedSuccessorRows());
            ExpectInactiveToday(await monitor.RefreshAsync(AfterMidnight.AddMinutes(1)), false,
                "restored abort evidence recovers complete non-active coverage");
            fixture.WriteSession("successor", OpenSuccessorRows());
            ExpectInactiveToday(await monitor.RefreshAsync(AfterMidnight.AddMinutes(1)), true,
                "a second abort loss is relevant while the start is recent");
            ExpectInactiveToday(await monitor.RefreshAsync(AfterMidnight.AddMinutes(13)), false,
                "at 00:14 the 23:58 start is sixteen minutes old so missing terminal evidence is dormant");
            ExpectInactiveToday(await monitor.RefreshAsync(AfterMidnight.AddMinutes(14)), false,
                "expired abort evidence does not leave permanent activity or partiality on idle");
        });

        Console.WriteLine($"RESULT CLOSURE COVERAGE: {passed} passed, {failures.Count} failed");
        if (failures.Count > 0)
            throw new InvalidOperationException("Closure coverage regressions:\n" + string.Join("\n", failures));
    }

    private static void Expect(DailySnapshot actual, long? input, long? cached, long? output,
        long? reasoning, bool partial, string context) => Check(actual, actual.InputTokens == input
            && actual.CachedInputTokens == cached && actual.OutputTokens == output && actual.ReasoningTokens == reasoning
            && actual.IsPartial == partial,
            $"{context}: expected input={input}, cached={cached}, output={output}, reasoning={reasoning}, Partial={partial}");

    private static void ExpectUnavailableTiming(DailySnapshot actual, string context) => Check(actual,
        actual.AverageTps is null && actual.ProcessingSeconds is null && actual.TimedTurnCount == 0,
        context + ": expected unavailable TPS and processing seconds with no timed completed turn");

    private static void ExpectInactiveToday(DailySnapshot actual, bool partial, string context)
    {
        Expect(actual, null, null, null, null, partial, context);
        Check(actual, actual.Day == new DateOnly(2026, 10, 21) && actual.ActiveTurns == 0 && actual.ActiveSeconds is null,
            context + ": expected today's active turns=0 and no active seconds");
        ExpectUnavailableTiming(actual, context);
    }

    private static void Check(DailySnapshot actual, bool condition, string context)
    {
        if (!condition) throw new InvalidOperationException(context + "; " + Describe(actual));
    }

    private static string Describe(DailySnapshot value) => $"actual input={value.InputTokens}, cached={value.CachedInputTokens}, "
        + $"output={value.OutputTokens}, reasoning={value.ReasoningTokens}, Partial={value.IsPartial}, "
        + $"ActiveTurns={value.ActiveTurns}, ActiveSeconds={value.ActiveSeconds}, TPS={value.AverageTps}, "
        + $"ProcessingSeconds={value.ProcessingSeconds}, TimedTurns={value.TimedTurnCount}, "
        + $"ExcludedTimingTurns={value.ExcludedTimingTurnCount}, Day={value.Day}; Status={value.Status}";

    private static string Session(string name) => "synthetic-closure-session-" + name;
    private static string Turn(string name) => "synthetic-closure-turn-" + name;

    private static string RootMetadata(string session, DateTimeOffset time) =>
        Row("session_meta", time, new { id = Session(session), source = "cli" });

    private static string ChildMetadata(string session, string parent, DateTimeOffset time) => Row("session_meta", time, new
    {
        id = Session(session),
        source = new { subagent = new { thread_spawn = new { parent_thread_id = Session(parent) } } }
    });

    private static string Modern(string session, string request, string turn, DateTimeOffset time) => Row("token_usage_record", time, new
    {
        thread_id = Session(session), session_id = "synthetic-closure-run", turn_id = Turn(turn), root_turn_id = Turn(turn),
        response_id = "synthetic-closure-request-" + request,
        usage = Vector(), turn_token_usage = Vector(), thread_token_usage = Vector()
    });

    private static object Vector() => new
    {
        input_tokens = 100, cached_input_tokens = 60, output_tokens = 40, reasoning_output_tokens = 10, total_tokens = 140
    };

    private static string Start(string turn, DateTimeOffset time) =>
        Row("event_msg", time, new { type = "task_started", turn_id = Turn(turn) });

    private static string Complete(string turn, DateTimeOffset time) => Row("event_msg", time, new
    {
        type = "task_complete", turn_id = Turn(turn), duration_ms = 10000, time_to_first_token_ms = 1000
    });

    private static string EarlyChildRows() => ChildMetadata("A", "parent", At.AddMinutes(-1))
        + Modern("A", "shared-request", "same-day-turn", At.AddSeconds(-30));

    private static string CurrentChildRows() => ChildMetadata("A", "parent", At.AddSeconds(-1))
        + Start("same-day-turn", At) + Modern("A", "shared-request", "same-day-turn", At.AddSeconds(9))
        + Complete("same-day-turn", At.AddSeconds(10));

    private static string OldOpenRows() => RootMetadata("activity", OldStart.AddHours(-1)) + Start("old-turn", OldStart);

    private static string OpenSuccessorRows() => RootMetadata("activity", SuccessorStart.AddMinutes(-1))
        + Start("successor-turn", SuccessorStart);

    private static string CompletedSuccessorRows() => OpenSuccessorRows()
        + Modern("activity", "successor-request", "successor-turn", SuccessorStart.AddSeconds(9))
        + Complete("successor-turn", SuccessorStart.AddSeconds(10));

    private static string AbortedSuccessorRows() => OpenSuccessorRows()
        + Modern("activity", "successor-request", "successor-turn", SuccessorStart.AddSeconds(9))
        + Row("event_msg", SuccessorStart.AddSeconds(10), new { type = "task_aborted", turn_id = Turn("successor-turn") });

    private static string Row(string type, DateTimeOffset timestamp, object payload) =>
        JsonSerializer.Serialize(new { type, timestamp, payload }) + "\n";

    private sealed class Fixture : IDisposable
    {
        private readonly DateTimeOffset _modifiedAt;
        private readonly string? _archiveDirectory;
        private int _archiveSequence;
        public string Home { get; }

        public Fixture(string name, DateTimeOffset modifiedAt)
        {
            _modifiedAt = modifiedAt;
            Home = Path.Combine(Path.GetTempPath(), "CodexDaily-closure-" + name + "-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(Home, "sessions"));
            Directory.CreateDirectory(Path.Combine(Home, "archived_sessions"));
            var archiveRoot = Environment.GetEnvironmentVariable("CODEX_DAILY_BOUNDARY_INPUTS");
            if (!string.IsNullOrWhiteSpace(archiveRoot)) _archiveDirectory = Path.Combine(archiveRoot, name);
        }

        public string WriteSession(string name, string contents)
        {
            // Both destinations receive the same bytes from this fixture's synthetic
            // string. Archival never reads or enumerates any existing log directory.
            var bytes = Utf8.GetBytes(contents);
            var path = Path.Combine(Home, "sessions", name + ".jsonl");
            File.WriteAllBytes(path, bytes);
            File.SetLastWriteTimeUtc(path, _modifiedAt.UtcDateTime);
            if (_archiveDirectory is not null)
            {
                Directory.CreateDirectory(_archiveDirectory);
                var archivePath = Path.Combine(_archiveDirectory, $"{++_archiveSequence:D2}-{name}.jsonl");
                using var archive = new FileStream(archivePath, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
                archive.Write(bytes);
            }
            return path;
        }

        public void Dispose() => Directory.Delete(Home, recursive: true);
    }
}
