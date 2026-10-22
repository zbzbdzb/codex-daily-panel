namespace CodexUsage.Core;

public sealed record TokenVector(long? Input, long? CachedInput, long? Output, long? ReasoningOutput, long? Total)
{
    public static TokenVector Zero { get; } = new(0, 0, 0, 0, 0);
    public static TokenVector Unknown { get; } = new(null, null, null, null, null);
    public bool IsComplete => Input.HasValue && CachedInput.HasValue && Output.HasValue && ReasoningOutput.HasValue && Total.HasValue;
    public bool IsValid => new[] { Input, CachedInput, Output, ReasoningOutput, Total }.All(x => x is null or >= 0)
        && !(CachedInput > Input) && !(ReasoningOutput > Output)
        && (Input is null || Output is null || Total is null || (decimal)Input + Output == Total);
    public double? CacheRatio => Input > 0 && CachedInput.HasValue ? (double)CachedInput.Value / Input.Value : null;
    public static TokenVector operator +(TokenVector a, TokenVector b) => new(Sum(a.Input,b.Input), Sum(a.CachedInput,b.CachedInput), Sum(a.Output,b.Output), Sum(a.ReasoningOutput,b.ReasoningOutput), Sum(a.Total,b.Total));
    private static long? Sum(long? a, long? b) => a.HasValue && b.HasValue && (decimal)a + b <= long.MaxValue ? a + b : null;
}

public sealed record UsageEvent(string SessionKey, string? TurnKey, string? RootTurnKey, string? RequestKey,
    DateTimeOffset Timestamp, long? Ordinal, TokenVector? Usage, TokenVector? TurnSnapshot,
    TokenVector? ThreadSnapshot, bool Legacy = false) : StatsEvent(SessionKey, Timestamp);
public sealed record StartEvent(string SessionKey, string TurnKey, DateTimeOffset Timestamp) : StatsEvent(SessionKey, Timestamp);
public sealed record CompletionEvent(string SessionKey, string TurnKey, DateTimeOffset Timestamp,
    long? DurationMs, long? TimeToFirstTokenMs) : StatsEvent(SessionKey, Timestamp);
public sealed record MetadataEvent(string SessionKey, DateTimeOffset Timestamp, bool IsChild,
    string? ParentKey) : StatsEvent(SessionKey, Timestamp);
public sealed record QuotaEvent(string SessionKey, DateTimeOffset Timestamp, string LimitKey,
    QuotaWindow? Primary, QuotaWindow? Secondary) : StatsEvent(SessionKey, Timestamp);
public abstract record StatsEvent(string SessionKey, DateTimeOffset Timestamp);
public sealed record QuotaWindow(double UsedPercent, long WindowMinutes, DateTimeOffset? ResetsAt)
{
    public double RemainingPercent => Math.Clamp(100 - UsedPercent, 0, 100);
}
public sealed record ParseResult(IReadOnlyList<StatsEvent> Events, bool Malformed = false, bool Unsupported = false);
public sealed class SourceContext(string sourceKey)
{
    public string SessionKey { get; set; } = sourceKey;
    public string? TurnKey { get; set; }
}
public sealed record RequestView(TokenVector Tokens, DateTimeOffset Timestamp);
public sealed record TurnView(string Key, TokenVector Tokens, TokenVector? Snapshot, long? DurationMs,
    long? TimeToFirstTokenMs, bool Completed, bool Incomplete, DateTimeOffset? EventTimestamp=null, DateTimeOffset? CompletionTimestamp=null, DateTimeOffset? TokenTimestamp=null)
{
    public double? AverageOutputSpeed => Completed && !Incomplete && DurationMs > 0 && Tokens.Output.HasValue ? Tokens.Output.Value / (DurationMs.Value / 1000d) : null;
}
public sealed record SessionView(string Key, string Label, bool IsChild, string? ParentKey,
    RequestView? LatestRequest, TurnView? CurrentTurn, TokenVector ThreadTokens, TokenVector? ThreadSnapshot,
    int RequestCount, bool Incomplete, IReadOnlyList<string> Issues, QuotaEvent? Quota,
    IReadOnlyList<TurnView> Turns, DateTimeOffset? LastEventTimestamp=null, DateTimeOffset? ThreadTokenTimestamp=null);
