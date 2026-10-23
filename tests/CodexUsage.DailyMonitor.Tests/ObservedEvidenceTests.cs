using System.Text;
using System.Text.Json;
using CodexUsage.Core;
using CodexUsage.Daily;
using CodexUsage.DailyPanel;

internal static class ObservedEvidenceTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 22, 12, 0, 0, TimeSpan.FromHours(8));
    private static readonly DateTimeOffset Now = At.AddMinutes(1);
    private static readonly TimeZoneInfo Zone = TimeZoneInfo.CreateCustomTimeZone("IntegrityUTC8", TimeSpan.FromHours(8), "UTC+8", "UTC+8");
    private static readonly UTF8Encoding Utf8 = new(false, true);

    public static async Task RunAsync(string? group = null)
    {
        Console.WriteLine("ASSEMBLY Core: " + typeof(EventParser).Assembly.Location);
        Console.WriteLine("ASSEMBLY DailyCore: " + typeof(DailyAggregator).Assembly.Location);
        Console.WriteLine("ASSEMBLY Monitor: " + typeof(SessionLogMonitor).Assembly.Location);
        var failures = new List<string>();
        var passed = 0;
        async Task Case(string id, Func<Checks, Task> action)
        {
            if (group is not null && !id.StartsWith(group, StringComparison.Ordinal)) return;
            var checks = new Checks(id, failures);
            var before = failures.Count;
            try { await action(checks); }
            catch (Exception error) { failures.Add(id + ": " + error); Console.WriteLine("ERROR: " + id + ": " + error); }
            if (failures.Count == before) { passed++; Console.WriteLine("PASS: " + id); }
        }

        foreach (var aborted in new[] { false, true })
        {
            await Case("D1-" + (aborted ? "abort" : "completion") + "-locked-and-restored", async check =>
            {
                using var fixture = new Fixture(check.Id);
                fixture.Write("current", Root(At.AddMinutes(-1)) + Start(At) + Usage(At.AddSeconds(9)));
                var terminal = fixture.Write("terminal", Root(At.AddMinutes(-1)) + (aborted ? Abort(At.AddSeconds(10)) : Complete(At.AddSeconds(10))));
                using var monitor = new SessionLogMonitor(fixture.Home, Zone);
                var baseline = await monitor.RefreshAsync(Now);
                check.Tokens(baseline, 100, 60, 40, 10, false, "baseline");
                check.Activity(baseline, 0, 10, "baseline");
                if (aborted) check.NoTiming(baseline, "baseline abort"); else check.Timing(baseline, 9, 40d / 9, "baseline completion");
                using (var locked = new FileStream(terminal, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                {
                    var unreadable = await monitor.RefreshAsync(Now);
                    check.Tokens(unreadable, 100, 60, 40, 10, true, "terminal path exists but cannot be read");
                    check.Activity(unreadable, 0, null, "known closure cannot become activity or replay an old interval");
                    check.NoTiming(unreadable, "unreadable closure cannot supply duration or TPS");
                    var idle = await monitor.RefreshAsync(Now.AddSeconds(10));
                    check.Activity(idle, 0, null, "locked idle");
                }
                var restored = await monitor.RefreshAsync(Now.AddSeconds(10));
                check.Tokens(restored, 100, 60, 40, 10, false, "readability restored");
                check.Activity(restored, 0, 10, "restored closure");
                if (aborted) check.NoTiming(restored, "restored abort"); else check.Timing(restored, 9, 40d / 9, "restored completion");
                check.Tokens(await monitor.RefreshAsync(Now.AddSeconds(10)), 100, 60, 40, 10, false, "restored idle deduplication");
            });
        }

        await Case("D1-start-only-control", async check =>
        {
            using var fixture = new Fixture(check.Id);
            fixture.Write("current", Root(At.AddMinutes(-1)) + Start(At));
            using var monitor = new SessionLogMonitor(fixture.Home, Zone);
            var first = await monitor.RefreshAsync(Now);
            check.Tokens(first, null, null, null, null, false, "never closed");
            check.Activity(first, 1, 60, "legitimate start-only");
            check.NoTiming(first, "active is not completed timing");
            check.Activity(await monitor.RefreshAsync(Now.AddSeconds(10)), 1, 70, "start-only idle");
        });

        await Case("D1-child-marker-locked", async check =>
        {
            using var fixture = new Fixture(check.Id);
            fixture.Write("current", Root(At.AddMinutes(-2)) + Start(At) + Usage(At.AddSeconds(9)) + Complete(At.AddSeconds(10)));
            var child = fixture.Write("child", Child(At.AddMinutes(-1)));
            using var monitor = new SessionLogMonitor(fixture.Home, Zone);
            check.Tokens(await monitor.RefreshAsync(Now), null, null, null, null, false, "known child baseline");
            using (var locked = new FileStream(child, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                var unavailable = await monitor.RefreshAsync(Now);
                check.Tokens(unavailable, null, null, null, null, true, "failed child source must not promote older root");
                check.Activity(unavailable, 0, null, "child remains outside root activity");
                check.NoTiming(unavailable, "child remains outside root timing");
            }
            check.Tokens(await monitor.RefreshAsync(Now), null, null, null, null, false, "child readability restored");
            fixture.Write("new-root", Root(At.AddSeconds(20)));
            var root = await monitor.RefreshAsync(Now);
            check.Tokens(root, 100, 60, 40, 10, false, "genuinely newer root supersedes child fact");
            check.Timing(root, 9, 40d / 9, "new root control");
        });

        await Case("D1-prior-day-origin-locked", async check =>
        {
            using var fixture = new Fixture(check.Id);
            fixture.Write("current", Root(At.AddMinutes(-1)) + Start(At) + Usage(At.AddSeconds(9)) + Complete(At.AddSeconds(10)));
            var origin = fixture.Write("origin", Root(At.AddDays(-1).AddMinutes(-1)) + Usage(At.AddDays(-1)));
            using var monitor = new SessionLogMonitor(fixture.Home, Zone);
            check.Tokens(await monitor.RefreshAsync(Now), null, null, null, null, false, "request first occurred yesterday");
            using (var locked = new FileStream(origin, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                var unavailable = await monitor.RefreshAsync(Now);
                check.Tokens(unavailable, null, null, null, null, true, "today's copy cannot change known first day");
                check.NoTiming(unavailable, "prior-day request cannot qualify for today's processing");
            }
            var restored = await monitor.RefreshAsync(Now);
            check.Tokens(restored, null, null, null, null, false, "origin restored");
            check.NoTiming(restored, "restored prior-day exclusion");
        });

        foreach (var variant in new[] { "abort", "different-completion", "earlier-start" })
        {
            foreach (var lose in new[] { true, false })
            {
                await Case("D2-" + variant + (lose ? "-lost-before-root" : "-intact-root-control"), async check =>
                {
                    using var fixture = new Fixture(check.Id);
                    var conflictRows = Child(At.AddMinutes(-2)) + (variant switch
                    {
                        "abort" => Abort(At.AddSeconds(5)),
                        "different-completion" => Complete(At.AddSeconds(10), 9000),
                        _ => Start(At.AddSeconds(-30))
                    });
                    var conflictPath = fixture.Write("conflict", conflictRows);
                    fixture.Write("current", Child(At.AddMinutes(-1)) + Start(At) + Usage(At.AddSeconds(9)) + Complete(At.AddSeconds(10)));
                    using var monitor = new SessionLogMonitor(fixture.Home, Zone);
                    check.Tokens(await monitor.RefreshAsync(Now), null, null, null, null, false, "child conflict is dormant");
                    if (lose)
                    {
                        File.Delete(conflictPath);
                        check.Tokens(await monitor.RefreshAsync(Now), null, null, null, null, false, "loss remains dormant in child scope");
                    }
                    fixture.Write("new-root", Root(At.AddSeconds(20)));
                    var root = await monitor.RefreshAsync(Now);
                    check.Tokens(root, 100, 60, 40, 10, true, "root retains known lifecycle conflict");
                    check.NoTiming(root, "lifecycle conflict cannot produce nine processing seconds");
                    check.Activity(root, 0, null, "conflicting lifecycle cannot produce an interval");
                    check.NoTiming(await monitor.RefreshAsync(Now), "root idle retains exclusion");
                    if (lose)
                    {
                        fixture.Write("conflict", conflictRows);
                        var restored = await monitor.RefreshAsync(Now);
                        check.Tokens(restored, 100, 60, 40, 10, true, "restored conflict is still a conflict");
                        check.NoTiming(restored, "restoration does not restore full timing eligibility");
                        check.Activity(restored, 0, null, "restored lifecycle conflict");
                    }
                    fixture.Write("new-child", Child(At.AddSeconds(30)));
                    check.Tokens(await monitor.RefreshAsync(Now), null, null, null, null, false, "conflict no longer affects root statistics");
                });
            }
        }

        await Case("D2-equivalent-closure-replay-control", async check =>
        {
            using var fixture = new Fixture(check.Id);
            var mirror = fixture.Write("mirror", Child(At.AddMinutes(-2)) + Complete(At.AddSeconds(10).ToUniversalTime()));
            fixture.Write("current", Child(At.AddMinutes(-1)) + Start(At) + Usage(At.AddSeconds(9)) + Complete(At.AddSeconds(10)));
            using var monitor = new SessionLogMonitor(fixture.Home, Zone);
            check.Tokens(await monitor.RefreshAsync(Now), null, null, null, null, false, "equivalent closures observed in child scope");
            File.Delete(mirror);
            check.Tokens(await monitor.RefreshAsync(Now), null, null, null, null, false, "equivalent mirror loss is dormant");
            fixture.Write("new-root", Root(At.AddSeconds(20)));
            var root = await monitor.RefreshAsync(Now);
            check.Tokens(root, 100, 60, 40, 10, false, "equivalent instants with different offsets are not lifecycle conflict");
            check.Timing(root, 9, 40d / 9, "same completion remains verifiable in current source");
            check.Activity(root, 0, 10, "duplicate closure does not suppress current interval");
        });

        foreach (var lose in new[] { true, false })
        {
            await Case("D3-same-position-request-conflict" + (lose ? "-lost-before-root" : "-intact-root-control"), async check =>
            {
                using var fixture = new Fixture(check.Id);
                var conflictRows = Child(At.AddMinutes(-2)) + Usage(At.AddSeconds(9));
                var conflictPath = fixture.Write("conflict", conflictRows);
                fixture.Write("current", Child(At.AddMinutes(-1)) + Start(At) + Usage(At.AddSeconds(9), revised: true) + Complete(At.AddSeconds(10)));
                using var monitor = new SessionLogMonitor(fixture.Home, Zone);
                check.Tokens(await monitor.RefreshAsync(Now), null, null, null, null, false, "child request conflict is dormant");
                if (lose)
                {
                    File.Delete(conflictPath);
                    check.Tokens(await monitor.RefreshAsync(Now), null, null, null, null, false, "lost request conflict remains dormant in child");
                }
                fixture.Write("new-root", Root(At.AddSeconds(20)));
                var root = await monitor.RefreshAsync(Now);
                check.Tokens(root, null, null, null, null, true, "neither vector is an authoritative request revision");
                check.NoTiming(root, "ambiguous request cannot reconcile completed turn");
                check.Activity(root, 0, 10, "current valid lifecycle remains independent of token conflict");
                check.Tokens(await monitor.RefreshAsync(Now), null, null, null, null, true, "root idle cannot select surviving conflicting vector");
                if (lose)
                {
                    fixture.Write("conflict", conflictRows);
                    var restored = await monitor.RefreshAsync(Now);
                    check.Tokens(restored, null, null, null, null, true, "restored request conflict remains partial");
                    check.NoTiming(restored, "restored conflict still excludes processing");
                }
                fixture.Write("new-child", Child(At.AddSeconds(30)));
                check.Tokens(await monitor.RefreshAsync(Now), null, null, null, null, false, "request conflict no longer affects root statistics");
            });
        }

        await Case("D3-conflict-proof-survives-reverse-restoration", async check =>
        {
            using var fixture = new Fixture(check.Id);
            var firstRows = Child(At.AddMinutes(-2)) + Usage(At.AddSeconds(9));
            var secondRows = Child(At.AddMinutes(-1)) + Start(At) + Usage(At.AddSeconds(9), revised: true) + Complete(At.AddSeconds(10));
            var firstPath = fixture.Write("first", firstRows);
            var secondPath = fixture.Write("second", secondRows);
            using var monitor = new SessionLogMonitor(fixture.Home, Zone);
            check.Tokens(await monitor.RefreshAsync(Now), null, null, null, null, false, "child observed both conflicting versions");
            File.Delete(firstPath);
            check.Tokens(await monitor.RefreshAsync(Now), null, null, null, null, false, "first side lost in child scope");
            File.Delete(secondPath);
            check.Tokens(await monitor.RefreshAsync(Now), null, null, null, null, false, "second side also lost while dormant");
            fixture.Write("new-root", Root(At.AddSeconds(20)));
            fixture.Write("first", firstRows);
            var reversed = await monitor.RefreshAsync(Now);
            check.Tokens(reversed, null, null, null, null, true, "restoring first side cannot erase the missing second side's conflict");
            check.NoTiming(reversed, "no historical numeric evidence is replayed");
            fixture.Write("second", secondRows);
            var restored = await monitor.RefreshAsync(Now);
            check.Tokens(restored, null, null, null, null, true, "both restored conflicting versions remain rejected");
            check.NoTiming(restored, "fully readable conflict still cannot reconcile timing");
        });

        foreach (var revision in new[] { false, true })
        {
            await Case("D3-" + (revision ? "legal-revision" : "identical-replay") + "-control", async check =>
            {
                using var fixture = new Fixture(check.Id);
                fixture.Write("earlier", Child(At.AddMinutes(-2)) + Usage(At.AddSeconds(revision ? 8 : 9)));
                fixture.Write("current", Child(At.AddMinutes(-1)) + Start(At) + Usage(At.AddSeconds(9), revised: revision, ordinal: revision ? 2 : 1) + Complete(At.AddSeconds(10)));
                using var monitor = new SessionLogMonitor(fixture.Home, Zone);
                check.Tokens(await monitor.RefreshAsync(Now), null, null, null, null, false, "child control");
                fixture.Write("new-root", Root(At.AddSeconds(20)));
                var root = await monitor.RefreshAsync(Now);
                check.Tokens(root, revision ? 200 : 100, revision ? 90 : 60, revision ? 80 : 40, revision ? 20 : 10, false, "legal root control");
                check.Timing(root, 9, (revision ? 80d : 40d) / 9, "legal root timing");
                check.Activity(root, 0, 10, "legal root activity interval");
            });
        }
        Console.WriteLine($"RESULT OBSERVED EVIDENCE: {passed} passed, {failures.Count} failed assertions");
        if (failures.Count > 0) throw new InvalidOperationException(string.Join("\n", failures));
    }

    private sealed class Checks(string id, List<string> failures)
    {
        public string Id => id;
        private void Check(DailySnapshot actual, bool condition, string context)
        {
            Console.WriteLine("SNAPSHOT " + id + " / " + context + ": " + JsonSerializer.Serialize(actual));
            if (!condition) { var message = id + " / " + context; failures.Add(message); Console.WriteLine("FAIL: " + message); }
        }
        public void Tokens(DailySnapshot x, long? input, long? cached, long? output, long? reasoning, bool partial, string context) =>
            Check(x, x.InputTokens == input && x.CachedInputTokens == cached && x.OutputTokens == output && x.ReasoningTokens == reasoning
                && x.IsPartial == partial, context + $" [want tokens={input}/{cached}/{output}/{reasoning}, partial={partial}]");
        public void Activity(DailySnapshot x, int active, double? seconds, string context) =>
            Check(x, x.ActiveTurns == active && Equal(x.ActiveSeconds, seconds), context + $" [want active={active}, seconds={seconds}]");
        public void NoTiming(DailySnapshot x, string context) => Check(x,
            x.ProcessingSeconds is null && x.AverageTps is null && x.TimedTurnCount == 0, context + " [want no processing/TPS]");
        public void Timing(DailySnapshot x, double processing, double tps, string context) => Check(x,
            Equal(x.ProcessingSeconds, processing) && Equal(x.AverageTps, tps) && x.TimedTurnCount == 1,
            context + $" [want processing={processing}, TPS={tps}, timed=1]");
        private static bool Equal(double? actual, double? wanted) => actual.HasValue == wanted.HasValue
            && (!wanted.HasValue || Math.Abs(actual!.Value - wanted.Value) < 0.000001);
    }

    private static string Row(string type, DateTimeOffset timestamp, object payload, long? ordinal = null) =>
        JsonSerializer.Serialize(new { type, timestamp, ordinal, payload }) + "\n";
    private static string Root(DateTimeOffset time) => Row("session_meta", time, new { id = "integrity-session", source = "cli" });
    private static string Child(DateTimeOffset time) => Row("session_meta", time, new
    { id = "integrity-session", source = new { subagent = new { thread_spawn = new { parent_thread_id = "integrity-parent" } } } });
    private static string Start(DateTimeOffset time) => Row("event_msg", time, new { type = "task_started", turn_id = "integrity-turn" });
    private static string Complete(DateTimeOffset time, long duration = 10000) => Row("event_msg", time,
        new { type = "task_complete", turn_id = "integrity-turn", duration_ms = duration, time_to_first_token_ms = 1000 });
    private static string Abort(DateTimeOffset time) => Row("event_msg", time, new { type = "task_aborted", turn_id = "integrity-turn" });
    private static string Usage(DateTimeOffset time, bool revised = false, long ordinal = 1)
    {
        var vector = revised
            ? new { input_tokens = 200, cached_input_tokens = 90, output_tokens = 80, reasoning_output_tokens = 20, total_tokens = 280 }
            : new { input_tokens = 100, cached_input_tokens = 60, output_tokens = 40, reasoning_output_tokens = 10, total_tokens = 140 };
        return Row("token_usage_record", time, new { thread_id = "integrity-session", session_id = "integrity-run",
            turn_id = "integrity-turn", root_turn_id = "integrity-turn", response_id = "integrity-request",
            usage = vector, turn_token_usage = vector, thread_token_usage = vector }, ordinal);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string? _archive;
        private int _sequence;
        public string Home { get; }
        public Fixture(string name)
        {
            Home = Path.Combine(Path.GetTempPath(), "CodexDaily-integrity-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(Home, "sessions"));
            Directory.CreateDirectory(Path.Combine(Home, "archived_sessions"));
            var archive = Environment.GetEnvironmentVariable("CODEX_DAILY_INTEGRITY_INPUTS");
            if (!string.IsNullOrWhiteSpace(archive)) { _archive = Path.Combine(archive, name); Directory.CreateDirectory(_archive); }
        }
        public string Write(string name, string contents)
        {
            var bytes = Utf8.GetBytes(contents);
            var path = Path.Combine(Home, "sessions", name + ".jsonl");
            File.WriteAllBytes(path, bytes);
            File.SetLastWriteTimeUtc(path, Now.UtcDateTime);
            if (_archive is not null)
            {
                using var copy = new FileStream(Path.Combine(_archive, $"{++_sequence:D2}-{name}.jsonl"), FileMode.CreateNew, FileAccess.Write);
                copy.Write(bytes);
            }
            return path;
        }
        public void Dispose() => Directory.Delete(Home, recursive: true);
    }
}
