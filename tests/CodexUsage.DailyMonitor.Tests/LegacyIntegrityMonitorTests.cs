using System.Text;
using System.Text.Json;
using CodexUsage.Daily;
using CodexUsage.DailyPanel;

internal static class LegacyIntegrityMonitorTests
{
    private static readonly DateTimeOffset At = DateTimeOffset.Parse("2026-10-21T04:00:00Z");
    private static readonly DateTimeOffset Now = At.AddMinutes(10);
    private static readonly TimeZoneInfo Zone = TimeZoneInfo.CreateCustomTimeZone(
        "SyntheticIntegrityUTC8", TimeSpan.FromHours(8), "UTC+8", "UTC+8");

    public static async Task RunAsync()
    {
        var checks = 0;
        void Check(bool condition, string name)
        {
            if (!condition) throw new InvalidOperationException("FAIL: " + name);
            checks++;
            Console.WriteLine("PASS: " + name);
        }

        using (var fixture = new Fixture("mixed"))
        {
            fixture.Write("modern-A", Metadata("synthetic-A") + Modern("synthetic-A"));
            fixture.Write("legacy-B", Metadata("synthetic-B") + Legacy(At.AddSeconds(1)));
            using var monitor = new SessionLogMonitor(fixture.Home, Zone);
            var first = await monitor.RefreshAsync(Now);
            Check(KnownModernTotals(first), "LEG-M01 mixed modern A + legacy B keeps only A's four token fields");
            Check(first.IsPartial && HasLegacyCause(first), "LEG-M01 mixed legacy coverage is partial and explained");
            var idle = await monitor.RefreshAsync(Now);
            Check(KnownModernTotals(idle) && idle.IsPartial && HasLegacyCause(idle),
                "LEG-M01 idle refresh retains known totals and legacy incompleteness");
        }

        using (var fixture = new Fixture("yesterday"))
        {
            fixture.Write("modern-A", Metadata("synthetic-A") + Modern("synthetic-A"));
            fixture.Write("legacy-B", Metadata("synthetic-B") + Legacy(At.AddDays(-1)));
            using var monitor = new SessionLogMonitor(fixture.Home, Zone);
            var snapshot = await monitor.RefreshAsync(Now);
            Check(KnownModernTotals(snapshot) && !snapshot.IsPartial && !HasLegacyCause(snapshot),
                "LEG-M02 yesterday's legacy snapshot in a modified-today file does not affect today's totals");
        }

        using (var fixture = new Fixture("child"))
        {
            fixture.Write("modern-A", Metadata("synthetic-A") + Modern("synthetic-A"));
            fixture.Write("legacy-child", Row("session_meta", At.AddHours(-1), new
            {
                id = "synthetic-child", source = new { subagent = new { thread_spawn = new { parent_thread_id = "synthetic-A" } } }
            }) + Legacy(At.AddSeconds(1)));
            using var monitor = new SessionLogMonitor(fixture.Home, Zone);
            var snapshot = await monitor.RefreshAsync(Now);
            Check(KnownModernTotals(snapshot) && !snapshot.IsPartial && !HasLegacyCause(snapshot),
                "LEG-M03 confirmed child legacy usage does not invalidate root statistics");
        }

        using (var fixture = new Fixture("quota-only"))
        {
            fixture.Write("modern-A", Metadata("synthetic-A") + Modern("synthetic-A"));
            fixture.Write("quota-B", Metadata("synthetic-B") + Row("event_msg", At.AddSeconds(1), new
            {
                type = "token_count", rate_limits = new
                {
                    limit_id = "synthetic-limit", secondary = new
                    {
                        used_percent = 20, window_minutes = 10080, resets_at = Now.AddDays(1).ToUnixTimeSeconds()
                    }
                }
            }));
            using var monitor = new SessionLogMonitor(fixture.Home, Zone);
            var snapshot = await monitor.RefreshAsync(Now);
            Check(KnownModernTotals(snapshot) && !snapshot.IsPartial && !HasLegacyCause(snapshot),
                "LEG-M04 token_count without info supplies no legacy usage evidence");
        }

        using (var fixture = new Fixture("missing-metadata"))
        {
            fixture.Write("modern-A", Metadata("synthetic-A") + Modern("synthetic-A"));
            fixture.Write("legacy-unknown", Legacy(At.AddSeconds(1)));
            using var monitor = new SessionLogMonitor(fixture.Home, Zone);
            var snapshot = await monitor.RefreshAsync(Now);
            Check(KnownModernTotals(snapshot) && snapshot.IsPartial
                && snapshot.Status.Contains("缺少会话元数据", StringComparison.Ordinal),
                "LEG-M05 legacy usage without metadata preserves known root totals and explains unknown scope");
        }

        using (var fixture = new Fixture("malformed"))
        {
            fixture.Write("modern-A", Metadata("synthetic-A") + Modern("synthetic-A"));
            fixture.Write("legacy-B", Metadata("synthetic-B") + Legacy(At.AddSeconds(1)) + "{malformed-synthetic-row}\n");
            using var monitor = new SessionLogMonitor(fixture.Home, Zone);
            var snapshot = await monitor.RefreshAsync(Now);
            Check(KnownModernTotals(snapshot) && snapshot.IsPartial,
                "LEG-M06 malformed mixed source retains the known modern subtotal as partial");
            Check(HasLegacyCause(snapshot) && snapshot.Status.Contains("格式不完整", StringComparison.Ordinal),
                "LEG-M06 source format diagnostic preserves the legacy incompleteness cause");
        }

        using (var fixture = new Fixture("truncated"))
        {
            fixture.Write("modern-A", Metadata("synthetic-A") + Modern("synthetic-A"));
            fixture.Write("legacy-B", Metadata("synthetic-B") + Legacy(At.AddSeconds(1)));
            fixture.Write("unfinished-C", "{");
            using var monitor = new SessionLogMonitor(fixture.Home, Zone);
            var snapshot = await monitor.RefreshAsync(Now);
            Check(KnownModernTotals(snapshot) && snapshot.IsPartial && HasLegacyCause(snapshot)
                && snapshot.Status.Contains("来源仍在读取或部分来源不可用", StringComparison.Ordinal),
                "LEG-M07 unfinished source preserves known totals, source partiality and legacy cause");
        }

        using (var fixture = new Fixture("verified-mirror"))
        {
            // The real parser supplies the same context turn from the preceding modern row.
            // Legacy has no invented RequestKey, RootTurnKey, or TurnSnapshot. Its thread
            // snapshot exactly matches the strictly earlier accepted modern history prefix.
            fixture.Write("mirror-A", Metadata("synthetic-A") + Modern("synthetic-A")
                + Row("event_msg", At.AddSeconds(1), new
                {
                    type = "token_count", info = new
                    {
                        last_token_usage = Vector(100, 60, 40, 10, 140),
                        total_token_usage = Vector(100, 60, 40, 10, 140)
                    }
                }));
            using var monitor = new SessionLogMonitor(fixture.Home, Zone);
            var snapshot = await monitor.RefreshAsync(Now);
            Check(KnownModernTotals(snapshot) && !snapshot.IsPartial && !HasLegacyCause(snapshot),
                "LEG-M08 parser-real legacy mirror of an earlier modern history prefix is not missing coverage");
        }

        Console.WriteLine($"RESULT LEGACY MONITOR: {checks} passed, 0 failed");
    }

