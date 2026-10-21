using CodexUsage.Core;
using CodexUsage.Daily;
using System.Text.Json;

internal static class LegacyIntegrityTests
{
    public static void Run(Action<string, Action> test)
    {
        test("LEG-01 A modern + B legacy: known input=100/output=40; Partial=true; legacy coverage unknown", () =>
        {
            var at = DateTimeOffset.Parse("2026-10-21T04:00:00Z");
            var vector = new TokenVector(100, 60, 40, 10, 140);
            var parser = new EventParser();
            var context = new SourceContext("synthetic-source-B");
            var legacy = parser.Parse("""
                {"type":"session_meta","timestamp":"2026-10-21T03:00:00Z","payload":{"id":"synthetic-B","source":"cli"}}
                """, context).Events.Concat(parser.Parse("""
                {"type":"event_msg","timestamp":"2026-10-21T04:00:01Z","payload":{"type":"token_count","info":{"last_token_usage":{"input_tokens":900,"cached_input_tokens":500,"output_tokens":100,"reasoning_output_tokens":20,"total_tokens":1000},"total_token_usage":{"input_tokens":900,"cached_input_tokens":500,"output_tokens":100,"reasoning_output_tokens":20,"total_tokens":1000}}}}
                """, context).Events);
            StatsEvent[] modern = [new MetadataEvent("synthetic-A", at.AddHours(-1), false, null),
                new UsageEvent("synthetic-A", "turn-A", "turn-A", "request-A", at, 1, vector, vector, vector)];
            var zone = TimeZoneInfo.CreateCustomTimeZone("IntegrityUTC8", TimeSpan.FromHours(8), "UTC+8", "UTC+8");
            var result = DailyAggregator.Summarize(modern.Concat(legacy), new DateOnly(2026, 10, 21), zone, at.AddMinutes(1));
            if (result.InputTokens != 100 || result.CachedInputTokens != 60 || result.OutputTokens != 40 || result.ReasoningTokens != 10)
                throw new Exception("Known modern totals must remain input=100/cached=60/output=40/reasoning=10; no legacy estimate may be added.");
            if (!result.IsPartial || !result.Status.Contains("旧格式", StringComparison.Ordinal))
                throw new Exception("B's unreconstructable legacy usage must mark the daily total incomplete with a legacy reason.");
        });

        var modern = Project(Meta("A"), Modern("A", "turn-A", "r-A", At, 1, A, A));
        Case(test, "LEG-02 pure modern unchanged", modern, A, false, null);
        Case(test, "LEG-03 modern replay and shuffled projected input", modern.Concat(modern).Reverse(), A, false, null);
        Case(test, "LEG-04 conflicting request revision stays rejected",
            Project(Meta("A"), Modern("A", "turn-A", "r-A", At, 1, A, A),
                Modern("A", "turn-A", "r-A", At, 1, B, B)), null, true, "请求修订冲突");
        Case(test, "LEG-05 pure legacy stays unknown",
            Project(Meta("A"), Start("turn-A", At.AddSeconds(-1)), Legacy(A, A, At, 1)), null, true, "旧格式");
        Case(test, "LEG-06 another turn in same session cannot hide legacy",
            Project(Meta("A"), Modern("A", "turn-A", "r-A", At, 1, A, A),
                Start("turn-B", At.AddSeconds(1)), Legacy(A, A, At.AddSeconds(2), 3)), A, true, "覆盖关系未知");
        Case(test, "LEG-07 complete matching aggregate checkpoint is covered",
            Project(Meta("A"), Modern("A", "turn-A", "r-A", At, 1, A, A),
                Legacy(A, A, At.AddSeconds(1), 2)), A, false, null);
        Case(test, "LEG-08 matching request values without thread snapshot prove nothing",
            Project(Meta("A"), Modern("A", "turn-A", "r-A", At, 1, A, null),
                Legacy(A, A, At.AddSeconds(1), 2)), A, true, "覆盖关系未知");
        Case(test, "LEG-09 matching snapshots without full modern history prove nothing",
            Project(Meta("A"), Modern("A", "turn-A", "r-A", At, 1, A, AB),
                Legacy(A, AB, At.AddSeconds(1), 2)), A, true, "覆盖关系未知");
        Case(test, "LEG-10 unknown contextual turn is not invented across sources",
            modern.Concat(Project(Meta("A"), Legacy(A, A, At.AddSeconds(1), 2))), A, true, "覆盖关系未知");
        Case(test, "LEG-11 a historical prefix can reconcile today's mirror",
            Project(Meta("A", at: At.AddDays(-1).AddHours(-1)),
                Modern("A", "yesterday", "old", At.AddDays(-1), 1, B, B),
                Modern("A", "turn-A", "r-A", At, 2, A, AB), Legacy(A, AB, At.AddSeconds(1), 3)), A, false, null);
        Case(test, "LEG-12 later exact replay does not move checkpoint evidence",
            Project(Meta("A"), Modern("A", "turn-A", "r-A", At, 1, A, A), Legacy(A, A, At.AddSeconds(1), 2),
                Modern("A", "turn-A", "r-A", At.AddSeconds(2), 3, A, A)), A, false, null);
        Case(test, "LEG-13 later thread-only correction invalidates old coverage evidence",
            Project(Meta("A"), Modern("A", "turn-A", "r-A", At, 1, A, A), Legacy(A, A, At.AddSeconds(1), 2),
                Modern("A", "turn-A", "r-A", At.AddSeconds(2), 3, A, AB)), A, true, "覆盖关系未知");
        Case(test, "LEG-14 modern counter revision never backfills legacy",
            Project(Meta("A"), Modern("A", "turn-A", "r-A", At, 1, A, A), Legacy(A, A, At.AddSeconds(1), 2),
                Modern("A", "turn-A", "r-A", At.AddSeconds(2), 3, B, B)), B, true, "覆盖关系未知");
        Case(test, "LEG-15 yesterday's legacy does not poison today",
            modern.Concat(Project(Meta("B"), Legacy(B, B, At.AddDays(-1), 1))), A, false, null);
        Case(test, "LEG-16 child legacy does not enter root completeness",
            modern.Concat(Project(Meta("B", child: true), Legacy(B, B, At, 1))), A, false, null);
        Case(test, "LEG-17 cross-day revision keeps original occurrence day",
            Project(Meta("A", at: At.AddDays(-1).AddHours(-1)),
                Modern("A", "yesterday", "old", At.AddDays(-1), 1, A, A),
                Modern("A", "yesterday", "old", At, 2, B, B)), null, false, null);
        Case(test, "LEG-18 covered replay of yesterday request invents no today tokens",
            Project(Meta("A", at: At.AddDays(-1).AddHours(-1)),
                Modern("A", "yesterday", "old", At.AddDays(-1), 1, A, A),
                Modern("A", "yesterday", "old", At, 2, B, B), Legacy(B, B, At.AddSeconds(1), 3)), null, false, null);
        Case(test, "LEG-19 missing metadata reason survives known modern subtotal",
            modern.Concat([new UsageEvent("unknown-root", null, null, null, At, 2, B, null, B, true)]),
            A, true, "缺少会话元数据");
        Case(test, "LEG-20 scanner partial survives a proven mirror",
            Project(Meta("A"), Modern("A", "turn-A", "r-A", At, 1, A, A), Legacy(A, A, At.AddSeconds(1), 2)),
            A, true, "来源仍在读取", sourcePartial: true);
        Case(test, "LEG-21 invalid legacy last vector cannot be hidden by a valid total snapshot",
            Project(Meta("A"), Modern("A", "turn-A", "r-A", At, 1, A, A),
                Legacy(new(-1, 0, 1, 0, 0), A, At.AddSeconds(1), 2)), A, true, "覆盖关系未知");
        Case(test, "LEG-22 unordered equal-time snapshot boundary stays unknown",
            Project(Meta("A"), Modern("A", "turn-A", "r-A", At, null, A, A), Legacy(A, A, At, null)),
            A, true, "覆盖关系未知");
        Case(test, "LEG-23 a legacy checkpoint before first modern occurrence is not guessed covered",
            Project(Meta("A"), Start("turn-A", At.AddSeconds(-2)), Legacy(A, A, At.AddSeconds(-1), 1),
                Modern("A", "turn-A", "r-A", At, 2, A, A)), A, true, "覆盖关系未知");
        Case(test, "LEG-24 mirror exact replay and unordered aggregation do not duplicate or warn",
            Project(Meta("A"), Modern("A", "turn-A", "r-A", At, 1, A, A), Legacy(A, A, At.AddSeconds(1), 2))
                .Concat(Project(Meta("A"), Modern("A", "turn-A", "r-A", At, 1, A, A), Legacy(A, A, At.AddSeconds(1), 2))).Reverse(),
            A, false, null);

        test("LEG-25 frozen parser contract: legacy has snapshots and contextual turn, no request/root/turn snapshot", () =>
        {
            var projected = Project(Meta("A"), Start("turn-A", At.AddSeconds(-1)), Legacy(A, AB, At, 2))
                .OfType<UsageEvent>().Single();
            if (!projected.Legacy || projected.RequestKey is not null || projected.RootTurnKey is not null || projected.TurnSnapshot is not null
                || projected.Usage != A || projected.ThreadSnapshot != AB || projected.TurnKey != EventParser.Key("turn", "turn-A"))
                throw new Exception("The baseline DLL does not project the documented legacy snapshot contract.");
        });
        Case(test, "LEG-26 different-request ordinal inversion leaves coverage unknown",
            Project(Meta("A"), Modern("A", "turn-A", "r-A", At, 10, A, A),
                Modern("A", "turn-A", "r-B", At.AddSeconds(1), 5, B, AB), Legacy(A, A, At.AddSeconds(2), 20)),
            AB, true, "覆盖关系未知");
        Case(test, "LEG-27 ambiguous equal-time prefix cannot prove a checkpoint",
            Project(Meta("A"), Modern("A", "turn-A", "r-A", At, null, A, A),
                Modern("A", "turn-A", "r-B", At, 2, B, AB), Legacy(B, AB, At.AddSeconds(1), 3)),
            AB, true, "覆盖关系未知");
        Case(test, "LEG-28 known equal-time ordinals reconcile a multi-request prefix",
            Project(Meta("A"), Modern("A", "turn-A", "r-A", At, 1, A, A),
                Modern("A", "turn-A", "r-B", At, 2, B, AB), Legacy(B, AB, At.AddSeconds(1), 3)),
            AB, false, null);
        Case(test, "LEG-29 incomplete legacy fields do not inherit complete coverage",
            Project(Meta("A"), Modern("A", "turn-A", "r-A", At, 1, A, A),
                Legacy(new(100, null, 40, 10, 140), A, At.AddSeconds(1), 2)), A, true, "覆盖关系未知");
        Case(test, "LEG-30 an ordinal-free checkpoint cannot hide a known prefix-to-legacy inversion",
            Project(Meta("A"), Modern("A", "turn-A", "r-A", At, 10, A, A),
                Modern("A", "turn-A", "r-zero", At.AddSeconds(1), null, TokenVector.Zero, A),
                Legacy(A, A, At.AddSeconds(2), 5)), A, true, "覆盖关系未知");
        Case(test, "LEG-31 each equal-position batch member supplies its own turn and snapshot",
            Project(Meta("A"), Modern("A", "turn-A", "r-A", At, 1, A, A),
                Modern("A", "turn-B", "r-B", At, 1, B, AB), Legacy(B, AB, At.AddSeconds(1), 2)),
            AB, false, null);
        Case(test, "LEG-32 an ordinal-free checkpoint cannot hide an inversion inside the prefix",
            Project(Meta("A"), Modern("A", "turn-A", "r-A", At, 10, A, A),
                Modern("A", "turn-A", "r-B", At.AddSeconds(1), 5, B, AB),
                Modern("A", "turn-A", "r-zero", At.AddSeconds(2), null, TokenVector.Zero, AB),
                Legacy(B, AB, At.AddSeconds(3), 20)), AB, true, "覆盖关系未知");
        Case(test, "LEG-33 a later thread revision cannot hide first-occurrence ordinal inversion",
            Project(Meta("A"), Modern("A", "turn-A", "r-A", At, 10, A, A),
                Modern("A", "turn-A", "r-B", At.AddSeconds(1), 5, B, AB),
                Modern("A", "turn-A", "r-A", At.AddSeconds(2), 20, A, AB),
                Legacy(B, AB, At.AddSeconds(3), 30)), AB, true, "覆盖关系未知");
        Case(test, "LEG-34 a later correction cannot hide an intermediate cross-request inversion",
            Project(Meta("A"), Modern("A", "turn-A", "r-A", At, 1, A, A),
                Modern("A", "turn-A", "r-A", At.AddSeconds(1), 10, B, B),
                Modern("A", "turn-A", "r-B", At.AddSeconds(2), 5, A, AB),
                Modern("A", "turn-A", "r-A", At.AddSeconds(3), 20, B, AB),
                Legacy(B, AB, At.AddSeconds(4), 30)), AB, true, "覆盖关系未知");
        Case(test, "LEG-35 an ordinal-free checkpoint with consistent known prefix order can cover a mirror",
            Project(Meta("A"), Modern("A", "turn-A", "r-A", At, 1, A, null),
                Modern("A", "turn-A", "r-zero", At.AddSeconds(1), null, TokenVector.Zero, A),
                Legacy(A, A, At.AddSeconds(2), 2)), A, false, null);
        Case(test, "LEG-36 a missing final ordinal cannot erase a known intermediate revision boundary",
            Project(Meta("A"), Modern("A", "turn-A", "r-A", At, 1, A, A),
                Modern("A", "turn-A", "r-A", At.AddSeconds(1), 10, B, B),
                Modern("A", "turn-A", "r-A", At.AddSeconds(2), null, A, A),
                Legacy(A, A, At.AddSeconds(3), 5)), A, true, "覆盖关系未知");
    }

