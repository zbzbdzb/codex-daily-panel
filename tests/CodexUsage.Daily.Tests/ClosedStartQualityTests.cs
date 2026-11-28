using CodexUsage.Core;
using CodexUsage.Daily;

internal static class ClosedStartQualityTests
{
    private static readonly DateOnly Day = new(2026, 10, 20);
    private static readonly TimeZoneInfo Zone = TimeZoneInfo.CreateCustomTimeZone("ClosedStartsUtc8", TimeSpan.FromHours(8), "UTC+8", "UTC+8");
    private static readonly DateTimeOffset Now = new(2026, 10, 20, 12, 0, 0, TimeSpan.FromHours(8));
    private static readonly DateTimeOffset Began = Now.AddMinutes(-1);
    private static readonly DateTimeOffset Finished = Began.AddSeconds(10);
    private static readonly TokenVector Tokens = new(100, 60, 40, 10, 140);
    private const string Root = "tied-root";

    internal static void Run(Action<string, Action> test)
    {
        test("Closed starts: two completions clear current activity conflict", () =>
            CheckClosed(Sum(Tied().Concat(Complete("a")).Concat(Complete("b"))), 200, 80, 2));
        test("Closed starts: completion and abort clear current activity conflict", () =>
            CheckClosed(Sum(Tied().Concat(Complete("a")).Append(Abort("b"))), 100, 40, 1));
        test("Closed starts: two aborts clear current activity conflict", () =>
            CheckClosed(Sum(Tied().Concat([Abort("a"), Abort("b")])), null, null, 0));

        test("Closed starts: exact replay preserves both types of terminal evidence", () =>
        {
            var current = Tied().Concat(Complete("a")).Append(Abort("b")).ToArray();
            var closureFacts = new[] { DailyClosureFact.From((CompletionEvent)Complete("a")[1]), DailyClosureFact.From(Abort("b")) };
            CheckClosed(Sum(current.Concat(current.Reverse()), facts: closureFacts), 100, 40, 1);
        });
        test("Closed starts: input partial survives removal of the activity reason", () =>
        {
            var snapshot = Sum(Tied().Concat(Complete("a")).Append(Abort("b")), partial: true);
            CheckStatistics(snapshot, 100, 40, 1);
            Check(snapshot.IsPartial && Has(snapshot, DailyIssueCodes.SourcePartial) && !Has(snapshot, DailyIssueCodes.CurrentStartConflict),
                "Independent incomplete source must remain visible with no current activity conflict.");
        });
        test("Closed starts: one or two unclosed candidates retain ambiguity", () =>
        {
            CheckAmbiguous(Sum(Tied()));
            var oneClosed = Sum(Tied().Concat(Complete("a")));
            CheckAmbiguous(oneClosed);
            CheckStatistics(oneClosed, 100, 40, 1);
        });
        test("Closed starts: reversed or future terminals cannot resolve a candidate", () =>
        {
            CheckAmbiguous(Sum(Tied().Concat(Complete("a")).Append(Abort("b", Began.AddTicks(-1)))));
            CheckAmbiguous(Sum(Tied().Concat(Complete("a")).Append(Abort("b", Now.AddTicks(1)))));
        });
        test("Closed starts: conflicting current and previous terminal evidence blocks resolution", () =>
        {
            var contradictory = Tied().Concat(Complete("a")).Concat(Complete("b")).Append(Abort("b"));
            var current = Sum(contradictory);
            CheckAmbiguous(current);
            Check(Has(current, DailyIssueCodes.TurnTimeConflict) && current.ProcessingSeconds == 9 &&
                current.InputTokens == 200 && current.OutputTokens == 80, "Conflict must preserve known totals and the other turn's TPS.");
            var historical = Sum(Tied().Concat(Complete("a")).Concat(Complete("b")),
                facts: [DailyClosureFact.From(Abort("b"))]);
            CheckAmbiguous(historical);
            Check(Has(historical, DailyIssueCodes.TurnTimeConflict), "Past contradictory terminal was ignored.");
        });
        test("Closed starts: historical closure alone cannot supply an observed close", () =>
        {
            var s = Sum(Tied().Concat(Complete("a")), facts: [DailyClosureFact.From(Abort("b"))],
                closureHistory: [(Root, "b", Finished)]);
            CheckAmbiguous(s);
            CheckStatistics(s, 100, 40, 1);
        });
        var early = Began.AddMilliseconds(-1);
        foreach (var (kind, observed, fact, excluded) in new (string, StatsEvent, DailyClosureFact, int)[]
        {
            ("abort", Abort("a", early), DailyClosureFact.From(Abort("a", early)), 0),
            ("completion", new CompletionEvent(Root, "a", early, 100, 20),
                DailyClosureFact.From(new CompletionEvent(Root, "a", early, 100, 20)), 1)
        })
        {
            test($"Closed starts: a lone reversed {kind} leaves a recent turn active", () =>
            {
                var current = Single();
                CheckStillActive(Sum(current.Append(observed)), excluded);
                CheckStillActive(Sum(current.Prepend(observed)), excluded);
            });
            test($"Closed starts: a retained reversed {kind} cannot suppress a current start", () =>
            {
                CheckStillActive(Sum(Single(), facts: [fact], closureHistory: [(Root, "a", early)]), 0);
                CheckStillActive(Sum(Single(), closureHistory: [(Root, "a", early)]), 0);
                var validHistory = Sum(Single(), closureHistory: [(Root, "a", Finished)]);
                Check(validHistory.ActiveTurns == 0 && validHistory.ActiveSeconds is null
                    && Has(validHistory, DailyIssueCodes.HistoricalClosureMissing),
                    "A genuine retained end still limits activity and supplies no interval.");
            });
            test($"Closed starts: conflicting reversed {kind} stays visible in either order or history", () =>
            {
                var current = Tied().Concat(Complete("a")).Concat(Complete("b")).ToArray();
                foreach (var snapshot in new[]
                {
                    Sum(current.Prepend(observed)), Sum(current.Append(observed)), Sum(current, facts: [fact])
                })
                {
                    CheckAmbiguous(snapshot);
                    Check(Has(snapshot, DailyIssueCodes.TurnTimeConflict) && snapshot.InputTokens == 200
                        && snapshot.OutputTokens == 80 && snapshot.ProcessingSeconds == 9 && snapshot.ActiveSeconds == 10
                        && snapshot.TimedTurnCount == 1 && snapshot.ExcludedTimingTurnCount == 1,
                        "Distinct terminal facts must exclude the conflicted turn and preserve its sibling's verified time.");
                }
            });
        }
        test("Closed starts: a retained later start disproves an otherwise plausible completion", () =>
        {
            var snapshot = Sum([
                new MetadataEvent(Root, Began.AddMinutes(-1), false, null),
                new UsageEvent(Root, "a", "a", "request-a", early.AddMilliseconds(-1), 1, Tokens, Tokens, null),
                new CompletionEvent(Root, "a", early, 100, 20)
            ], startHistory: [new StartEvent(Root, "a", Began)]);
            Check(snapshot.InputTokens == 100 && snapshot.OutputTokens == 40 && snapshot.ActiveTurns == 0
                && snapshot.ProcessingSeconds is null && snapshot.AverageTps is null && snapshot.ActiveSeconds is null
                && snapshot.TimedTurnCount == 0 && snapshot.ExcludedTimingTurnCount == 1,
                "A retained same-turn start rules out a premature completion even within the duration skew tolerance.");
        });
        test("Closed starts: resolving one root leaves ambiguity in another", () =>
        {
            var rows = Tied().Concat(Complete("a")).Append(Abort("b")).Concat([
                new MetadataEvent("other-root", Began.AddMinutes(-1), false, null),
                new StartEvent("other-root", "a", Began),
                new StartEvent("other-root", "b", Began)
            ]);
            CheckAmbiguous(Sum(rows));
        });
        test("Closed starts: previous-day starts do not create today's activity conflict", () =>
        {
            var old = Began.AddDays(-1);
            var snapshot = Sum([
                new MetadataEvent(Root, old.AddMinutes(-1), false, null),
                new StartEvent(Root, "a", old), new StartEvent(Root, "b", old)
            ]);
            Check(!snapshot.IsPartial && !Has(snapshot, DailyIssueCodes.CurrentStartConflict) &&
                snapshot.ActiveTurns == 0 && snapshot.TurnCount == 0, "Previous-day ambiguity leaked into today's statistics.");
        });
    }

