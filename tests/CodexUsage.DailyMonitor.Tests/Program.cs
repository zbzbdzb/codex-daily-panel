using System.Text;
using System.Text.Json;
using CodexUsage.DailyPanel;

if (args is ["--observed-evidence", var group]) { await ObservedEvidenceTests.RunAsync(group); return; }
if (args is ["--monitor-disposal"]) { await MonitorDisposalTests.RunAsync(); return; }

var directory = Path.Combine(Path.GetTempPath(), "CodexDaily-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(directory);
var now = new DateTimeOffset(2026, 10, 20, 12, 0, 0, TimeSpan.FromHours(8));
var timezone = TimeZoneInfo.FindSystemTimeZoneById("China Standard Time");
var passes = 0;
void Check(bool condition, string name) { if (!condition) throw new InvalidOperationException("FAIL: " + name); passes++; Console.WriteLine("PASS: " + name); }
string Row(string kind, object payload) => JsonSerializer.Serialize(new { type = kind, timestamp = now.AddMinutes(-2), payload }) + "\n";
string Usage(string response, long output) => Row("token_usage_record", new {
    thread_id="test-session", session_id="run", turn_id="turn", root_turn_id="turn", response_id=response,
    usage=new { input_tokens=100L, cached_input_tokens=80L, output_tokens=output, reasoning_output_tokens=0L, total_tokens=100+output },
    turn_token_usage=new { input_tokens=100L, cached_input_tokens=80L, output_tokens=output, reasoning_output_tokens=0L, total_tokens=100+output },
    thread_token_usage=new { input_tokens=100L, cached_input_tokens=80L, output_tokens=output, reasoning_output_tokens=0L, total_tokens=100+output }
});
try
{
    using var monitor = new SessionLogMonitor(directory, timezone);
    var missing = await monitor.RefreshAsync(now);
    Check(missing.InputTokens is null && missing.SourceCount == 0, "missing source is unknown");
    var sessions = Path.Combine(directory, "sessions", "2026", "01", "01");
    Directory.CreateDirectory(sessions);
    var path = Path.Combine(sessions, "rollout-test.jsonl");
    var metadata = Row("session_meta", new { id="test-session", source="cli" });
    File.WriteAllText(path, metadata + Usage("one", 25), new UTF8Encoding(false));
    File.SetLastWriteTimeUtc(path, now.UtcDateTime);
    var first = await monitor.RefreshAsync(now);
    Check(first.InputTokens == 100 && first.OutputTokens == 25, "resumed old folder counted by event day");
    var idle = await monitor.RefreshAsync(now);
    Check(idle.OutputTokens == 25, "idle refresh does not add totals");
    var secondRow = Usage("two", 35);
    File.AppendAllText(path, secondRow[..^1], new UTF8Encoding(false));
    File.SetLastWriteTimeUtc(path, now.UtcDateTime);
    var half = await monitor.RefreshAsync(now);
    Check(half.OutputTokens == 25, "unfinished JSONL row not counted");
    File.AppendAllText(path, "\n", new UTF8Encoding(false));
    var appended = await monitor.RefreshAsync(now);
    Check(appended.InputTokens == 200 && appended.OutputTokens == 60, "completed append counted exactly once");
    File.WriteAllText(path, metadata + Usage("replacement", 7), new UTF8Encoding(false));
    var replaced = await monitor.RefreshAsync(now);
    Check(replaced.OutputTokens == 7 && replaced.InputTokens == 100, "truncate replacement removes prior contributions");
    var archived = Path.Combine(directory, "archived_sessions");
    Directory.CreateDirectory(archived);
    File.Copy(path, Path.Combine(archived, "copy.jsonl"));
    var replay = await monitor.RefreshAsync(now);
    Check(replay.OutputTokens == 7, "cross-directory replay deduplicated");
    File.Delete(path);
    File.Delete(Path.Combine(archived, "copy.jsonl"));
    var deleted = await monitor.RefreshAsync(now);
    Check(deleted.OutputTokens is null, "deleted file removes stale totals");
    // Replace the timestamp structurally so serializer formatting cannot weaken the test.
    var taskBadTime = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(Usage("bad-time", 9))!;
    var taskBadRow = JsonSerializer.Serialize(new { type="token_usage_record", timestamp="invalid-time", payload=taskBadTime["payload"] }) + "\n";
    File.WriteAllText(path, metadata + Usage("valid", 7) + taskBadRow, new UTF8Encoding(false));
    var invalidTimestamp = await monitor.RefreshAsync(now);
    Check(invalidTimestamp.IsPartial && invalidTimestamp.OutputTokens == 7, "unknown timestamp marks daily subtotal partial");
    string Quota(double used) => Row("event_msg", new { type="token_count", rate_limits=new { limit_id="codex", secondary=new { used_percent=used, window_minutes=10080, resets_at=now.AddDays(1).ToUnixTimeSeconds() } } });
    File.WriteAllText(path, metadata + Usage("valid", 7) + Quota(20) + Quota(30), new UTF8Encoding(false));
    var quotaConflict = await monitor.RefreshAsync(now);
    Check(quotaConflict.Weekly is null, "equal-time conflicting quota rows stay unavailable");
    using var dstMonitor = new SessionLogMonitor(directory, TimeZoneInfo.FindSystemTimeZoneById("Pacific SA Standard Time"));
    var dst = await dstMonitor.RefreshAsync(new DateTimeOffset(2026, 9, 6, 12, 0, 0, TimeSpan.FromHours(-3)));
    Check(dst.InputTokens is null, "skipped midnight does not break refresh");
    await CoverageLossTests.RunAsync();
    await ExclusionCoverageTests.RunAsync();
    await HistoricalDecisionTests.RunAsync();
    await ClosureCoverageTests.RunAsync();
    await ToolTimingTests.RunAsync();
    await OversizedLineTests.RunAsync();
    EnvelopeValidatorTests.Run();
    await LifecycleHistoryTests.RunAsync();
    await LegacyIntegrityMonitorTests.RunAsync();
    await ObservedEvidenceTests.RunAsync();
    await HistoricalDurationMonitorTests.RunAsync();
    await MonitorDisposalTests.RunAsync();
    Console.WriteLine($"RESULT: {passes} passed, 0 failed");
}
finally { Directory.Delete(directory, true); }
