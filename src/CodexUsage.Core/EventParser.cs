using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CodexUsage.Core;

// Only allowlisted statistics are projected. Raw JSON is never retained or logged.
public sealed class EventParser
{
    public static string Key(params string?[] parts) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Concat(parts.Select(p => $"{p?.Length ?? -1}:{p}")))));
    public ParseResult Parse(string line, SourceContext context)
    {
        var previousSession=context.SessionKey; var previousTurn=context.TurnKey;
        try
        {
            using var document = JsonDocument.Parse(line.TrimStart('\uFEFF'), new JsonDocumentOptions { MaxDepth = 64 });
            var root = document.RootElement;
            // TryGetProperty chooses the last duplicate. Never choose a statistical
            // value or event identity from an ambiguous envelope.
            if (DuplicateKeys(root)) return new([], Malformed: true);
            var outer = String(root,"type");
            var payload = Object(root,"payload");
            if (outer == "event_msg" && DuplicateKeys(payload, "type")) return new([], Malformed: true);
            var kind = outer == "event_msg" ? String(payload,"type") : outer;
            if (kind is not ("session_meta" or "token_usage_record" or "token_count" or "task_complete" or "task_started")) return new([]);
            if (DuplicateStatistics(payload, kind)) return new([], Malformed: true);
            if (UnknownVersion(root) || UnknownVersion(payload)) return new([], Unsupported: true);
            var timestamp = DateTimeOffset.TryParse(String(root,"timestamp"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var time) ? time : DateTimeOffset.MinValue;
            var events = new List<StatsEvent>();
            if (kind == "session_meta")
            {
                var id = String(payload,"id");
                if (id is null) return new([], Unsupported: true);
                var source = Object(payload,"source");
                var subagent = Object(source,"subagent");
                var isChild = subagent.ValueKind != JsonValueKind.Undefined;
                var spawn = Object(subagent,"thread_spawn");
                var parent = String(spawn,"parent_thread_id");
                var parentField = Object(spawn,"parent_thread_id");
                if (isChild && (subagent.ValueKind != JsonValueKind.Object
                    || (spawn.ValueKind != JsonValueKind.Undefined && spawn.ValueKind != JsonValueKind.Object)
                    || (parentField.ValueKind != JsonValueKind.Undefined && parent is null)))
                    return new([], Unsupported: true);
                // Existing child sources may omit their parent relation. Preserve
                // known child identity without inventing an unknown parent.
                var nextSession=Key("session",id);
                if(context.SessionKey!=nextSession) context.TurnKey=null;
                context.SessionKey = nextSession;
                events.Add(new MetadataEvent(context.SessionKey,timestamp,isChild,parent is null ? null : Key("session",parent)));
            }
            else if (kind == "task_started") { context.TurnKey = HashField(payload,"turn_id"); if(context.TurnKey is { } started) events.Add(new StartEvent(context.SessionKey,started,timestamp)); else return new([],Unsupported:true); }
            else if (kind == "token_usage_record")
            {
                var thread = String(payload,"thread_id");
                if(thread is null) return new([], Unsupported:true);
                var session=String(payload,"session_id"); var turn=String(payload,"turn_id"); var parent=String(payload,"root_turn_id"); var response=String(payload,"response_id");
                context.SessionKey=Key("session",thread); context.TurnKey=turn is null ? null : Key("turn",turn);
                var identity= session is not null && turn is not null && parent is not null && response is not null ? Key("request",thread,session,turn,parent,response) : null;
                events.Add(new UsageEvent(context.SessionKey,context.TurnKey,parent is null ? null : Key("turn",parent),identity,timestamp,Number(root,"ordinal"),Tokens(Object(payload,"usage")),Tokens(Object(payload,"turn_token_usage")),Tokens(Object(payload,"thread_token_usage"))));
            }
            else if (kind == "token_count")
            {
                var info=Object(payload,"info");
                // Legacy last_token_usage has no trustworthy request identity. Keep snapshots only.
                if (info.ValueKind == JsonValueKind.Object)
                    events.Add(new UsageEvent(context.SessionKey,context.TurnKey,null,null,timestamp,Number(root,"ordinal"),Tokens(Object(info,"last_token_usage")),null,Tokens(Object(info,"total_token_usage")),true));
            }
            else if (kind == "task_complete")
            {
                var turn=HashField(payload,"turn_id");
                if(turn is null) return new([],Unsupported:true);
                events.Add(new CompletionEvent(context.SessionKey,turn,timestamp,Nonnegative(payload,"duration_ms"),Nonnegative(payload,"time_to_first_token_ms")));
            }
            var limits=Object(payload,"rate_limits");
            if (limits.ValueKind==JsonValueKind.Object)
                events.Add(new QuotaEvent(context.SessionKey,timestamp,Key("limit",String(limits,"limit_id") ?? "unknown"),Window(Object(limits,"primary")),Window(Object(limits,"secondary"))));
            return new(events);
        }
        catch(JsonException) { context.SessionKey=previousSession; context.TurnKey=previousTurn; return new([],Malformed:true); }
        catch(ArgumentException) { context.SessionKey=previousSession; context.TurnKey=previousTurn; return new([],Malformed:true); }
        catch(InvalidOperationException) { context.SessionKey=previousSession; context.TurnKey=previousTurn; return new([],Malformed:true); }
    }
    private static bool DuplicateKeys(JsonElement node, string? onlyName = null)
    {
        if (node.ValueKind != JsonValueKind.Object) return false;
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in node.EnumerateObject())
        {
            if (onlyName is not null && property.Name != onlyName) continue;
            if (!names.Add(property.Name)) return true;
        }
        return false;
    }
    private static bool DuplicateStatistics(JsonElement payload, string? kind)
    {
        if (DuplicateKeys(payload)) return true;
        // Inspect only known statistics containers, never conversation, tool or
        // other unknown nested content. Nonstatistical events return above.
        var limits = Object(payload,"rate_limits");
        if (DuplicateKeys(limits) || DuplicateKeys(Object(limits,"primary"))
            || DuplicateKeys(Object(limits,"secondary"))) return true;
        if (kind == "token_usage_record")
            return DuplicateKeys(Object(payload,"usage"))
                || DuplicateKeys(Object(payload,"turn_token_usage"))
                || DuplicateKeys(Object(payload,"thread_token_usage"));
        if (kind == "token_count")
        {
            var info = Object(payload,"info");
            return DuplicateKeys(info) || DuplicateKeys(Object(info,"last_token_usage"))
                || DuplicateKeys(Object(info,"total_token_usage"));
        }
        if (kind == "session_meta")
        {
            var source = Object(payload,"source"); var subagent = Object(source,"subagent");
            return DuplicateKeys(source) || DuplicateKeys(subagent)
                || DuplicateKeys(Object(subagent,"thread_spawn"));
        }
        return false;
    }
    private static bool UnknownVersion(JsonElement e) => Property(e,"schema_version") is var v && v.ValueKind != JsonValueKind.Undefined && v.ToString() != "1";
    private static JsonElement Property(JsonElement e,string name) => e.ValueKind==JsonValueKind.Object && e.TryGetProperty(name,out var v) ? v : default;
    private static JsonElement Object(JsonElement e,string name) => Property(e,name);
    private static string? String(JsonElement e,string name) => Property(e,name) is var v && v.ValueKind==JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetString()) ? v.GetString() : null;
    private static string? HashField(JsonElement e,string name) => String(e,name) is { } value ? Key("turn",value) : null;
    private static long? Number(JsonElement e,string name) => Property(e,name) is var v && v.ValueKind==JsonValueKind.Number && v.TryGetInt64(out var n) ? n : null;
    private static long? Nonnegative(JsonElement e,string name) => Number(e,name) is >=0 and var value ? value : null;
    private static TokenVector? Tokens(JsonElement e) => e.ValueKind != JsonValueKind.Object ? null : new(Number(e,"input_tokens"),Number(e,"cached_input_tokens"),Number(e,"output_tokens"),Number(e,"reasoning_output_tokens"),Number(e,"total_tokens"));
    private static QuotaWindow? Window(JsonElement e)
    {
        var used=Property(e,"used_percent"); var minutes=Nonnegative(e,"window_minutes");
        if (used.ValueKind!=JsonValueKind.Number || !used.TryGetDouble(out var percent) || !double.IsFinite(percent) || percent<0 || percent>100 || minutes is null or 0) return null;
        DateTimeOffset? reset=null;
        if(Nonnegative(e,"resets_at") is { } seconds) { try { reset=DateTimeOffset.FromUnixTimeSeconds(seconds); } catch(ArgumentOutOfRangeException) { } }
        return new(percent,minutes.Value,reset);
    }
}
