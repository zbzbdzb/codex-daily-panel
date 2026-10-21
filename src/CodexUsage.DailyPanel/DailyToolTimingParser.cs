using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using CodexUsage.Core;
using CodexUsage.Daily;

namespace CodexUsage.DailyPanel;

/// <summary>Projects only hashed identities and tool timestamps. The lifecycle is deliberately
/// independent of token parser context, which late usage corrections can move to an older turn.</summary>
internal sealed class DailyToolTimingParser(string sourceKey)
{
    private const int MaximumActiveTurns = 128;
    private readonly HashSet<string> _active = new(StringComparer.Ordinal);
    private string _session = sourceKey;
    private DateTimeOffset? _latestStart;
    private bool _lifecycleUncertain;
    internal string SessionKey => _session;

    public IReadOnlyList<StatsEvent> Parse(string line, IReadOnlyList<StatsEvent> statistics)
    {
        try
        {
            using var document = JsonDocument.Parse(line.TrimStart('\uFEFF'), new JsonDocumentOptions { MaxDepth = 64 });
            var root = document.RootElement;
            string? outer = Text(root, "type");
            var payload = Field(root, "payload");
            string? kind = Text(payload, "type");
            if (outer == "session_meta")
            {
                var metadata = statistics.OfType<MetadataEvent>().SingleOrDefault();
                // Invalid metadata can change session identity, so it has no reliable turn scope.
                if (metadata is null) return SessionUnavailable(DateTimeOffset.MinValue);
                if (_session != metadata.SessionKey) { _active.Clear(); _latestStart = null; _lifecycleUncertain = false; }
                _session = metadata.SessionKey;
                return [];
            }
            if (outer is not ("response_item" or "event_msg")) return [];
            if (outer == "event_msg" && kind is not ("task_started" or "task_complete" or "task_aborted" or "turn_aborted")) return [];
            if (!Unique(root) || !Unique(payload) || !Version(root) || !Version(payload))
            {
                _lifecycleUncertain |= outer == "event_msg";
                // Ambiguous envelope/schema can conceal ownership outside the current active turn.
                return SessionUnavailable(Time(root));
            }
            var time = Time(root);
            if (outer == "event_msg")
            {
                string? turn = Hash(payload, "turn_id", "turn");
                if (turn is null || time == DateTimeOffset.MinValue)
                {
                    _lifecycleUncertain = true;
                    // A lifecycle row without identity/time may refer to any turn in this session.
                    return SessionUnavailable(time);
                }
                if (kind == "task_started")
                {
                    if (_latestStart is { } latest && time < latest)
                    {
                        // A late historical start cannot move the current lifecycle backwards.
                        return SessionUnavailable(time);
                    }
                    if (_latestStart is null || time > _latestStart)
                    {
                        // Root-turn starts are successors: an older turn may have no completion
                        // after compaction/interruption. Same-time distinct starts remain ambiguous.
                        _active.Clear();
                        _latestStart = time;
                    }
                    if (_active.Count >= MaximumActiveTurns && !_active.Contains(turn))
                    {
                        _lifecycleUncertain = true;
                        // Dropped lifecycle state makes subsequent ownership uncertain session-wide.
                        return SessionUnavailable(time);
                    }
                    _active.Add(turn);
                }
                else _active.Remove(turn);
                return [];
            }
            if (kind is "message" or "agent_message" or "additional_tools" or "reasoning" or "compaction" or "compaction_summary") return [];
            bool call = kind is "function_call" or "custom_tool_call";
            bool result = kind is "function_call_output" or "custom_tool_call_output";
            // Future/unknown item kinds may introduce tool ownership rules we cannot interpret.
            if (!call && !result) return SessionUnavailable(time);
            // A delayed historical tool row cannot inherit the current turn's identity, even
            // when it is the only active turn. Its original owner is no longer established.
            if (_latestStart is { } latestStart && time < latestStart) return SessionUnavailable(time);
            string family = kind!.StartsWith("custom_", StringComparison.Ordinal) ? "custom" : "function";
            string? callKey = Text(payload, "call_id") is { } callId ? EventParser.Key("tool-call", family, callId) : null;
            string? payloadTurn = Hash(payload, "turn_id", "turn");
            string? rootTurn = Hash(root, "turn_id", "turn");
            string? explicitTurn = payloadTurn ?? rootTurn;
            bool malformedTurn = Field(payload, "turn_id").ValueKind != JsonValueKind.Undefined && payloadTurn is null
                || Field(root, "turn_id").ValueKind != JsonValueKind.Undefined && rootTurn is null
                || rootTurn is not null && payloadTurn is not null && rootTurn != payloadTurn;
            string? turnKey = explicitTurn ?? (_active.Count == 1 ? _active.Single() : null);
            if (_lifecycleUncertain || malformedTurn || turnKey is null || !_active.Contains(turnKey)
                || callKey is null || time == DateTimeOffset.MinValue)
            {
                bool knownTurn = !_lifecycleUncertain && !malformedTurn && turnKey is not null && _active.Contains(turnKey);
                // Only bad call/time data with otherwise proven ownership can be limited to one turn.
                return knownTurn ? TurnUnavailable(time, turnKey!) : SessionUnavailable(time);
            }
            return [new DailyToolEvent(_session, turnKey, callKey, call ? DailyToolPhase.Call : DailyToolPhase.Result, time)];
        }
        // Unparseable data provides no dependable ownership evidence.
        catch (JsonException) { return SessionUnavailable(DateTimeOffset.MinValue); }
        catch (InvalidOperationException) { return SessionUnavailable(DateTimeOffset.MinValue); }
    }

    private IReadOnlyList<StatsEvent> SessionUnavailable(DateTimeOffset time) =>
        [new DailyTimingUnavailableEvent(_session, null, time)];
    private IReadOnlyList<StatsEvent> TurnUnavailable(DateTimeOffset time, string turn) =>
        [new DailyTimingUnavailableEvent(_session, turn, time)];
    private static JsonElement Field(JsonElement node, string name) => node.ValueKind == JsonValueKind.Object && node.TryGetProperty(name, out var value) ? value : default;
    private static string? Text(JsonElement node, string name) => Field(node, name) is var field && field.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(field.GetString()) ? field.GetString() : null;
    private static string? Hash(JsonElement node, string name, string category) => Text(node, name) is { } value ? EventParser.Key(category, value) : null;
    private static DateTimeOffset Time(JsonElement root) => Text(root, "timestamp") is { } text
        && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var time) ? time : DateTimeOffset.MinValue;
    private static bool Unique(JsonElement node) => node.ValueKind == JsonValueKind.Object
        && node.EnumerateObject().Select(p => p.Name).Distinct(StringComparer.Ordinal).Count() == node.EnumerateObject().Count();
    private static bool Version(JsonElement node) => Field(node, "schema_version") is var value
        && (value.ValueKind == JsonValueKind.Undefined || value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int n) && n == 1
            || value.ValueKind == JsonValueKind.String && value.GetString() == "1");
}