    private static StatsEvent[] Tied() =>
    [
        new MetadataEvent(Root, Began.AddMinutes(-1), false, null),
        new StartEvent(Root, "a", Began),
        new StartEvent(Root, "b", Began)
    ];

    private static StatsEvent[] Complete(string turn) =>
    [
        new UsageEvent(Root, turn, turn, "request-" + turn, Finished.AddSeconds(-1), 1, Tokens, Tokens, null),
        new CompletionEvent(Root, turn, Finished, 10000, 1000)
    ];

    private static StatsEvent[] Single() =>
    [
        new MetadataEvent(Root, Began.AddMinutes(-1), false, null),
        new StartEvent(Root, "a", Began),
        new UsageEvent(Root, "a", "a", "request-a", Finished, 1, Tokens, Tokens, null)
    ];

    private static DailyTurnAbortedEvent Abort(string turn, DateTimeOffset? at = null) => new(Root, turn, at ?? Finished);
    private static DailySnapshot Sum(IEnumerable<StatsEvent> rows, bool partial = false,
        IEnumerable<DailyClosureFact>? facts = null,
        IEnumerable<(string Session, string Turn, DateTimeOffset At)>? closureHistory = null,
        IEnumerable<StartEvent>? startHistory = null) =>
        DailyAggregator.Summarize(rows, Day, Zone, Now, partial, closureFacts: facts, closureHistory: closureHistory,
            startHistory: startHistory);

