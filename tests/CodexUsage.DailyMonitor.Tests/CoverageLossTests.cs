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

        await Case("COV-26 loss and recovery retain timing for each independently verified turn", async () =>
        {
            using var fixture = new Fixture("timing-per-turn");
            var first = Metadata("A") + Completed("A", "r1", At);
            var second = Completed("A", "r2", At.AddMinutes(1));
            var third = Completed("A", "r3", At.AddMinutes(2));
            fixture.WriteSession("A", first + second + third);
            using var monitor = new SessionLogMonitor(fixture.Home, Zone);
            ExpectTiming(await monitor.RefreshAsync(Now), 300, 120, 27, 3, 0, false, "all three turns");
            fixture.WriteSession("A", first);
            var truncated = await monitor.RefreshAsync(Now);
            ExpectTiming(truncated, 100, 40, 9, 1, 0, true, "only the verified prefix remains");
            Require(truncated.Issues.Any(x => x.Code == DailyIssueCodes.CoverageLost), "truncation still loses daily coverage");

            var mirror = fixture.WriteArchive("split", Metadata("A") + second);
            ExpectTiming(await monitor.RefreshAsync(Now), 200, 80, 18, 2, 0, true, "exact split recovery with third still missing");
            using (var locked = new FileStream(mirror, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                ExpectTiming(await monitor.RefreshAsync(Now), 100, 40, null, 0, 1, true, "an unreadable mirror has an unknown tail in the same session");
            ExpectTiming(await monitor.RefreshAsync(Now), 200, 80, 18, 2, 0, true, "unlocked mirror proves its turn again");
            fixture.WriteSession("A", first + second + third);
            ExpectTiming(await monitor.RefreshAsync(Now), 300, 120, 27, 3, 0, false, "restoration and exact mirror do not double count");
        });

        await Case("COV-27 withdrawn same-order conflict does not invalidate intact sibling timing", async () =>
        {
            using var fixture = new Fixture("timing-beside-conflict");
            var first = Metadata("A") + Completed("A", "r1", At);
            var conflict = Modern("A", "r1", 0, 0, 0, 0, At.AddSeconds(9));
            var second = Completed("A", "r2", At.AddMinutes(1));
            fixture.WriteSession("A", first + conflict + second);
            using var monitor = new SessionLogMonitor(fixture.Home, Zone);
            ExpectTiming(await monitor.RefreshAsync(Now), 100, 40, 9, 1, 1, true, "both versions are observed");
            fixture.WriteSession("A", first + second);
            var withdrawn = await monitor.RefreshAsync(Now);
            ExpectTiming(withdrawn, 100, 40, 9, 1, 1, true, "only the conflicting turn remains excluded");
            Require(withdrawn.Issues.Any(x => x.Code == DailyIssueCodes.RequestRevisionConflict), "historical conflict is still active");
            Require(withdrawn.Issues.Any(x => x.Code == DailyIssueCodes.CoverageLost), "withdrawal still loses coverage");
        });

        await Case("COV-28 opaque removed bytes still block timing within their source session", async () =>
        {
            using var fixture = new Fixture("timing-with-opaque-loss");
            var complete = Metadata("A") + Completed("A", "r1", At) + Completed("A", "r2", At.AddMinutes(1));
            fixture.WriteSession("A", complete + "{invalid}\n");
            using var monitor = new SessionLogMonitor(fixture.Home, Zone);
            await monitor.RefreshAsync(Now);
            fixture.WriteSession("A", complete);
            var missing = await monitor.RefreshAsync(Now);
            ExpectTiming(missing, 200, 80, null, 0, 2, true, "missing raw prefix may contain timing for either turn");
            Require(missing.Issues.Any(x => x.Code == DailyIssueCodes.CoverageLost), "opaque loss remains visible");
        });


        foreach (var legacy in new[] { false, true })
        {
            await Case(legacy
                ? "COV-30 losing an unscoped legacy snapshot preserves a complete modern sibling's timing"
                : "COV-29 losing newer equivalent root metadata preserves a complete sibling's timing", async () =>
            {
                using var fixture = new Fixture(legacy ? "unscoped-legacy" : "different-root-timestamp");
                var lost = Metadata("A") + (legacy ? Row("event_msg", At.AddSeconds(-2),
                    new { type = "token_count", info = new { total_token_usage = Vector(123, 6, 17, 4) } }) : "")
                    + Completed("A", "r1", At);
                var survivor = Metadata("A", legacy ? At : At.AddMinutes(-1)) + Completed("A", "r2", At.AddMinutes(1));
                var path = fixture.WriteSession("newer", lost);
                fixture.WriteArchive("survivor", survivor);
                using var monitor = new SessionLogMonitor(fixture.Home, Zone);
                ExpectTiming(await monitor.RefreshAsync(Now), 200, 80, 18, 2, 0, legacy, "both sources are readable");

                File.Delete(path);
                var partial = await monitor.RefreshAsync(Now);
                ExpectTiming(partial, 100, 40, 9, 1, 0, true, "independently verified surviving turn");
                Require(partial.Issues.Any(x => x.Code == DailyIssueCodes.CoverageLost), "a lost source still leaves coverage debt");
                ExpectTiming(await monitor.RefreshAsync(Now), 100, 40, 9, 1, 0, true, "idle after source loss");

                fixture.WriteSession("newer", lost);
                var restored = await monitor.RefreshAsync(Now);
                ExpectTiming(restored, 200, 80, 18, 2, 0, legacy, "exact source restoration");
                Require(restored.Issues.All(x => x.Code != DailyIssueCodes.CoverageLost), "exact restoration clears source coverage debt");
                Require(restored.Issues.Any(x => x.Code == DailyIssueCodes.LegacyCoverageUnknown) == legacy,
                    "restoration preserves the independent legacy coverage decision");
            });
        }


        await Case("COV-31 withdrawn latest scope conflict blocks timing until a verified newer root", async () =>
        {
            using var fixture = new Fixture("withdrawn-conflicting-scope");
            fixture.WriteSession("latest-root", Metadata("A"));
            var child = Row("session_meta", At.AddHours(-1), new
            {
                id = "synthetic-coverage-A",
                source = new { subagent = new { thread_spawn = new { parent_thread_id = "synthetic-parent" } } }
            });
            var childPath = fixture.WriteSession("latest-child", child);
            fixture.WriteArchive("body", Metadata("A", At.AddMinutes(-1)) + Completed("A", "r1", At));
            using var monitor = new SessionLogMonitor(fixture.Home, Zone);
            var conflict = await monitor.RefreshAsync(Now);
            Require(conflict.ProcessingSeconds is null && conflict.Issues.Any(x => x.Code == DailyIssueCodes.MetadataConflict),
                "initial equal-time classification conflict");
            File.Delete(childPath);
            var withdrawn = await monitor.RefreshAsync(Now);
            ExpectTiming(withdrawn, 100, 40, null, 0, 1, true, "verified same-time root does not replace lost conflicting child");
            Require(withdrawn.Issues.Any(x => x.Code == DailyIssueCodes.CoverageLost), "conflict withdrawal leaves coverage debt");
            ExpectTiming(await monitor.RefreshAsync(Now), 100, 40, null, 0, 1, true, "idle does not forget the conflict");

            var newer = fixture.WriteSession("newer-root", Metadata("A", At.AddSeconds(1)));
            var resolved = await monitor.RefreshAsync(Now);
            Require(resolved.ProcessingSeconds == 9 && resolved.TimedTurnCount == 1, "strictly newer verified root can supersede conflict timing");
            File.Delete(newer);
            ExpectTiming(await monitor.RefreshAsync(Now), 100, 40, null, 0, 1, true, "lost newer root reactivates the conflict");
            fixture.WriteSession("latest-child", child);
            Require((await monitor.RefreshAsync(Now)).Issues.Any(x => x.Code == DailyIssueCodes.MetadataConflict),
                "exact restoration returns to current metadata conflict");
        });

        await Case("COV-32 a budget-skipped rewritten source cannot contribute cached timing", async () =>
        {
            using var fixture = new Fixture("budget-skipped-timing");
            var filler = fixture.WriteSession("a-filler", "");
            var newest = fixture.WriteSession("b-current-root", Metadata("A"));
            fixture.WriteArchive("survivor", Metadata("A", At.AddMinutes(-1)) + Completed("A", "r1", At));
            using var monitor = new SessionLogMonitor(fixture.Home, Zone,
                directory => SessionLogMonitor.EnumerateDirectory(directory).OrderBy(entry => entry.Name, StringComparer.Ordinal));
            ExpectTiming(await monitor.RefreshAsync(Now), 100, 40, 9, 1, 0, false, "initial verified timing");

            File.Delete(newest);
            fixture.WriteArchive("survivor", Completed("A", "r1", At));
            FillReadBudget(filler);
            var skipped = await monitor.RefreshAsync(Now);
            Require(monitor.IsLoading, "fixture must exercise the read budget in this refresh");
            ExpectTiming(skipped, 100, 40, null, 0, 1, true, "stale subtotal is partial and cannot prove timing");
            var verified = await monitor.RefreshAsync(Now);
            Require(verified.InputTokens is null && verified.ProcessingSeconds is null
                && verified.Issues.Any(x => x.Code == DailyIssueCodes.MetadataMissing), "actual reread cannot establish a root");
        });


        await Case("COV-33 budget skips preserve verified mirrors but exclude cache-only sibling timing", async () =>
        {
            using var fixture = new Fixture("verified-timing-beside-budget-skip");
            var verified = Metadata("A") + Completed("A", "r1", At);
            fixture.WriteSession("a-verified", verified);
            var filler = fixture.WriteSession("b-filler", "");
            fixture.WriteArchive("cached", verified + Completed("A", "r2", At.AddMinutes(1)));
            using var monitor = new SessionLogMonitor(fixture.Home, Zone,
                directory => SessionLogMonitor.EnumerateDirectory(directory).OrderBy(entry => entry.Name, StringComparer.Ordinal));
            ExpectTiming(await monitor.RefreshAsync(Now), 200, 80, 18, 2, 0, false, "both turns initially verified");

            fixture.WriteArchive("cached", verified);
            FillReadBudget(filler);
            var skipped = await monitor.RefreshAsync(Now);
            Require(monitor.IsLoading && skipped.IsPartial && skipped.InputTokens == 200 && skipped.OutputTokens == 80,
                "the skipped source may contribute only a partial cached token subtotal");
            Require(skipped.ProcessingSeconds == 9 && skipped.TimedTurnCount == 1 && skipped.ExcludedTimingTurnCount == 1
                && skipped.AverageTps is { } tps && Math.Abs(tps - 40d / 9) < .000001,
                "only the turn proved by the verified mirror supplies timing and TPS output");
            ExpectTiming(await monitor.RefreshAsync(Now), 100, 40, 9, 1, 0, true, "rereading withdraws the stale turn");
        });


        await Case("COV-34 withdrawn scope conflict applies when a same-day turn arrives later", async () =>
        {
            using var fixture = new Fixture("scope-conflict-before-usage");
            var olderRoot = Metadata("A", At.AddMinutes(-1));
            fixture.WriteArchive("older-root", olderRoot);
            var child = Row("session_meta", At.AddHours(-1), new
            {
                id = "synthetic-coverage-A",
                source = new { subagent = new { thread_spawn = new { parent_thread_id = "synthetic-parent" } } }
            });
            var disputed = fixture.WriteSession("conflict", Metadata("A") + child);
            using var monitor = new SessionLogMonitor(fixture.Home, Zone);
            Require((await monitor.RefreshAsync(Now)).Issues.Any(x => x.Code == DailyIssueCodes.MetadataConflict),
                "the latest conflict was observed before any turn");
            File.Delete(disputed);
            await monitor.RefreshAsync(Now);
            fixture.WriteArchive("older-root", olderRoot + Completed("A", "r1", At));
            ExpectTiming(await monitor.RefreshAsync(Now), 100, 40, null, 0, 1, true, "late turn still lacks a definitive root");
        });


        foreach (var alternating in new[] { false, true })
        {
            await Case(alternating
                ? "COV-36 replaying the sides separately cannot erase an observed scope conflict"
                : "COV-35 an observed scope conflict survives withdrawal under a newer verified root", async () =>
            {
                using var fixture = new Fixture(alternating ? "alternating-scope" : "superseded-scope");
                fixture.WriteArchive("a-body", Metadata("A", At.AddMinutes(-1)) + Completed("A", "a", At, 60, 13));
                fixture.WriteArchive("b-independent", Metadata("B") + Completed("B", "b", At.AddMinutes(1)));
                var rootSide = Metadata("A");
                var childSide = ChildMetadata("A");
                var bothSides = rootSide + childSide;
                var disputed = fixture.WriteSession("conflict", bothSides);
                using var monitor = new SessionLogMonitor(fixture.Home, Zone);
                var liveConflict = await monitor.RefreshAsync(Now);
                ExpectTiming(liveConflict, 100, 40, 9, 1, 0, true, "current conflict excludes A from root totals");
                Require(liveConflict.Issues.Any(x => x.Code == DailyIssueCodes.MetadataConflict), "the equal-time scope conflict was observed");

                if (alternating)
                {
                    File.Delete(disputed);
                    ExpectTiming(await monitor.RefreshAsync(Now), 200, 100, 9, 1, 1, true,
                        "withdrawn tie leaves the two known token totals and only B's timing", timedOutput: 40);
                }

                var newer = fixture.WriteSession("newer", Metadata("A", At.AddSeconds(1)));
                if (alternating)
                {
                    fixture.WriteSession("single-side", childSide);
                    ExpectTiming(await monitor.RefreshAsync(Now), 200, 100, 21, 2, 0, true,
                        "new root permits timing while the missing root-side coverage is still unresolved");
                    fixture.WriteSession("single-side", rootSide);
                    ExpectTiming(await monitor.RefreshAsync(Now), 200, 100, 21, 2, 0, false, "new root with root side");
                }
                else
                {
                    ExpectTiming(await monitor.RefreshAsync(Now), 200, 100, 21, 2, 0, false, "new root with both sides");
                    File.Delete(disputed);
                    ExpectTiming(await monitor.RefreshAsync(Now), 200, 100, 21, 2, 0, false, "new root after tie withdrawal");
                }

                File.Delete(newer);
                foreach (var stage in new[] { "withdrawal of the newer root", "idle after withdrawal" })
                {
                    var fallback = await monitor.RefreshAsync(Now);
                    var sequence = alternating ? "alternating" : "superseded";
                    Console.WriteLine($"SCOPE TIMING {sequence}, {stage}: {fallback.InputTokens}/{fallback.OutputTokens}, "
                        + $"{fallback.ProcessingSeconds:R}s, TPS={fallback.AverageTps:R}, "
                        + $"{fallback.TimedTurnCount}/{fallback.ExcludedTimingTurnCount}, partial={fallback.IsPartial}");
                    ExpectTiming(fallback, 200, 100, 9, 1, 1, true, stage, timedOutput: 40);
                    Require(fallback.Issues.Any(x => x.Code == DailyIssueCodes.CoverageLost), stage + " retains the historical timing gap");
                }

                fixture.WriteSession("conflict", bothSides);
                var restored = await monitor.RefreshAsync(Now);
                ExpectTiming(restored, 100, 40, 9, 1, 0, true, "exact joint restore reports the current classification conflict");
                Require(restored.Issues.Any(x => x.Code == DailyIssueCodes.MetadataConflict),
                    "both sides jointly restore the current classification conflict");
                fixture.WriteSession("newer", Metadata("A", At.AddSeconds(1)));
                ExpectTiming(await monitor.RefreshAsync(Now), 200, 100, 21, 2, 0, false, "latest root verified again");
            });
        }

        await Case("COV-37 a budget-skipped later root cannot cover an observed conflict", async () =>
        {
            using var fixture = new Fixture("scope-conflict-skipped-root");
            fixture.WriteSession("a-body", Metadata("A", At.AddMinutes(-1)) + Completed("A", "a", At));
            fixture.WriteSession("b-independent", Metadata("B") + Completed("B", "b", At.AddMinutes(1)));
            var filler = fixture.WriteSession("c-filler", "");
            var disputed = fixture.WriteArchive("conflict", Metadata("A") + ChildMetadata("A"));
            using var monitor = new SessionLogMonitor(fixture.Home, Zone,
                directory => SessionLogMonitor.EnumerateDirectory(directory).OrderBy(entry => entry.Name, StringComparer.Ordinal));
            ExpectTiming(await monitor.RefreshAsync(Now), 100, 40, 9, 1, 0, true, "original jointly observed conflict");
            fixture.WriteArchive("newer", Metadata("A", At.AddSeconds(1)));
            ExpectTiming(await monitor.RefreshAsync(Now), 200, 80, 18, 2, 0, false, "newer root verified while conflict is still present");
            File.Delete(disputed);
            FillReadBudget(filler);
            var skipped = await monitor.RefreshAsync(Now);
            Require(monitor.IsLoading, "newer root must be skipped by the read budget");
            ExpectTiming(skipped, 200, 80, 9, 1, 1, true, "cached newer root cannot cover an observed conflict", timedOutput: 40);
            ExpectTiming(await monitor.RefreshAsync(Now), 200, 80, 18, 2, 0, false, "rereading confirms the newer root");
        });

        await Case("COV-38 a conflict observed with a retained child fact still applies after midnight", async () =>
        {
            using var fixture = new Fixture("scope-conflict-historical-child");
            var olderA = Metadata("A", At.AddMinutes(-1));
            var stableB = Metadata("B");
            fixture.WriteArchive("a-body", olderA + Completed("A", "a", At));
            fixture.WriteArchive("b-independent", stableB + Completed("B", "b", At.AddMinutes(1)));
            var child = fixture.WriteSession("child", ChildMetadata("A"));
            using var monitor = new SessionLogMonitor(fixture.Home, Zone);
            ExpectTiming(await monitor.RefreshAsync(Now), 100, 40, 9, 1, 0, false, "confirmed child excluded");
            File.Delete(child);
            await monitor.RefreshAsync(Now);

            var rootSide = fixture.WriteSession("root-tie", Metadata("A"));
            var tied = await monitor.RefreshAsync(Now);
            Require(tied.Issues.Any(x => x.Code == DailyIssueCodes.MetadataConflict),
                "current root and retained child prove the conflicting relationship at one instant");
            File.Delete(rootSide);
            fixture.WriteSession("child", ChildMetadata("A"));
            var newer = fixture.WriteSession("newer", Metadata("A", At.AddSeconds(1)));
            var covered = await monitor.RefreshAsync(Now);
            ExpectTiming(covered, 200, 80, 18, 2, 0, true, "newer root proves timing after the child reappears");
            Require(covered.Issues.Any(x => x.Code == DailyIssueCodes.CoverageLost),
                "the separately withdrawn root-side record still leaves a daily coverage gap");
            File.Delete(child);
            ExpectTiming(await monitor.RefreshAsync(Now), 200, 80, 18, 2, 0, true, "newer root remains");
            File.Delete(newer);
            ExpectTiming(await monitor.RefreshAsync(Now), 200, 80, 9, 1, 1, true, "fallback to older root", timedOutput: 40);

            var tomorrow = Now.AddDays(1);
            var a = fixture.WriteArchive("a-body", olderA + Completed("A", "a-next-day", At.AddDays(1)));
            var b = fixture.WriteArchive("b-independent", stableB + Completed("B", "b-next-day", At.AddDays(1).AddMinutes(1)));
            File.SetLastWriteTimeUtc(a, tomorrow.UtcDateTime);
            File.SetLastWriteTimeUtc(b, tomorrow.UtcDateTime);
            ExpectTiming(await monitor.RefreshAsync(tomorrow), 200, 80, 9, 1, 1, true,
                "day rollover retains the classification fact while replacing daily totals", timedOutput: 40);
        });

        Console.WriteLine($"RESULT COVERAGE LOSS: {passed} passed, {failures.Count} failed");
        if (failures.Count > 0)
            throw new InvalidOperationException("Coverage loss regressions:\n" + string.Join("\n", failures));
    }

    private static void FillReadBudget(string path)
    {
        using (var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read))
        {
            var padding = Utf8.GetBytes(Row("response_item", At, new { type = "message", content = new string('x', 32768) }));
            while (stream.Position < 66L * 1024 * 1024) stream.Write(padding);
        }
        File.SetLastWriteTimeUtc(path, Now.UtcDateTime);
    }


    private static void ExpectTiming(DailySnapshot actual, long input, long output, double? seconds,
        int included, int excluded, bool partial, string context, long? timedOutput = null)
    {
        Require(actual.InputTokens == input && actual.OutputTokens == output && actual.ProcessingSeconds == seconds
            && actual.TimedTurnCount == included && actual.ExcludedTimingTurnCount == excluded && actual.IsPartial == partial,
            $"{context}: actual input={actual.InputTokens}, output={actual.OutputTokens}, processing={actual.ProcessingSeconds}, "
            + $"included={actual.TimedTurnCount}, excluded={actual.ExcludedTimingTurnCount}, partial={actual.IsPartial}; {actual.Status}");
        Require(seconds is { } duration
            ? actual.AverageTps is { } tps && Math.Abs(tps - (timedOutput ?? output) / duration) < .000001
            : actual.AverageTps is null, $"{context}: unexpected average TPS {actual.AverageTps}");
    }

    private static string Completed(string session, string request, DateTimeOffset start, long output = 40, int durationSeconds = 10) =>
        Row("event_msg", start, new { type = "task_started", turn_id = "synthetic-turn-" + request })
        + Modern(session, request, 100, 60, output, 10, start.AddSeconds(durationSeconds - 1))
        + Row("event_msg", start.AddSeconds(durationSeconds),
            new { type = "task_complete", turn_id = "synthetic-turn-" + request, duration_ms = durationSeconds * 1000, time_to_first_token_ms = 1000 });


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

    private static string ChildMetadata(string session) => Row("session_meta", At.AddHours(-1), new
    {
        id = "synthetic-coverage-" + session,
        source = new { subagent = new { thread_spawn = new { parent_thread_id = "synthetic-parent" } } }
    });

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
