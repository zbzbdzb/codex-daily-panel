using System.Text;
using System.Text.Json;
using CodexUsage.DailyPanel;

internal static class OversizedLineTests
{
    private const int LargeBytes = 5 * 1024 * 1024;
    private static readonly UTF8Encoding Utf8 = new(false, true);

    public static async Task RunAsync()
    {
        string home = Path.Combine(Path.GetTempPath(), "CodexDaily-large-" + Guid.NewGuid().ToString("N"));
        string sessions = Path.Combine(home, "sessions");
        Directory.CreateDirectory(sessions);
        string path = Path.Combine(sessions, "rollout.jsonl");
        var now = new DateTimeOffset(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);
        string metadata = JsonSerializer.Serialize(new { type = "session_meta", timestamp = now, payload = new { id = "large-session", source = "cli" } }) + "\n";
        string usage = JsonSerializer.Serialize(new { type = "token_usage_record", timestamp = now, payload = new {
            thread_id = "large-session", session_id = "run", turn_id = "turn", root_turn_id = "turn", response_id = "request",
            usage = new { input_tokens = 100, cached_input_tokens = 50, output_tokens = 25, reasoning_output_tokens = 0, total_tokens = 125 }
        } }) + "\n";
        int count = 0;
        void Check(bool condition, string name)
        {
            if (!condition) throw new InvalidOperationException("FAIL: " + name);
            count++;
            Console.WriteLine("PASS: " + name);
        }
        void WriteLarge(string prefix, string suffix, byte fill = (byte)'x', bool trailingUsage = true)
        {
            using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
            stream.Write(Utf8.GetBytes(metadata + prefix));
            byte[] block = Enumerable.Repeat(fill, 64 * 1024).ToArray();
            for (int bytes = 0; bytes < LargeBytes; bytes += block.Length) stream.Write(block);
            stream.Write(Utf8.GetBytes(suffix + (trailingUsage ? usage : "")));
            File.SetLastWriteTimeUtc(path, now.UtcDateTime);
        }
        async Task CheckRow(string prefix, string suffix, bool partial, string name, byte fill = (byte)'x')
        {
            WriteLarge(prefix, suffix, fill);
            using var monitor = new SessionLogMonitor(home, TimeZoneInfo.Utc);
            var snapshot = await monitor.RefreshAsync(now);
            Check(snapshot.IsPartial == partial && snapshot.OutputTokens == 25, name);
        }
        try
        {
            await CheckRow("{\"type\":\"response_item\",\"payload\":{\"text\":\"", "\"}}\n", false, "oversized ordinary response retains complete statistics");
            await CheckRow("{\"type\":\"compacted\",\"payload\":{\"text\":\"", "\"}}\n", false, "oversized known compacted envelope retains complete statistics");
            await CheckRow("{\"payload\":{\"type\":\"token_usage_record\",\"text\":\"", "\"},\"type\":\"response_item\"}\n", false, "root type after huge body is classified structurally");
            await CheckRow("{\"type\":\"response_item\",\"payload\":{\"text\":\"", "\"},\"type\":\"token_usage_record\"}\n", true, "duplicate root type cannot hide statistics");
            await CheckRow("{\"type\":\"token_usage_record\",\"payload\":{\"text\":\"", "\"}}\n", true, "oversized statistical envelope remains partial");
            await CheckRow("{\"payload\":{\"type\":\"response_item\",\"text\":\"", "\"}}\n", true, "nested ordinary type is not root evidence");
            await CheckRow("{\"type\":\"response_item\",\"payload\":{\"text\":\"", "\\q\"}}\n", true, "bad string escape in oversized ordinary row remains partial");
            await CheckRow("{\"type\":\"response_item\",\"payload\":{\"text\":\"", "\"}}\n", true, "invalid UTF8 in discarded text remains partial", 0xff);
            await CheckRow("{\"type\":\"response_item\",\"payload\":{\"text\":", "}}\n", true, "oversized invalid literal remains partial");
            await CheckRow("{\"type\":\"response_item\",\"ty\\u0070e\":\"response_item\",\"text\":\"", "\"}\n", true, "escaped duplicate root property remains partial");
            await CheckRow("{\"type\":\"response_item\",\"payload\":", "}\n", false, "oversized valid number is validated without token buffering", (byte)'9');
            WriteLarge("{\"type\":\"response_item\",\"payload\":\"", "\"}\n");
            using (var reader = new DailyLogReader(path))
            {
                int delivered = 0;
                DailyReadBatchResult result;
                do
                {
                    result = await reader.ReadAvailableAsync(_ => delivered++);
                    Check(reader.RetainedLineBytes <= 4 * 1024 * 1024 && !result.OversizedLine, "reader raw line memory stays bounded while discarding ordinary text");
                } while (result.HasMore);
                Check(delivered == 2 && !result.WaitingForCompleteLine && reader.RetainedLineBytes <= 64 * 1024,
                    "oversized conversation never reaches projection and its buffer is released");
            }
            WriteLarge("{\"type\":\"response_item\",\"payload\":\"", "", trailingUsage: false);
            using (var monitor = new SessionLogMonitor(home, TimeZoneInfo.Utc))
            {
                var half = await monitor.RefreshAsync(now);
                Check(half.IsPartial && half.OutputTokens is null, "unfinished oversized row remains partial across polls");
                File.AppendAllText(path, "\"}\n" + usage, Utf8);
                var complete = await monitor.RefreshAsync(now);
                Check(!complete.IsPartial && complete.OutputTokens == 25, "completed oversized append clears provisional partial state");
                WriteLarge("{\"type\":\"token_usage_record\",\"payload\":\"", "\"}\n");
                var bad = await monitor.RefreshAsync(now);
                Check(bad.IsPartial, "same length rewritten oversized statistics detected");
                File.WriteAllText(path, metadata + usage, Utf8);
                var reset = await monitor.RefreshAsync(now);
                Check(reset.IsPartial && reset.OutputTokens == 25
                    && reset.Status.Contains("4 MiB", StringComparison.Ordinal),
                    "replacement cannot erase lost oversized statistics or their diagnostic");
            }
            string priorUsage = usage.Replace(now.ToString("yyyy-MM-dd"), now.AddDays(-1).ToString("yyyy-MM-dd"), StringComparison.Ordinal);
            File.WriteAllText(path, metadata + priorUsage + usage, Utf8);
            using (var monitor = new SessionLogMonitor(home, TimeZoneInfo.Utc))
            {
                var lateReplay = await monitor.RefreshAsync(now);
                Check(!lateReplay.IsPartial && lateReplay.OutputTokens is null,
                    "prior-day request replay today retains its original daily allocation");
                File.AppendAllText(path, usage.Replace("\"request\"", "\"today-request\"", StringComparison.Ordinal), Utf8);
                var todayRequest = await monitor.RefreshAsync(now);
                Check(!todayRequest.IsPartial && todayRequest.OutputTokens == 25 && todayRequest.InputTokens == 100,
                    "today totals exclude retained prior-day request history");
            }
            Console.WriteLine($"OVERSIZED RESULT: {count} passed, 0 failed");
        }
        finally { Directory.Delete(home, recursive: true); }
    }
}
