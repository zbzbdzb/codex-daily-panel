using System.Collections;
using CodexUsage.Core;
using CodexUsage.Daily;
using CodexUsage.DailyPanel;

var failed = 0;
var cases = 0;
void Check(string name, Action body)
{
    cases++;
    try { body(); Console.WriteLine("PASS " + name); }
    catch (Exception exception) { failed++; Console.WriteLine("FAIL " + name + ": " + exception.Message); }
}
void Need(bool condition, string reason) { if (!condition) throw new InvalidOperationException(reason); }
void Equal<T>(T expected, T actual, string reason) => Need(EqualityComparer<T>.Default.Equals(expected, actual), $"{reason}: expected {expected}, got {actual}");
// Reflection deliberately lets the first RED execute against the pre-feature
// model. It checks actual reason production, not translated Status parsing.
string[] Codes(DailySnapshot snapshot)
{
    var property = typeof(DailySnapshot).GetProperty("Issues");
    Need(property is not null, "DailySnapshot does not expose structured issue diagnostics");
    return ((IEnumerable)property!.GetValue(snapshot)!).Cast<object>()
        .Select(issue => (string)issue.GetType().GetProperty("Code")!.GetValue(issue)!).ToArray();
}
var zone = TimeZoneInfo.CreateCustomTimeZone("StateTestsUtcPlus8", TimeSpan.FromHours(8), "UTC+8", "UTC+8");
var now = new DateTimeOffset(2026, 10, 23, 12, 0, 0, TimeSpan.FromHours(8));
var day = new DateOnly(2026, 10, 23);
var at = now.AddMinutes(-1);
var tokens = new TokenVector(100, 60, 40, 10, 140);
StatsEvent[] Normal() => [new MetadataEvent("root", at.AddMinutes(-1), false, null),
    new StartEvent("root", "turn", at.AddSeconds(-10)),
    new UsageEvent("root", "turn", "turn", "request", at.AddSeconds(-1), 1, tokens, tokens, null),
    new CompletionEvent("root", "turn", at, 10000, 1000)];
DailySnapshot Sum(IEnumerable<StatsEvent> rows, bool partial = false) => DailyAggregator.Summarize(rows, day, zone, now, partial);

Check("Modern totals timing and Status compatibility are unchanged", () => {
    var value = Sum(Normal());
    Equal(100L, value.InputTokens, "input"); Equal(40L, value.OutputTokens, "output");
    Equal(9d, value.ProcessingSeconds, "net seconds"); Equal(40d / 9, value.AverageTps, "TPS");
    Need(!value.IsPartial, "complete modern result became partial");
    Equal("本地日志 · 根会话；平均 TPS 为扣除首字与工具等待后的估算", value.Status, "legacy Status");
});
Check("Source partial emits its code and preserves known subtotal", () => {
    var value = Sum(Normal(), true);
    Equal(100L, value.InputTokens, "known input"); Need(value.IsPartial, "partial flag");
    Need(Codes(value).Contains("source.partial"), "source diagnostic missing");
});
Check("Unknown metadata emits structured reason without inventing total", () => {
    var value = Sum(Normal().Where(row => row is not MetadataEvent));
    Need(value.InputTokens is null && value.IsPartial, "unscoped rows must stay unknown");
    Need(Codes(value).Contains("session.metadata_missing"), "metadata diagnostic missing");
});
Check("Duration conflict retains token subtotal and emits precise code", () => {
    var value = Sum(Normal().Select(row => row is StartEvent start ? start with { Timestamp = at.AddSeconds(-40) } : row));
    Equal(100L, value.InputTokens, "input"); Need(value.AverageTps is null && value.IsPartial, "invalid duration entered TPS");
    Need(Codes(value).Contains("timing.duration_mismatch"), "duration diagnostic missing");
});
Check("No-data informational issue does not imply Partial", () => {
    var value = Sum([]);
    Need(value.InputTokens is null && !value.IsPartial, "no-data quality changed");
    Need(Codes(value).Contains("data.no_tokens"), "no-data code missing");
});