    private static bool Has(DailySnapshot snapshot, string code) => snapshot.Issues.Any(issue => issue.Code == code);
    private static void Check(bool value, string reason)
    {
        if (!value) throw new InvalidOperationException(reason);
    }

    private static void CheckClosed(DailySnapshot snapshot, long? input, long? output, int completions)
    {
        CheckStatistics(snapshot, input, output, completions);
        Check(!snapshot.IsPartial && !Has(snapshot, DailyIssueCodes.CurrentStartConflict),
            "All tied starts have observed consistent closure.");
    }

    private static void CheckAmbiguous(DailySnapshot snapshot) =>
        Check(snapshot.IsPartial && Has(snapshot, DailyIssueCodes.CurrentStartConflict) && snapshot.ActiveTurns == 0,
            "Incomplete tied starts must remain ambiguous without guessing an active turn.");

    private static void CheckStillActive(DailySnapshot snapshot, int excluded) =>
        Check(snapshot.InputTokens == 100 && snapshot.OutputTokens == 40 && snapshot.ActiveTurns == 1
            && snapshot.ActiveSeconds == 60 && snapshot.ProcessingSeconds is null && snapshot.AverageTps is null
            && snapshot.TimedTurnCount == 0 && snapshot.ExcludedTimingTurnCount == excluded
            && !Has(snapshot, DailyIssueCodes.HistoricalClosureMissing) && !Has(snapshot, DailyIssueCodes.TurnTimeConflict),
            "A single premature end neither closes a recent turn nor supplies timing or a distinct-terminal conflict.");

    private static void CheckStatistics(DailySnapshot snapshot, long? input, long? output, int completions)
    {
        Check(snapshot.InputTokens == input && snapshot.OutputTokens == output, "Known token totals changed.");
        Check(snapshot.ActiveTurns == 0 && snapshot.TurnCount == 2 && snapshot.ActiveSeconds == 10,
            "Turn count or ten-second activity union changed.");
        Check(snapshot.TimedTurnCount == completions && snapshot.ExcludedTimingTurnCount == 0, "Timing coverage changed.");
        if (completions == 0)
            Check(snapshot.ProcessingSeconds is null && snapshot.AverageTps is null, "Aborts entered TPS.");
        else
            Check(snapshot.ProcessingSeconds == 9d * completions && snapshot.AverageTps is { } tps &&
                Math.Abs(tps - 40d / 9) < 1e-8, "Processing time or TPS changed.");
    }
}
