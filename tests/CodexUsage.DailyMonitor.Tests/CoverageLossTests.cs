using System.Text;
using System.Text.Json;
using CodexUsage.Daily;
using CodexUsage.DailyPanel;

internal static class CoverageLossTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 21, 12, 0, 0, TimeSpan.FromHours(8));
    private static readonly DateTimeOffset Now = At.AddMinutes(10);
    private static readonly TimeZoneInfo Zone = TimeZoneInfo.CreateCustomTimeZone(
        "SyntheticCoverageUTC8", TimeSpan.FromHours(8), "UTC+8", "UTC+8");
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

        await Case("COV-01 truncation withdraws old counters and retains daily coverage debt", async () =>
        {
            using var fixture = new Fixture("truncate");
            var first = Metadata("A") + Modern("A", "r1", 100, 60, 40, 10);
            fixture.WriteSession("A", first + Modern("A", "r2", 200, 80, 70, 20));
            using var monitor = new SessionLogMonitor(fixture.Home, Zone);
            Expect(await monitor.RefreshAsync(Now), 300, 140, 110, 30, false, "before truncation");
            fixture.WriteSession("A", first);
            Expect(await monitor.RefreshAsync(Now), 100, 60, 40, 10, true, "truncated subtotal");
            Expect(await monitor.RefreshAsync(Now), 100, 60, 40, 10, true, "idle debt persists");
        });

        await Case("COV-02 equal-length replacement does not imply complete daily coverage", async () =>
        {
            using var fixture = new Fixture("same-length");
            var oldRows = Metadata("A") + Modern("A", "rA", 100, 60, 40, 10);
            var newRows = Metadata("A") + Modern("A", "rB", 200, 80, 70, 20);
            Require(Utf8.GetByteCount(oldRows) == Utf8.GetByteCount(newRows), "fixture must preserve byte length");
            fixture.WriteSession("A", oldRows);
            using var monitor = new SessionLogMonitor(fixture.Home, Zone);
            Expect(await monitor.RefreshAsync(Now), 100, 60, 40, 10, false, "before replacement");
            fixture.WriteSession("A", newRows);
            Expect(await monitor.RefreshAsync(Now), 200, 80, 70, 20, true, "replacement subtotal");
        });

        await Case("COV-03 longer replacement also retains missing generation evidence", async () =>
        {
            using var fixture = new Fixture("longer");
            fixture.WriteSession("A", Metadata("A") + Modern("A", "r1", 100, 60, 40, 10));
            using var monitor = new SessionLogMonitor(fixture.Home, Zone);
            Expect(await monitor.RefreshAsync(Now), 100, 60, 40, 10, false, "before longer replacement");
            fixture.WriteSession("A", Metadata("A") + Modern("A", "a-much-longer-request", 200, 80, 70, 20)
                + Row("response_item", At, new { type = "message", content = new string('x', 2048) }));
            Expect(await monitor.RefreshAsync(Now), 200, 80, 70, 20, true, "longer replacement subtotal");
        });

        await Case("COV-04 deleting A preserves only B's readable subtotal and a loss reason", async () =>
        {
            using var fixture = new Fixture("delete");
            var a = fixture.WriteSession("A", Metadata("A") + Modern("A", "rA", 100, 60, 40, 10));
            fixture.WriteSession("B", Metadata("B") + Modern("B", "rB", 200, 80, 70, 20));
            using var monitor = new SessionLogMonitor(fixture.Home, Zone);
            Expect(await monitor.RefreshAsync(Now), 300, 140, 110, 30, false, "before deletion");
            File.Delete(a);
            var lost = await monitor.RefreshAsync(Now);
            Expect(lost, 200, 80, 70, 20, true, "B remains readable");
            Require(lost.SourceCount == 1, "debt must not count as a current source");
            Expect(await monitor.RefreshAsync(Now), 200, 80, 70, 20, true, "idle deletion remains partial");
        });

        await Case("COV-05 exact clean full replay restores coverage without restoring stale totals", async () =>
        {
            using var fixture = new Fixture("full-replay");
            var first = Metadata("A") + Modern("A", "r1", 100, 60, 40, 10);
            var complete = first + Modern("A", "r2", 200, 80, 70, 20);
            fixture.WriteSession("A", complete);
            using var monitor = new SessionLogMonitor(fixture.Home, Zone);
            Expect(await monitor.RefreshAsync(Now), 300, 140, 110, 30, false, "original full file");
            fixture.WriteSession("A", first);
            Expect(await monitor.RefreshAsync(Now), 100, 60, 40, 10, true, "before replay");
            fixture.WriteSession("A", complete);
            Expect(await monitor.RefreshAsync(Now), 300, 140, 110, 30, false, "full replay proves recovery");
            Expect(await monitor.RefreshAsync(Now), 300, 140, 110, 30, false, "recovered idle poll");
        });

        await Case("COV-06 split migration from sessions to archive recovers the full event union", async () =>
        {
            using var fixture = new Fixture("split-migration");
            var metadata = Metadata("A");
            var one = Modern("A", "r1", 100, 60, 40, 10);
            var two = Modern("A", "r2", 200, 80, 70, 20);
            var old = fixture.WriteSession("A", metadata + one + two);
            using var monitor = new SessionLogMonitor(fixture.Home, Zone);
            Expect(await monitor.RefreshAsync(Now), 300, 140, 110, 30, false, "original source");
            File.Delete(old);
            fixture.WriteArchive("part-one", metadata + one);
            Expect(await monitor.RefreshAsync(Now), 100, 60, 40, 10, true, "incomplete migration");
            fixture.WriteArchive("part-two", metadata + two);
            Expect(await monitor.RefreshAsync(Now), 300, 140, 110, 30, false, "all migrated events present");
        });

        await Case("COV-07 repeated recovered events across files never double count", async () =>
        {
            using var fixture = new Fixture("duplicate-replay");
            var rows = Metadata("A") + Modern("A", "r1", 100, 60, 40, 10);
            var old = fixture.WriteSession("A", rows);
            using var monitor = new SessionLogMonitor(fixture.Home, Zone);
            Expect(await monitor.RefreshAsync(Now), 100, 60, 40, 10, false, "original request");
            File.Delete(old);
            Expect(await monitor.RefreshAsync(Now), null, null, null, null, true, "all current counters withdrawn");
            fixture.WriteArchive("copy-one", rows + Modern("A", "r1", 100, 60, 40, 10));
            fixture.WriteArchive("copy-two", rows);
            Expect(await monitor.RefreshAsync(Now), 100, 60, 40, 10, false, "duplicate replay counted once");
            Expect(await monitor.RefreshAsync(Now), 100, 60, 40, 10, false, "duplicate idle poll");
        });

        await Case("COV-08 yesterday's unresolved loss expires at the local date boundary", async () =>
        {
            using var fixture = new Fixture("rollover");
            var old = fixture.WriteSession("yesterday", Metadata("A", At.AddDays(-1))
                + Modern("A", "yesterday-request", 100, 60, 40, 10, At.AddDays(-1)));
            using var monitor = new SessionLogMonitor(fixture.Home, Zone);
            Expect(await monitor.RefreshAsync(Now.AddDays(-1)), 100, 60, 40, 10, false, "yesterday known");
            File.Delete(old);
            Expect(await monitor.RefreshAsync(Now.AddDays(-1).AddSeconds(1)), null, null, null, null, true, "yesterday lost");
            fixture.WriteSession("today", Metadata("B") + Modern("B", "today-request", 200, 80, 70, 20));
            Expect(await monitor.RefreshAsync(Now), 200, 80, 70, 20, false, "today has independent coverage");
        });

        await Case("COV-09 losing a yesterday-only file does not create today's coverage debt", async () =>
        {
            using var fixture = new Fixture("historical-only");
            var old = fixture.WriteSession("yesterday", Metadata("A", At.AddDays(-1))
                + Modern("A", "yesterday-request", 100, 60, 40, 10, At.AddDays(-1)));
            fixture.WriteSession("today", Metadata("B") + Modern("B", "today-request", 200, 80, 70, 20));
            using var monitor = new SessionLogMonitor(fixture.Home, Zone);
            Expect(await monitor.RefreshAsync(Now), 200, 80, 70, 20, false, "only today's request counted");
            File.Delete(old);
            Expect(await monitor.RefreshAsync(Now), 200, 80, 70, 20, false, "historical removal does not taint today");
        });

        await Case("COV-10 residual valid events cannot erase lost malformed input", async () =>
        {
            using var fixture = new Fixture("malformed");
            var clean = Metadata("A") + Modern("A", "r1", 100, 60, 40, 10);
            fixture.WriteSession("A", clean + "{malformed-synthetic-row}\n");
            using var monitor = new SessionLogMonitor(fixture.Home, Zone);
            Expect(await monitor.RefreshAsync(Now), 100, 60, 40, 10, true, "malformed source subtotal");
            fixture.WriteSession("A", clean);
            Expect(await monitor.RefreshAsync(Now), 100, 60, 40, 10, true, "malformed evidence was removed");
            Expect(await monitor.RefreshAsync(Now), 100, 60, 40, 10, true, "opaque debt persists on idle");
        });

        await Case("COV-11 a clean mirror cannot erase a deleted malformed source's unknown bytes", async () =>
        {
            using var fixture = new Fixture("malformed-mirror");
            var clean = Metadata("A") + Modern("A", "r1", 100, 60, 40, 10);
            var old = fixture.WriteSession("A", clean + "{unknown-synthetic-statistics}\n");
            using var monitor = new SessionLogMonitor(fixture.Home, Zone);
            Expect(await monitor.RefreshAsync(Now), 100, 60, 40, 10, true, "unknown source contents");
            File.Delete(old);
            fixture.WriteArchive("clean-copy", clean);
            Expect(await monitor.RefreshAsync(Now), 100, 60, 40, 10, true, "valid event equality cannot prove unknown input");
        });

        await Case("COV-12 exclusive read failure clears after the original file is readable again", async () =>
        {
            using var fixture = new Fixture("locked");
            var path = fixture.WriteSession("A", Metadata("A") + Modern("A", "r1", 100, 60, 40, 10));
            using var monitor = new SessionLogMonitor(fixture.Home, Zone);
            Expect(await monitor.RefreshAsync(Now), 100, 60, 40, 10, false, "readable baseline");
            using (var held = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                Expect(await monitor.RefreshAsync(Now), null, null, null, null, true, "locked source is unavailable");
            Expect(await monitor.RefreshAsync(Now), 100, 60, 40, 10, false, "original complete source recovered");
            Expect(await monitor.RefreshAsync(Now), 100, 60, 40, 10, false, "read error does not become permanent");
        });

        await Case("COV-13 losing an uncovered legacy source cannot make the remaining modern subtotal complete", async () =>
        {
            using var fixture = new Fixture("legacy-loss");
            fixture.WriteSession("modern", Metadata("A") + Modern("A", "r1", 100, 60, 40, 10));
            var legacy = fixture.WriteSession("legacy", Metadata("B") + Row("event_msg", At, new
            {
                type = "token_count", info = new
                {
                    last_token_usage = Vector(900, 500, 100, 20),
                    total_token_usage = Vector(900, 500, 100, 20)
                }
            }));
            using var monitor = new SessionLogMonitor(fixture.Home, Zone);
            var mixed = await monitor.RefreshAsync(Now);
            Expect(mixed, 100, 60, 40, 10, true, "legacy is never added to modern subtotal");
            Require(mixed.Status.Contains("旧格式", StringComparison.Ordinal), "baseline must expose the legacy cause");
            File.Delete(legacy);
            Expect(await monitor.RefreshAsync(Now), 100, 60, 40, 10, true, "legacy loss retains incomplete coverage");
        });

        await Case("COV-14 matching request identity alone cannot prove the withdrawn revision returned", async () =>
        {
            using var fixture = new Fixture("changed-revision");
            fixture.WriteSession("A", Metadata("A") + Modern("A", "r1", 100, 60, 40, 10));
            using var monitor = new SessionLogMonitor(fixture.Home, Zone);
            Expect(await monitor.RefreshAsync(Now), 100, 60, 40, 10, false, "original request revision");
            fixture.WriteSession("A", Metadata("A") + Modern("A", "r1", 200, 80, 70, 20));
            Expect(await monitor.RefreshAsync(Now), 200, 80, 70, 20, true, "same identity has different payload evidence");
        });

        await Case("COV-15 deleting a source that failed its first read cannot erase unknown coverage", async () =>
        {
            using var fixture = new Fixture("initial-lock");
            var unavailable = fixture.WriteSession("A", Metadata("A") + Modern("A", "rA", 100, 60, 40, 10));
            fixture.WriteSession("B", Metadata("B") + Modern("B", "rB", 200, 80, 70, 20));
            using var monitor = new SessionLogMonitor(fixture.Home, Zone);
            using (var held = new FileStream(unavailable, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                Expect(await monitor.RefreshAsync(Now), 200, 80, 70, 20, true, "A has no retained events but its read failed");
            File.Delete(unavailable);
            Expect(await monitor.RefreshAsync(Now), 200, 80, 70, 20, true, "deleting unread A cannot complete B's subtotal");
            Expect(await monitor.RefreshAsync(Now), 200, 80, 70, 20, true, "unknown first-read loss persists on idle");
        });

        await Case("COV-16 read failure followed by half-line loss cannot be repaired by a clean event mirror", async () =>
        {
            using var fixture = new Fixture("half-line-loss");
            var clean = Metadata("A") + Modern("A", "r1", 100, 60, 40, 10);
            var pending = Modern("A", "r2", 200, 80, 70, 20);
            var path = fixture.WriteSession("A", clean + pending[..(pending.Length / 2)]);
            using var monitor = new SessionLogMonitor(fixture.Home, Zone);
            Expect(await monitor.RefreshAsync(Now), 100, 60, 40, 10, true, "unfinished second row");
            using (var held = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                Expect(await monitor.RefreshAsync(Now), null, null, null, null, true, "half-line source temporarily unreadable");
            File.Delete(path);
            fixture.WriteArchive("clean-mirror", clean);
            Expect(await monitor.RefreshAsync(Now), 100, 60, 40, 10, true, "mirror lacks unknown half-line bytes");
        });

        await Case("COV-17 exact raw half-line prefix replayed and completed across paths proves recovery", async () =>
        {
            using var fixture = new Fixture("half-line-complete");
            var clean = Metadata("A") + Modern("A", "r1", 100, 60, 40, 10);
            var pending = Modern("A", "r2", 200, 80, 70, 20);
            var path = fixture.WriteSession("A", clean + pending[..(pending.Length / 2)]);
            using var monitor = new SessionLogMonitor(fixture.Home, Zone);
            Expect(await monitor.RefreshAsync(Now), 100, 60, 40, 10, true, "original raw prefix is unfinished");
            File.Delete(path);
            Expect(await monitor.RefreshAsync(Now), null, null, null, null, true, "unfinished source removed");
            fixture.WriteArchive("completed-replay", clean + pending);
            Expect(await monitor.RefreshAsync(Now), 300, 140, 110, 30, false, "exact prefix plus complete second row recovers coverage");
            Expect(await monitor.RefreshAsync(Now), 300, 140, 110, 30, false, "recovered prefix is not counted twice");
        });

        await Case("COV-18 an already drained longer mirror can replay a newly challenged raw prefix", async () =>
        {
            using var fixture = new Fixture("already-drained-mirror");
            var clean = Metadata("A") + Modern("A", "r1", 100, 60, 40, 10);
            var pending = Modern("A", "r2", 200, 80, 70, 20);
            var shortPath = fixture.WriteSession("short", clean + pending[..(pending.Length / 2)]);
            fixture.WriteArchive("long-mirror", clean + pending);
            using var monitor = new SessionLogMonitor(fixture.Home, Zone);
            Expect(await monitor.RefreshAsync(Now), 300, 140, 110, 30, true, "long mirror drained while short source has a half-line");
            File.Delete(shortPath);
            Expect(await monitor.RefreshAsync(Now), 300, 140, 110, 30, false, "existing longer mirror proves the newly lost prefix");
            Expect(await monitor.RefreshAsync(Now), 300, 140, 110, 30, false, "replay does not leave permanent debt or duplicate totals");
        });

        await Case("COV-19 a later quota snapshot does not hide proof that the old generation was replayed", async () =>
        {
            using var fixture = new Fixture("quota-replay");
            var original = Metadata("A") + Modern("A", "r1", 100, 60, 40, 10) + Quota(At, 20);
            var path = fixture.WriteSession("A", original);
            using var monitor = new SessionLogMonitor(fixture.Home, Zone);
            var baseline = await monitor.RefreshAsync(Now);
            Expect(baseline, 100, 60, 40, 10, false, "original generation with quota");
            Require(baseline.Weekly?.UsedPercent == 20, "original quota sample is 20 percent");
            File.Delete(path);
            Expect(await monitor.RefreshAsync(Now), null, null, null, null, true, "generation removed before replay");
            fixture.WriteArchive("replay-with-latest-quota", original + Quota(At.AddSeconds(1), 30));
            var replay = await monitor.RefreshAsync(Now);
            Expect(replay, 100, 60, 40, 10, false, "full old generation replayed before quota update");
            Require(replay.Weekly?.UsedPercent == 30, "latest quota replaces the old sample without permanent coverage debt");
            Expect(await monitor.RefreshAsync(Now), 100, 60, 40, 10, false, "quota replacement remains fully covered on idle");
        });

        await Case("COV-20 deleting a confirmed child source does not taint independent root coverage", async () =>
        {
            using var fixture = new Fixture("child-loss");
            fixture.WriteSession("root", Metadata("A") + Modern("A", "root-request", 100, 60, 40, 10));
            var child = fixture.WriteSession("child", Row("session_meta", At.AddHours(-1), new
            {
                id = "synthetic-coverage-child",
                source = new { subagent = new { thread_spawn = new { parent_thread_id = "synthetic-coverage-A" } } }
            }) + Modern("child", "child-request", 200, 80, 70, 20));
            using var monitor = new SessionLogMonitor(fixture.Home, Zone);
            Expect(await monitor.RefreshAsync(Now), 100, 60, 40, 10, false, "confirmed child is outside root statistics");
            File.Delete(child);
            Expect(await monitor.RefreshAsync(Now), 100, 60, 40, 10, false, "child deletion does not create root coverage debt");
        });

        await Case("COV-21 deleting yesterday's request replayed today does not taint today's independent request", async () =>
        {
            using var fixture = new Fixture("historical-request-replay");
            var historical = fixture.WriteSession("historical", Metadata("A", At.AddDays(-1))
                + Modern("A", "same-request", 100, 60, 40, 10, At.AddDays(-1))
                + Modern("A", "same-request", 100, 60, 40, 10, At));
            fixture.WriteSession("today", Metadata("B") + Modern("B", "today-request", 200, 80, 70, 20));
            using var monitor = new SessionLogMonitor(fixture.Home, Zone);
            Expect(await monitor.RefreshAsync(Now), 200, 80, 70, 20, false, "request replay keeps yesterday's first occurrence");
            File.Delete(historical);
            Expect(await monitor.RefreshAsync(Now), 200, 80, 70, 20, false, "historical replay loss has no independent daily coverage");
        });

        await Case("COV-22 deleting a stale yesterday start does not create today's active coverage debt", async () =>
        {
            using var fixture = new Fixture("stale-start-loss");
            var stale = fixture.WriteSession("stale", Metadata("A", At.AddDays(-1))
                + Row("event_msg", At.AddDays(-1), new { type = "task_started", turn_id = "synthetic-stale-turn" }));
            fixture.WriteSession("today", Metadata("B") + Modern("B", "today-request", 200, 80, 70, 20));
            using var monitor = new SessionLogMonitor(fixture.Home, Zone);
            var baseline = await monitor.RefreshAsync(Now);
            Expect(baseline, 200, 80, 70, 20, false, "yesterday's unfinished start is older than 15 minutes");
            Require(baseline.ActiveTurns == 0 && baseline.ActiveSeconds is null, "stale start is not observed running today");
            File.Delete(stale);
            var after = await monitor.RefreshAsync(Now);
            Expect(after, 200, 80, 70, 20, false, "stale start deletion does not taint today's independent request");
            Require(after.ActiveTurns == 0 && after.ActiveSeconds is null, "deleting stale start does not invent daily activity");
        });

        await Case("COV-23 recovering from a transient read lock preserves an existing format cause", async () =>
        {
            using var fixture = new Fixture("malformed-lock-recovery");
            var path = fixture.WriteSession("A", Metadata("A") + Modern("A", "r1", 100, 60, 40, 10)
                + "{malformed-synthetic-format-row}\n");
            using var monitor = new SessionLogMonitor(fixture.Home, Zone);
            var baseline = await monitor.RefreshAsync(Now);
            Expect(baseline, 100, 60, 40, 10, true, "format error is present before the read lock");
            Require(baseline.Status.Contains("格式", StringComparison.Ordinal), "baseline includes the source format cause");
            using (var held = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                Expect(await monitor.RefreshAsync(Now), null, null, null, null, true, "malformed source is temporarily unreadable");
            var recovered = await monitor.RefreshAsync(Now);
            Expect(recovered, 100, 60, 40, 10, true, "read recovery does not repair malformed bytes");
            Require(recovered.Status.Contains("格式", StringComparison.Ordinal), "format cause survives the transient read failure");
            Expect(await monitor.RefreshAsync(Now), 100, 60, 40, 10, true, "format partiality remains on idle");
        });

        await Case("COV-24 removing unrelated yesterday history preserves today's fully replayed request", async () =>
        {
            using var fixture = new Fixture("unrelated-history-removal");
            var metadata = Metadata("A");
            var today = Modern("A", "today-request", 200, 80, 70, 20);
            fixture.WriteSession("A", metadata
                + Modern("A", "unrelated-yesterday-request", 100, 60, 40, 10, At.AddDays(-1)) + today);
            using var monitor = new SessionLogMonitor(fixture.Home, Zone);
            Expect(await monitor.RefreshAsync(Now), 200, 80, 70, 20, false, "unrelated old request is outside today's subtotal");
            fixture.WriteSession("A", metadata + today);
            Expect(await monitor.RefreshAsync(Now), 200, 80, 70, 20, false, "today's exact request remains after old history is removed");
            Expect(await monitor.RefreshAsync(Now), 200, 80, 70, 20, false, "unrelated history creates no persistent debt");
        });

        await Case("COV-25 deleting a today-only no-op replay respects the request's first occurrence in another file", async () =>
        {
            using var fixture = new Fixture("global-request-occurrence");
            var metadata = Metadata("A", At.AddDays(-1));
            fixture.WriteSession("first-occurrence", metadata
                + Modern("A", "same-request", 100, 60, 40, 10, At.AddDays(-1)));
            var replay = fixture.WriteSession("today-replay", metadata
                + Modern("A", "same-request", 100, 60, 40, 10, At));
            fixture.WriteSession("independent-today", Metadata("B")
                + Modern("B", "today-request", 200, 80, 70, 20));
            using var monitor = new SessionLogMonitor(fixture.Home, Zone);
            Expect(await monitor.RefreshAsync(Now), 200, 80, 70, 20, false, "global first occurrence assigns A to yesterday");
            File.Delete(replay);
            Expect(await monitor.RefreshAsync(Now), 200, 80, 70, 20, false, "removing A's today replay leaves independent B complete");
            Expect(await monitor.RefreshAsync(Now), 200, 80, 70, 20, false, "source-local replay cannot create daily coverage debt");
        });

        Console.WriteLine($"RESULT COVERAGE LOSS: {passed} passed, {failures.Count} failed");
        if (failures.Count > 0)
            throw new InvalidOperationException("Coverage loss regressions:\n" + string.Join("\n", failures));
    }

    private static void Expect(DailySnapshot actual, long? input, long? cached, long? output,
        long? reasoning, bool partial, string context)
    {
        Require(actual.InputTokens == input && actual.CachedInputTokens == cached
            && actual.OutputTokens == output && actual.ReasoningTokens == reasoning && actual.IsPartial == partial,
            $"{context}: expected input={input}, cached={cached}, output={output}, reasoning={reasoning}, Partial={partial}; "
            + $"actual input={actual.InputTokens}, cached={actual.CachedInputTokens}, output={actual.OutputTokens}, "
            + $"reasoning={actual.ReasoningTokens}, Partial={actual.IsPartial}; Status={actual.Status}");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static string Metadata(string session, DateTimeOffset? time = null) =>
        Row("session_meta", (time ?? At).AddHours(-1), new { id = "synthetic-coverage-" + session, source = "cli" });

    private static string Modern(string session, string request, long input, long cached, long output,
        long reasoning, DateTimeOffset? time = null) => Row("token_usage_record", time ?? At, new
        {
            thread_id = "synthetic-coverage-" + session, session_id = "synthetic-run",
            turn_id = "synthetic-turn-" + request, root_turn_id = "synthetic-turn-" + request,
            response_id = "synthetic-request-" + request,
            usage = Vector(input, cached, output, reasoning),
            turn_token_usage = Vector(input, cached, output, reasoning),
            thread_token_usage = Vector(input, cached, output, reasoning)
        });

    private static object Vector(long input, long cached, long output, long reasoning) => new
    {
        input_tokens = input, cached_input_tokens = cached, output_tokens = output,
        reasoning_output_tokens = reasoning, total_tokens = input + output
    };

    private static string Quota(DateTimeOffset timestamp, double used) => Row("event_msg", timestamp, new
    {
        type = "token_count", rate_limits = new
        {
            limit_id = "synthetic-coverage-limit",
            secondary = new { used_percent = used, window_minutes = 10080, resets_at = Now.AddDays(1).ToUnixTimeSeconds() }
        }
    });

    private static string Row(string type, DateTimeOffset timestamp, object payload) =>
        JsonSerializer.Serialize(new { type, timestamp, payload }) + "\n";

    private sealed class Fixture : IDisposable
    {
        public string Home { get; }

        public Fixture(string name)
        {
            Home = Path.Combine(Path.GetTempPath(), "CodexDaily-coverage-" + name + "-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(Home, "sessions"));
            Directory.CreateDirectory(Path.Combine(Home, "archived_sessions"));
        }

        public string WriteSession(string name, string contents) => Write("sessions", name, contents);
        public string WriteArchive(string name, string contents) => Write("archived_sessions", name, contents);

        private string Write(string folder, string name, string contents)
        {
            var path = Path.Combine(Home, folder, name + ".jsonl");
            File.WriteAllText(path, contents, Utf8);
            File.SetLastWriteTimeUtc(path, Now.UtcDateTime);
            return path;
        }

        public void Dispose() => Directory.Delete(Home, recursive: true);
    }
}