Check("Fresh presentation retains exact snapshot and observed active count", () => {
    var snapshot = Sum(Normal()) with { ActiveTurns = 1 };
    var state = new DailyPresentationState { Snapshot = snapshot };
    var projected = state.Project(now, zone);
    Need(ReferenceEquals(snapshot, projected.Snapshot), "projection must not mutate numeric snapshot");
    Need(!projected.IsStale && !projected.IsDifferentDay, "fresh value became stale");
    Equal(1, projected.DisplayActiveTurns, "current observation");
});
Check("Whole-refresh failure retains original values date and timestamp as stale", () => {
    var snapshot = Sum(Normal()) with { ActiveTurns = 1 };
    var projected = new DailyPresentationState { Snapshot = snapshot, RefreshFailed = true, LastAttemptAt = now.AddMinutes(1) }
        .Project(now.AddMinutes(1), zone);
    Equal(100L, projected.Snapshot!.InputTokens, "retained input"); Equal(now, projected.Snapshot.UpdatedAt, "original observation timestamp");
    Equal(day, projected.Snapshot.Day, "original day"); Need(projected.IsStale, "retained snapshot not labelled stale");
    Need(projected.DisplayActiveTurns is null, "old active observation presented as current");
    Need(projected.Issues.Any(x => x.Code == "ui.refresh_failed"), "whole-refresh failure reason absent");
});
Check("Midnight while paused retains yesterday with visible stale day and no active claim", () => {
    var snapshot = Sum(Normal()) with { ActiveTurns = 1, UpdatedAt = new DateTimeOffset(2026, 10, 23, 23, 59, 59, TimeSpan.FromHours(8)) };
    var projected = new DailyPresentationState { Snapshot = snapshot, IsPaused = true }.Project(snapshot.UpdatedAt.AddSeconds(1), zone);
    Equal(new DateOnly(2026, 10, 24), projected.CurrentDay, "explicit local midnight");
    Equal(day, projected.Snapshot!.Day, "retained values stay dated");
    Need(projected.IsStale && projected.IsDifferentDay && projected.State.IsPaused, "paused/date quality must coexist");
    Need(projected.DisplayActiveTurns is null, "yesterday active count leaked into today");
    Need(projected.Issues.Any(x => x.Code == "ui.stale_day"), "stale-day reason absent");
});
Check("Paused current-day snapshot suppresses current activity but preserves observed data", () => {
    var snapshot = Sum(Normal()) with { ActiveTurns = 2 };
    var projected = new DailyPresentationState { Snapshot = snapshot, IsPaused = true }.Project(now.AddMinutes(16), zone);
    Need(projected.DisplayActiveTurns is null, "paused old activity promoted to current");
    Equal(2, projected.Snapshot!.ActiveTurns, "numeric snapshot semantics were changed");
    Equal(100L, projected.Snapshot.InputTokens, "paused known subtotal");
});
Check("Retained activity expires at conservative 15 minute observation bound without changing snapshot", () => {
    var snapshot = Sum(Normal()) with { ActiveTurns = 1 };
    var state = new DailyPresentationState { Snapshot = snapshot, IsBusy = true };
    Equal(1, state.Project(now.AddMinutes(15), zone).DisplayActiveTurns, "exact observation boundary");
    var expired = state.Project(now.AddMinutes(15).AddTicks(1), zone);
    Need(expired.DisplayActiveTurns is null, "retained activity survived beyond any possible original 15 minute window");
    Equal(1, expired.Snapshot!.ActiveTurns, "source activity changed");
    Need(expired.Issues.Any(x => x.Code == "activity.observation_expired"), "expired activity reason absent");
    Need((state with { Snapshot = snapshot with { ActiveTurns = 0 } }).Project(now.AddMinutes(16), zone).DisplayActiveTurns is null,
        "old zero activity observation cannot establish current inactivity");
});
Check("Future snapshot cannot claim current activity or allowance and stays explicitly dated", () => {
    var snapshot = Sum(Normal()) with { UpdatedAt = now.AddSeconds(1), ActiveTurns = 1, Weekly = new(25, now.AddHours(1), now) };
    var projected = new DailyPresentationState { Snapshot = snapshot }.Project(now, zone);
    Need(projected.IsStale && projected.DisplayActiveTurns is null && projected.DisplayWeekly is null, "future snapshot presented current claims");
    Equal(now.AddSeconds(1), projected.Snapshot!.UpdatedAt, "source timestamp changed");
    Equal(100L, projected.Snapshot.InputTokens, "retained token amount changed");
    Need(projected.Issues.Any(x => x.Code == "ui.snapshot_future"), "future source-time reason absent");
});
Check("Future weekly observation cannot be displayed even when reset is valid", () => {
    var snapshot = Sum(Normal()) with { Weekly = new(25, now.AddHours(1), now.AddSeconds(1)) };
    var projected = new DailyPresentationState { Snapshot = snapshot }.Project(now, zone);
    Need(projected.DisplayWeekly is null && !projected.IsStale, "future weekly observation invalidated valid token snapshot or appeared current");
    Need(projected.Issues.Any(x => x.Code == "quota.observation_future"), "future weekly reason absent");
});
Check("New partial snapshot replaces prior larger subtotal without stale fallback", () => {
    var old = Sum(Normal()) with { InputTokens = 200, OutputTokens = 80 };
    var state = new DailyPresentationState { Snapshot = old, RefreshFailed = true };
    var next = Sum(Normal(), true) with { UpdatedAt = now.AddSeconds(10) };
    var projected = (state with { Snapshot = next, RefreshFailed = false }).Project(now.AddSeconds(10), zone);
    Equal(100L, projected.Snapshot!.InputTokens, "new verifiable subtotal");
    Need(projected.Snapshot.IsPartial && !projected.IsStale, "new partial must not be confused with retained error snapshot");
});
Check("Reset expiry uses current time even when observation is frozen by pause", () => {
    var snapshot = Sum(Normal()) with { Weekly = new(25, now.AddSeconds(10), now.AddMinutes(-2)) };
    var state = new DailyPresentationState { Snapshot = snapshot, IsPaused = true };
    Need(state.Project(now.AddSeconds(9), zone).DisplayWeekly is not null, "valid observed quota hidden early");
    var expired = state.Project(now.AddSeconds(10), zone);
    Need(expired.DisplayWeekly is null, "expired allowance survived because UpdatedAt did not advance");
    Need(expired.Issues.Any(x => x.Code == "quota.expired"), "expiry reason absent");
    Need(snapshot.Weekly is not null, "original snapshot mutated");
});
Check("Unknown-reset weekly lifetime exactly matches existing 15 minute contract", () => {
    var snapshot = Sum(Normal()) with { Weekly = new(25, null, now.AddMinutes(-15)) };
    var state = new DailyPresentationState { Snapshot = snapshot, IsPaused = true };
    Need(state.Project(now, zone).DisplayWeekly is not null, "15 minute boundary must remain valid");
    Need(state.Project(now.AddTicks(1), zone).DisplayWeekly is null, "older than 15 minutes must expire using now");
});
Check("Explicit time zone governs stale day rather than host zone or UTC day", () => {
    var snapshot = Sum(Normal()) with { UpdatedAt = DateTimeOffset.Parse("2026-10-23T16:01:00+00:00") };
    var projected = new DailyPresentationState { Snapshot = snapshot }.Project(snapshot.UpdatedAt, zone);
    Equal(new DateOnly(2026, 10, 24), projected.CurrentDay, "UTC+8 current date");
    Need(projected.IsStale, "same UTC day masked local midnight");
});
Check("Initial failure and reading states coexist without invented snapshot", () => {
    var state = new DailyPresentationState { IsBusy = true, RefreshFailed = true, LastAttemptAt = now };
    var projected = state.Project(now, zone);
    Need(projected.Snapshot is null && projected.DisplayActiveTurns is null && projected.DisplayWeekly is null, "empty state invented values");
    Need(projected.State.IsBusy && projected.State.RefreshFailed, "activity erased failure reason");
});
Check("Presentation issues coexist with immutable source diagnostics", () => {
    var snapshot = Sum(Normal(), true);
    var issue = new DailyIssue("settings.read_failed");
    var projected = new DailyPresentationState { Snapshot = snapshot, Issues = [issue] }.Project(now, zone);
    Need(projected.Issues.Any(x => x.Code == "source.partial") && projected.Issues.Contains(issue), "issue layers were overwritten");
    Need(!snapshot.Issues.Contains(issue), "presentation mutated source snapshot");
});

