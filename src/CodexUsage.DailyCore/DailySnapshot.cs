using CodexUsage.Core;

namespace CodexUsage.Daily;

public sealed record WeeklyAllowance(double UsedPercent, DateTimeOffset? ResetsAt, DateTimeOffset ObservedAt);

public sealed record DailySnapshot(DateOnly Day, DateTimeOffset UpdatedAt, long? InputTokens,
    long? CachedInputTokens, long? OutputTokens, long? ReasoningTokens, double? ProcessingSeconds,
    double? ActiveSeconds, double? AverageTps, int ChatCount, int TurnCount, int ActiveTurns,
    int SourceCount, bool IsPartial, string Status, WeeklyAllowance? Weekly)
{
    public IReadOnlyList<DailyIssue> Issues { get; init; } = [];
    public int TimedTurnCount { get; init; }
    public int ExcludedTimingTurnCount { get; init; }
    public double? ExcludedWaitSeconds { get; init; }
}

// The frozen parser has no abort projection. The standalone monitor may project only
// a known abort event's hashed session/turn identity and timestamp into this type.
public sealed record DailyTurnAbortedEvent(string SessionKey, string TurnKey, DateTimeOffset Timestamp)
    : StatsEvent(SessionKey, Timestamp);
