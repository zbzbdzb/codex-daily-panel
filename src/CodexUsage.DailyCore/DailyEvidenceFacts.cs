using System.Security.Cryptography;
using System.Text.Json;
using CodexUsage.Core;

namespace CodexUsage.Daily;

public enum DailyClosureKind { Completed, Aborted }

// A closure's identity, kind and fingerprint can exclude a contradictory turn.
// No historical duration, TTFT, counter or activity interval is retained here.
public sealed record DailyClosureFact(string Session, string Turn, DateTimeOffset At,
    DailyClosureKind Kind, string Fingerprint)
{
    public static DailyClosureFact From(CompletionEvent value) => new(value.SessionKey, value.TurnKey, value.Timestamp,
        DailyClosureKind.Completed, Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(
            value with { Timestamp = value.Timestamp.ToUniversalTime() }))));

    public static DailyClosureFact From(DailyTurnAbortedEvent value) => new(value.SessionKey, value.TurnKey, value.Timestamp,
        DailyClosureKind.Aborted, Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(
            value with { Timestamp = value.Timestamp.ToUniversalTime() }))));
}