Check("Concurrent manual requests coalesce to exactly one follow-up", () => {
    var coordinator = new RefreshCoordinator();
    Need(coordinator.Request(RefreshReason.Initial), "initial refresh not started");
    for (var index = 0; index < 25; index++) Need(!coordinator.Request(RefreshReason.Manual), "busy manual started overlapping work");
    Need(coordinator.IsBusy && coordinator.HasPendingRefresh, "pending manual absent");
    Need(coordinator.Complete() && coordinator.IsBusy && !coordinator.HasPendingRefresh, "first completion must start one pending follow-up");
    Need(!coordinator.Complete() && !coordinator.IsBusy, "more than one follow-up started");
});
Check("Automatic busy ticks never create a follow-up", () => {
    var coordinator = new RefreshCoordinator();
    Need(coordinator.Request(RefreshReason.Initial), "initial");
    for (var index = 0; index < 25; index++) Need(!coordinator.Request(RefreshReason.Automatic), "busy auto overlaps");
    Need(!coordinator.HasPendingRefresh && !coordinator.Complete(), "auto created a follow-up");
});
Check("Pause permits in-flight completion and explicit manual refresh only", () => {
    var coordinator = new RefreshCoordinator();
    Need(coordinator.Request(RefreshReason.Initial), "initial");
    Need(!coordinator.SetPaused(true) && coordinator.IsPaused && coordinator.IsBusy, "pause cancelled in-flight work");
    Need(!coordinator.Request(RefreshReason.Automatic), "paused auto started");
    Need(!coordinator.Complete() && !coordinator.IsBusy, "pause must not auto-refresh after completion");
    Need(coordinator.Request(RefreshReason.Manual), "paused manual must work");
    Need(!coordinator.Complete(), "manual created extra work");
});
Check("Manual pending request survives pause and resume remains one follow-up", () => {
    var coordinator = new RefreshCoordinator();
    Need(coordinator.Request(RefreshReason.Initial), "initial");
    Need(!coordinator.Request(RefreshReason.Manual), "queued manual");
    Need(!coordinator.SetPaused(true), "pause started work");
    Need(!coordinator.SetPaused(false) && coordinator.HasPendingRefresh, "resume during busy should queue one");
    Need(coordinator.Complete() && !coordinator.Complete(), "manual and resume expanded to two follow-ups");
});
Check("Resume from idle requests immediate refresh and unchanged pause state does not", () => {
    var coordinator = new RefreshCoordinator();
    Need(!coordinator.SetPaused(true), "pause start");
    Need(coordinator.SetPaused(false) && coordinator.IsBusy, "resume did not start");
    Need(!coordinator.SetPaused(false) && !coordinator.HasPendingRefresh, "duplicate unchecked event created a refresh");
    Need(!coordinator.Complete(), "duplicate resume queued");
});
Check("Exit suppresses all new work and queued follow-ups while allowing current settlement", () => {
    var coordinator = new RefreshCoordinator();
    Need(coordinator.Request(RefreshReason.Initial), "initial");
    coordinator.Request(RefreshReason.Manual); coordinator.Exit();
    Need(coordinator.IsExiting && coordinator.IsBusy && !coordinator.HasPendingRefresh, "exit state incorrect");
    foreach (var reason in Enum.GetValues<RefreshReason>()) Need(!coordinator.Request(reason), "start after exit");
    Need(!coordinator.SetPaused(false), "resume after exit");
    Need(!coordinator.Complete() && !coordinator.IsBusy, "exit queued follow-up");
});
Check("Repausing discards obsolete queued resume but preserves explicit manual intent", () => {
    var coordinator = new RefreshCoordinator();
    Need(coordinator.Request(RefreshReason.Initial), "initial");
    coordinator.SetPaused(true); coordinator.SetPaused(false);
    Need(coordinator.HasPendingRefresh, "busy resume was not queued");
    coordinator.SetPaused(true);
    Need(!coordinator.HasPendingRefresh && !coordinator.Complete(), "obsolete resume started after pause");
    Need(coordinator.Request(RefreshReason.Manual), "paused explicit manual");
    coordinator.SetPaused(false); coordinator.Request(RefreshReason.Manual); coordinator.SetPaused(true);
    Need(coordinator.HasPendingRefresh && coordinator.Complete(), "pause removed user's explicit manual request");
    Need(!coordinator.Complete(), "unexpected extra follow-up");
});