    private static bool KnownModernTotals(DailySnapshot value) => value.InputTokens == 100
        && value.CachedInputTokens == 60 && value.OutputTokens == 40 && value.ReasoningTokens == 10;

    private static bool HasLegacyCause(DailySnapshot value) => value.Status.Contains("旧格式", StringComparison.Ordinal)
        && (value.Status.Contains("无法", StringComparison.Ordinal) || value.Status.Contains("不能", StringComparison.Ordinal));

    private static string Metadata(string session) => Row("session_meta", At.AddHours(-1), new { id = session, source = "cli" });

    private static string Modern(string session) => Row("token_usage_record", At, new
    {
        thread_id = session, session_id = "synthetic-run", turn_id = "synthetic-turn", root_turn_id = "synthetic-turn",
        response_id = "synthetic-request", usage = Vector(100, 60, 40, 10, 140),
        turn_token_usage = Vector(100, 60, 40, 10, 140), thread_token_usage = Vector(100, 60, 40, 10, 140)
    });

    private static string Legacy(DateTimeOffset timestamp) => Row("event_msg", timestamp, new
    {
        type = "token_count", info = new
        {
            last_token_usage = Vector(900, 500, 100, 20, 1000),
            total_token_usage = Vector(900, 500, 100, 20, 1000)
        }
    });

    private static object Vector(long input, long cached, long output, long reasoning, long total) => new
    {
        input_tokens = input, cached_input_tokens = cached, output_tokens = output,
        reasoning_output_tokens = reasoning, total_tokens = total
    };

    private static string Row(string type, DateTimeOffset timestamp, object payload) =>
        JsonSerializer.Serialize(new { type, timestamp, payload }) + "\n";

    private sealed class Fixture : IDisposable
    {
        public string Home { get; }

        public Fixture(string name)
        {
            Home = Path.Combine(Path.GetTempPath(), "CodexDaily-legacy-" + name + "-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(Home, "sessions"));
        }

        public void Write(string name, string contents)
        {
            var path = Path.Combine(Home, "sessions", name + ".jsonl");
            File.WriteAllText(path, contents, new UTF8Encoding(false));
            File.SetLastWriteTimeUtc(path, Now.UtcDateTime);
        }

        public void Dispose() => Directory.Delete(Home, recursive: true);
    }
}
