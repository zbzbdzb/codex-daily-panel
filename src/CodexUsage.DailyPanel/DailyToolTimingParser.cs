using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using CodexUsage.Core;
using CodexUsage.Daily;

namespace CodexUsage.DailyPanel;

/// <summary>Projects timing and abort facts with hashed identities. Active-turn tracking is
/// independent of token parser context, which late usage corrections can move to an older turn.</summary>
internal sealed class DailyToolTimingParser(string sourceKey)
{
    private const int MaximumTrackedTurns = 128;
    // Turns at the latest start timestamp, including any observed ends.
    private readonly Dictionary<string, DateTimeOffset?> _turnEnds = new(StringComparer.Ordinal);
    private string _session = sourceKey;
    private DateTimeOffset? _latestStart;
    private bool _lifecycleUncertain;
    internal string SessionKey => _session;

    public IReadOnlyList<StatsEvent> Parse(string line, IReadOnlyList<StatsEvent> statistics,
        string abortSessionKey, out bool malformedAbort)
    {
        malformedAbort = false;
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
                if (_session != metadata.SessionKey) { _turnEnds.Clear(); _latestStart = null; _lifecycleUncertain = false; }
                _session = metadata.SessionKey;
                return [];
            }
            bool abort = (outer is null or "event_msg") && kind is ("task_aborted" or "turn_aborted");
            if (!abort && outer is not ("response_item" or "event_msg")) return [];
            if (outer == "event_msg" && !abort && kind is not ("task_started" or "task_complete")) return [];
            if ((abort && outer is null) || !Unique(root) || !Unique(payload)
                || !HasSupportedVersion(root) || !HasSupportedVersion(payload))
            {
                malformedAbort = abort;
                _lifecycleUncertain |= abort || outer == "event_msg";
                // Ambiguous envelope/schema can conceal ownership outside the current active turn.
                return SessionUnavailable(Time(root));
            }
            var time = abort ? AbortTime(root) : Time(root);
            if (outer == "event_msg")
            {
                string? turn = Hash(payload, "turn_id", "turn");
                if (turn is null || time == DateTimeOffset.MinValue)
                {
                    malformedAbort = abort;
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
                        _turnEnds.Clear();
                        _latestStart = time;
                    }
                    if (_turnEnds.Count >= MaximumTrackedTurns && !_turnEnds.ContainsKey(turn))
                    {
                        _lifecycleUncertain = true;
                        // Dropped lifecycle state makes subsequent ownership uncertain session-wide.
                        return SessionUnavailable(time);
                    }
                    _turnEnds.TryAdd(turn, null);
                }
                else
                {
                    // Retain the latest eligible end for this turn so delayed
                    // tools are checked at their own timestamps. Preserve raw
                    // terminal facts for aggregation, including conflicts.
                    if (_latestStart is { } started && time >= started
                        && _turnEnds.TryGetValue(turn, out var priorEnd) && (priorEnd is null || time > priorEnd.Value))
                        _turnEnds[turn] = time;
                    if (abort) return [new DailyTurnAbortedEvent(abortSessionKey, turn, time)];
                }
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
            string? turnKey = explicitTurn ?? UniqueOwnerAt(time);
            bool knownTurn = !_lifecycleUncertain && !malformedTurn && turnKey is not null
                && _turnEnds.TryGetValue(turnKey, out var end) && (end is null || time <= end.Value);
            if (!knownTurn || callKey is null || time == DateTimeOffset.MinValue)
            {
                // Only bad call/time data with otherwise proven ownership can be limited to one turn.
                return knownTurn ? TurnUnavailable(time, turnKey!) : SessionUnavailable(time);
            }
            return [new DailyToolEvent(_session, turnKey, callKey, call ? DailyToolPhase.Call : DailyToolPhase.Result, time)];
        }
        // Unparseable data provides no dependable ownership evidence.
        catch (JsonException) { return SessionUnavailable(DateTimeOffset.MinValue); }
        catch (InvalidOperationException) { return SessionUnavailable(DateTimeOffset.MinValue); }
    }

    private string? UniqueOwnerAt(DateTimeOffset time)
    {
        string? owner = null;
        foreach (var (turn, end) in _turnEnds)
        {
            // Equal timestamps do not establish which event happened first.
            if (end is { } ended && ended < time) continue;
            if (owner is not null) return null;
            owner = turn;
        }
        return owner;
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
    private static DateTimeOffset AbortTime(JsonElement root) => Field(root, "timestamp") is { ValueKind: JsonValueKind.String } timestamp
        && timestamp.TryGetDateTimeOffset(out var time) ? time : DateTimeOffset.MinValue;
    private static bool Unique(JsonElement node) => node.ValueKind == JsonValueKind.Object
        && node.EnumerateObject().Select(p => p.Name).Distinct(StringComparer.Ordinal).Count() == node.EnumerateObject().Count();
    private static bool HasSupportedVersion(JsonElement node) => Field(node, "schema_version") is var value
        && (value.ValueKind == JsonValueKind.Undefined || value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int n) && n == 1
            || value.ValueKind == JsonValueKind.String && value.GetString() == "1");
}