Check("Revision conflict emits stable code without changing refusal or Status", () => {
    var rows = Normal();
    var original = rows.OfType<UsageEvent>().Single();
    var value = Sum(rows.Append(original with { Usage = new(200, 120, 80, 20, 280) }));
    Need(value.InputTokens is null && value.IsPartial && value.AverageTps is null, "conflict refusal changed");
    Need(value.Issues.Any(x => x.Code == DailyIssueCodes.RequestRevisionConflict), "revision code absent");
    Need(value.Status.Contains("请求修订冲突", StringComparison.Ordinal), "legacy conflict message absent");
});
Check("Unknown legacy coverage emits code and retains independently known modern subtotal", () => {
    var other = new StatsEvent[] { new MetadataEvent("legacy-root", at, false, null),
        new UsageEvent("legacy-root", null, null, null, at, null, tokens, null, tokens, true) };
    var value = Sum(Normal().Concat(other));
    Need(value.InputTokens == 100 && value.OutputTokens == 40 && value.IsPartial, "known subtotal or legacy incompleteness changed");
    Need(value.Issues.Any(x => x.Code == DailyIssueCodes.LegacyCoverageUnknown), "legacy code absent");
});
Check("Informational active and timing coverage parameters are invariant strings", () => {
    var second = Normal().Select(row => row switch {
        MetadataEvent metadata => metadata with { SessionKey = "second" },
        StartEvent start => start with { SessionKey = "second", TurnKey = "second" },
        UsageEvent usage => usage with { SessionKey = "second", TurnKey = "second", RootTurnKey = "second", RequestKey = "second" },
        CompletionEvent completion => completion with { SessionKey = "second", TurnKey = "second", TimeToFirstTokenMs = null },
        _ => row });
    var value = Sum(Normal().Concat(second).Concat([new MetadataEvent("running", at, false, null), new StartEvent("running", "running", now.AddSeconds(-3))]));
    Need(!value.IsPartial && value.AverageTps is not null, "informational coverage changed numeric quality");
    var active = value.Issues.Single(x => x.Code == DailyIssueCodes.ObservedActive);
    Equal("1", active.Parameters["count"], "active param");
    var coverage = value.Issues.Single(x => x.Code == DailyIssueCodes.TimingCoveragePartial);
    Equal("1", coverage.Parameters["included"], "included param"); Equal("1", coverage.Parameters["excluded"], "excluded param");
    Equal(value.Issues.Count, value.Issues.Select(x => x.Code).Distinct(StringComparer.Ordinal).Count(), "diagnostics must not duplicate");
});
Check("Independent weekly windows remain unavailable with structured reason", () => {
    var value = Sum(Normal().Concat([new QuotaEvent("root", at, "first", null, new(10, 10080, now.AddDays(1))),
        new QuotaEvent("root", at, "second", null, new(20, 10080, now.AddDays(1)))]));
    Need(value.Weekly is null && !value.IsPartial, "multiple weekly semantics changed");
    Need(value.Issues.Any(x => x.Code == DailyIssueCodes.MultipleWeekly), "weekly ambiguity code absent");
});
MonitorDiagnosticsTests.Run(Check);

Console.WriteLine($"RESULT cases={cases}; passed={cases - failed}; failed={failed}");
return failed == 0 ? 0 : 1;
