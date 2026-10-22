using System.Text;
using System.Text.Json;
using CodexUsage.DailyPanel;
using CodexUsage.Core;
using CodexUsage.Daily;

internal static class ToolTimingTests
{
    private static readonly UTF8Encoding Utf8 = new(false, true);
    public static async Task RunAsync()
    {
        string home = Path.Combine(Path.GetTempPath(), "CodexDaily-timing-" + Guid.NewGuid().ToString("N"));
        string sessions = Path.Combine(home, "sessions");
        Directory.CreateDirectory(sessions);
        string path = Path.Combine(sessions, "rollout.jsonl");
        var start = new DateTimeOffset(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);
        string Row(string type, DateTimeOffset time, object payload) => JsonSerializer.Serialize(new { type, timestamp = time, payload }) + "\n";
        string metadata = Row("session_meta", start, new { id = "timing-session", source = "cli" });
        string began = Row("event_msg", start, new { type = "task_started", turn_id = "turn" });
        string usage = Row("token_usage_record", start.AddSeconds(9), new {
            thread_id = "timing-session", session_id = "run", turn_id = "turn", root_turn_id = "turn", response_id = "request",
            usage = new { input_tokens = 100, cached_input_tokens = 50, output_tokens = 100, reasoning_output_tokens = 0, total_tokens = 200 },
            turn_token_usage = new { input_tokens = 100, cached_input_tokens = 50, output_tokens = 100, reasoning_output_tokens = 0, total_tokens = 200 }
        });
        string complete = Row("event_msg", start.AddSeconds(10), new { type = "task_complete", turn_id = "turn", duration_ms = 10000, time_to_first_token_ms = 1000 });
        string Tool(string type, double seconds, string call = "call", string? turn = null) => Row("response_item", start.AddSeconds(seconds),
            turn is null ? new { type, call_id = call, arguments = "private body", output = "private result" } as object
                : new { type, call_id = call, turn_id = turn, arguments = "private body", output = "private result" });
        int passes = 0;
        void Check(bool condition, string name)
        {
            if (!condition) throw new InvalidOperationException("FAIL: " + name);
            passes++;
            Console.WriteLine("PASS: " + name);
        }
        void Write(string rows)
        {
            File.WriteAllText(path, rows, Utf8);
            File.SetLastWriteTimeUtc(path, start.AddMinutes(1).UtcDateTime);
        }
        async Task CheckRows(string rows, double? seconds, string name)
        {
            Write(rows);
            using var monitor = new SessionLogMonitor(home, TimeZoneInfo.Utc);
            var snapshot = await monitor.RefreshAsync(start.AddMinutes(1));
            Check(snapshot.OutputTokens == 100 && (seconds is null ? snapshot.AverageTps is null && snapshot.ProcessingSeconds is null
                : snapshot.ProcessingSeconds is { } p && Math.Abs(p - seconds.Value) < 0.000001
                    && snapshot.AverageTps is { } t && Math.Abs(t - 100 / seconds.Value) < 0.000001), name);
        }
        try
        {
            await CheckRows(metadata + began + Tool("function_call", 2.125) + Tool("function_call_output", 5.625) + usage + complete,
                5.5, "native JSONL pairs tool milliseconds and removes TTFT");
            await CheckRows(metadata + began + Tool("custom_tool_call", 2) + Tool("custom_tool_call_output", 5) + usage + complete,
                6, "custom tool calls are projected");
            await CheckRows(metadata + began + Tool("function_call", 2)
                + Row("response_item", start.AddSeconds(3), new { type = "agent_message", id = "message", author = "assistant", recipient = "all",
                    content = new[] { new { type = "text", text = "ordinary agent content" } }, internal_chat_message_metadata_passthrough = new { } })
                + Tool("function_call_output", 5) + usage + complete,
                6, "ordinary agent_message preserves TTFT and paired tool timing");
            string oldStart = Row("event_msg", start.AddSeconds(-20), new { type = "task_started", turn_id = "old-unfinished-turn" });
            await CheckRows(metadata + oldStart + began + Tool("function_call", 2) + Tool("function_call_output", 5) + usage + complete,
                6, "later start supersedes historical unfinished turn for tool ownership");
            await CheckRows(metadata + oldStart + began
                + Row("event_msg", start.AddSeconds(1), new { type = "task_complete", turn_id = "old-unfinished-turn", duration_ms = 21000, time_to_first_token_ms = 1000 })
                + Tool("function_call", 2) + Tool("function_call_output", 5) + usage + complete,
                6, "late old completion cannot clear newer active lifecycle");
            await CheckRows(metadata + began + oldStart + Tool("function_call", 2) + Tool("function_call_output", 5) + usage + complete,
                null, "out-of-order older start cannot roll active lifecycle backward");
            await CheckRows(metadata + began + Tool("function_call", 2) + Tool("function_call_output", 5) + Tool("function_call", 2)
                + Tool("function_call_output", 5) + usage + complete, 6, "replayed tool rows deduplicate");
            string priorUsage = usage.Replace("\"turn_id\":\"turn\"", "\"turn_id\":\"old-turn\"", StringComparison.Ordinal)
                .Replace("\"root_turn_id\":\"turn\"", "\"root_turn_id\":\"old-turn\"", StringComparison.Ordinal)
                .Replace("\"response_id\":\"request\"", "\"response_id\":\"old-request\"", StringComparison.Ordinal)
                .Replace("2026-10-21", "2026-10-20", StringComparison.Ordinal);
            await CheckRows(metadata + began + Tool("function_call", 2) + priorUsage + Tool("function_call_output", 5) + usage + complete,
                6, "late usage cannot change tool lifecycle association");
            await CheckRows(metadata + began + Tool("function_call", 2, turn: "other-turn") + Tool("function_call_output", 5) + usage + complete,
                null, "explicit tool turn conflicting with lifecycle excludes timing");
            await CheckRows(metadata + began + usage + complete
                + Row("event_msg", start.AddSeconds(20), new { type = "task_started", turn_id = "next-turn" })
                + Tool("function_call", 22, turn: "turn") + Tool("function_call_output", 25, turn: "turn")
                + Row("event_msg", start.AddSeconds(30), new { type = "task_complete", turn_id = "next-turn", duration_ms = 10000, time_to_first_token_ms = 1000 }),
                null, "late tools naming completed turn cannot hide behind another active turn");
            await CheckRows(metadata + began + usage + complete
                + Row("event_msg", start.AddSeconds(20), new { type = "task_started", turn_id = "next-turn" })
                + Tool("function_call", 2) + Tool("function_call_output", 5)
                + Row("event_msg", start.AddSeconds(30), new { type = "task_complete", turn_id = "next-turn", duration_ms = 10000, time_to_first_token_ms = 1000 }),
                null, "late untagged historical tool times cannot attach to newer active turn");
            foreach (string abortType in new[] { "task_aborted", "turn_aborted" })
                await CheckRows(metadata + began + usage + complete
                    + Row("event_msg", start.AddSeconds(20), new { type = "task_started", turn_id = "next-turn" })
                    + Row("event_msg", start.AddSeconds(25), new { type = abortType })
                    + Row("event_msg", start.AddSeconds(30), new { type = "task_complete", turn_id = "next-turn", duration_ms = 10000, time_to_first_token_ms = 1000 }),
                    null, $"malformed {abortType} with unknown ownership excludes completed session timing");
            await CheckRows(metadata + began + Tool("function_call", 2) + usage + complete, null, "unmatched call never becomes zero wait");
            await CheckRows(metadata + began + Tool("function_call_output", 5) + usage + complete, null, "unmatched result never becomes zero wait");
            await CheckRows(metadata + began + Tool("function_call", 2) + Tool("custom_tool_call_output", 5) + usage + complete,
                null, "different tool protocol families cannot form a matching pair");
            await CheckRows(metadata + Tool("function_call", 2) + Tool("function_call_output", 5) + began + usage + complete,
                null, "tool rows outside lifecycle invalidate session timing");
            await CheckRows(metadata + began + Tool("function_call", 2) + Tool("function_call_output", 5).Replace("\"call_id\":\"call\"", "\"call_id\":\"\"", StringComparison.Ordinal) + usage + complete,
                null, "empty call identity invalidates timing");
            await CheckRows(metadata + began + Tool("function_call", 2) + Tool("function_call_output", 5).Replace("\"timestamp\":", "\"schema_version\":2,\"timestamp\":", StringComparison.Ordinal) + usage + complete,
                null, "unsupported tool envelope version invalidates timing");
            await CheckRows(metadata + began + Tool("function_call", 2) + Tool("function_call_output", 5).Replace("\"call_id\":", "\"call_id\":\"other\",\"call_id\":", StringComparison.Ordinal) + usage + complete,
                null, "duplicate tool payload fields invalidate timing");
            await CheckRows(metadata + began + Tool("function_call", 2) + Tool("web_search_call", 5) + Tool("function_call_output", 5) + usage + complete,
                null, "unsupported tool kind cannot be treated as zero wait");
            await CheckRows(metadata + began + Row("event_msg", start, new { type = "task_started", turn_id = "another-turn" })
                + Tool("function_call", 2) + Tool("function_call_output", 5) + usage + complete, null, "ambiguous active lifecycles exclude tool timing");
            await CheckRows(metadata + began + Row("event_msg", start.AddSeconds(1), new { type = "task_aborted", turn_id = "turn" })
                + Tool("function_call", 2) + Tool("function_call_output", 5) + usage + complete, null, "aborted lifecycle cannot associate later tools");
            await CheckRows(metadata + began + Tool("function_call", 2).Replace("\"timestamp\":", "\"turn_id\":\"another-turn\",\"timestamp\":", StringComparison.Ordinal)
                + Tool("function_call_output", 5) + usage + complete, null, "root explicit turn cannot contradict active lifecycle");
            await CheckRows(metadata + began + Tool("function_call", 2) + Tool("function_call_output", 5).Replace(start.AddSeconds(5).ToString("yyyy-MM-ddTHH:mm:sszzz"), "invalid-time", StringComparison.Ordinal) + usage + complete,
                null, "missing usable tool timestamp invalidates timing");
            string hugeOutput = new string('x', 5 * 1024 * 1024);
            await CheckRows(metadata + began + Tool("function_call", 2)
                + Row("response_item", start.AddSeconds(5), new { type = "function_call_output", call_id = "call", output = hugeOutput }) + usage + complete,
                6, "oversized tool result retains timing without body");
            await CheckRows(metadata + began + Tool("function_call", 2)
                + Row("response_item", start.AddSeconds(5), new { output = hugeOutput, call_id = "call", type = "function_call_output" }) + usage + complete,
                6, "oversized tool fields after body are projected");
            await CheckRows(metadata + began + Tool("function_call", 2)
                + Row("response_item", start.AddSeconds(5), new { schema_version = 1, type = "function_call_output", call_id = "call", output = hugeOutput }) + usage + complete,
                6, "oversized supported numeric schema version is retained");
            await CheckRows(metadata + began + Tool("function_call", 2)
                + Row("response_item", start.AddSeconds(5), new { type = "function_call_output", output = hugeOutput }).TrimEnd('\n').TrimEnd('}')
                + ",\"extra\":{\"call_id\":\"call\"}}\n" + usage + complete,
                null, "oversized sibling object cannot supply tool identity");
            await CheckRows(metadata + began + Row("response_item", start.AddSeconds(5), new { type = "message", content = hugeOutput }) + usage + complete,
                9, "oversized ordinary message does not discard reliable timing");
            await CheckRows(metadata + began + Tool("function_call", 2)
                + Row("response_item", start.AddSeconds(3), new { type = "agent_message", id = "message", author = "assistant", recipient = "all",
                    content = new[] { new { type = "text", text = hugeOutput } }, internal_chat_message_metadata_passthrough = new { } })
                + Tool("function_call_output", 5) + usage + complete,
                6, "oversized agent_message preserves paired tool timing without retaining content");
            await CheckRows(metadata + began + Row("response_item", start.AddSeconds(5), new { type = "message", content = hugeOutput })
                .Replace("\"timestamp\":", "\"schema_version\":2,\"timestamp\":", StringComparison.Ordinal) + usage + complete,
                null, "oversized ordinary message with unsupported root version excludes timing");
            await CheckRows(metadata + began + Row("response_item", start.AddSeconds(5), new { schema_version = 2, type = "message", content = hugeOutput }) + usage + complete,
                null, "oversized ordinary message with unsupported payload version excludes timing");
            await CheckRows(metadata + began + Row("response_item", start.AddSeconds(5), new { content = hugeOutput }) + usage + complete,
                null, "oversized unknown payload cannot imply no tool waits");
            await CheckRows(metadata + began + Tool("function_call", 2)
                + Row("response_item", start.AddSeconds(3), new { type = "additional_tools", id = "definitions", role = "system",
                    tools = new[] { new { type = "function", name = "ordinary_tool_definition" } } })
                + Tool("function_call_output", 5) + usage + complete,
                6, "additional_tools definitions preserve paired execution timing");
            await CheckRows(metadata + began + Tool("function_call", 2)
                + Row("response_item", start.AddSeconds(3), new { type = "additional_tools", id = "definitions", role = "system",
                    tools = new[] { new { type = "function", description = hugeOutput } } })
                + Tool("function_call_output", 5) + usage + complete,
                6, "oversized additional_tools definitions preserve timing without retaining definitions");
            var context = new SourceContext("source");
            var parser = new EventParser();
            var timing = new DailyToolTimingParser("source");
            timing.Parse(metadata, parser.Parse(metadata, context).Events);
            timing.Parse(began, parser.Parse(began, context).Events);
            var projected = timing.Parse(Tool("function_call", 2), []);
            var projectedTool = projected.OfType<DailyToolEvent>().Single();
            string serialized = JsonSerializer.Serialize(projectedTool);
            Check(projectedTool.SessionKey == EventParser.Key("session", "timing-session")
                && projectedTool.TurnKey == EventParser.Key("turn", "turn") && projectedTool.CallKey == EventParser.Key("tool-call", "function", "call")
                && !serialized.Contains("private", StringComparison.Ordinal) && !serialized.Contains("timing-session", StringComparison.Ordinal),
                "tool projection contains hashed identities and no bodies");
            Write(metadata + began + Tool("function_call", 2) + Tool("function_call_output", 5) + usage + complete);
            using (var monitor = new SessionLogMonitor(home, TimeZoneInfo.Utc))
            {
                var first = await monitor.RefreshAsync(start.AddMinutes(1));
                Check(first.AverageTps is not null, "complete source exposes adjusted timing");
                File.AppendAllText(path, "{\"type\":\"response_item\"", Utf8);
                var pending = await monitor.RefreshAsync(start.AddMinutes(1));
                Check(pending.OutputTokens == 100 && pending.AverageTps is null, "pending source suppresses provisional adjusted timing");
                File.AppendAllText(path, ",\"payload\":{\"type\":\"message\"}}\n", Utf8);
                var drained = await monitor.RefreshAsync(start.AddMinutes(1));
                Check(drained.AverageTps is not null, "completed ordinary append clears provisional timing exclusion");
                Write(metadata + began + usage + complete);
                var reset = await monitor.RefreshAsync(start.AddMinutes(1));
                Check(reset.IsPartial && reset.ProcessingSeconds is null && reset.AverageTps is null,
                    "file replacement cannot turn lost tool timing into a trusted zero-wait estimate");
            }
            Console.WriteLine($"TOOL TIMING RESULT: {passes} passed, 0 failed");
        }
        finally { Directory.Delete(home, recursive: true); }
    }
}
