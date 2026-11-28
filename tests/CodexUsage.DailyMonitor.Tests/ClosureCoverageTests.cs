using System.Text;
using System.Text.Json;
using CodexUsage.Daily;
using CodexUsage.DailyPanel;

internal static class ClosureCoverageTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 21, 12, 0, 0, TimeSpan.FromHours(8));
    private static readonly DateTimeOffset Now = At.AddMinutes(10);
    private static readonly DateTimeOffset AfterMidnight = new(2026, 10, 21, 0, 1, 0, TimeSpan.FromHours(8));
    private static readonly DateTimeOffset OldStart = new(2026, 10, 20, 23, 55, 0, TimeSpan.FromHours(8));
    private static readonly DateTimeOffset SuccessorStart = new(2026, 10, 20, 23, 58, 0, TimeSpan.FromHours(8));
    private static readonly TimeZoneInfo Zone = TimeZoneInfo.CreateCustomTimeZone(
        "SyntheticClosureUTC8", TimeSpan.FromHours(8), "UTC+8", "UTC+8");
    private static readonly UTF8Encoding Utf8 = new(false, true);

    public static async Task RunAsync()
    {
        var passed = 0;
        var failures = new List<string>();
        async Task Case(string name, Func<Task> action)
        {
            try
            {
                await action();
                passed++;
                Console.WriteLine("PASS: " + name);
            }
            catch (Exception exception)
            {
                var failure = name + ": " + exception.Message;
                failures.Add(failure);
                Console.WriteLine("FAIL: " + failure);
            }
        }

        await Case("CON-01 an available same-day occurrence before the turn start excludes adjusted TPS after becoming root", async () =>
        {
            using var fixture = new Fixture("same-day-origin-present-control", Now);
            fixture.WriteSession("early-child-occurrence", EarlyChildRows());
            fixture.WriteSession("current-child-completed-turn", CurrentChildRows());
            using var monitor = new SessionLogMonitor(fixture.Home, Zone);
            var child = await monitor.RefreshAsync(Now);
            Expect(child, null, null, null, null, false, "initial child A contributes no root counters");
            ExpectUnavailableTiming(child, "initial child A contributes no root TPS");
            fixture.WriteSession("newer-root-A", RootMetadata("A", At.AddSeconds(20)));
            var root = await monitor.RefreshAsync(Now);
            Expect(root, 100, 60, 40, 10, false, "retained 11:59:30 occurrence keeps the current request counted once today");
            ExpectUnavailableTiming(root, "a request first observed before 12:00:00 does not qualify for the completed turn's TPS");
            Check(root, root.ExcludedTimingTurnCount == 1, "exactly the completed root turn is excluded from timing");
            var idle = await monitor.RefreshAsync(Now);
            Expect(idle, 100, 60, 40, 10, false, "available original occurrence remains complete and deduplicated on idle");
            ExpectUnavailableTiming(idle, "idle does not move the first occurrence into the later turn interval");
        });

        await Case("CON-02 losing a same-day child occurrence before becoming root cannot manufacture nine processing seconds", async () =>
        {
            using var fixture = new Fixture("same-day-origin-lost-before-root", Now);
            var early = fixture.WriteSession("early-child-occurrence", EarlyChildRows());
            fixture.WriteSession("current-child-completed-turn", CurrentChildRows());
            using var monitor = new SessionLogMonitor(fixture.Home, Zone);
            Expect(await monitor.RefreshAsync(Now), null, null, null, null, false,
                "both same-day occurrences are initially in confirmed child scope");
            File.Delete(early);
            Expect(await monitor.RefreshAsync(Now), null, null, null, null, false,
                "missing early occurrence stays dormant while current child metadata excludes the session");
            fixture.WriteSession("newer-root-A", RootMetadata("A", At.AddSeconds(20)));
            var root = await monitor.RefreshAsync(Now);
            Expect(root, 100, 60, 40, 10, true, "current root counters are known but the 11:59:30 occurrence proof is missing");
            ExpectUnavailableTiming(root, "missing same-day pre-start evidence cannot turn this request into a nine-second TPS sample");
            var idle = await monitor.RefreshAsync(Now);
            Expect(idle, 100, 60, 40, 10, true, "same-day occurrence uncertainty persists on idle");
            ExpectUnavailableTiming(idle, "idle cannot admit the 12:00:09 copy as the first occurrence");
            fixture.WriteSession("early-child-occurrence", EarlyChildRows());
            var restored = await monitor.RefreshAsync(Now);
            Expect(restored, 100, 60, 40, 10, false, "restored original occurrence recovers complete current root coverage");
            ExpectUnavailableTiming(restored, "available earlier occurrence still prevents TPS qualification");
            var restoredIdle = await monitor.RefreshAsync(Now);
            Expect(restoredIdle, 100, 60, 40, 10, false, "restored same-day history does not duplicate the request");
            ExpectUnavailableTiming(restoredIdle, "restored exclusion remains stable on idle");
        });

        await Case("CON-03 removing only a known completion cannot restart its retained historical start", async () =>
        {
            using var fixture = new Fixture("completion-truncated-after-today-read", AfterMidnight);
            fixture.WriteSession("old-unfinished-turn", OldOpenRows());
            var completionOnlyRows = OpenSuccessorRows() + Complete("successor-turn", SuccessorStart.AddSeconds(10));
            fixture.WriteSession("successor", completionOnlyRows);
            using var monitor = new SessionLogMonitor(fixture.Home, Zone);
            ExpectInactiveToday(await monitor.RefreshAsync(AfterMidnight), false,
                "today already observed the successor start and its prior-day completion");
            fixture.WriteSession("successor", OpenSuccessorRows());
            ExpectInactiveToday(await monitor.RefreshAsync(AfterMidnight), true,
                "retaining the exact metadata and start bytes while removing only completion cannot restart the successor");
            ExpectInactiveToday(await monitor.RefreshAsync(AfterMidnight.AddMinutes(1)), true,
                "00:02 idle cannot accrue active seconds for a turn already known completed");
            fixture.WriteSession("successor", completionOnlyRows);
            ExpectInactiveToday(await monitor.RefreshAsync(AfterMidnight.AddMinutes(1)), false,
                "restored completion proves complete non-active history");
            ExpectInactiveToday(await monitor.RefreshAsync(AfterMidnight.AddMinutes(1)), false,
                "restored closure stays non-active and complete on idle");
        });

        await Case("CON-04 a completion read before midnight remains binding after start-only truncation and rollover", async () =>
        {
            using var fixture = new Fixture("completion-truncated-before-rollover", AfterMidnight);
            var beforeMidnight = new DateTimeOffset(2026, 10, 20, 23, 59, 0, TimeSpan.FromHours(8));
            fixture.WriteSession("old-unfinished-turn", OldOpenRows());
            fixture.WriteSession("successor", CompletedSuccessorRows());
            using var monitor = new SessionLogMonitor(fixture.Home, Zone);
            var yesterday = await monitor.RefreshAsync(beforeMidnight);
            Expect(yesterday, 100, 60, 40, 10, false, "complete successor request belongs to yesterday");
            Check(yesterday, yesterday.ActiveTurns == 0 && yesterday.ActiveSeconds is { } active
                && Math.Abs(active - 10) < 0.000001, "only the completed successor supplies yesterday's ten active seconds");
            Check(yesterday, yesterday.ProcessingSeconds is { } processing && Math.Abs(processing - 9) < 0.000001
                && yesterday.AverageTps is { } tps && Math.Abs(tps - 4.444444444444445) < 0.000001,
                "baseline uses the unchanged ten-second duration minus one-second TTFT formula");
            var yesterdayIdle = await monitor.RefreshAsync(beforeMidnight.AddSeconds(10));
            Expect(yesterdayIdle, 100, 60, 40, 10, false, "last pre-midnight refresh still contains the completion");
            Check(yesterdayIdle, yesterdayIdle.ActiveTurns == 0, "pre-midnight idle does not restart either historical turn");
            fixture.WriteSession("successor", OpenSuccessorRows());
            ExpectInactiveToday(await monitor.RefreshAsync(AfterMidnight), true,
                "rollover cannot forget a completion when its exact metadata and start remain readable");
            ExpectInactiveToday(await monitor.RefreshAsync(AfterMidnight.AddMinutes(1)), true,
                "00:02 idle preserves the known closed state without today's duration");
            fixture.WriteSession("successor", CompletedSuccessorRows());
            ExpectInactiveToday(await monitor.RefreshAsync(AfterMidnight.AddMinutes(1)), false,
                "restored prior-day completion settles rollover coverage");
        });

        await Case("CON-05 a start with no observed terminal event remains a legitimate recent active turn", async () =>
        {
            using var fixture = new Fixture("never-closed-start-control", AfterMidnight);
            fixture.WriteSession("open-only", RootMetadata("activity", SuccessorStart.AddMinutes(-1))
                + Start("successor-turn", SuccessorStart));
            using var monitor = new SessionLogMonitor(fixture.Home, Zone);
            var active = await monitor.RefreshAsync(AfterMidnight);
            Expect(active, null, null, null, null, false, "a genuinely unfinished recent turn has complete observed coverage");
            Check(active, active.ActiveTurns == 1 && active.ActiveSeconds is { } seconds
                && Math.Abs(seconds - 60) < 0.000001, "a never-closed start contributes sixty clipped active seconds at 00:01");
            ExpectUnavailableTiming(active, "an active turn does not invent completed-turn processing or TPS");
            var idle = await monitor.RefreshAsync(AfterMidnight.AddMinutes(1));
            Expect(idle, null, null, null, null, false, "start-only control remains complete at 00:02");
            Check(idle, idle.ActiveTurns == 1 && idle.ActiveSeconds is { } secondsAtTwo
                && Math.Abs(secondsAtTwo - 120) < 0.000001, "the genuinely open turn may accrue 120 clipped active seconds");
            ExpectUnavailableTiming(idle, "start-only idle never qualifies as completed timing");
        });

        await Case("CON-06 losing an observed abort cannot restart its retained start and its guard may expire", async () =>
        {
            using var fixture = new Fixture("abort-truncated-and-expired", AfterMidnight);
            fixture.WriteSession("old-unfinished-turn", OldOpenRows());
            fixture.WriteSession("successor", AbortedSuccessorRows());
            using var monitor = new SessionLogMonitor(fixture.Home, Zone);
            ExpectInactiveToday(await monitor.RefreshAsync(AfterMidnight), false,
                "a known prior-day abort closes the successor before today's interval");
            fixture.WriteSession("successor", OpenSuccessorRows());
            ExpectInactiveToday(await monitor.RefreshAsync(AfterMidnight), true,
                "an aborted successor cannot become active merely because its abort row disappeared");
            ExpectInactiveToday(await monitor.RefreshAsync(AfterMidnight.AddMinutes(1)), true,
                "00:02 idle preserves the observed abort decision");
            fixture.WriteSession("successor", AbortedSuccessorRows());
            ExpectInactiveToday(await monitor.RefreshAsync(AfterMidnight.AddMinutes(1)), false,
                "restored abort evidence recovers complete non-active coverage");
            fixture.WriteSession("successor", OpenSuccessorRows());
            ExpectInactiveToday(await monitor.RefreshAsync(AfterMidnight.AddMinutes(1)), true,
                "a second abort loss is relevant while the start is recent");
            ExpectInactiveToday(await monitor.RefreshAsync(AfterMidnight.AddMinutes(13)), false,
                "at 00:14 the 23:58 start is sixteen minutes old so missing terminal evidence is dormant");
            ExpectInactiveToday(await monitor.RefreshAsync(AfterMidnight.AddMinutes(14)), false,
                "expired abort evidence does not leave permanent activity or partiality on idle");
        });

        await Case("CON-07 appending an abort settles the tied start and its archived replay", async () =>
        {
            using var fixture = new Fixture("tied-start-abort-and-archive", Now);
            var prefix = TiedStartRows() + Complete("tied-first", At.AddSeconds(10));
            var path = fixture.WriteSession("tied-starts", prefix);
            using var monitor = new SessionLogMonitor(fixture.Home, Zone);
            ExpectTiedStarts(await monitor.RefreshAsync(Now), 9, true,
                "one known completion cannot resolve the other tied start");
            var abort = Row("event_msg", At.AddSeconds(10), new { type = "task_aborted", turn_id = Turn("tied-second") });
            fixture.AppendSession(path, abort);
            ExpectTiedStarts(await monitor.RefreshAsync(Now), 9, false,
                "a current completion and abort close both tied candidates without changing timing or totals");
            fixture.WriteSession("mirror", prefix + abort, archived: true);
            var replay = await monitor.RefreshAsync(Now);
            Check(replay, replay.SourceCount == 2, "the archived replay must be read");
            ExpectTiedStarts(replay, 9, false,
                "exact archived duplicates of starts, requests and closures leave the resolved state unchanged");
        });

        await Case("CON-08 a retired terminal alone cannot settle a tied start", async () =>
        {
            using var fixture = new Fixture("tied-start-terminal-loss", Now);
            var prefix = TiedStartRows() + Complete("tied-first", At.AddSeconds(10));
            var last = Complete("tied-second", At.AddSeconds(10));
            var path = fixture.WriteSession("tied-starts", prefix + last);
            using var monitor = new SessionLogMonitor(fixture.Home, Zone);
            ExpectTiedStarts(await monitor.RefreshAsync(Now), 18, false,
                "all tied starts have known current completions");
            fixture.TruncateSession(path, Utf8.GetByteCount(prefix));
            ExpectTiedStarts(await monitor.RefreshAsync(Now), 9, true,
                "the removed closure cannot settle the current tie or provide completed-turn TPS");
            ExpectTiedStarts(await monitor.RefreshAsync(Now.AddSeconds(1)), 9, true,
                "an idle refresh cannot promote the retained closure");
            fixture.AppendSession(path, last);
            ExpectTiedStarts(await monitor.RefreshAsync(Now.AddSeconds(1)), 18, false,
                "restoring the exact original bytes resolves the current tie again");
        });

        foreach (var kind in new[] { "task_aborted", "turn_aborted" })
        {
            foreach (var versionInPayload in new[] { false, true })
            {
                var scope = versionInPayload ? "payload" : "envelope";
                await Case($"CON-09 {kind} rejects an unsupported {scope} version", async () =>
                {
                    using var fixture = new Fixture($"unsupported-{kind}-{scope}", Now);
                    var prefix = TiedStartRows() + Complete("tied-first", At.AddSeconds(10));
                    var path = fixture.WriteSession("tied-starts", prefix);
                    using var monitor = new SessionLogMonitor(fixture.Home, Zone);
                    ExpectTiedStarts(await monitor.RefreshAsync(Now), 9, true, "before unsupported abort append");

                    fixture.AppendSession(path, AbortRow(kind, "tied-second", At.AddSeconds(12),
                        envelopeVersion: versionInPayload ? null : 2, payloadVersion: versionInPayload ? 2 : null));
                    var rejected = await monitor.RefreshAsync(Now);
                    ExpectTiedStarts(rejected, null, true, "unsupported abort cannot settle the tied start");
                    Check(rejected, rejected.Issues.Any(issue => issue.Code == DailyIssueCodes.SourceUnreadable),
                        "unsupported abort must contribute a source quality diagnostic");

                    fixture.WriteSession("valid-archive", prefix + AbortRow(kind, "tied-second", At.AddSeconds(10)), archived: true);
                    var restored = await monitor.RefreshAsync(Now);
                    ExpectTiedStarts(restored, null, false, "a distinct, supported archived abort settles the tie", partial: true);
                    Check(restored, restored.SourceCount == 2
                        && restored.Issues.Any(issue => issue.Code == DailyIssueCodes.SourceUnreadable),
                        "archived recovery must preserve the independent unsupported-source diagnostic");
                });
            }

            await Case($"CON-10 {kind} accepts absent, numeric and string version 1", async () =>
            {
                foreach (var version in new object?[] { null, 1, "1" })
                {
                    using var fixture = new Fixture($"supported-{kind}-{version ?? "absent"}", Now);
                    var prefix = TiedStartRows() + Complete("tied-first", At.AddSeconds(10));
                    fixture.WriteSession("tied-starts", prefix + AbortRow(kind, "tied-second", At.AddSeconds(10), version, version));
                    using var monitor = new SessionLogMonitor(fixture.Home, Zone);
                    ExpectTiedStarts(await monitor.RefreshAsync(Now), 9, false, $"supported version {version ?? "absent"}");
                }
            });
        }

        await Case("CON-11 an unsupported abort never becomes historical closure evidence", async () =>
        {
            using var fixture = new Fixture("unsupported-abort-history", Now);
            const string turn = "unsupported-abort-history";
            var prefix = RootMetadata(turn, At.AddMinutes(-1)) + Start(turn, At);
            var path = fixture.WriteSession("open-turn", prefix + AbortRow("task_aborted", turn, At.AddSeconds(10), payloadVersion: 2));
            using var monitor = new SessionLogMonitor(fixture.Home, Zone);
            var rejected = await monitor.RefreshAsync(Now);
            Check(rejected, rejected.IsPartial && rejected.ActiveTurns == 1,
                "unsupported abort must leave the single verified turn running");

            fixture.TruncateSession(path, Utf8.GetByteCount(prefix));
            var afterRemoval = await monitor.RefreshAsync(Now);
            Check(afterRemoval, afterRemoval.ActiveTurns == 1
                && !afterRemoval.Issues.Any(issue => issue.Code == DailyIssueCodes.HistoricalClosureMissing),
                "removing the unsupported row must not create missing historical closure evidence");
        });

        await Case("CON-12 unsupported non-statistics event mentioning abort is ignored", async () =>
        {
            using var fixture = new Fixture("unsupported-non-statistics", Now);
            var prefix = TiedStartRows() + Complete("tied-first", At.AddSeconds(10))
                + AbortRow("task_aborted", "tied-second", At.AddSeconds(10));
            var ignored = JsonSerializer.Serialize(new
            {
                type = "event_msg", timestamp = At.AddSeconds(11), schema_version = 2,
                payload = new { type = "user_message", schema_version = 2, message = "synthetic turn_aborted mention" }
            }) + "\n";
            fixture.WriteSession("tied-starts", prefix + ignored);
            using var monitor = new SessionLogMonitor(fixture.Home, Zone);
            ExpectTiedStarts(await monitor.RefreshAsync(Now), 9, false, "unsupported non-statistics row must not change source quality");
        });

        async Task ExpectMalformedAbort(string label, string kind, string malformed)
        {
            using var fixture = new Fixture(label, Now);
            var prefix = TiedStartRows() + Complete("tied-first", At.AddSeconds(10));
            fixture.WriteSession("current", prefix + malformed);
            using var monitor = new SessionLogMonitor(fixture.Home, Zone);

            var singleSource = await monitor.RefreshAsync(Now);
            ExpectTiedStarts(singleSource, null, true, "malformed abort cannot settle the current tie");
            ExpectAbortSourceDiagnostics(singleSource, "current malformed abort");

            fixture.WriteSession("valid-archive", prefix + AbortRow(kind, "tied-second", At.AddSeconds(10), 1, "1"), archived: true);
            var withArchive = await monitor.RefreshAsync(Now);
            ExpectTiedStarts(withArchive, null, false, "a valid archived abort settles the tie", partial: true);
            Check(withArchive, withArchive.SourceCount == 2, "the legal archived closure must be read");
            ExpectAbortSourceDiagnostics(withArchive, "malformed abort alongside archived closure");
        }

        var secondTurn = Turn("tied-second");
        foreach (var kind in new[] { "task_aborted", "turn_aborted" })
        {
            await Case($"CON-13 {kind} duplicate payload version stays partial after archived closure", () =>
                ExpectMalformedAbort($"ambiguous-version-{kind}", kind,
                    AbortEnvelope($$"""{"type":"{{kind}}","turn_id":"{{secondTurn}}","schema_version":2,"schema_version":1}""")));
        }

        var validPayload = $$"""{"type":"turn_aborted","turn_id":"{{secondTurn}}"}""";
        var malformedCases = new (string Name, string Row)[]
        {
            ("duplicate-payload-kind", AbortEnvelope(
                $$"""{"type":"turn_aborted","turn_id":"{{secondTurn}}","type":"user_message"}""")),
            ("duplicate-turn-id", AbortEnvelope(
                $$"""{"type":"turn_aborted","turn_id":"{{Turn("tied-first")}}","turn_id":"{{secondTurn}}"}""")),
            ("duplicate-envelope-kind", AbortEnvelope(validPayload).Replace(
                "\"type\":\"event_msg\"", "\"type\":\"event_msg\",\"type\":\"response_item\"", StringComparison.Ordinal)),
            ("duplicate-payload-structure", AbortEnvelope(validPayload).Replace(
                validPayload, validPayload + ""","payload":null""", StringComparison.Ordinal)),
            ("duplicate-timestamp", AbortEnvelope(validPayload, "\"invalid\",\"timestamp\":\"2026-10-21T12:00:10+08:00\"")),
            ("invalid-timestamp", AbortEnvelope(validPayload, "\"2026-10-21 12:00:10 +08:00\"")),
            ("missing-turn-id", AbortEnvelope("""{"type":"turn_aborted"}""")),
            ("blank-turn-id", AbortEnvelope("""{"type":"turn_aborted","turn_id":" "}""")),
            ("numeric-turn-id", AbortEnvelope("""{"type":"turn_aborted","turn_id":1}"""))
        };
        foreach (var (name, row) in malformedCases)
        {
            await Case($"CON-14 {name} on an abort is a source issue", () =>
                ExpectMalformedAbort(name, "turn_aborted", row));
        }

        await Case("CON-15 duplicate fields in a non-statistics payload do not diagnose an abort", async () =>
        {
            using var fixture = new Fixture("unrelated-abort-text", Now);
            var prefix = TiedStartRows() + Complete("tied-first", At.AddSeconds(10))
                + AbortRow("turn_aborted", "tied-second", At.AddSeconds(10), "1", 1);
            var ignored = AbortEnvelope("""
                {"type":"user_message","schema_version":1,"schema_version":2,"turn_id":null,"turn_id":"ignored","message":"task_aborted turn_aborted"}
                """);
            fixture.WriteSession("current", prefix + ignored);
            using var monitor = new SessionLogMonitor(fixture.Home, Zone);
            var snapshot = await monitor.RefreshAsync(Now);
            ExpectTiedStarts(snapshot, 9, false, "a supported abort resolves activity while unrelated text stays ignored");
            Check(snapshot, !snapshot.Issues.Any(issue => issue.Code == DailyIssueCodes.SourceUnreadable),
                "abort words outside the event kind are not an abort record");
        });

        const string escapedKind = @"turn_\u0061borted";
        await Case("CON-16 JSON-escaped supported abort kind closes the turn", async () =>
        {
            using var fixture = new Fixture("legal-escaped-kind", Now);
            var prefix = TiedStartRows() + Complete("tied-first", At.AddSeconds(10));
            fixture.WriteSession("current", prefix + AbortEnvelope(
                $$"""{"type":"{{escapedKind}}","turn_id":"{{secondTurn}}"}"""));
            using var monitor = new SessionLogMonitor(fixture.Home, Zone);
            ExpectTiedStarts(await monitor.RefreshAsync(Now), 9, false, "the decoded abort kind supplies closure");
        });
        await Case("CON-16 JSON-escaped abort kind with duplicate version stays partial", () =>
            ExpectMalformedAbort("ambiguous-escaped-kind", "turn_aborted", AbortEnvelope(
                $$"""{"type":"{{escapedKind}}","turn_id":"{{secondTurn}}","schema_version":2,"schema_version":1}""")));

        const string outerType = "\"type\":\"event_msg\",";
        foreach (var kind in new[] { "task_aborted", "turn_aborted" })
        {
            var envelope = AbortEnvelope($$"""{"type":"{{kind}}","turn_id":"{{secondTurn}}"}""");
            await Case($"CON-17 {kind} with missing outer type keeps source and timing diagnostics", () =>
                ExpectMalformedAbort($"missing-outer-{kind}", kind, envelope.Replace(outerType, "", StringComparison.Ordinal)));
            await Case($"CON-17 {kind} with numeric outer type keeps source and timing diagnostics", () =>
                ExpectMalformedAbort($"numeric-outer-{kind}", kind, envelope.Replace(outerType, "\"type\":3,", StringComparison.Ordinal)));
        }

        var invalidOuterTypes = new (string Name, string Field)[]
        {
            ("null-outer", "\"type\":null,"),
            ("boolean-outer", "\"type\":false,"),
            ("array-outer", """ "type":["event_msg"], """),
            ("object-outer", """ "type":{"value":"event_msg"}, """),
            ("blank-outer", """ "type":" ", """),
            ("escaped-outer-key", """ "t\u0079pe":7, """),
            ("duplicate-escaped-outer-key", """ "type":"event_msg","t\u0079pe":7, """)
        };
        foreach (var (name, field) in invalidOuterTypes)
        {
            await Case($"CON-18 {name} cannot validate an abort envelope", () =>
                ExpectMalformedAbort(name, "turn_aborted", AbortEnvelope(validPayload).Replace(outerType, field, StringComparison.Ordinal)));
        }

        await Case("CON-19 escaped object member names keep a valid abort recognizable", async () =>
        {
            using var fixture = new Fixture("escaped-abort-member-names", Now);
            var escapedEnvelope = AbortEnvelope($$"""{"t\u0079pe":"turn_aborted","turn_id":"{{secondTurn}}"}""")
                .Replace(outerType, """ "t\u0079pe":"event_msg", """, StringComparison.Ordinal)
                .Replace("\"payload\":", """ "pay\u006coad": """, StringComparison.Ordinal);
            fixture.WriteSession("current", TiedStartRows() + Complete("tied-first", At.AddSeconds(10)) + escapedEnvelope);
            using var monitor = new SessionLogMonitor(fixture.Home, Zone);
            ExpectTiedStarts(await monitor.RefreshAsync(Now), 9, false, "decoded keys preserve a supported abort");
        });

        await Case("CON-20 abort prose and other event types do not become malformed aborts", async () =>
        {
            using var fixture = new Fixture("non-abort-envelopes", Now);
            var prose = AbortEnvelope("""{"type":"user_message","message":"task_aborted turn_aborted"}""")
                .Replace(outerType, "\"type\":3,", StringComparison.Ordinal);
            var nested = AbortEnvelope("""{"type":"message","content":{"type":"turn_aborted"}}""")
                .Replace(outerType, "\"type\":\"response_item\",", StringComparison.Ordinal);
            var unrelated = AbortEnvelope(validPayload)
                .Replace(outerType, "\"type\":\"other_envelope\",", StringComparison.Ordinal);
            var path = fixture.WriteSession("current", TiedStartRows() + Complete("tied-first", At.AddSeconds(10))
                + prose + nested + unrelated);
            using var monitor = new SessionLogMonitor(fixture.Home, Zone);
            var before = await monitor.RefreshAsync(Now);
            ExpectTiedStarts(before, 9, true, "unrelated envelopes neither close the tie nor remove trustworthy timing");
            Check(before, !before.Issues.Any(issue => issue.Code == DailyIssueCodes.SourceUnreadable),
                "unrelated envelopes do not add a source-quality diagnosis");

            fixture.AppendSession(path, AbortRow("turn_aborted", "tied-second", At.AddSeconds(10)));
            ExpectTiedStarts(await monitor.RefreshAsync(Now), 9, false, "only the actual abort event closes the tie");
        });

        const string toolSession = "tool-coverage";
        const string timedTurn = "timed";
        var toolStart = RootMetadata(toolSession, At.AddSeconds(-10)) + Start(timedTurn, At);
        var toolCall = Tool("function_call", "original", At.AddSeconds(2));
        var toolOutput = Tool("function_call_output", "original", At.AddSeconds(5));
        var toolCompletion = Modern(toolSession, "timed-request", timedTurn, At.AddSeconds(9))
            + Complete(timedTurn, At.AddSeconds(10));
        var originalTools = toolStart + toolCall + toolOutput + toolCompletion;
        var lateSibling = Start("late-sibling", At) + AbortRow("task_aborted", "late-sibling", At.AddSeconds(8));
        foreach (var change in new[] { "append", "replace", "archive", "exact-replace", "missing", "substitute" })
        {
            await Case($"CON-21 tool coverage after {change} uses the same raw facts", async () =>
            {
                using var fixture = new Fixture("tool-coverage-" + change, Now);
                var live = fixture.WriteSession("current", originalTools);
                using var monitor = new SessionLogMonitor(fixture.Home, Zone);
                ExpectToolCoverage(await monitor.RefreshAsync(Now), 1, timed: true, lost: false, "initial inferred pair");

                var final = originalTools + lateSibling;
                var current = live;
                switch (change)
                {
                    case "append":
                        fixture.AppendSession(live, lateSibling);
                        break;
                    case "archive":
                        current = Path.Combine(fixture.Home, "archived_sessions", Path.GetFileName(live));
                        File.Move(live, current);
                        fixture.AppendSession(current, lateSibling);
                        break;
                    case "replace":
                    case "exact-replace":
                    {
                        if (change == "exact-replace") final = originalTools;
                        await ReplacePreservedSession(fixture, live, originalTools, final);
                        break;
                    }
                    case "missing":
                        final = toolStart + toolCall + toolCompletion + lateSibling;
                        ReplaceSession(fixture, live, final);
                        break;
                    case "substitute":
                        final = toolStart + Tool("function_call", "different-call", At.AddSeconds(2))
                            + Tool("function_call_output", "different-call", At.AddSeconds(5)) + toolCompletion;
                        ReplaceSession(fixture, live, final);
                        using (var cold = new SessionLogMonitor(fixture.Home, Zone))
                            ExpectToolCoverage(await cold.RefreshAsync(Now), 1, timed: true, lost: false,
                                "the substituted pair is independently complete but does not cover the original pair");
                        break;
                }
                if (!File.ReadAllBytes(current).SequenceEqual(Utf8.GetBytes(final)))
                    throw new InvalidOperationException("The fixture did not preserve the intended synthetic bytes.");

                bool lost = change is "missing" or "substitute";
                bool timed = change == "exact-replace";
                var turns = change is "exact-replace" or "substitute" ? 1 : 2;
                ExpectToolCoverage(await monitor.RefreshAsync(Now.AddSeconds(1)), turns, timed, lost, "after updating the source");
                ExpectToolCoverage(await monitor.RefreshAsync(Now.AddSeconds(2)), turns, timed, lost, "idle refresh");

                if (change == "missing")
                {
                    ReplaceSession(fixture, live, originalTools + lateSibling);
                    ExpectToolCoverage(await monitor.RefreshAsync(Now.AddSeconds(3)), 2, timed: false, lost: false,
                        "restoring the identical tool output clears coverage loss while ownership remains ambiguous");
                }
            });
        }

        var siblingStart = Start("early-sibling", At);
        var siblingEnd = AbortRow("task_aborted", "early-sibling", At.AddSeconds(1));
        var uncertainPair = toolStart + siblingStart + toolCall + toolOutput + toolCompletion;
        var recoveredPair = uncertainPair + siblingEnd;
        foreach (var change in new[] { "append", "replace", "archive", "missing-sibling-start", "missing-tool-result", "substitute" })
        {
            await Case($"CON-22 tool timing recovery after {change} requires the original evidence", async () =>
            {
                using var fixture = new Fixture("tool-recovery-" + change, Now);
                var live = fixture.WriteSession("current", uncertainPair);
                using var monitor = new SessionLogMonitor(fixture.Home, Zone);
                ExpectToolCoverage(await monitor.RefreshAsync(Now), 2, timed: false, lost: false,
                    "the initially uncertain pair excludes timing independently of source coverage", activityConflict: true);

                var final = recoveredPair;
                var current = live;
                switch (change)
                {
                    case "append":
                        fixture.AppendSession(live, siblingEnd);
                        break;
                    case "replace":
                        await ReplacePreservedSession(fixture, live, uncertainPair, recoveredPair);
                        break;
                    case "archive":
                        current = Path.Combine(fixture.Home, "archived_sessions", Path.GetFileName(live));
                        File.Move(live, current);
                        fixture.AppendSession(current, siblingEnd);
                        break;
                    case "missing-sibling-start":
                        final = toolStart + toolCall + toolOutput + toolCompletion + siblingEnd;
                        ReplaceSession(fixture, live, final);
                        break;
                    case "missing-tool-result":
                        final = toolStart + siblingStart + toolCall + toolCompletion + siblingEnd;
                        ReplaceSession(fixture, live, final);
                        break;
                    case "substitute":
                        final = toolStart + siblingStart + Tool("function_call", "replacement-pair", At.AddSeconds(2))
                            + Tool("function_call_output", "replacement-pair", At.AddSeconds(5)) + toolCompletion + siblingEnd;
                        ReplaceSession(fixture, live, final);
                        using (var cold = new SessionLogMonitor(fixture.Home, Zone))
                            ExpectToolCoverage(await cold.RefreshAsync(Now), 2, timed: true, lost: false,
                                "the new pair is independently usable without covering the retired pair");
                        break;
                }
                if (!File.ReadAllBytes(current).SequenceEqual(Utf8.GetBytes(final)))
                    throw new InvalidOperationException("The fixture did not preserve the intended synthetic bytes.");

                bool lost = change is "missing-sibling-start" or "missing-tool-result" or "substitute";
                ExpectToolCoverage(await monitor.RefreshAsync(Now.AddSeconds(1)), 2, timed: !lost, lost, "after requalification");
                ExpectToolCoverage(await monitor.RefreshAsync(Now.AddSeconds(2)), 2, timed: !lost, lost, "idle refresh");
                if (change is "missing-sibling-start" or "missing-tool-result")
                {
                    ReplaceSession(fixture, live, recoveredPair);
                    ExpectToolCoverage(await monitor.RefreshAsync(Now.AddSeconds(3)), 2, timed: true, lost: false,
                        "exact restoration clears the coverage loss and restores timing");
                    ExpectToolCoverage(await monitor.RefreshAsync(Now.AddSeconds(4)), 2, timed: true, lost: false,
                        "exact restoration stays qualified on idle refresh");
                }
            });
        }

        var midnight = AfterMidnight.AddMinutes(-1);
        var priorDay = midnight.AddSeconds(-1);
        var midnightMeta = RootMetadata("midnight", midnight.AddSeconds(-10));
        var firstStart = Start("prior-A", priorDay);
        var competingStart = Start("prior-B", priorDay);
        var earlyEnd = AbortRow("task_aborted", "prior-B", midnight);
        var lateEnd = AbortRow("task_aborted", "prior-B", midnight.AddSeconds(8));
        foreach (var change in new[] { "missing-start", "missing-start-midspan", "split-start", "complete-replacement", "late-end", "irrelevant-history" })
        {
            await Case($"CON-23 cross-day tool ownership after {change}", async () =>
            {
                using var fixture = new Fixture("midnight-ownership-" + change, AfterMidnight);
                bool midspan = change == "missing-start-midspan";
                string rows = Tool("function_call", "midnight-pair", midnight.AddSeconds(1))
                    + Tool("function_call_output", "midnight-pair", midnight.AddSeconds(midspan ? 15 : 4))
                    + AbortRow("task_aborted", "prior-A", midnight.AddSeconds(midspan ? 16 : 9))
                    + Modern("midnight", "midnight-request", "inferred-C", midnight.AddSeconds(11))
                    + Complete("inferred-C", midnight.AddSeconds(12));
                string complete = midnightMeta + firstStart + competingStart + rows;
                string initial = change == "irrelevant-history"
                    ? midnightMeta + Start("earlier-closed", priorDay.AddSeconds(-8))
                        + AbortRow("task_aborted", "earlier-closed", priorDay.AddSeconds(-7))
                        + firstStart + competingStart + rows
                    : complete;
                string live = fixture.WriteSession("live", initial);
                using var monitor = new SessionLogMonitor(fixture.Home, Zone);
                var first = await monitor.RefreshAsync(AfterMidnight);
                ExpectMidnightTiming(first, timed: false, lost: false, "both prior-day starts are present");

                bool missing = change is "missing-start" or "missing-start-midspan" or "split-start";
                string next = missing ? midnightMeta + firstStart + rows
                    : complete + (change == "late-end" ? lateEnd : earlyEnd);
                string? sibling = change == "split-start"
                    ? fixture.WriteSession("separate", midnightMeta + competingStart + earlyEnd, archived: true) : null;
                ReplaceSession(fixture, live, next);
                bool timed = change is "complete-replacement" or "irrelevant-history";
                ExpectMidnightTiming(await monitor.RefreshAsync(AfterMidnight.AddSeconds(1)), timed, lost: missing,
                    "after replacing the live source");
                ExpectMidnightTiming(await monitor.RefreshAsync(AfterMidnight.AddSeconds(2)), timed, lost: missing,
                    "idle refresh retains the same evidence");
                if (missing)
                {
                    if (sibling is not null) File.Delete(sibling);
                    ReplaceSession(fixture, live, complete + earlyEnd);
                    ExpectMidnightTiming(await monitor.RefreshAsync(AfterMidnight.AddSeconds(3)), timed: true, lost: false,
                        "the original competing start and its early end are now together with the tool");
                }
            });
        }

        Console.WriteLine($"RESULT CLOSURE COVERAGE: {passed} passed, {failures.Count} failed");
        if (failures.Count > 0)
            throw new InvalidOperationException("Closure coverage regressions:\n" + string.Join("\n", failures));
    }

    private static void Expect(DailySnapshot actual, long? input, long? cached, long? output,
        long? reasoning, bool partial, string context) => Check(actual, actual.InputTokens == input
            && actual.CachedInputTokens == cached && actual.OutputTokens == output && actual.ReasoningTokens == reasoning
            && actual.IsPartial == partial,
            $"{context}: expected input={input}, cached={cached}, output={output}, reasoning={reasoning}, Partial={partial}");

    private static void ExpectUnavailableTiming(DailySnapshot actual, string context) => Check(actual,
        actual.AverageTps is null && actual.ProcessingSeconds is null && actual.TimedTurnCount == 0,
        context + ": expected unavailable TPS and processing seconds with no timed completed turn");

    private static void ExpectMidnightTiming(DailySnapshot actual, bool timed, bool lost, string context)
    {
        Expect(actual, 100, 60, 40, 10, lost, context);
        Check(actual, actual.Issues.Any(issue => issue.Code == DailyIssueCodes.CoverageLost) == lost
            && actual.Issues.Any(issue => issue.Code == DailyIssueCodes.SourcePartial) == lost,
            context + ": expected source coverage diagnostics to reflect the missing ownership evidence");
        if (timed)
            Check(actual, actual.ProcessingSeconds == 9 && actual.TimedTurnCount == 1 && actual.ExcludedTimingTurnCount == 0
                && actual.AverageTps is { } tps && Math.Abs(tps - 40d / 9) < 0.000001,
                context + ": expected nine seconds of independently verifiable timing");
        else
        {
            ExpectUnavailableTiming(actual, context);
            Check(actual, actual.ExcludedTimingTurnCount == 1, context + ": expected one excluded completion");
        }
    }

    private static void ExpectToolCoverage(DailySnapshot actual, int turns, bool timed, bool lost, string context,
        bool activityConflict = false)
    {
        Expect(actual, 100, 60, 40, 10, lost || activityConflict, context);
        Check(actual, actual.SourceCount == 1 && actual.TurnCount == turns && actual.ActiveTurns == 0
            && actual.ActiveSeconds == 10
            && actual.Issues.Any(issue => issue.Code == DailyIssueCodes.CoverageLost) == lost
            && actual.Issues.Any(issue => issue.Code == DailyIssueCodes.SourcePartial) == lost
            && actual.Issues.Any(issue => issue.Code == DailyIssueCodes.CurrentStartConflict) == activityConflict,
            context + ": expected unchanged activities and an independent coverage diagnostic");
        if (timed)
            Check(actual, actual.ProcessingSeconds == 6 && actual.TimedTurnCount == 1 && actual.ExcludedTimingTurnCount == 0
                && actual.AverageTps is { } tps && Math.Abs(tps - 40d / 6) < 0.000001, context + ": expected the observed six-second TPS");
        else
        {
            ExpectUnavailableTiming(actual, context);
            Check(actual, actual.ExcludedTimingTurnCount == 1, context + ": expected one excluded completion");
        }
    }

    private static void ReplaceSession(Fixture fixture, string path, string contents) =>
        File.Move(fixture.WriteSession("replacement", contents), path, overwrite: true);

    private static async Task ReplacePreservedSession(Fixture fixture, string path, string prefix, string contents)
    {
        // The bytes establish the same prefix. With no coverage-replay request,
        // a cursor reset on the separately created replacement proves identity changed.
        using var identityProbe = new DailyLogReader(path);
        var first = await identityProbe.ReadAvailableAsync(_ => { });
        if (first.ResetDetected || first.BytesRead != Utf8.GetByteCount(prefix))
            throw new InvalidOperationException("Identity probe did not establish the original cursor.");
        ReplaceSession(fixture, path, contents);
        var replaced = await identityProbe.ReadAvailableAsync(_ => { });
        if (!replaced.ResetDetected || replaced.BytesRead != Utf8.GetByteCount(contents))
            throw new InvalidOperationException("The byte-preserving replacement did not reset by identity.");
    }

    private static void ExpectAbortSourceDiagnostics(DailySnapshot actual, string context) => Check(actual,
        actual.Issues.Any(issue => issue.Code == DailyIssueCodes.SourceUnreadable)
        && actual.Issues.Any(issue => issue.Code == DailyIssueCodes.SourcePartial)
        && actual.Issues.Any(issue => issue.Code == DailyIssueCodes.TimingUnavailable),
        context + ": expected source and timing diagnostics to remain independent of activity conflict");

    private static void ExpectInactiveToday(DailySnapshot actual, bool partial, string context)
    {
        Expect(actual, null, null, null, null, partial, context);
        Check(actual, actual.Day == new DateOnly(2026, 10, 21) && actual.ActiveTurns == 0 && actual.ActiveSeconds is null,
            context + ": expected today's active turns=0 and no active seconds");
        ExpectUnavailableTiming(actual, context);
    }

    private static void ExpectTiedStarts(DailySnapshot actual, double? processing, bool conflict, string context, bool? partial = null)
    {
        Expect(actual, 200, 120, 80, 20, partial ?? conflict, context);
        Check(actual, actual.Issues.Any(issue => issue.Code == DailyIssueCodes.CurrentStartConflict) == conflict,
            context + ": expected current-start conflict to track the unresolved tie");
        Check(actual, actual.TurnCount == 2 && actual.ActiveTurns == 0 && actual.ActiveSeconds == 10,
            context + ": expected two turns, zero running, and ten active seconds");
        if (processing is null) ExpectUnavailableTiming(actual, context);
        else Check(actual, actual.ProcessingSeconds == processing && actual.TimedTurnCount == processing.Value / 9
            && actual.AverageTps is { } tps && Math.Abs(tps - 40d / 9) < 0.000001,
            context + ": expected retained completed-turn timing");
    }

    private static void Check(DailySnapshot actual, bool condition, string context)
    {
        if (!condition) throw new InvalidOperationException(context + "; " + Describe(actual));
    }

    private static string Describe(DailySnapshot value) => $"actual input={value.InputTokens}, cached={value.CachedInputTokens}, "
        + $"output={value.OutputTokens}, reasoning={value.ReasoningTokens}, Partial={value.IsPartial}, "
        + $"ActiveTurns={value.ActiveTurns}, ActiveSeconds={value.ActiveSeconds}, TPS={value.AverageTps}, "
        + $"ProcessingSeconds={value.ProcessingSeconds}, TimedTurns={value.TimedTurnCount}, "
        + $"ExcludedTimingTurns={value.ExcludedTimingTurnCount}, Day={value.Day}; Status={value.Status}";

    private static string Session(string name) => "synthetic-closure-session-" + name;
    private static string Turn(string name) => "synthetic-closure-turn-" + name;

    private static string RootMetadata(string session, DateTimeOffset time) =>
        Row("session_meta", time, new { id = Session(session), source = "cli" });

    private static string ChildMetadata(string session, string parent, DateTimeOffset time) => Row("session_meta", time, new
    {
        id = Session(session),
        source = new { subagent = new { thread_spawn = new { parent_thread_id = Session(parent) } } }
    });

    private static string Modern(string session, string request, string turn, DateTimeOffset time) => Row("token_usage_record", time, new
    {
        thread_id = Session(session), session_id = "synthetic-closure-run", turn_id = Turn(turn), root_turn_id = Turn(turn),
        response_id = "synthetic-closure-request-" + request,
        usage = Vector(), turn_token_usage = Vector(), thread_token_usage = Vector()
    });

    private static object Vector() => new
    {
        input_tokens = 100, cached_input_tokens = 60, output_tokens = 40, reasoning_output_tokens = 10, total_tokens = 140
    };

    private static string Start(string turn, DateTimeOffset time) =>
        Row("event_msg", time, new { type = "task_started", turn_id = Turn(turn) });

    private static string Complete(string turn, DateTimeOffset time) => Row("event_msg", time, new
    {
        type = "task_complete", turn_id = Turn(turn), duration_ms = 10000, time_to_first_token_ms = 1000
    });

    private static string Tool(string kind, string call, DateTimeOffset time) =>
        Row("response_item", time, new { type = kind, call_id = call });

    private static string AbortRow(string kind, string turn, DateTimeOffset timestamp, object? envelopeVersion = null, object? payloadVersion = null)
    {
        var payload = new Dictionary<string, object> { ["type"] = kind, ["turn_id"] = Turn(turn) };
        if (payloadVersion is not null) payload["schema_version"] = payloadVersion;
        var envelope = new Dictionary<string, object> { ["type"] = "event_msg", ["timestamp"] = timestamp, ["payload"] = payload };
        if (envelopeVersion is not null) envelope["schema_version"] = envelopeVersion;
        return JsonSerializer.Serialize(envelope) + "\n";
    }

    private static string AbortEnvelope(string payload, string? timestamp = null)
    {
        timestamp ??= JsonSerializer.Serialize(At.AddSeconds(10));
        return "{\"type\":\"event_msg\",\"timestamp\":" + timestamp + ",\"payload\":" + payload + "}\n";
    }

    private static string EarlyChildRows() => ChildMetadata("A", "parent", At.AddMinutes(-1))
        + Modern("A", "shared-request", "same-day-turn", At.AddSeconds(-30));

    private static string CurrentChildRows() => ChildMetadata("A", "parent", At.AddSeconds(-1))
        + Start("same-day-turn", At) + Modern("A", "shared-request", "same-day-turn", At.AddSeconds(9))
        + Complete("same-day-turn", At.AddSeconds(10));

    private static string TiedStartRows() => RootMetadata("tied", At.AddMinutes(-1))
        + Start("tied-first", At) + Start("tied-second", At)
        + Modern("tied", "tied-first-request", "tied-first", At.AddSeconds(9))
        + Modern("tied", "tied-second-request", "tied-second", At.AddSeconds(9));

    private static string OldOpenRows() => RootMetadata("activity", OldStart.AddHours(-1)) + Start("old-turn", OldStart);

    private static string OpenSuccessorRows() => RootMetadata("activity", SuccessorStart.AddMinutes(-1))
        + Start("successor-turn", SuccessorStart);

    private static string CompletedSuccessorRows() => OpenSuccessorRows()
        + Modern("activity", "successor-request", "successor-turn", SuccessorStart.AddSeconds(9))
        + Complete("successor-turn", SuccessorStart.AddSeconds(10));

    private static string AbortedSuccessorRows() => OpenSuccessorRows()
        + Modern("activity", "successor-request", "successor-turn", SuccessorStart.AddSeconds(9))
        + Row("event_msg", SuccessorStart.AddSeconds(10), new { type = "task_aborted", turn_id = Turn("successor-turn") });

    private static string Row(string type, DateTimeOffset timestamp, object payload) =>
        JsonSerializer.Serialize(new { type, timestamp, payload }) + "\n";

    private sealed class Fixture : IDisposable
    {
        private readonly DateTimeOffset _modifiedAt;
        private readonly string? _archiveDirectory;
        private int _archiveSequence;
        public string Home { get; }

        public Fixture(string name, DateTimeOffset modifiedAt)
        {
            _modifiedAt = modifiedAt;
            Home = Path.Combine(Path.GetTempPath(), "CodexDaily-closure-" + name + "-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(Home, "sessions"));
            Directory.CreateDirectory(Path.Combine(Home, "archived_sessions"));
            var archiveRoot = Environment.GetEnvironmentVariable("CODEX_DAILY_BOUNDARY_INPUTS");
            if (!string.IsNullOrWhiteSpace(archiveRoot)) _archiveDirectory = Path.Combine(archiveRoot, name);
        }

        public string WriteSession(string name, string contents, bool archived = false)
        {
            // Both destinations receive the same bytes from this fixture's synthetic
            // string. Archival never reads or enumerates any existing log directory.
            var bytes = Utf8.GetBytes(contents);
            var path = Path.Combine(Home, archived ? "archived_sessions" : "sessions", name + ".jsonl");
            File.WriteAllBytes(path, bytes);
            File.SetLastWriteTimeUtc(path, _modifiedAt.UtcDateTime);
            if (_archiveDirectory is not null)
            {
                Directory.CreateDirectory(_archiveDirectory);
                var archivePath = Path.Combine(_archiveDirectory, $"{++_archiveSequence:D2}-{name}.jsonl");
                using var archive = new FileStream(archivePath, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
                archive.Write(bytes);
            }
            return path;
        }

        public void AppendSession(string path, string contents)
        {
            File.AppendAllText(path, contents, Utf8);
            File.SetLastWriteTimeUtc(path, _modifiedAt.UtcDateTime);
        }

        public void TruncateSession(string path, long byteLength)
        {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Write)) stream.SetLength(byteLength);
            File.SetLastWriteTimeUtc(path, _modifiedAt.UtcDateTime);
        }

        public void Dispose() => Directory.Delete(Home, recursive: true);
    }
}
