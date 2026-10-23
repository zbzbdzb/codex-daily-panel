using System.Collections;
using System.Reflection;
using System.Text.Json;
using CodexUsage.DailyPanel;

internal static class MonitorDisposalTests
{
    public static async Task RunAsync()
    {
        var failures = new List<string>();
        foreach (var cancelAndAwait in new[] { false, true })
        {
            var name = cancelAndAwait ? "R1-cancel-and-await-before-dispose-control" : "R1-dispose-with-active-and-queued-refresh";
            var directory = Path.Combine(Path.GetTempPath(), "CodexDaily-dispose-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(directory, "sessions"));
            var now = new DateTimeOffset(2026, 10, 22, 12, 1, 0, TimeSpan.FromHours(8));
            var contents = JsonSerializer.Serialize(new { type = "session_meta", timestamp = now, payload = new { id = "disposal-synthetic", source = "cli" } }) + "\n";
            File.WriteAllText(Path.Combine(directory, "sessions", "synthetic.jsonl"), contents);
            var archive = Environment.GetEnvironmentVariable("CODEX_DAILY_INTEGRITY_INPUTS");
            if (!string.IsNullOrWhiteSpace(archive))
            {
                Directory.CreateDirectory(Path.Combine(archive, name));
                using var saved = new StreamWriter(new FileStream(Path.Combine(archive, name, "input.jsonl"), FileMode.CreateNew, FileAccess.Write));
                saved.Write(contents);
            }
            using var monitor = new SessionLogMonitor(directory, TimeZoneInfo.Utc);
            using var lifetime = new CancellationTokenSource();
            using var queuedCancellation = new CancellationTokenSource();
            try
            {
                await monitor.RefreshAsync(now);
                // Test-only barrier: hold the real reader's gate before entering
                // RefreshAsync, so one refresh holds the monitor gate and one waits.
                var sources = (IDictionary)typeof(SessionLogMonitor).GetField("_sources", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(monitor)!;
                var source = sources.Values.Cast<object>().Single();
                var reader = source.GetType().GetProperty("Reader")!.GetValue(source)!;
                var readerGate = (SemaphoreSlim)reader.GetType().GetField("_gate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(reader)!;
                var monitorGate = (SemaphoreSlim)typeof(SessionLogMonitor).GetField("_gate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(monitor)!;
                await readerGate.WaitAsync();
                var active = monitor.RefreshAsync(now, lifetime.Token);
                if (monitorGate.CurrentCount != 0 || active.IsCompleted) throw new InvalidOperationException("Active refresh did not reach the controlled reader barrier");
                var queued = monitor.RefreshAsync(now, queuedCancellation.Token);
                if (queued.IsCompleted) throw new InvalidOperationException("Second refresh did not queue at the monitor barrier");
                if (cancelAndAwait)
                {
                    // This is ExitAsync's monitor-facing order: cancel, await the
                    // refresh, then dispose. It does not launch or test a GUI EXE.
                    lifetime.Cancel();
                    queuedCancellation.Cancel();
                    await Observe(active, "active cancelled", true);
                    await Observe(queued, "queued cancelled", true);
                    readerGate.Release();
                    monitor.Dispose();
                }
                else
                {
                    monitor.Dispose();
                    readerGate.Release();
                    await Observe(active, "active after Dispose", false);
                    await Observe(queued, "queued after Dispose", false);
                    queuedCancellation.Cancel();
                }
                try { await monitor.RefreshAsync(now); failures.Add(name + ": refresh accepted after Dispose"); }
                catch (ObjectDisposedException error) { Console.WriteLine(name + " post-dispose: " + error.GetType().Name + " / " + error.ObjectName); }
                if (sources.Count != 0 || !(bool)reader.GetType().GetField("_disposed", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(reader)!)
                    failures.Add(name + ": sources or reader remained live after refreshes settled");
                else Console.WriteLine(name + " cleanup: sources empty and reader disposed");
                Console.WriteLine("DONE: " + name);

                async Task Observe(Task task, string stage, bool cancellationExpected)
                {
                    try
                    {
                        await task.WaitAsync(TimeSpan.FromSeconds(2));
                        Console.WriteLine(name + " / " + stage + ": completed");
                        if (cancellationExpected) failures.Add(name + " / " + stage + ": expected cancellation");
                    }
                    catch (OperationCanceledException) when (cancellationExpected) { Console.WriteLine(name + " / " + stage + ": cancelled as expected"); }
                    catch (ObjectDisposedException error) when (!cancellationExpected && error.ObjectName == typeof(SessionLogMonitor).FullName)
                    { Console.WriteLine(name + " / " + stage + ": monitor rejected disposed refresh"); }
                    catch (Exception error)
                    {
                        failures.Add(name + " / " + stage + ": " + error.GetType().Name + " / " + error.Message);
                        Console.WriteLine("FAIL: " + name + " / " + stage + ": " + error);
                    }
                }
            }
            finally { Directory.Delete(directory, recursive: true); }
        }
        Console.WriteLine($"RESULT MONITOR DISPOSAL: {failures.Count} failures");
        if (failures.Count > 0) throw new InvalidOperationException(string.Join("\n", failures));
    }
}
