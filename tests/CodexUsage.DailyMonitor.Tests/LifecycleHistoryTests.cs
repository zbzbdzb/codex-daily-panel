using System.Text;
using System.Text.Json;
using CodexUsage.DailyPanel;

internal static class LifecycleHistoryTests
{
    public static async Task RunAsync()
    {
        string home = Path.Combine(Path.GetTempPath(), "CodexDaily-midnight-" + Guid.NewGuid().ToString("N"));
        string sessions = Path.Combine(home, "sessions");
        Directory.CreateDirectory(sessions);
        string path = Path.Combine(sessions, "rollout.jsonl");
        var now = new DateTimeOffset(2026, 10, 21, 0, 2, 0, TimeSpan.Zero);
        var start = now.AddMinutes(-4);
        string Row(string type, DateTimeOffset time, object payload) => JsonSerializer.Serialize(new { type, timestamp = time, payload }) + "\n";
        string metadata = Row("session_meta", start, new { id = "midnight-session", source = "cli" });
        string began = Row("event_msg", start, new { type = "task_started", turn_id = "turn" });
        string Usage(DateTimeOffset time) => Row("token_usage_record", time, new {
            thread_id = "midnight-session", session_id = "run", turn_id = "turn", root_turn_id = "turn", response_id = "request",
            usage = new { input_tokens = 100, cached_input_tokens = 50, output_tokens = 25, reasoning_output_tokens = 0, total_tokens = 125 }
        });
        int passes = 0;
        void Check(bool condition, string name)
        {
            if (!condition) throw new InvalidOperationException("FAIL: " + name);
            passes++;
            Console.WriteLine("PASS: " + name);
        }
        void Write(string rows)
        {
            File.WriteAllText(path, rows, new UTF8Encoding(false));
            File.SetLastWriteTimeUtc(path, now.UtcDateTime);
        }
        try
        {
            Write(metadata + began + Usage(start.AddMinutes(1)));
            using (var monitor = new SessionLogMonitor(home, TimeZoneInfo.Utc))
            {
                var active = await monitor.RefreshAsync(now);
                Check(active.ActiveTurns == 1 && active.ActiveSeconds == 120,
                    "prior-day ongoing turn retains clipped activity after midnight");
                File.AppendAllText(path, Row("event_msg", now.AddMinutes(-1), new { type = "task_complete", turn_id = "turn" }), new UTF8Encoding(false));
                var complete = await monitor.RefreshAsync(now);
                Check(complete.ActiveTurns == 0 && complete.ActiveSeconds == 60 && complete.ProcessingSeconds is null,
                    "cross-midnight completion without duration keeps known clipped interval");
            }
            foreach (string kind in new[] { "task_complete", "task_aborted", "turn_aborted" })
            {
                Write(metadata + began + Usage(start.AddSeconds(10))
                    + Row("event_msg", start.AddSeconds(30), new { type = kind, turn_id = "turn" }));
                using var monitor = new SessionLogMonitor(home, TimeZoneInfo.Utc);
                var prior = await monitor.RefreshAsync(now.AddMinutes(-3));
                Check(prior.ActiveTurns == 0, $"previous day {kind} turn is already closed");
                var today = await monitor.RefreshAsync(now);
                Check(today.ActiveTurns == 0 && today.ActiveSeconds is null,
                    $"prior-day {kind} does not reappear active after rollover");
            }
            Console.WriteLine($"LIFECYCLE RESULT: {passes} passed, 0 failed");
        }
        finally { Directory.Delete(home, recursive: true); }
    }
}
