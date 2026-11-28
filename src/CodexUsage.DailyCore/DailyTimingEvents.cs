using CodexUsage.Core;

namespace CodexUsage.Daily;

public enum DailyToolPhase { Call, Result }

public sealed record DailyToolEvent(string SessionKey, string? TurnKey, string? CallKey,
    DailyToolPhase Phase, DateTimeOffset Timestamp) : StatsEvent(SessionKey, Timestamp);

public sealed record DailyTimingUnavailableEvent(string SessionKey, string? TurnKey,
    DateTimeOffset Timestamp, DateTimeOffset? StartTimestamp = null, string? ToolCallKey = null) : StatsEvent(SessionKey, Timestamp)
{
    // A verified source already provided the extents of this uncertain wait.
    // Keep it separate from unresolved endpoints and from session-wide gaps.
    internal DailyTimingWindow? ObservedToolWait { get; init; }
}

internal readonly record struct DailyTimingWindow(DateTimeOffset Start, DateTimeOffset End)
{
    internal bool IsObservedAt(DateTimeOffset now) => Start != DateTimeOffset.MinValue && End <= now;

    internal bool Overlaps(DailyTimingWindow other) => Start <= other.End && End >= other.Start;

    internal DailyTimingWindow Include(DailyTimingWindow other) =>
        new(Start < other.Start ? Start : other.Start, End > other.End ? End : other.End);

    internal static DailyTimingWindow? FromCompletion(CompletionEvent completed)
    {
        if (completed.DurationMs is not > 0) return null;
        try { return new(completed.Timestamp.AddMilliseconds(-completed.DurationMs.Value), completed.Timestamp); }
        catch (ArgumentOutOfRangeException) { return null; }
    }

    internal static Dictionary<(string Session, string Call), DailyTimingWindow> UncertainCalls(
        IEnumerable<DailyTimingUnavailableEvent> unavailable, IEnumerable<DailyToolEvent> tools, DateTimeOffset now)
    {
        var scoped = unavailable.Where(x => x.ObservedToolWait is null && x.StartTimestamp.HasValue
            && x.ToolCallKey is not null && x.Timestamp != DateTimeOffset.MinValue && x.Timestamp <= now)
            .Select(x => (Session: x.SessionKey, Call: x.ToolCallKey!, x.Timestamp)).ToArray();
        var calls = scoped.Select(x => (x.Session, x.Call)).ToHashSet();
        if (calls.Count == 0) return [];
        return scoped.Concat(tools.Where(x => x.CallKey is not null && x.Timestamp != DateTimeOffset.MinValue
                && x.Timestamp <= now && calls.Contains((x.SessionKey, x.CallKey)))
            .Select(x => (Session: x.SessionKey, Call: x.CallKey!, x.Timestamp)))
            .GroupBy(x => (x.Session, x.Call)).ToDictionary(group => group.Key,
                group => new DailyTimingWindow(group.Min(x => x.Timestamp), group.Max(x => x.Timestamp)));
    }
}