    private static readonly DateTimeOffset At = DateTimeOffset.Parse("2026-10-21T02:00:00Z");
    private static readonly TimeZoneInfo Zone = TimeZoneInfo.CreateCustomTimeZone("IntegrityUTC8", TimeSpan.FromHours(8), "UTC+8", "UTC+8");
    private static readonly TokenVector A = new(100, 60, 40, 10, 140);
    private static readonly TokenVector B = new(200, 90, 80, 20, 280);
    private static readonly TokenVector AB = new(300, 150, 120, 30, 420);

    private static void Case(Action<string, Action> test, string name, IEnumerable<StatsEvent> events,
        TokenVector? expected, bool partial, string? reason, bool sourcePartial = false) =>
        test($"{name}: input={expected?.Input.ToString() ?? "unknown"}/output={expected?.Output.ToString() ?? "unknown"}; Partial={partial}; reason={reason ?? "none"}", () =>
        {
            var result = DailyAggregator.Summarize(events, new DateOnly(2026, 10, 21), Zone, At.AddHours(2), sourcePartial);
            if (result.InputTokens != expected?.Input || result.CachedInputTokens != expected?.CachedInput
                || result.OutputTokens != expected?.Output || result.ReasoningTokens != expected?.ReasoningOutput)
                throw new Exception("Modern token fields differ from the explicit expected vector.");
            if (result.IsPartial != partial) throw new Exception($"Expected IsPartial={partial}, got {result.IsPartial}.");
            if (reason is not null && !result.Status.Contains(reason, StringComparison.Ordinal))
                throw new Exception("Expected incomplete cause is missing: " + reason);
            if (!partial && result.Status.Contains("统计不完整", StringComparison.Ordinal))
                throw new Exception("A proven complete input must not carry an incomplete cause.");
        });

