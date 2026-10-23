using System.Text;
using System.Text.Json;
using CodexUsage.Daily;
using CodexUsage.DailyPanel;

internal static class HistoricalDurationMonitorTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 22, 12, 0, 0, TimeSpan.FromHours(8));
    private static readonly DateTimeOffset Now = At.AddMinutes(1);
    private static readonly TimeZoneInfo Zone = TimeZoneInfo.CreateCustomTimeZone("F1MonitorUTC8", TimeSpan.FromHours(8), "UTC+8", "UTC+8");
    private static readonly UTF8Encoding Utf8 = new(false, true);

    public static async Task RunAsync()
    {
        var failures = new List<string>();
        var assertions = 0;
        var passed = 0;
        foreach (var mismatch in new[] { true, false })
        {
            var name = "F1-MON-" + (mismatch ? "contradictory" : "compatible") + "-unique-start-loss-before-root";
            var before = failures.Count;
            void Check(bool condition, string context)
            {
                assertions++;
                if (!condition) { failures.Add(name + " / " + context); Console.WriteLine("FAIL: " + failures[^1]); }
            }
            try
            {
                using var fixture = new Fixture(name);
                var startRows = Child(At.AddMinutes(-2)) + Start(mismatch ? At.AddSeconds(-30) : At);
                var startPath = fixture.Write("A-start", startRows);
                // No current start in B: this exercises a unique retained start,
                // not the separate multiple-start conflict contract.
                fixture.Write("B-usage-completion", Child(At.AddMinutes(-1)) + Usage(At.AddSeconds(9)) + Complete(At.AddSeconds(10)));
                using var monitor = new SessionLogMonitor(fixture.Home, Zone);
                async Task Capture(string stage, bool root, double? activity, DateTimeOffset time)
                {
                    var result = await monitor.RefreshAsync(time);
                    Console.WriteLine("SNAPSHOT " + name + " / " + stage + ": " + JsonSerializer.Serialize(result));
                    fixture.ArchiveSnapshot(stage, result);
                    var excluded = root && mismatch;
                    Check(root
                        ? result.InputTokens == 100 && result.CachedInputTokens == 60 && result.OutputTokens == 40 && result.ReasoningTokens == 10
                        : result.InputTokens is null && result.CachedInputTokens is null && result.OutputTokens is null && result.ReasoningTokens is null,
                        stage + " token vector remains root=100/60/40/10 or child=unknown");
                    Check(result.IsPartial == excluded, stage + " partial follows known duration contradiction");
                    Check(result.Status.Contains("回合声明耗时与起止时间不一致", StringComparison.Ordinal) == excluded,
                        stage + " duration mismatch reason follows eligibility");
                    Check(root && !mismatch
                        ? Equal(result.ProcessingSeconds, 9) && Equal(result.AverageTps, 40d / 9) && Equal(result.ExcludedWaitSeconds, 1)
                        : result.ProcessingSeconds is null && result.AverageTps is null && result.ExcludedWaitSeconds is null,
                        stage + " processing, TPS and excluded wait use eligible current completion only");
                    Check(result.TimedTurnCount == (root && !mismatch ? 1 : 0) && result.ExcludedTimingTurnCount == (excluded ? 1 : 0),
                        stage + " included/excluded timing counts agree");
                    Check(result.ActiveTurns == 0, stage + " closed turn cannot become active");
                    Check(Equal(result.ActiveSeconds, activity), stage + " history cannot backfill the current activity interval");
                }

                await Capture("01-present-child", false, null, Now);
                fixture.Delete(startPath);
                await Capture("02-lost-child", false, null, Now);
                fixture.Write("C-new-root", Root(At.AddSeconds(20)));
                await Capture("03-lost-root", true, 10, Now);
                await Capture("04-lost-root-idle", true, 10, Now.AddMinutes(2));
                fixture.Write("A-start", startRows);
                await Capture("05-restored-root", true, mismatch ? 40 : 10, Now.AddMinutes(2));
                await Capture("06-restored-root-idle", true, mismatch ? 40 : 10, Now.AddMinutes(3));
            }
            catch (Exception error) { failures.Add(name + ": " + error); Console.WriteLine("ERROR: " + failures[^1]); }
            if (failures.Count == before) { passed++; Console.WriteLine("PASS: " + name); }
        }
        Console.WriteLine($"RESULT F1 MONITOR: {passed}/2 cases passed; {assertions} assertions; {failures.Count} failures");
        if (failures.Count > 0) throw new InvalidOperationException(string.Join("\n", failures));
    }

    private static bool Equal(double? actual, double? expected) => actual.HasValue == expected.HasValue
        && (!expected.HasValue || Math.Abs(actual!.Value - expected.Value) < .000001);
    private static string Row(string type, DateTimeOffset timestamp, object payload, long? ordinal = null) =>
        JsonSerializer.Serialize(new { type, timestamp, ordinal, payload }) + "\n";
    private static string Root(DateTimeOffset time) => Row("session_meta", time, new { id = "f1-monitor-session", source = "cli" });
    private static string Child(DateTimeOffset time) => Row("session_meta", time, new
    { id = "f1-monitor-session", source = new { subagent = new { thread_spawn = new { parent_thread_id = "f1-monitor-parent" } } } });
    private static string Start(DateTimeOffset time) => Row("event_msg", time, new { type = "task_started", turn_id = "f1-monitor-turn" });
    private static string Complete(DateTimeOffset time) => Row("event_msg", time,
        new { type = "task_complete", turn_id = "f1-monitor-turn", duration_ms = 10000, time_to_first_token_ms = 1000 });
    private static string Usage(DateTimeOffset time)
    {
        var vector = new { input_tokens = 100, cached_input_tokens = 60, output_tokens = 40, reasoning_output_tokens = 10, total_tokens = 140 };
        return Row("token_usage_record", time, new { thread_id = "f1-monitor-session", session_id = "f1-monitor-run",
            turn_id = "f1-monitor-turn", root_turn_id = "f1-monitor-turn", response_id = "f1-monitor-request",
            usage = vector, turn_token_usage = vector, thread_token_usage = vector }, 1);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string? _archive;
        private int _sequence;
        public string Home { get; }
        public Fixture(string name)
        {
            Home = Path.Combine(Path.GetTempPath(), "CodexDaily-F1-" + Guid.NewGuid().ToString("N"));
            EnsurePhysical(Path.GetDirectoryName(Home)!);
            Directory.CreateDirectory(Path.Combine(Home, "sessions"));
            Directory.CreateDirectory(Path.Combine(Home, "archived_sessions"));
            var archive = Environment.GetEnvironmentVariable("CODEX_DAILY_INTEGRITY_INPUTS");
            if (!string.IsNullOrWhiteSpace(archive))
            {
                _archive = Path.Combine(archive, name);
                Directory.CreateDirectory(_archive);
                EnsurePhysical(_archive);
            }
        }
        public string Write(string name, string contents)
        {
            var path = Path.Combine(Home, "sessions", name + ".jsonl");
            EnsurePhysical(Path.GetDirectoryName(path)!);
            var bytes = Utf8.GetBytes(contents);
            using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write)) stream.Write(bytes);
            File.SetLastWriteTimeUtc(path, Now.UtcDateTime);
            if (_archive is not null) Save(Path.Combine(_archive, $"{++_sequence:D2}-{name}.jsonl"), bytes);
            return path;
        }
        public void ArchiveSnapshot(string stage, DailySnapshot snapshot)
        {
            if (_archive is not null)
                Save(Path.Combine(_archive, stage + ".json"), Utf8.GetBytes(JsonSerializer.Serialize(snapshot)));
        }
        public void Delete(string path)
        {
            if (!Path.GetFullPath(path).StartsWith(Path.GetFullPath(Home) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Deletion escaped synthetic home.");
            EnsurePhysical(path);
            File.Delete(path);
        }
        public void Dispose()
        {
            EnsurePhysical(Home);
            Directory.Delete(Home, recursive: true);
        }
        private static void Save(string path, byte[] bytes)
        {
            using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
            stream.Write(bytes);
        }
        private static void EnsurePhysical(string path)
        {
            var full = Path.GetFullPath(path);
            while (!string.IsNullOrEmpty(full))
            {
                if ((File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Synthetic fixture must not traverse links: " + full);
                full = Path.GetDirectoryName(full);
            }
        }
    }
}
