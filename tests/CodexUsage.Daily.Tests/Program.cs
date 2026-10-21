using CodexUsage.Core;
using CodexUsage.Daily;

var failures = 0;
var checks = 0;
var day = new DateOnly(2026, 10, 20);
var zone = TimeZoneInfo.CreateCustomTimeZone("DailyTestsUtcPlus8", TimeSpan.FromHours(8), "UTC+8", "UTC+8");
var now = DateTimeOffset.Parse("2026-10-20T05:00:00Z");
var end = DateTimeOffset.Parse("2026-10-20T01:00:20Z");
var a = new TokenVector(100, 60, 40, 10, 140);
var b = new TokenVector(200, 90, 80, 20, 280);
void Test(string name, Action body)
{
    checks++;
    try { body(); Console.WriteLine("PASS " + name); }
    catch (Exception error) { failures++; Console.WriteLine("FAIL " + name + ": " + error.Message); }
}
void Need(bool condition, string reason) { if (!condition) throw new Exception(reason); }
void Equal<T>(T expected, T actual, string reason) { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"{reason}: expected {expected}, got {actual}"); }
void Near(double expected, double? actual, string reason) { Need(actual.HasValue && Math.Abs(expected - actual.Value) < .000001, reason + $": expected {expected}, got {actual}"); }
UsageEvent Usage(string session, string turn, string request, TokenVector tokens, DateTimeOffset at, long ordinal = 1,
    TokenVector? snapshot = null) => new(session, turn, turn, request, at, ordinal, tokens, snapshot ?? tokens, null);
StatsEvent[] Completed(string session = "root", string turn = "turn", string request = "request", TokenVector? tokens = null,
    DateTimeOffset? completedAt = null, long? duration = 20000, long? ttft = 0)
{
    var at = completedAt ?? end;
    return [new MetadataEvent(session, at.AddHours(-1), false, null),
        new StartEvent(session, turn, at.AddMilliseconds(-(duration ?? 20000))),
        Usage(session, turn, request, tokens ?? a, at.AddSeconds(-1)),
        new CompletionEvent(session, turn, at, duration, ttft)];
}
DailySnapshot Sum(IEnumerable<StatsEvent> events, DateTimeOffset? observed = null, bool partial = false) =>
    DailyAggregator.Summarize(events, day, zone, observed ?? now, partial);
DailyToolEvent Tool(DailyToolPhase phase, double secondsBeforeEnd, string? call = "tool", string? turn = "turn", string session = "root") =>
    new(session, turn, call, phase, end.AddSeconds(-secondsBeforeEnd));