    private static StatsEvent[] Project(params string[] rows)
    {
        var parser = new EventParser();
        var context = new SourceContext("synthetic-integrity-source");
        return rows.SelectMany(row =>
        {
            var parsed = parser.Parse(row, context);
            if (parsed.Malformed || parsed.Unsupported) throw new Exception("Synthetic fixture violates the frozen parser contract.");
            return parsed.Events;
        }).ToArray();
    }

    private static object? Fields(TokenVector? value) => value is null ? null : new
    {
        input_tokens = value.Input, cached_input_tokens = value.CachedInput, output_tokens = value.Output,
        reasoning_output_tokens = value.ReasoningOutput, total_tokens = value.Total
    };
    private static string Meta(string session, bool child = false, DateTimeOffset? at = null) =>
        JsonSerializer.Serialize(new { type = "session_meta", timestamp = at ?? At.AddHours(-1), payload = new
        {
            id = session,
            source = child ? (object)new { subagent = new { thread_spawn = new { parent_thread_id = "A", depth = 1 } } } : "cli"
        } });
    private static string Start(string turn, DateTimeOffset at) => JsonSerializer.Serialize(new
        { type = "event_msg", timestamp = at, payload = new { type = "task_started", turn_id = turn } });
    private static string Modern(string session, string turn, string request, DateTimeOffset at, long? ordinal,
        TokenVector usage, TokenVector? thread) => JsonSerializer.Serialize(new
        {
            type = "token_usage_record", timestamp = at, ordinal, payload = new
            {
                thread_id = session, session_id = "synthetic-run", turn_id = turn, root_turn_id = turn, response_id = request,
                usage = Fields(usage), turn_token_usage = Fields(usage), thread_token_usage = Fields(thread)
            }
        });
    private static string Legacy(TokenVector? last, TokenVector? total, DateTimeOffset at, long? ordinal) => JsonSerializer.Serialize(new
    {
        type = "event_msg", timestamp = at, ordinal,
        payload = new { type = "token_count", info = new { last_token_usage = Fields(last), total_token_usage = Fields(total) } }
    });
}
