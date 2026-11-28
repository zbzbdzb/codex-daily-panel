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
        async Task<DailySnapshot> ReadRows(string rows)
        {
            Write(rows);
            using var monitor = new SessionLogMonitor(home, TimeZoneInfo.Utc);
            return await monitor.RefreshAsync(start.AddMinutes(1));
        }
        async Task CheckRows(string rows, double? seconds, string name)
        {
            var snapshot = await ReadRows(rows);
            Check(snapshot.OutputTokens == 100 && (seconds is null ? snapshot.AverageTps is null && snapshot.ProcessingSeconds is null
                : snapshot.ProcessingSeconds is { } p && Math.Abs(p - seconds.Value) < 0.000001
                    && snapshot.AverageTps is { } t && Math.Abs(t - 100 / seconds.Value) < 0.000001), name);
        }
        void CheckLifecycleTiming(DailySnapshot snapshot, double? seconds, bool activityConflict, double activitySeconds,
            string name, bool unreconciledSibling = false, bool terminalConflict = false)
        {
            bool timingMatches = seconds is null
                ? snapshot.ProcessingSeconds is null && snapshot.AverageTps is null && snapshot.TimedTurnCount == 0
                : snapshot.ProcessingSeconds == seconds && snapshot.AverageTps is { } t && Math.Abs(t - 40 / seconds.Value) < 0.000001
                    && snapshot.TimedTurnCount == 1;
            int excluded = (seconds is null ? 1 : 0) + (unreconciledSibling ? 1 : 0);
            Check(snapshot.InputTokens == 100 && snapshot.OutputTokens == 40 && timingMatches
                && snapshot.ActiveSeconds == activitySeconds && snapshot.ExcludedTimingTurnCount == excluded
                && snapshot.Issues.Any(issue => issue.Code == DailyIssueCodes.CurrentStartConflict) == activityConflict
                && snapshot.Issues.Any(issue => issue.Code == DailyIssueCodes.TurnTimeConflict) == terminalConflict
                && snapshot.Issues.Any(issue => issue.Code == DailyIssueCodes.CompletionReconciliation) == unreconciledSibling,
                $"{name} (net={snapshot.ProcessingSeconds}, TPS={snapshot.AverageTps}, activity={snapshot.ActiveSeconds}, status={snapshot.Status})");
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
            string siblingStart = Row("event_msg", start, new { type = "task_started", turn_id = "another-turn" });
            string tied = metadata + began + siblingStart;
            var tokens40 = new { input_tokens = 100, cached_input_tokens = 50, output_tokens = 40, reasoning_output_tokens = 0, total_tokens = 140 };
            string finish = Row("token_usage_record", start.AddSeconds(9), new
            {
                thread_id = "timing-session", session_id = "run", turn_id = "turn", root_turn_id = "turn", response_id = "request",
                usage = tokens40, turn_token_usage = tokens40
            }) + complete;
            string implicitTools = Tool("function_call", 2) + Tool("function_call_output", 5);
            string explicitTools = Tool("function_call", 2, turn: "turn") + Tool("function_call_output", 5, turn: "turn");
            foreach (string endType in new[] { "task_aborted", "turn_aborted", "task_complete" })
            {
                bool completedSibling = endType == "task_complete";
                string lateSiblingEnd = Row("event_msg", start.AddSeconds(8), new
                {
                    type = endType, turn_id = "another-turn", duration_ms = 8000, time_to_first_token_ms = 0
                });
                CheckLifecycleTiming(await ReadRows(metadata + began + finish + implicitTools + siblingStart + lateSiblingEnd),
                    null, false, 10, $"{endType}: delayed sibling start invalidates inferred tools read after completion",
                    unreconciledSibling: completedSibling);
                CheckLifecycleTiming(await ReadRows(metadata + began + implicitTools + siblingStart + lateSiblingEnd + finish),
                    null, false, 10, $"{endType}: delayed sibling start invalidates inferred tools read before completion",
                    unreconciledSibling: completedSibling);
                CheckLifecycleTiming(await ReadRows(tied + finish + implicitTools + lateSiblingEnd),
                    null, false, 10, $"{endType}: prior sibling start produces the same unavailable result",
                    unreconciledSibling: completedSibling);
                CheckLifecycleTiming(await ReadRows(metadata + began + finish + explicitTools + siblingStart + lateSiblingEnd),
                    6, false, 10, $"{endType}: delayed sibling start does not invalidate explicit tools",
                    unreconciledSibling: completedSibling);

                Write(metadata + began + finish + implicitTools + lateSiblingEnd);
                using var retained = new SessionLogMonitor(home, TimeZoneInfo.Utc);
                var refreshAt = start.AddMinutes(1);
                CheckLifecycleTiming(await retained.RefreshAsync(refreshAt), 6, false, 10,
                    $"{endType}: initial retained refresh has one established tool owner",
                    unreconciledSibling: completedSibling);
                File.AppendAllText(path, siblingStart, Utf8);
                File.SetLastWriteTimeUtc(path, refreshAt.UtcDateTime);
                CheckLifecycleTiming(await retained.RefreshAsync(refreshAt), null, false, 10,
                    $"{endType}: appending a sibling start revokes previously inferred timing without changing tokens",
                    unreconciledSibling: completedSibling);
            }

            foreach (int oldTurns in new[] { 128, 129 })
            {
                var rows = new StringBuilder(metadata);
                for (int index = 0; index < oldTurns; index++)
                {
                    string identity = $"old-{index}";
                    rows.Append(Row("event_msg", start.AddSeconds(-2), new { type = "task_started", turn_id = identity }));
                    rows.Append(Row("event_msg", start.AddSeconds(-1), new { type = "task_aborted", turn_id = identity }));
                }
                var successor = await ReadRows(rows.ToString() + began + explicitTools + finish);
                CheckLifecycleTiming(successor, 6, false, 11,
                    $"{oldTurns} ended old identities do not suppress the independently timed successor");
                Check(successor.TurnCount == oldTurns + 1, $"{oldTurns} ended old identities remain observable");
            }

            foreach (int count in new[] { 128, 129 })
            {
                var cohort = new StringBuilder(metadata + began);
                for (int index = 1; index < count; index++)
                {
                    string identity = $"peer-{index}";
                    cohort.Append(Row("event_msg", start, new { type = "task_started", turn_id = identity }));
                    cohort.Append(Row("event_msg", start.AddSeconds(1), new { type = "task_aborted", turn_id = identity }));
                }
                string unknownTools = Tool("function_call", 2, turn: "never-started")
                    + Tool("function_call_output", 5, turn: "never-started");
                CheckLifecycleTiming(await ReadRows(cohort.ToString() + unknownTools + finish), null, false, 10,
                    $"an unverified explicit tool owner cannot become zero wait in a {count}-turn cohort");
                CheckLifecycleTiming(await ReadRows(cohort.ToString() + explicitTools + finish), 6, false, 10,
                    $"a verified explicit tool owner retains timing in a {count}-turn cohort");
            }

            string successorRows = Row("event_msg", start.AddSeconds(20), new { type = "task_started", turn_id = "successor" })
                + Tool("function_call", 22, "successor-call", "successor")
                + Tool("function_call_output", 25, "successor-call", "successor")
                + Row("token_usage_record", start.AddSeconds(29), new
                {
                    thread_id = "timing-session", session_id = "run", turn_id = "successor", root_turn_id = "successor",
                    response_id = "successor-request", usage = tokens40, turn_token_usage = tokens40
                })
                + Row("event_msg", start.AddSeconds(30),
                    new { type = "task_complete", turn_id = "successor", duration_ms = 10000, time_to_first_token_ms = 1000 });
            string siblingAbort = Row("event_msg", start.AddSeconds(8),
                new { type = "task_aborted", turn_id = "another-turn" });
            foreach (bool siblingKnown in new[] { false, true })
            {
                string earlierRows = siblingKnown ? siblingStart + implicitTools + finish : implicitTools + finish + siblingStart;
                var snapshot = await ReadRows(metadata + began + earlierRows + siblingAbort + successorRows);
                Check(snapshot.InputTokens == 200 && snapshot.OutputTokens == 80 && snapshot.TimedTurnCount == 1
                    && snapshot.ExcludedTimingTurnCount == 1 && snapshot.ProcessingSeconds == 6
                    && snapshot.AverageTps is { } retainedTps && Math.Abs(retainedTps - 40d / 6) < 0.000001,
                    $"ambiguity in an earlier ordinary cohort leaves a verified successor timed (sibling known: {siblingKnown})");
            }
            CheckLifecycleTiming(await ReadRows(metadata + successorRows), 6, false, 10,
                "the successor times identically when it is the only cohort");

            string unsignedStart = JsonSerializer.Serialize(new
            {
                type = "event_msg", timestamp = "2026-10-21T12:00:00",
                payload = new { type = "task_started", turn_id = "turn" }
            }) + "\n";
            CheckLifecycleTiming(await ReadRows(metadata + unsignedStart + implicitTools + finish), 6, false, 10,
                "an offsetless start uses the same assumed UTC instant for implicit ownership and statistics");
            CheckLifecycleTiming(await ReadRows(metadata + unsignedStart + explicitTools + finish), 6, false, 10,
                "the offsetless start with explicit ownership gives the same result");
            CheckLifecycleTiming(await ReadRows(metadata + began + implicitTools + finish), 6, false, 10,
                "an explicit UTC start gives the same result");

            string earlySiblingAbort = Row("event_msg", start.AddSeconds(1),
                new { type = "task_aborted", turn_id = "another-turn" });
            string futureSiblingAbort = Row("event_msg", start.AddHours(1),
                new { type = "task_aborted", turn_id = "another-turn" });
            Write(tied + earlySiblingAbort + implicitTools + finish + futureSiblingAbort);
            using (var futureMonitor = new SessionLogMonitor(home, TimeZoneInfo.Utc))
            {
                var before = await futureMonitor.RefreshAsync(start.AddMinutes(1));
                CheckLifecycleTiming(before, 6, false, 10, "a future terminal cannot invalidate current tool timing yet");
                Check(before.Issues.Any(issue => issue.Code == DailyIssueCodes.TimestampFuture),
                    "a future terminal remains reported during the current snapshot");
                var after = await futureMonitor.RefreshAsync(start.AddHours(1).AddSeconds(1));
                CheckLifecycleTiming(after, null, true, 10,
                    "the same Monitor reassesses timing once the terminal timestamp is reached", terminalConflict: true);
                Check(!after.Issues.Any(issue => issue.Code == DailyIssueCodes.TimestampFuture),
                    "a terminal that has reached its timestamp is no longer reported as future");
            }

            var overflowRows = new StringBuilder(metadata);
            var oldBegan = start.AddSeconds(-20);
            for (int index = 0; index < 129; index++)
            {
                string identity = $"overflow-{index}";
                overflowRows.Append(Row("event_msg", oldBegan, new { type = "task_started", turn_id = identity }));
                if (index != 0)
                    overflowRows.Append(Row("event_msg", oldBegan.AddSeconds(index == 128 ? 8 : 1),
                        new { type = "task_aborted", turn_id = identity }));
            }
            overflowRows.Append(Tool("function_call", -18, "old-call"));
            overflowRows.Append(Tool("function_call_output", -15, "old-call"));
            overflowRows.Append(Row("token_usage_record", oldBegan.AddSeconds(9), new
            {
                thread_id = "timing-session", session_id = "run", turn_id = "overflow-0", root_turn_id = "overflow-0",
                response_id = "old-request", usage = tokens40, turn_token_usage = tokens40
            }));
            overflowRows.Append(Row("event_msg", oldBegan.AddSeconds(10),
                new { type = "task_complete", turn_id = "overflow-0", duration_ms = 10000, time_to_first_token_ms = 1000 }));
            var overflow = await ReadRows(overflowRows.ToString() + began + explicitTools + finish);
            Check(overflow.InputTokens == 200 && overflow.OutputTokens == 80 && overflow.TimedTurnCount == 1
                && overflow.ExcludedTimingTurnCount == 1 && overflow.ProcessingSeconds == 6
                && overflow.AverageTps is { } successorTps && Math.Abs(successorTps - 40d / 6) < 0.000001,
                "ambiguous tools in the overflowing old group are excluded while the successor retains its timing");

            foreach (string endType in new[] { "task_aborted", "turn_aborted", "task_complete" })
            {
                bool completedSibling = endType == "task_complete";
                string EndSibling(int seconds) => Row("event_msg", start.AddSeconds(seconds), new
                {
                    type = endType, turn_id = "another-turn", duration_ms = seconds * 1000, time_to_first_token_ms = 0
                });
                foreach (int endAt in new[] { 2, 3, 20 })
                {
                    string end = EndSibling(endAt);
                    foreach (bool endFirst in new[] { false, true })
                    {
                        string middle = endFirst ? end + implicitTools : implicitTools + end;
                        CheckLifecycleTiming(await ReadRows(tied + middle + finish), null, false, Math.Max(10, endAt),
                            $"{endType}: an untagged tool pair at +2/+5s with sibling end at +{endAt}s has uncertain ownership (end row first: {endFirst})",
                            unreconciledSibling: completedSibling);
                    }
                }
                string earlyEnd = EndSibling(1);
                CheckLifecycleTiming(await ReadRows(tied + earlyEnd + earlyEnd + implicitTools + finish), 6, false, 10,
                    $"{endType}: replayed early sibling end leaves one owner at both tool timestamps",
                    unreconciledSibling: completedSibling);
                CheckLifecycleTiming(await ReadRows(tied + earlyEnd + siblingStart + implicitTools + finish), 6, false, 10,
                    $"{endType}: exact sibling start replay does not erase an observed end",
                    unreconciledSibling: completedSibling);
                CheckLifecycleTiming(await ReadRows(tied + EndSibling(20) + explicitTools + finish), 6, false, 20,
                    $"{endType}: an overlapping sibling does not invalidate an explicitly owned tool pair",
                    unreconciledSibling: completedSibling);
                CheckLifecycleTiming(await ReadRows(tied + EndSibling(20) + finish + explicitTools), 6, false, 20,
                    $"{endType}: delayed explicit tools fit their own completed turn's observed interval",
                    unreconciledSibling: completedSibling);
            }
            foreach (string abortType in new[] { "task_aborted", "turn_aborted" })
            {
                string Abort(string turn, DateTimeOffset at) => Row("event_msg", at, new { type = abortType, turn_id = turn });
                var valid = Abort("another-turn", start.AddSeconds(1));
                var reversed = Abort("another-turn", start.AddMilliseconds(-1));
                var reversedMain = Abort("turn", start.AddMilliseconds(-1));
                var settledSibling = tied + valid + explicitTools;
                CheckLifecycleTiming(await ReadRows(settledSibling + reversedMain + finish), null, true, 1,
                    $"{abortType}: reversed end written before completion remains conflicting", terminalConflict: true);
                CheckLifecycleTiming(await ReadRows(settledSibling + finish + reversedMain), null, true, 1,
                    $"{abortType}: reversed end written after completion remains conflicting", terminalConflict: true);
                CheckLifecycleTiming(await ReadRows(tied + reversed + valid + explicitTools + finish), 6, true, 10,
                    $"{abortType}: reversed end before a valid end of the same kind retains the conflict", terminalConflict: true);
                CheckLifecycleTiming(await ReadRows(tied + valid + reversed + explicitTools + finish), 6, true, 10,
                    $"{abortType}: reversed end after a valid end of the same kind retains the conflict", terminalConflict: true);

                foreach (bool reverseFirst in new[] { true, false })
                {
                    Write(settledSibling + (reverseFirst ? reversedMain : finish));
                    using var retained = new SessionLogMonitor(home, TimeZoneInfo.Utc);
                    var first = await retained.RefreshAsync(start.AddMinutes(1));
                    Check(first.SourceCount == 1 && first.TurnCount == 2
                        && (reverseFirst ? first.ProcessingSeconds is null && first.IsPartial : first.ProcessingSeconds == 6 && !first.IsPartial),
                        $"{abortType}: retained reader observes the first terminal before append ({reverseFirst})");
                    File.AppendAllText(path, reverseFirst ? finish : reversedMain, Utf8);
                    File.SetLastWriteTimeUtc(path, start.AddMinutes(1).UtcDateTime);
                    CheckLifecycleTiming(await retained.RefreshAsync(start.AddMinutes(1)), null, true, 1,
                        $"{abortType}: retained reader preserves appended conflict ({reverseFirst})", terminalConflict: true);
                    CheckLifecycleTiming(await retained.RefreshAsync(start.AddMinutes(1)), null, true, 1,
                        $"{abortType}: retained reader keeps the conflict on idle refresh ({reverseFirst})", terminalConflict: true);
                }

                CheckLifecycleTiming(await ReadRows(tied + valid + implicitTools + finish), 6, false, 10,
                    $"{abortType}: valid same-turn end licenses implicit tool pairing");
                CheckLifecycleTiming(await ReadRows(tied + implicitTools + finish), null, true, 10,
                    $"{abortType}: omitted end leaves tool ownership ambiguous");
                CheckLifecycleTiming(await ReadRows(tied + reversed + implicitTools + finish), null, true, 10,
                    $"{abortType}: end earlier than that turn's start cannot license implicit tool pairing");
                CheckLifecycleTiming(await ReadRows(tied + reversed + explicitTools + finish), 6, true, 10,
                    $"{abortType}: reversed sibling end does not invalidate explicitly owned tools");
                CheckLifecycleTiming(await ReadRows(tied + valid + valid + implicitTools + finish), 6, false, 10,
                    $"{abortType}: replayed valid end preserves timing and activity closure");
                CheckLifecycleTiming(await ReadRows(metadata + oldStart + began
                    + Abort("old-unfinished-turn", start.AddSeconds(-1)) + implicitTools + finish), 6, false, 29,
                    $"{abortType}: late-arriving older turn end retains its own activity interval");

                string archive = Path.Combine(home, "archived_sessions");
                Directory.CreateDirectory(archive);
                string archivedPath = Path.Combine(archive, "abort.jsonl");
                try
                {
                    File.WriteAllText(archivedPath, metadata + valid, Utf8);
                    File.SetLastWriteTimeUtc(archivedPath, start.AddMinutes(1).UtcDateTime);
                    CheckLifecycleTiming(await ReadRows(tied + explicitTools + finish), 6, false, 10,
                        $"{abortType}: a lone valid archived end resolves activity without reassigning explicit tools");
                    CheckLifecycleTiming(await ReadRows(tied + valid + explicitTools + finish), 6, false, 10,
                        $"{abortType}: an exact archived replay of a valid end has no conflict");
                    CheckLifecycleTiming(await ReadRows(tied + reversed + explicitTools + finish), 6, true, 10,
                        $"{abortType}: a distinct valid archived end exposes conflict and preserves explicit sibling timing", terminalConflict: true);
                    File.WriteAllText(archivedPath, metadata + reversedMain, Utf8);
                    File.SetLastWriteTimeUtc(archivedPath, start.AddMinutes(1).UtcDateTime);
                    CheckLifecycleTiming(await ReadRows(settledSibling + finish), null, true, 1,
                        $"{abortType}: reversed archived end conflicts with the current completion", terminalConflict: true);
                }
                finally { File.Delete(archivedPath); }
            }
            string SiblingCompletion(DateTimeOffset at) => Row("event_msg", at,
                new { type = "task_complete", turn_id = "another-turn", duration_ms = 0, time_to_first_token_ms = 0 });
            string reversedCompletion = SiblingCompletion(start.AddMilliseconds(-1));
            CheckLifecycleTiming(await ReadRows(tied + implicitTools + finish), null, true, 10,
                "task_complete: omitted sibling completion leaves implicit tool ownership ambiguous");
            CheckLifecycleTiming(await ReadRows(tied + SiblingCompletion(start) + implicitTools + finish), 6, false, 10,
                "task_complete: sibling completion at its start allows implicit tool pairing", unreconciledSibling: true);
            CheckLifecycleTiming(await ReadRows(tied + reversedCompletion + explicitTools + finish), 6, true, 10,
                "task_complete: reversed sibling completion preserves explicitly owned tools", unreconciledSibling: true);
            CheckLifecycleTiming(await ReadRows(tied + reversedCompletion + implicitTools + finish), null, true, 10,
                "task_complete: sibling completion earlier than its start cannot license implicit tool pairing", unreconciledSibling: true);
            foreach (string endType in new[] { "task_aborted", "turn_aborted", "task_complete" })
            {
                string reversedOnly = Row("event_msg", start.AddMilliseconds(-1),
                    new { type = endType, turn_id = "turn", duration_ms = 0, time_to_first_token_ms = 0 });
                foreach (string rows in new[] { metadata + reversedOnly + began, metadata + began + reversedOnly })
                {
                    var active = await ReadRows(rows);
                    Check(active.ActiveTurns == 1 && active.ActiveSeconds == 60 && active.ProcessingSeconds is null
                        && active.AverageTps is null && !active.Issues.Any(issue => issue.Code == DailyIssueCodes.TurnTimeConflict),
                        $"{endType}: a single reversed end leaves the recent turn open ({rows.IndexOf(reversedOnly, StringComparison.Ordinal)})");
                }
            }
            string reversedMainCompletion = Row("event_msg", start.AddMilliseconds(-1),
                new { type = "task_complete", turn_id = "turn", duration_ms = 0, time_to_first_token_ms = 0 });
            foreach (string rows in new[] { metadata + began + explicitTools + reversedMainCompletion + finish,
                metadata + began + explicitTools + finish + reversedMainCompletion })
            {
                var conflict = await ReadRows(rows);
                Check(conflict.InputTokens == 100 && conflict.OutputTokens == 40 && conflict.IsPartial
                    && conflict.ActiveTurns == 0 && conflict.ActiveSeconds is null && conflict.ProcessingSeconds is null
                    && conflict.AverageTps is null && conflict.TimedTurnCount == 0 && conflict.ExcludedTimingTurnCount == 1
                    && conflict.Issues.Any(issue => issue.Code == DailyIssueCodes.TurnTimeConflict),
                    "task_complete: two distinct terminal facts exclude the completed turn regardless of line order");
            }

            string independentPath = Path.Combine(sessions, "independent.jsonl");
            string independent = metadata + began + explicitTools + finish;
            independent = independent.Replace("\"timing-session\"", "\"independent-session\"", StringComparison.Ordinal);
            try
            {
                File.WriteAllText(independentPath, independent, Utf8);
                File.SetLastWriteTimeUtc(independentPath, start.AddMinutes(1).UtcDateTime);
                var subtotal = await ReadRows(metadata + began + explicitTools + reversedMainCompletion + finish);
                Check(subtotal.InputTokens == 200 && subtotal.OutputTokens == 80 && subtotal.IsPartial
                    && subtotal.ActiveSeconds == 10 && subtotal.ProcessingSeconds == 6
                    && subtotal.AverageTps is { } independentTps && Math.Abs(independentTps - 40 / 6d) < .000001
                    && subtotal.TimedTurnCount == 1 && subtotal.ExcludedTimingTurnCount == 1
                    && subtotal.Issues.Any(issue => issue.Code == DailyIssueCodes.TurnTimeConflict),
                    "same-turn conflict leaves the independently verified other root's timing and the 280-token subtotal");
            }
            finally { File.Delete(independentPath); }
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
            timing.Parse(metadata, parser.Parse(metadata, context).Events, context.SessionKey, out _);
            timing.Parse(began, parser.Parse(began, context).Events, context.SessionKey, out _);
            var projected = timing.Parse(Tool("function_call", 2, turn: "turn"), [], context.SessionKey, out _);
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
