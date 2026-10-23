using System.Collections.ObjectModel;

namespace CodexUsage.Daily;

/// <summary>
/// Language-neutral diagnostic emitted where the reason is established.
/// Informational codes do not imply IsPartial; that existing field retains its meaning.
/// Parameters contain bounded presentation values, never log text or source paths.
/// </summary>
public sealed record DailyIssue(string Code)
{
    public IReadOnlyDictionary<string, string> Parameters { get; init; } =
        new ReadOnlyDictionary<string, string>(new Dictionary<string, string>());

    public static DailyIssue With(string code, params (string Key, string Value)[] parameters) => new(code)
    {
        Parameters = new ReadOnlyDictionary<string, string>(parameters.ToDictionary(x => x.Key, x => x.Value, StringComparer.Ordinal))
    };
}

public static class DailyIssueCodes
{
    public const string SourcePartial = "source.partial";
    public const string MetadataConflict = "session.metadata_conflict";
    public const string MetadataMissing = "session.metadata_missing";
    public const string TimestampMissing = "event.timestamp_missing";
    public const string TimestampFuture = "event.timestamp_future";
    public const string RequestIdentityMissing = "request.identity_missing";
    public const string RequestRevisionConflict = "request.revision_conflict";
    public const string RequestTokensInvalid = "request.tokens_invalid";
    public const string TokenFieldsMissing = "tokens.fields_missing";
    public const string TokenOverflow = "tokens.overflow";
    public const string LegacyCoverageUnknown = "legacy.coverage_unknown";
    public const string CurrentStartConflict = "activity.start_conflict";
    public const string TurnTimeConflict = "timing.event_conflict";
    public const string HistoricalClosureMissing = "activity.closure_missing";
    public const string HistoricalStartMissing = "activity.start_missing";
    public const string DurationMismatch = "timing.duration_mismatch";
    public const string CompletionReconciliation = "timing.reconciliation";
    public const string LocalRootScope = "scope.local_root";
    public const string NoTokens = "data.no_tokens";
    public const string ObservedActive = "activity.observed";
    public const string EstimatedTps = "timing.estimated";
    public const string TimingCoveragePartial = "timing.coverage_partial";
    public const string TimingUnavailable = "timing.unavailable";
    public const string MultipleWeekly = "quota.multiple_windows";
    public const string SourceNotFound = "source.not_found";
    public const string SourceLoading = "source.loading";
    public const string SourceOversized = "source.oversized";
    public const string SourceUnreadable = "source.unreadable";
    public const string CoverageLost = "source.coverage_lost";
    public const string RefreshFailed = "ui.refresh_failed";
    public const string StaleDay = "ui.stale_day";
    public const string WeeklyExpired = "quota.expired";
    public const string ActivityObservationExpired = "activity.observation_expired";
    public const string SnapshotTimeFuture = "ui.snapshot_future";
    public const string WeeklyObservationFuture = "quota.observation_future";
}
