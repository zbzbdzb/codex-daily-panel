using System.Text;
using System.Text.Json;
using CodexUsage.Daily;
using CodexUsage.DailyPanel;

internal static class MonitorDiagnosticsTests
{
    internal static void Run(Action<string, Action> check)
    {
        var now = new DateTimeOffset(2026, 10, 23, 12, 0, 0, TimeSpan.FromHours(8));
        var zone = TimeZoneInfo.CreateCustomTimeZone("MonitorDiagnosticsUtcPlus8", TimeSpan.FromHours(8), "UTC+8", "UTC+8");
        var home = Path.Combine(Path.GetTempPath(), "CodexDaily-state-synthetic-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(home);
        // The launcher isolates TEMP. Keep originals beside the synthetic home
        // so the deletion/restoration test does not destroy its reproduction.
        Console.WriteLine("FIXTURE " + home);
        using var monitor = new SessionLogMonitor(home, zone);
        void Need(bool condition, string reason) { if (!condition) throw new InvalidOperationException(reason); }
        bool Has(DailySnapshot snapshot, string code) => snapshot.Issues.Any(issue => issue.Code == code);
        string Row(string type, object payload) => JsonSerializer.Serialize(new { type, timestamp = now.AddMinutes(-2), payload }) + "\n";
        var metadata = Row("session_meta", new { id = "synthetic-state-root", source = "cli" });
        var usage = Row("token_usage_record", new
        {
            thread_id = "synthetic-state-root", session_id = "synthetic-run", turn_id = "turn", root_turn_id = "turn", response_id = "synthetic-request",
            usage = new { input_tokens = 100, cached_input_tokens = 60, output_tokens = 40, reasoning_output_tokens = 10, total_tokens = 140 },
            turn_token_usage = new { input_tokens = 100, cached_input_tokens = 60, output_tokens = 40, reasoning_output_tokens = 10, total_tokens = 140 }
        });
        var original = metadata + usage;
        File.WriteAllText(Path.Combine(home, "original-fixture.txt"), original, new UTF8Encoding(false));
        var sessions = Path.Combine(home, "sessions");
        var path = Path.Combine(sessions, "synthetic.jsonl");
        void Write(string text)
        {
            File.WriteAllText(path, text, new UTF8Encoding(false));
            File.SetLastWriteTimeUtc(path, now.UtcDateTime);
        }
        DailySnapshot Refresh() => monitor.RefreshAsync(now).GetAwaiter().GetResult();

        check("Monitor missing physical log directory emits reason with unknown nonpartial values", () => {
            var value = Refresh();
            Need(value.InputTokens is null && !value.IsPartial && value.SourceCount == 0, "missing directory compatibility changed");
            Need(Has(value, DailyIssueCodes.SourceNotFound) && Has(value, DailyIssueCodes.NoTokens), "missing-directory structured reasons absent");
        });
        Directory.CreateDirectory(sessions);
        Write(original);
        check("Monitor valid synthetic file produces known subtotal without source errors", () => {
            var value = Refresh();
            Need(value.InputTokens == 100 && value.OutputTokens == 40 && !value.IsPartial, "known subtotal");
            Need(Has(value, DailyIssueCodes.LocalRootScope) && !Has(value, DailyIssueCodes.SourceUnreadable), "valid source diagnostics");
        });
        check("Monitor actual locked file preserves compatible partial reason and recovers", () => {
            using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                var value = Refresh();
                Need(value.IsPartial && Has(value, DailyIssueCodes.SourceUnreadable) && Has(value, DailyIssueCodes.SourcePartial), "actual access failure diagnostics missing");
                Need(value.Status.Contains("部分日志不可读取或格式不完整", StringComparison.Ordinal), "compatibility reason changed");
            }
            var restored = Refresh();
            Need(restored.InputTokens == 100 && !restored.IsPartial && !Has(restored, DailyIssueCodes.SourceUnreadable), "unlock did not clear corresponding reason");
        });
        check("Monitor partial line preserves subtotal and emits source partial then clears", () => {
            File.AppendAllText(path, "{", new UTF8Encoding(false));
            File.SetLastWriteTimeUtc(path, now.UtcDateTime);
            var value = Refresh();
            Need(value.InputTokens == 100 && value.IsPartial && Has(value, DailyIssueCodes.SourcePartial), "pending line diagnostics or subtotal");
            File.AppendAllText(path, "}\n", new UTF8Encoding(false));
            var complete = Refresh();
            Need(complete.InputTokens == 100 && !complete.IsPartial, "completed non-statistical row must settle partial");
        });
        check("Same monitor deletion emits coverage-lost and recovery removes corresponding issue", () => {
            File.Delete(path);
            var lost = Refresh();
            Need(lost.InputTokens is null && lost.IsPartial && Has(lost, DailyIssueCodes.CoverageLost), "historical loss missing after deletion");
            Write(original + "{}\n");
            var restored = Refresh();
            Need(restored.InputTokens == 100 && !restored.IsPartial && !Has(restored, DailyIssueCodes.CoverageLost), "restoration diagnostic not settled");
        });
    }
}