Test("No observations remain unknown rather than zero", () => {
    var s = Sum([]); Need(s.InputTokens is null && s.OutputTokens is null && s.AverageTps is null && s.ProcessingSeconds is null && s.ActiveSeconds is null, "empty values must be unknown"); Equal(0, s.ChatCount, "chat count"); Equal(0, s.TimedTurnCount, "empty accepted count"); Equal(0, s.ExcludedTimingTurnCount, "empty excluded count"); Need(s.ExcludedWaitSeconds is null, "unknown waits must not imply observed zero"); Need(!s.IsPartial, "empty source is not a corrupt source");
});
Test("Complete daily request with observed zero TTFT retains its duration", () => {
    var s = Sum(Completed()); Equal(100L, s.InputTokens, "input"); Equal(60L, s.CachedInputTokens, "cached"); Equal(40L, s.OutputTokens, "output"); Equal(10L, s.ReasoningTokens, "reasoning subset"); Near(20, s.ProcessingSeconds, "processing"); Near(20, s.ActiveSeconds, "active"); Near(2, s.AverageTps, "zero-wait TPS"); Equal(1, s.ChatCount, "chat count"); Equal(1, s.TurnCount, "turn count"); Need(!s.IsPartial, "complete turn should reconcile");
});
Test("Observed TTFT is removed from processing but not active time", () => {
    var s = Sum(Completed(ttft: 5000)); Near(15, s.ProcessingSeconds, "TTFT-adjusted processing"); Near(20, s.ActiveSeconds, "active still includes TTFT"); Near(40d / 15, s.AverageTps, "output divided by adjusted processing"); Equal(40L, s.OutputTokens, "daily output unchanged"); Equal(1, s.TimedTurnCount, "accepted count"); Equal(0, s.ExcludedTimingTurnCount, "excluded count"); Near(5, s.ExcludedWaitSeconds, "accepted wait union");
});
Test("Missing negative or excessive TTFT cannot fall back to full duration", () => {
    foreach (var ttft in new long?[] { null, -1, 20000, 21000, long.MaxValue }) {
        var s = Sum(Completed(ttft: ttft)); Equal(40L, s.OutputTokens, "observed output survives unavailable timing"); Near(20, s.ActiveSeconds, "active time stays observed"); Need(s.ProcessingSeconds is null && s.AverageTps is null, "invalid TTFT used full duration");
    }
});
Test("TPS output and adjusted duration cover exactly the same accepted turns", () => {
    var s = Sum(Completed(ttft: 5000).Concat(Completed("other", "other-turn", "other-request", b, ttft: null))); Equal(120L, s.OutputTokens, "all observed daily output"); Near(15, s.ProcessingSeconds, "only accepted adjusted duration"); Near(40d / 15, s.AverageTps, "only accepted output numerator"); Equal(1, s.TimedTurnCount, "one accepted count"); Equal(1, s.ExcludedTimingTurnCount, "one excluded count"); Near(5, s.ExcludedWaitSeconds, "wait coverage matches accepted turns");
});
Test("Matched tool wait is removed from accepted processing time", () => {
    var s = Sum(Completed(ttft: 2000).Concat([Tool(DailyToolPhase.Call, 15), Tool(DailyToolPhase.Result, 10)])); Near(13, s.ProcessingSeconds, "twenty minus two TTFT and five tool seconds"); Near(40d / 13, s.AverageTps, "adjusted TPS"); Near(20, s.ActiveSeconds, "active remains observed");
});
Test("Tool wait overlapping TTFT is deducted only once", () => {
    var s = Sum(Completed(ttft: 5000).Concat([Tool(DailyToolPhase.Call, 18), Tool(DailyToolPhase.Result, 12)])); Near(12, s.ProcessingSeconds, "wait union is eight seconds"); Near(40d / 12, s.AverageTps, "overlap-adjusted TPS"); Near(8, s.ExcludedWaitSeconds, "TTFT and tool union");
});
Test("Parallel and nested tool waits form one union within each turn", () => {
    StatsEvent[] tools = [Tool(DailyToolPhase.Call, 16, "a"), Tool(DailyToolPhase.Result, 10, "a"), Tool(DailyToolPhase.Call, 13, "b"), Tool(DailyToolPhase.Result, 6, "b"), Tool(DailyToolPhase.Call, 12, "nested"), Tool(DailyToolPhase.Result, 11, "nested")]; var s = Sum(Completed().Concat(tools)); Near(10, s.ProcessingSeconds, "parallel tool union covers ten seconds"); Near(4, s.AverageTps, "parallel waits not double deducted");
});
Test("Replayed tool metadata never increases deductions", () => {
    StatsEvent[] tools = [Tool(DailyToolPhase.Call, 15), Tool(DailyToolPhase.Result, 10)]; var s = Sum(Completed().Concat(tools).Concat(tools.Reverse()).Concat(tools)); Near(15, s.ProcessingSeconds, "one five-second wait after replay"); Near(40d / 15, s.AverageTps, "replayed tool TPS");
});
Test("Unmatched or conflicting tool metadata excludes only its affected turn", () => {
    StatsEvent[][] cases = [ [Tool(DailyToolPhase.Call, 15)], [Tool(DailyToolPhase.Result, 10)], [Tool(DailyToolPhase.Call, 15), Tool(DailyToolPhase.Call, 14), Tool(DailyToolPhase.Result, 10)], [Tool(DailyToolPhase.Call, 15), Tool(DailyToolPhase.Result, 10), Tool(DailyToolPhase.Result, 9)] ]; foreach (var tools in cases) {
        var s = Sum(Completed().Concat(Completed("other", "other-turn", "other-request", b)).Concat(tools)); Equal(120L, s.OutputTokens, "all daily output retained"); Near(20, s.ProcessingSeconds, "only unaffected turn timed"); Near(4, s.AverageTps, "excluded output not divided by accepted time"); Near(20, s.ActiveSeconds, "observed union unchanged");
    }
});
Test("Reversed nonpositive or out-of-lifecycle tool intervals cannot inflate TPS", () => {
    StatsEvent[][] cases = [ [Tool(DailyToolPhase.Call, 10), Tool(DailyToolPhase.Result, 15)], [Tool(DailyToolPhase.Call, 10), Tool(DailyToolPhase.Result, 10)], [Tool(DailyToolPhase.Call, 21), Tool(DailyToolPhase.Result, 15)], [Tool(DailyToolPhase.Call, 10), Tool(DailyToolPhase.Result, -1)] ]; foreach (var tools in cases) { var s = Sum(Completed().Concat(tools)); Need(s.AverageTps is null && s.ProcessingSeconds is null, "invalid interval entered adjusted timing"); Equal(40L, s.OutputTokens, "invalid timing preserves tokens"); }
});
Test("Tool metadata with missing call invalid phase or unknown time cannot enter TPS", () => {
    StatsEvent[] cases = [Tool(DailyToolPhase.Call, 15, null), Tool(DailyToolPhase.Call, 15, " "), Tool((DailyToolPhase)42, 15), Tool(DailyToolPhase.Call, 15) with { Timestamp = DateTimeOffset.MinValue }, Tool(DailyToolPhase.Call, 15) with { Timestamp = now.AddSeconds(1) }]; foreach (var tool in cases) { var s = Sum(Completed().Append(tool)); Need(s.AverageTps is null && s.ProcessingSeconds is null, "invalid tool evidence was ignored"); }
});
Test("Unavailable timing evidence excludes its explicit turn without dropping tokens", () => {
    var s = Sum(Completed().Concat(Completed("root", "second", "second-request", b)).Append(new DailyTimingUnavailableEvent("root", "turn", end))); Equal(120L, s.OutputTokens, "both token totals retained"); Near(20, s.ProcessingSeconds, "second turn remains timed"); Near(4, s.AverageTps, "only second output timed");
});
Test("Unknown turn tool or unavailable evidence makes session timing unavailable", () => {
    StatsEvent[] cases = [Tool(DailyToolPhase.Call, 15, turn: null), Tool(DailyToolPhase.Call, 15, turn: " "), new DailyTimingUnavailableEvent("root", null, end), new DailyTimingUnavailableEvent("root", " ", end)]; foreach (var evidence in cases) { var s = Sum(Completed().Concat(Completed("other", "other-turn", "other-request", b)).Append(evidence)); Near(20, s.ProcessingSeconds, "unrelated session remains timed"); Near(4, s.AverageTps, "unknown attribution cannot borrow root turn"); Equal(120L, s.OutputTokens, "all daily tokens preserved"); }
});
Test("Unrelated explicit turn evidence cannot poison a different completed turn", () => {
    var s = Sum(Completed().Concat([Tool(DailyToolPhase.Call, 15, turn: "unrelated"), new DailyTimingUnavailableEvent("root", "unrelated", end)])); Near(20, s.ProcessingSeconds, "unrelated turn excluded alone"); Near(2, s.AverageTps, "completed turn still accepted"); Equal(1, s.TurnCount, "timing metadata does not invent observed turns");
});
Test("Tool call identity reused across turns cannot pair arbitrarily", () => {
    StatsEvent[] tools = [Tool(DailyToolPhase.Call, 15), Tool(DailyToolPhase.Result, 10), Tool(DailyToolPhase.Call, 15, turn: "second"), Tool(DailyToolPhase.Result, 10, turn: "second")]; var s = Sum(Completed().Concat(Completed("root", "second", "second-request", b)).Concat(tools)); Need(s.AverageTps is null && s.ProcessingSeconds is null, "cross-turn collision was paired independently"); Equal(120L, s.OutputTokens, "identity ambiguity must preserve tokens");
});
Test("Tool identity scoped to different sessions remains independent", () => {
    StatsEvent[] tools = [Tool(DailyToolPhase.Call, 15), Tool(DailyToolPhase.Result, 10), Tool(DailyToolPhase.Call, 12, turn: "other-turn", session: "other"), Tool(DailyToolPhase.Result, 10, turn: "other-turn", session: "other")]; var s = Sum(Completed().Concat(Completed("other", "other-turn", "other-request", b)).Concat(tools)); Near(33, s.ProcessingSeconds, "fifteen plus eighteen adjusted seconds"); Near(120d / 33, s.AverageTps, "session-local call identities");
});
Test("Tool metadata of a known child cannot affect root timing", () => {
    StatsEvent[] child = [new MetadataEvent("child", end, true, "root"), new DailyTimingUnavailableEvent("child", null, end), Tool(DailyToolPhase.Call, 15, turn: null, session: "child")]; var s = Sum(Completed().Concat(child)); Near(20, s.ProcessingSeconds, "child tool metadata excluded"); Near(2, s.AverageTps, "root TPS survives child evidence"); Need(!s.IsPartial, "ignored child evidence does not mark root partial");
});
Test("A wait union consuming the complete duration cannot produce infinite TPS", () => {
    var s = Sum(Completed(ttft: 5000).Concat([Tool(DailyToolPhase.Call, 15), Tool(DailyToolPhase.Result, 0)])); Need(s.AverageTps is null && s.ProcessingSeconds is null, "zero adjusted duration accepted"); Near(20, s.ActiveSeconds, "active duration retained"); Equal(0, s.TimedTurnCount, "zero net not accepted"); Equal(1, s.ExcludedTimingTurnCount, "zero net excluded"); Need(s.ExcludedWaitSeconds is null, "no accepted denominator means no accepted wait sum");
});
Test("TTFT anchor uses declared duration despite tolerated explicit-start skew", () => {
    var ev = Completed(ttft: 2000).Select(x => x is StartEvent start ? start with { Timestamp = start.Timestamp.AddMilliseconds(-500) } : x); var s = Sum(ev.Concat([Tool(DailyToolPhase.Call, 18.5), Tool(DailyToolPhase.Result, 17)])); Near(17, s.ProcessingSeconds, "TTFT interval begins at declared start and overlaps half a second"); Near(20.5, s.ActiveSeconds, "explicit observed interval retains skew"); Near(3, s.ExcludedWaitSeconds, "TTFT plus tool union anchored consistently");
});
Test("Tool outside declared duration is rejected despite tolerated start skew", () => {
    var ev = Completed().Select(x => x is StartEvent start ? start with { Timestamp = start.Timestamp.AddMilliseconds(-500) } : x); var s = Sum(ev.Concat([Tool(DailyToolPhase.Call, 20.25), Tool(DailyToolPhase.Result, 19)])); Need(s.AverageTps is null && s.ProcessingSeconds is null, "outside-declaration tool was subtracted from a shorter denominator"); Near(20.5, s.ActiveSeconds, "active interval remains observed");
});
Test("Tool before explicit start is rejected even when within declared duration", () => {
    var ev = Completed().Select(x => x is StartEvent start ? start with { Timestamp = start.Timestamp.AddMilliseconds(500) } : x); var s = Sum(ev.Concat([Tool(DailyToolPhase.Call, 19.75), Tool(DailyToolPhase.Result, 19)])); Need(s.AverageTps is null && s.ProcessingSeconds is null, "out-of-observed-lifecycle tool accepted"); Near(19.5, s.ActiveSeconds, "active interval remains observed");
});
Test("A declared duration without explicit start supports bounded tool timing", () => {
    var s = Sum(Completed(ttft: 2000).Where(x => x is not StartEvent).Concat([Tool(DailyToolPhase.Call, 15), Tool(DailyToolPhase.Result, 10)])); Near(13, s.ProcessingSeconds, "inferred lifecycle tool wait"); Near(20, s.ActiveSeconds, "inferred complete interval");
});
Test("Submillisecond tool timing retains precision instead of rounding net time to zero", () => {
    StatsEvent[] tools = [Tool(DailyToolPhase.Call, 0) with { Timestamp = end.AddMilliseconds(-1) }, Tool(DailyToolPhase.Result, 0) with { Timestamp = end.AddTicks(-1) }]; var s = Sum(Completed(duration: 1).Select(x => x is UsageEvent usage ? usage with { Timestamp = end.AddTicks(-1) } : x).Concat(tools)); Near(.0000001, s.ProcessingSeconds, "one tick remains after wait"); Near(400000000, s.AverageTps, "finite tick-resolution speed");
});
Test("Session unavailable evidence after completion invalidates earlier session timings", () => {
    var s = Sum(Completed().Append(new DailyTimingUnavailableEvent("root", null, now))); Need(s.AverageTps is null && s.ProcessingSeconds is null, "session-wide source gap at current observation was ignored"); Equal(1, s.ExcludedTimingTurnCount, "completed turn excluded by source gap"); Equal(40L, s.OutputTokens, "tokens retained despite source gap");
});
Test("Complete tool pairs from future timestamps cannot silently disappear", () => {
    StatsEvent[] tools = [Tool(DailyToolPhase.Call, 15) with { Timestamp = now.AddSeconds(1) }, Tool(DailyToolPhase.Result, 10) with { Timestamp = now.AddSeconds(2) }]; var s = Sum(Completed().Concat(tools)); Need(s.AverageTps is null && s.ProcessingSeconds is null && s.IsPartial, "future complete tool pair ignored as zero wait");
});
Test("Identity replay across files never increases token or time totals", () => {
    var ev = Completed(); var s = Sum(ev.Concat(ev).Concat(ev)); Equal(100L, s.InputTokens, "replay input"); Equal(40L, s.OutputTokens, "replay output"); Near(20, s.ProcessingSeconds, "replay processing"); Equal(1, s.TurnCount, "replay turn count");
});
Test("Different request identities retain identical token values", () => {
    var ev = Completed().ToList(); ev.RemoveAll(x => x is UsageEvent); ev.Add(Usage("root", "turn", "r1", a, end.AddSeconds(-2), 1, a)); ev.Add(Usage("root", "turn", "r2", a, end.AddSeconds(-1), 2, a + a));
    var s = Sum(ev); Equal(200L, s.InputTokens, "distinct input"); Equal(80L, s.OutputTokens, "distinct output"); Near(4, s.AverageTps, "reconciled complete sum");
});
Test("A newer valid revision replaces its prior identity", () => {
    var ev = Completed().ToList(); ev.Add(Usage("root", "turn", "request", b, end.AddMilliseconds(-500), 2)); var s = Sum(ev); Equal(200L, s.InputTokens, "revision input"); Near(4, s.AverageTps, "revision TPS"); Need(!s.IsPartial, "valid revision should reconcile");
});
Test("Same-vector replay written after completion keeps the original occurrence and TPS", () => {
    var ev = Completed().ToList(); ev.Add(Usage("root", "turn", "request", a, end.AddSeconds(5), 2)); var s = Sum(ev); Equal(100L, s.InputTokens, "replay input"); Equal(40L, s.OutputTokens, "replay output"); Near(20, s.ProcessingSeconds, "original complete duration"); Near(2, s.AverageTps, "late replay must not move occurrence outside the turn"); Need(!s.IsPartial, "exact replay is not missing history");
});
Test("Changed valid counters written after completion reconcile against the updated snapshot", () => {
    var ev = Completed().ToList(); ev.Add(Usage("root", "turn", "request", b, end.AddSeconds(5), 2)); var s = Sum(ev); Equal(200L, s.InputTokens, "latest input replaces original"); Equal(80L, s.OutputTokens, "latest output replaces original"); Near(20, s.ProcessingSeconds, "duration remains the completed turn duration"); Near(4, s.AverageTps, "updated reconciled output uses the original lifecycle"); Need(!s.IsPartial, "valid corrected snapshot should reconcile");
});
Test("An earlier request's no-op replay cannot replace the final turn snapshot order", () => {
    var ev = Completed().Where(x => x is not UsageEvent).ToList(); ev.Add(Usage("root", "turn", "r1", a, end.AddSeconds(-2), 1, a)); ev.Add(Usage("root", "turn", "r2", b, end.AddSeconds(-1), 2, a + b)); ev.Add(Usage("root", "turn", "r1", a, end.AddSeconds(5), 3, a)); var s = Sum(ev); Equal(300L, s.InputTokens, "two distinct requests"); Near(6, s.AverageTps, "no-op replay must not promote an intermediate cumulative snapshot"); Need(!s.IsPartial, "replayed intermediate snapshot is not a revision");
});
Test("A changed earlier request may supply a corrected final cumulative snapshot", () => {
    var ev = Completed().Where(x => x is not UsageEvent).ToList(); ev.Add(Usage("root", "turn", "r1", a, end.AddSeconds(-2), 1, a)); ev.Add(Usage("root", "turn", "r2", a, end.AddSeconds(-1), 2, a + a)); ev.Add(Usage("root", "turn", "r1", b, end.AddSeconds(5), 3, a + b)); var s = Sum(ev); Equal(300L, s.InputTokens, "corrected total input"); Near(6, s.AverageTps, "corrected final cumulative snapshot must use revision order"); Need(!s.IsPartial, "updated full snapshot should reconcile");
});
Test("A late snapshot-only correction supplies reconciliation without moving occurrence", () => {
    var ev = Completed().Select(x => x is UsageEvent usage ? usage with { TurnSnapshot = null } : x).ToList(); ev.Add(Usage("root", "turn", "request", a, end.AddSeconds(5), 2)); var s = Sum(ev); Equal(100L, s.InputTokens, "same counters retained"); Near(2, s.AverageTps, "new snapshot evidence must be considered at its revision order"); Need(!s.IsPartial, "late valid snapshot repairs missing reconciliation");
});
Test("A late contradictory snapshot cannot reuse earlier matching reconciliation", () => {
    var ev = Completed().ToList(); ev.Add(Usage("root", "turn", "request", a, end.AddSeconds(5), 2, b)); var s = Sum(ev); Equal(100L, s.InputTokens, "valid observed counters retained"); Need(s.AverageTps is null && s.ProcessingSeconds is null && s.IsPartial, "changed snapshot evidence must not borrow older matching proof");
});
Test("A revision across local midnight stays on its first occurrence day", () => {
    var finished = DateTimeOffset.Parse("2026-10-20T15:59:55Z"); var afterMidnight = DateTimeOffset.Parse("2026-10-20T16:01:00Z"); var ev = Completed(completedAt: finished).ToList(); ev.Add(Usage("root", "turn", "request", b, finished.AddSeconds(10), 2)); var originalDay = Sum(ev, afterMidnight); Equal(200L, originalDay.InputTokens, "latest counters remain on original local day"); Equal(80L, originalDay.OutputTokens, "revised output remains on original day"); Near(4, originalDay.AverageTps, "completed original-day TPS updates with valid counters"); var nextDay = DailyAggregator.Summarize(ev, day.AddDays(1), zone, afterMidnight); Need(nextDay.InputTokens is null && nextDay.OutputTokens is null && nextDay.AverageTps is null, "revision arrival must not become next-day usage"); Equal(0, nextDay.ChatCount, "revision arrival alone is not a new daily chat"); Equal(0, nextDay.TurnCount, "revision arrival alone is not a new daily turn");
});
Test("An invalid late revision still rejects the identity's contribution", () => {
    var ev = Completed().ToList(); ev.Add(Usage("root", "turn", "request", new TokenVector(-1, 0, 1, 0, 0), end.AddSeconds(5), 2)); var s = Sum(ev); Need(s.InputTokens is null && s.OutputTokens is null && s.AverageTps is null && s.IsPartial, "invalid late revision must not fall back to original counters");
});
Test("Conflicting late revisions still reject the identity's contribution", () => {
    var ev = Completed().ToList(); ev.Add(Usage("root", "turn", "request", a, end.AddSeconds(5), 2)); ev.Add(Usage("root", "turn", "request", b, end.AddSeconds(5), 2)); var s = Sum(ev); Need(s.InputTokens is null && s.OutputTokens is null && s.AverageTps is null && s.IsPartial, "conflicting late revisions must not choose a vector");
});
Test("A request first observed after completion does not invent an in-turn occurrence", () => {
    var ev = Completed().Where(x => x is not UsageEvent).ToList(); ev.Add(Usage("root", "turn", "request", a, end.AddSeconds(5), 2)); var s = Sum(ev); Equal(100L, s.InputTokens, "valid observed counters remain visible"); Need(s.AverageTps is null && s.ProcessingSeconds is null, "no actual in-turn observation supports timing"); Near(20, s.ActiveSeconds, "only explicit observed interval remains");
});
Test("Equal-order conflicting identity invalidates its contribution", () => {
    var ev = Completed().ToList(); ev.Add(Usage("root", "turn", "request", b, end.AddSeconds(-1))); var s = Sum(ev); Need(s.InputTokens is null && s.OutputTokens is null && s.AverageTps is null && s.IsPartial, "conflicting contribution must be excluded");
});
Test("Conflicted request does not remove an unrelated valid request", () => {
    var ev = Completed().Concat(Completed("other", "other-turn", "other-request", b)).ToList(); ev.Add(Usage("root", "turn", "request", b, end.AddSeconds(-1))); var s = Sum(ev); Equal(200L, s.InputTokens, "valid other session survives"); Near(4, s.AverageTps, "only unrelated completed turn"); Need(s.IsPartial, "conflict remains explicit");
});
Test("Timestamp and ordinal inversion cannot silently choose a revision", () => {
    var ev = Completed().ToList(); ev.Add(Usage("root", "turn", "request", b, end, 0)); var s = Sum(ev); Need(s.InputTokens is null && s.IsPartial, "inconsistent ordering must reject");
});
Test("Same-time missing ordinal conflict cannot choose a revision", () => {
    var ev = Completed().ToList(); ev.Add(Usage("root", "turn", "request", b, end.AddSeconds(-1)) with { Ordinal = null }); var s = Sum(ev); Need(s.InputTokens is null && s.IsPartial, "missing ordinal ambiguity must reject");
});
Test("Negative and inconsistent subset fields never contribute", () => {
    foreach (var invalid in new[] { new TokenVector(-1, 0, 1, 0, 0), new TokenVector(10, 11, 1, 0, 11), new TokenVector(10, 0, 1, 2, 11), new TokenVector(10, 0, 1, 0, 12) }) {
        var s = Sum(Completed(tokens: invalid)); Need(s.InputTokens is null && s.OutputTokens is null && s.AverageTps is null && s.IsPartial, "invalid vector retained");
    }
});
Test("Missing fields preserve known fields and never imply zeros", () => {
    var s = Sum(Completed(tokens: new TokenVector(100, null, null, null, null))); Equal(100L, s.InputTokens, "known input"); Need(s.CachedInputTokens is null && s.OutputTokens is null && s.ReasoningTokens is null && s.IsPartial && s.AverageTps is null, "missing fields must remain unknown");
});
Test("A true observed zero remains zero", () => {
    var s = Sum(Completed(tokens: TokenVector.Zero)); Equal(0L, s.InputTokens, "zero input"); Equal(0L, s.OutputTokens, "zero output"); Near(0, s.AverageTps, "zero output TPS"); Need(!s.IsPartial, "observed zero is valid");
});
Test("Modern and legacy mirrors never double count", () => {
    var ev = Completed().ToList(); ev.Add(new UsageEvent("root", "turn", null, null, end, null, b, null, b, true)); var s = Sum(ev); Equal(100L, s.InputTokens, "legacy mirror input"); Equal(40L, s.OutputTokens, "legacy mirror output"); Near(2, s.AverageTps, "legacy mirror TPS");
});
Test("Legacy-only data remains unknown and partial", () => {
    var s = Sum([new MetadataEvent("root", end, false, null), new UsageEvent("root", null, null, null, end, null, a, null, a, true)]); Need(s.InputTokens is null && s.OutputTokens is null && s.IsPartial, "legacy snapshots cannot establish daily usage");
});
Test("Today's legacy snapshot counts its observed chat and optional turn without token totals", () => {
    StatsEvent[] ev = [new MetadataEvent("root", end.AddDays(-1), false, null), new UsageEvent("root", null, null, null, end, null, a, null, a, true)]; var s = Sum(ev); Equal(1, s.ChatCount, "today's observed legacy chat"); Equal(0, s.TurnCount, "missing legacy turn identity remains unknown"); Need(s.InputTokens is null && s.OutputTokens is null && s.IsPartial, "legacy token totals must remain unknown"); var knownTurn = Sum(ev.Select(x => x is UsageEvent usage ? usage with { TurnKey = "known-legacy-turn" } : x)); Equal(1, knownTurn.ChatCount, "known legacy chat"); Equal(1, knownTurn.TurnCount, "observed legacy turn identity"); Need(knownTurn.InputTokens is null && knownTurn.OutputTokens is null, "legacy turn identity cannot establish token usage");
});
Test("Known children do not enter root tokens counts or timing", () => {
    var ev = Completed().Concat(Completed("child", "child-turn", "child-request", b)).ToList(); ev.RemoveAll(x => x is MetadataEvent m && m.SessionKey == "child"); ev.Add(new MetadataEvent("child", end.AddHours(-2), true, "root")); var s = Sum(ev); Equal(100L, s.InputTokens, "root-only input"); Equal(1, s.ChatCount, "root chat count"); Equal(1, s.TurnCount, "root turn count"); Near(20, s.ProcessingSeconds, "root processing"); Need(!s.IsPartial, "excluded child does not invalidate root");
});
Test("Missing request identity never invents a daily contribution", () => {
    var ev = Completed().Select(x => x is UsageEvent u ? u with { RequestKey = null } : x); var s = Sum(ev); Need(s.InputTokens is null && s.IsPartial, "unidentified request counted");
});
Test("Missing session metadata cannot silently claim a root-only total", () => {
    var s = Sum(Completed().Where(x => x is not MetadataEvent)); Need(s.InputTokens is null && s.OutputTokens is null && s.IsPartial, "unproven session scope contributed tokens"); Need(s.Status.Contains("根会话", StringComparison.Ordinal), "unknown root scope must be explained");
});
Test("Local midnight assigns usage by event time not UTC calendar date", () => {
    var midnight = DateTimeOffset.Parse("2026-10-19T16:00:00Z"); var ev = Completed(completedAt: midnight.AddSeconds(20)); var s = Sum(ev); Equal(100L, s.InputTokens, "UTC previous-day record belongs to local today"); Near(2, s.AverageTps, "fully local same-day duration");
});
Test("A crossing-midnight turn contributes only today's requests and no TPS", () => {
    var midnight = DateTimeOffset.Parse("2026-10-19T16:00:00Z"); StatsEvent[] ev = [new MetadataEvent("root", midnight.AddHours(-1), false, null), new StartEvent("root", "turn", midnight.AddSeconds(-10)), Usage("root", "turn", "r1", a, midnight.AddSeconds(-1), 1, a), Usage("root", "turn", "r2", b, midnight.AddSeconds(9), 2, a + b), new CompletionEvent("root", "turn", midnight.AddSeconds(10), 20000, null)];
    var s = Sum(ev); Equal(200L, s.InputTokens, "only today's request input"); Equal(80L, s.OutputTokens, "only today's output"); Need(s.AverageTps is null && s.ProcessingSeconds is null, "cross-day turn must not enter matched same-day speed"); Near(10, s.ActiveSeconds, "active interval clipped at local midnight"); Need(!s.IsPartial, "valid cross-day history is not corrupt");
});
Test("Parallel turn durations add while active intervals form a union", () => {
    var s = Sum(Completed().Concat(Completed("other", "other-turn", "other-request", b, end.AddSeconds(10)))); Near(40, s.ProcessingSeconds, "processing sums both turns"); Near(30, s.ActiveSeconds, "overlap must not count twice"); Near(3, s.AverageTps, "output over sum duration");
});
Test("Completion lacking duration keeps valid tokens but no invented TPS", () => {
    var s = Sum(Completed(duration: null)); Equal(100L, s.InputTokens, "tokens survive missing duration"); Need(s.AverageTps is null && s.ProcessingSeconds is null, "duration was guessed"); Near(20, s.ActiveSeconds, "explicit start/end interval remains observed"); Need(!s.IsPartial, "missing timing alone does not invalidate token totals"); Need(s.Status.Contains("耗时", StringComparison.Ordinal), "status must explain timing unavailable");
});
Test("Gross disagreement between explicit elapsed time and declared duration cannot inflate TPS", () => {
    var ev = Completed().Select(x => x is CompletionEvent completion ? completion with { DurationMs = 1000 } : x); var s = Sum(ev); Equal(100L, s.InputTokens, "valid input retained"); Equal(40L, s.OutputTokens, "valid output retained"); Near(20, s.ActiveSeconds, "explicit interval remains observed"); Need(s.AverageTps is null && s.ProcessingSeconds is null && s.IsPartial, "inconsistent declared duration inflated throughput"); Need(s.Status.Contains("起止时间", StringComparison.Ordinal), "timing conflict must be explained");
});
Test("Small log scheduling skew does not discard valid declared timing", () => {
    var ev = Completed().Select(x => x is StartEvent start ? start with { Timestamp = start.Timestamp.AddMilliseconds(-500) } : x); var s = Sum(ev); Near(20, s.ProcessingSeconds, "declared processing duration retained"); Near(20.5, s.ActiveSeconds, "observed interval retained"); Near(2, s.AverageTps, "small scheduling skew tolerance"); Need(!s.IsPartial, "small skew should not mark a complete turn partial");
});
Test("TPS numerator only includes turns with accepted matching durations", () => {
    var s = Sum(Completed().Concat(Completed("other", "other-turn", "other-request", b, duration: null))); Equal(120L, s.OutputTokens, "all daily outputs remain visible"); Near(20, s.ProcessingSeconds, "only matched duration"); Near(2, s.AverageTps, "unmatched output must not inflate TPS");
});
Test("Mismatching turn snapshot suppresses timing and reports incomplete history", () => {
    var ev = Completed().Select(x => x is UsageEvent u ? u with { TurnSnapshot = b } : x); var s = Sum(ev); Equal(100L, s.InputTokens, "observed input remains visible"); Need(s.AverageTps is null && s.IsPartial, "mismatching turn cannot claim completeness");
});
Test("A missing completion snapshot cannot claim reliable throughput", () => {
    var ev = Completed().Select(x => x is UsageEvent u ? u with { TurnSnapshot = null } : x); var s = Sum(ev); Need(s.AverageTps is null && s.IsPartial, "missing snapshot accepted as reconciled");
});
Test("A recent observed ongoing turn has active time and no completed TPS", () => {
    StatsEvent[] ev = [new MetadataEvent("root", now.AddHours(-1), false, null), new StartEvent("root", "active", now.AddMinutes(-5)), Usage("root", "active", "active-request", a, now.AddMinutes(-1))]; var s = Sum(ev); Equal(1, s.ActiveTurns, "observed active count"); Near(300, s.ActiveSeconds, "active time to current observation"); Need(s.AverageTps is null && s.ProcessingSeconds is null, "running turn must not enter completed speed"); Equal(100L, s.InputTokens, "running token total");
});
Test("An old unmatched start is not declared active", () => {
    var s = Sum([new MetadataEvent("root", now.AddHours(-1), false, null), new StartEvent("root", "stale", now.AddMinutes(-16))]); Equal(0, s.ActiveTurns, "stale start wrongly active"); Need(s.ActiveSeconds is null, "stale open interval was extended indefinitely");
});
Test("An explicit abort ends observed interval and never enters completed TPS", () => {
    StatsEvent[] ev = [new MetadataEvent("root", now.AddHours(-1), false, null), new StartEvent("root", "aborted", now.AddSeconds(-20)), Usage("root", "aborted", "r", a, now.AddSeconds(-15)), new DailyTurnAbortedEvent("root", "aborted", now.AddSeconds(-10))]; var s = Sum(ev); Equal(0, s.ActiveTurns, "aborted turn still active"); Near(10, s.ActiveSeconds, "abort interval"); Need(s.AverageTps is null, "abort cannot enter completion TPS");
});
Test("A superseded unmatched start cannot extend the active union", () => {
    StatsEvent[] ev = [new MetadataEvent("root", now.AddHours(-1), false, null), new StartEvent("root", "old", now.AddMinutes(-10)), new StartEvent("root", "latest", now.AddMinutes(-5))]; var s = Sum(ev); Equal(1, s.ActiveTurns, "latest observed start only"); Near(300, s.ActiveSeconds, "superseded open start excluded");
});
Test("Simultaneous distinct root starts do not guess which turn is active", () => {
    StatsEvent[] ev = [new MetadataEvent("root", now.AddHours(-1), false, null), new StartEvent("root", "first", now.AddMinutes(-5)), new StartEvent("root", "second", now.AddMinutes(-5))]; var s = Sum(ev); Equal(0, s.ActiveTurns, "ambiguous equal-time latest turn selected"); Need(s.ActiveSeconds is null && s.IsPartial, "ambiguous active interval should remain unknown");
});
Test("A trustworthy duration supplies an interval when explicit start is absent", () => {
    var s = Sum(Completed().Where(x => x is not StartEvent)); Near(20, s.ProcessingSeconds, "declared processing duration"); Near(20, s.ActiveSeconds, "completion-derived observed interval"); Near(2, s.AverageTps, "matched output per duration");
});
Test("A prior-day request omitted by the daily reader fails snapshot reconciliation", () => {
    var midnight = DateTimeOffset.Parse("2026-10-19T16:00:00Z"); StatsEvent[] ev = [new MetadataEvent("root", midnight.AddHours(-1), false, null), Usage("root", "turn", "r2", b, midnight.AddSeconds(9), 2, a + b), new CompletionEvent("root", "turn", midnight.AddSeconds(10), 20000, null)]; var s = Sum(ev); Equal(200L, s.InputTokens, "observed today input retained"); Need(s.AverageTps is null && s.ProcessingSeconds is null && s.IsPartial, "truncated cross-day turn accepted as complete");
});
Test("Conflicting completions never choose an arbitrary duration", () => {
    var ev = Completed().ToList(); ev.Add(new CompletionEvent("root", "turn", end, 30000, null)); var s = Sum(ev); Need(s.AverageTps is null && s.ProcessingSeconds is null && s.IsPartial, "completion conflict accepted"); Equal(100L, s.InputTokens, "valid token fields survive timing conflict");
});
Test("Events from the future cannot inflate current daily totals", () => {
    var s = Sum(Completed(completedAt: now.AddMinutes(5))); Need(s.InputTokens is null && s.AverageTps is null, "future statistics counted"); Need(s.IsPartial, "future source needs explicit partial status");
});
Test("Overflow becomes unknown without wrapping other known fields", () => {
    var huge = new TokenVector(long.MaxValue, 0, 0, 0, long.MaxValue); var s = Sum(Completed(tokens: huge).Concat(Completed("other", "other-turn", "other-request", a))); Need(s.InputTokens is null && s.IsPartial, "input overflow wrapped"); Equal(40L, s.OutputTokens, "independent output field should remain known");
});
Test("Explicit scanner partial flag survives valid statistical data", () => {
    var s = Sum(Completed(), partial: true); Need(s.IsPartial, "scanner boundary was lost"); Equal(100L, s.InputTokens, "known observed value retained");
});
Test("Actual weekly window shows latest valid original sample timestamp", () => {
    var observed = now.AddHours(-1); var s = Sum([new QuotaEvent("root", observed, "weekly-bucket", new QuotaWindow(25, 300, now.AddHours(1)), new QuotaWindow(40, 10080, now.AddDays(3)))]); Need(s.Weekly is not null, "weekly window missing"); Equal(40d, s.Weekly!.UsedPercent, "actual weekly percent"); Equal(observed, s.Weekly.ObservedAt, "quota original sample time");
});
Test("Expired weekly window is unavailable", () => {
    var s = Sum([new QuotaEvent("root", now.AddMinutes(-1), "bucket", null, new QuotaWindow(40, 10080, now))]); Need(s.Weekly is null, "expired quota shown");
});
Test("A 300-minute quota cannot be labeled weekly", () => {
    var s = Sum([new QuotaEvent("root", now, "bucket", new QuotaWindow(25, 300, now.AddHours(1)), null)]); Need(s.Weekly is null, "short window mislabeled weekly");
});
Test("A newer bucket snapshot removes its older unknown weekly value", () => {
    var s = Sum([new QuotaEvent("root", now.AddMinutes(-2), "bucket", null, new QuotaWindow(40, 10080, now.AddDays(3))), new QuotaEvent("root", now.AddMinutes(-1), "bucket", new QuotaWindow(25, 300, now.AddHours(1)), null)]); Need(s.Weekly is null, "older bucket sample retained after replacement");
});
Test("A future quota sample is never presented as already observed", () => {
    var s = Sum([new QuotaEvent("root", now.AddMinutes(1), "bucket", null, new QuotaWindow(40, 10080, now.AddDays(3)))]); Need(s.Weekly is null, "future quota shown");
});
Test("Weekly snapshot without reset requires a recent known sample", () => {
    var recent = Sum([new QuotaEvent("root", now.AddMinutes(-5), "bucket", null, new QuotaWindow(40, 10080, null))]); Need(recent.Weekly is not null, "recent optional-reset sample unavailable"); var stale = Sum([new QuotaEvent("root", now.AddMinutes(-16), "bucket", null, new QuotaWindow(40, 10080, null))]); Need(stale.Weekly is null, "stale unknown-reset quota shown");
});
Test("Different valid weekly buckets never collapse into one allowance", () => {
    var s = Sum([new QuotaEvent("root", now.AddMinutes(-2), "first-bucket", null, new QuotaWindow(40, 10080, now.AddDays(3))), new QuotaEvent("root", now.AddMinutes(-1), "second-bucket", null, new QuotaWindow(30, 10080, now.AddDays(2)))]); Need(s.Weekly is null, "different buckets silently selected or combined"); Need(s.Status.Contains("多个", StringComparison.Ordinal), "multiple windows must be explained");
});
Test("Same-bucket equal-time quota mirrors across chats remain one allowance", () => {
    var window = new QuotaWindow(40, 10080, now.AddDays(3)); var s = Sum([new QuotaEvent("root", now.AddMinutes(-1), "bucket", null, window), new QuotaEvent("other", now.AddMinutes(-1), "bucket", null, window)]); Need(s.Weekly is not null && s.Weekly.UsedPercent == 40, "identical account quota mirrors treated as conflict");
});

Console.WriteLine($"Daily aggregation: {checks - failures}/{checks} passed; {failures} failed.");
return failures == 0 ? 0 : 1;
