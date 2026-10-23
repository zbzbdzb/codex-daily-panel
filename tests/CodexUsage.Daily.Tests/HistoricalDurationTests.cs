using CodexUsage.Core;
using CodexUsage.Daily;

internal static class HistoricalDurationTests
{
    private static readonly DateTimeOffset End = DateTimeOffset.Parse("2026-10-23T04:00:10Z");
    private static readonly DateOnly Day = new(2026, 10, 23);
    private static readonly TimeZoneInfo Zone = TimeZoneInfo.CreateCustomTimeZone("F1UTC8", TimeSpan.FromHours(8), "UTC+8", "UTC+8");
    private static readonly TokenVector Tokens = new(100, 60, 40, 10, 140);

    public static void Run(Action<string, Action> test)
    {
        test("F1 unique contradictory historical start excludes timing but keeps tokens and current activity", () =>
        {
            var history = new[] { new StartEvent("root", "turn", End.AddSeconds(-40)) };
            var result = Sum(Rows(), history);
            Check(result, excluded: true, active: 10);
            // The retained fact is still negative evidence after an idle refresh.
            var idle = Sum(Rows(), history, End.AddHours(1));
            Check(idle, excluded: true, active: 10);
        });

        // Literal boundaries exercise both sides of max(1000ms, duration * 5%).
        (long duration, long elapsed, bool excluded)[] boundaries =
        [
            (10000, 9000, false), (10000, 11000, false),
            (10000, 8999, true), (10000, 11001, true),
            (40000, 38000, false), (40000, 42000, false),
            (40000, 37999, true), (40000, 42001, true)
        ];
        foreach (var (duration, elapsed, excluded) in boundaries)
        {
            test($"F1 duration {duration}ms / historical elapsed {elapsed}ms: Partial={excluded}", () =>
            {
                var result = Sum(Rows(duration), [new StartEvent("root", "turn", End.AddMilliseconds(-elapsed))]);
                Check(result, excluded, duration / 1000d, duration / 1000d - 1);
            });
        }

        test("F1 equivalent timezone replay is one contradictory start", () =>
        {
            StartEvent[] history = [new("root", "turn", DateTimeOffset.Parse("2026-10-23T11:59:30+08:00")),
                new("root", "turn", DateTimeOffset.Parse("2026-10-23T03:59:30Z"))];
            Check(Sum(Rows(), history), excluded: true, active: 10);
        });

        foreach (var identity in new[] { (Session: "other", Turn: "turn"), (Session: "root", Turn: "other") })
        {
            test($"F1 unrelated historical start {identity.Session}/{identity.Turn} leaves timing valid", () =>
            {
                Check(Sum(Rows(), [new StartEvent(identity.Session, identity.Turn, End.AddSeconds(-40))]),
                    excluded: false, active: 10);
            });
        }

        test("F1 compatible retained start cannot replace duration-derived activity", () =>
        {
            // An 11-second retained interval is within tolerance, but only the
            // current completion's declared 10 seconds may supply activity.
            Check(Sum(Rows(), [new StartEvent("root", "turn", End.AddSeconds(-11))]), excluded: false, active: 10);
        });

        test("F1 retained start cannot backfill missing duration or activity", () =>
        {
            var result = Sum(Rows(null), [new StartEvent("root", "turn", End.AddSeconds(-10))]);
            Need(result.InputTokens == 100 && result.OutputTokens == 40, "known token subtotal changed");
            Need(result.ActiveSeconds is null && result.ProcessingSeconds is null && result.AverageTps is null,
                "historical start supplied a missing interval or duration");
            Need(result.TimedTurnCount == 0 && result.ExcludedTimingTurnCount == 1 && result.ActiveTurns == 0,
                "missing duration must stay excluded without becoming active");
            Need(!result.IsPartial, "a compatible retained start alone must not invent a partial reason");
        });

        test("F1 retained start alone cannot create a current turn or activity", () =>
        {
            var result = Sum([new MetadataEvent("root", End.AddHours(-1), false, null)],
                [new StartEvent("root", "turn", End.AddSeconds(-10))]);
            Need(result.TurnCount == 0 && result.ActiveTurns == 0 && result.ActiveSeconds is null,
                "historical start created a turn or activity");
            Need(result.InputTokens is null && result.ProcessingSeconds is null && !result.IsPartial,
                "historical start created statistics or a partial reason");
        });
    }

    private static StatsEvent[] Rows(long? duration = 10000) =>
    [
        new MetadataEvent("root", End.AddHours(-1), false, null),
        new UsageEvent("root", "turn", "turn", "request", End.AddSeconds(-1), 1, Tokens, Tokens, null),
        new CompletionEvent("root", "turn", End, duration, 1000)
    ];

    private static DailySnapshot Sum(IEnumerable<StatsEvent> rows, IEnumerable<StartEvent> history, DateTimeOffset? now = null) =>
        DailyAggregator.Summarize(rows, Day, Zone, now ?? End.AddMinutes(1), startHistory: history);

    private static void Check(DailySnapshot result, bool excluded, double active, double processing = 9)
    {
        Need(result.InputTokens == 100 && result.CachedInputTokens == 60 && result.OutputTokens == 40
            && result.ReasoningTokens == 10, "historical timing altered valid token subtotal");
        Need(result.ActiveSeconds == active && result.ActiveTurns == 0, "historical timing backfilled activity");
        Need(result.IsPartial == excluded, "wrong partial state");
        Need(result.Status.Contains("回合声明耗时与起止时间不一致", StringComparison.Ordinal) == excluded,
            "duration mismatch reason disagrees with eligibility");
        Need(result.TimedTurnCount == (excluded ? 0 : 1) && result.ExcludedTimingTurnCount == (excluded ? 1 : 0),
            "included/excluded counts disagree with eligibility");
        if (excluded)
            Need(result.ProcessingSeconds is null && result.AverageTps is null && result.ExcludedWaitSeconds is null,
                "contradictory retained start became eligible timing");
        else
            Need(result.ProcessingSeconds == processing && result.AverageTps is { } tps && Math.Abs(tps - 40 / processing) < .000001,
                "compatible or unrelated history changed valid timing");
    }

    private static void Need(bool condition, string reason)
    {
        if (!condition) throw new Exception(reason);
    }
}
