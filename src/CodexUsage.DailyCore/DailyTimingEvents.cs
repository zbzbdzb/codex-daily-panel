using CodexUsage.Core;

namespace CodexUsage.Daily;

public enum DailyToolPhase { Call, Result }

public sealed record DailyToolEvent(string SessionKey, string? TurnKey, string? CallKey,
    DailyToolPhase Phase, DateTimeOffset Timestamp) : StatsEvent(SessionKey, Timestamp);

public sealed record DailyTimingUnavailableEvent(string SessionKey, string? TurnKey,
    DateTimeOffset Timestamp) : StatsEvent(SessionKey, Timestamp);
