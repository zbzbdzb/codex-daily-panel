using CodexUsage.Core;

namespace CodexUsage.Daily;

public enum DailyToolPhase { Call, Result }

public sealed record DailyToolEvent(string SessionKey, string? TurnKey, string? CallKey,
    DailyToolPhase Phase, DateTimeOffset Timestamp) : StatsEvent(SessionKey, Timestamp);

public sealed record DailyTimingUnavailableEvent(string SessionKey, string? TurnKey,
    DateTimeOffset Timestamp, DateTimeOffset? StartTimestamp = null, string? ToolCallKey = null) : StatsEvent(SessionKey, Timestamp)
{
    internal DailyToolPhase? ToolPhase { get; init; }

    // A verified source already provided the extents of this uncertain wait.
    // Keep it separate from unresolved endpoints and from session-wide gaps.
    internal DailyTimingWindow? ObservedToolWait { get; init; }
}

internal readonly record struct DailyToolEndpoint(DailyToolPhase Phase, DateTimeOffset Timestamp);

internal readonly record struct DailyTimingWindow(DateTimeOffset Start, DateTimeOffset End)
{
    internal bool IsObservedAt(DateTimeOffset now) => Start != DateTimeOffset.MinValue && End <= now;

    internal bool Overlaps(DailyTimingWindow other) => Start <= other.End && End >= other.Start;

    internal static DailyTimingWindow? FromCompletion(CompletionEvent completed)
    {
        if (completed.DurationMs is not > 0) return null;
        try { return new(completed.Timestamp.AddMilliseconds(-completed.DurationMs.Value), completed.Timestamp); }
        catch (ArgumentOutOfRangeException) { return null; }
    }

    // Callers deduplicate using the identity available at their boundary.
    internal static DailyTimingWindow? TryPair(IEnumerable<DailyToolEndpoint> endpoints, DateTimeOffset now)
    {
        var rows = endpoints.Take(3).ToArray();
        if (rows.Length != 2) return null;
        var (call, result) = rows[0].Phase == DailyToolPhase.Call ? (rows[0], rows[1]) : (rows[1], rows[0]);
        return FromOrderedEndpoints(call, result, now);
    }

    internal static DailyTimingWindow? FromOrderedEndpoints(DailyToolEndpoint call, DailyToolEndpoint result, DateTimeOffset now) =>
        call.Phase == DailyToolPhase.Call && result.Phase == DailyToolPhase.Result
            && call.Timestamp != DateTimeOffset.MinValue && call.Timestamp < result.Timestamp && result.Timestamp <= now
            ? new(call.Timestamp, result.Timestamp) : null;
}
