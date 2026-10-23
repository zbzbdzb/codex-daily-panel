using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using CodexUsage.DailyPanel;

if (args.Length == 2 && args[0] == "--activation-worker")
{
    using var instance = SingleInstanceCoordinator.Acquire(args[1]);
    return !instance.IsPrimary && instance.TryActivateAsync(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult() ? 0 : 1;
}
if (args.Length != 2 || args[0] != "--test-root" || !Path.IsPathFullyQualified(args[1]))
{
    Console.Error.WriteLine("Require --test-root <new physical synthetic directory>.");
    return 2;
}
var root = args[1];
if (Directory.Exists(root)) throw new InvalidOperationException("Test root must be new.");
Directory.CreateDirectory(root);
var passed = 0;
var failed = 0;
void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
void Test(string name, Action body)
{
    try { body(); passed++; Console.WriteLine("PASS " + name); }
    catch (Exception e) { failed++; Console.WriteLine("FAIL " + name + ": " + e.Message); }
}
PanelSettingsStore Store(string name) => new(Path.Combine(root, name));
void Fixture(string name, string text)
{
    var directory = Path.Combine(root, name);
    Directory.CreateDirectory(directory);
    File.WriteAllText(Path.Combine(directory, "settings.json"), text);
}
Test("missing store defaults without creating a directory", () =>
{
    var result = Store("missing").Load();
    Check(result.Settings == new PanelSettings() && result.Issue is null, "Expected system defaults.");
    Check(!Directory.Exists(Path.Combine(root, "missing")), "Load must not write.");
});
Test("atomic save persists only version theme language and round trips", () =>
{
    var store = Store("roundtrip");
    var settings = new PanelSettings(1, ThemePreference.Dark, LanguagePreference.ZhCN);
    Check(store.Save(settings).Issue is null, "Save failed.");
    Check(store.Load().Settings == settings, "Round trip changed preferences.");
    using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(store.DirectoryPath, "settings.json")));
    Check(document.RootElement.EnumerateObject().Count() == 3, "Unexpected persisted data.");
    Check(!Directory.EnumerateFiles(store.DirectoryPath).Any(p => p.EndsWith(".tmp", StringComparison.Ordinal)), "Temporary file leaked.");
    Check(store.Save(settings with { Theme = ThemePreference.Light }).Issue is null, "Replace failed.");
    Check(store.Load().Settings.Theme == ThemePreference.Light, "Replacement not visible.");
});
Test("corrupt settings produce nonfatal defaults", () =>
{
    Fixture("corrupt", "{broken");
    var result = Store("corrupt").Load();
    Check(result.Issue?.Code == "settings.invalid" && result.Settings == new PanelSettings(), "Missing invalid-settings fallback.");
});
Test("oversized settings are bounded", () =>
{
    Fixture("oversized", new string(' ', 20000));
    Check(Store("oversized").Load().Issue?.Code == "settings.too_large", "Unbounded or wrong oversized result.");
});
Test("future schema is not silently accepted", () =>
{
    Fixture("future", "{\"schemaVersion\":2,\"theme\":\"Dark\",\"language\":\"EnUS\"}");
    Check(Store("future").Load().Issue?.Code == "settings.unsupported_version", "Future schema accepted.");
});
Test("invalid enums and duplicate keys fall back", () =>
{
    Fixture("invalid-enum", "{\"schemaVersion\":1,\"theme\":\"99\",\"language\":\"System\"}");
    Fixture("duplicate", "{\"schemaVersion\":1,\"theme\":\"Dark\",\"theme\":\"Light\",\"language\":\"System\"}");
    Check(Store("invalid-enum").Load().Issue?.Code == "settings.invalid", "Undefined enum accepted.");
    Check(Store("duplicate").Load().Issue?.Code == "settings.invalid", "Duplicate settings accepted.");
});
Test("read-only destination fails nonfatally and preserves old bytes", () =>
{
    var store = Store("readonly");
    Check(store.Save(new PanelSettings()).Issue is null, "Fixture save failed.");
    var path = Path.Combine(store.DirectoryPath, "settings.json");
    var before = File.ReadAllBytes(path);
    File.SetAttributes(path, FileAttributes.ReadOnly);
    try
    {
        var wanted = new PanelSettings(1, ThemePreference.Dark);
        var result = store.Save(wanted);
        Check(result.Issue?.Code == "settings.write_failed" && result.Settings == wanted, "Save failure must retain session choice.");
        Check(before.SequenceEqual(File.ReadAllBytes(path)), "Failed replacement changed old bytes.");
    }
    finally { File.SetAttributes(path, FileAttributes.Normal); }
});
Test("directory occupied by file is a nonfatal write error", () =>
{
    var path = Path.Combine(root, "occupied"); File.WriteAllText(path, "synthetic");
    Check(new PanelSettingsStore(path).Save(new PanelSettings()).Issue?.Code == "settings.write_failed", "Write exception escaped.");
});
Test("locked settings read fails nonfatally", () =>
{
    Fixture("locked", "{}");
    using var handle = new FileStream(Path.Combine(root, "locked", "settings.json"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    Check(Store("locked").Load().Issue?.Code == "settings.read_failed", "Locked file was ignored.");
});
Test("legacy demo render snapshot and explicit directories parse", () =>
{
    Check(DailyOptions.TryParse(["--demo", "--snapshot", Path.Combine(root, "demo.json"), "--codex-home", root, "--settings-dir", Path.Combine(root, "prefs")], out var options, out _), "Legacy demo snapshot rejected.");
    Check(options!.Demo && options.SnapshotPath == Path.Combine(root, "demo.json") && options.Home == root, "Legacy values changed.");
    Check(DailyOptions.TryParse(["--render", Path.Combine(root, "demo.png")], out options, out _) && options!.Demo, "Render no longer implies demo.");
});
Test("invalid CLI is bounded and never throws", () =>
{
    foreach (var input in new[] { new[] { "--bad" }, new[] { "--snapshot" }, new[] { "--snapshot", "--demo" }, new[] { "--codex-home", "a\0b" }, new[] { "--codex-home", " " }, new[] { "--codex-home", new string('x', 40000) } })
    {
        Check(!DailyOptions.TryParse(input, out _, out var error), "Invalid arguments accepted.");
        Check(error is { Length: > 0 and <= 256 }, "Unbounded/missing error.");
    }
});
Test("render snapshot conflict and missing settings path reject", () =>
{
    Check(!DailyOptions.TryParse(["--render", "r.png", "--snapshot", "s.json"], out _, out _), "Incompatible modes accepted.");
    Check(!DailyOptions.TryParse(["--settings-dir"], out _, out _), "Missing settings value accepted.");
});
Test("help and version do not resolve invalid paths or access stores", () =>
{
    Check(DailyOptions.TryParse(["--help", "--codex-home", "a\0b"], out var help, out _) && help!.ShowHelp, "Help resolved a data path.");
    Check(DailyOptions.TryParse(["--version"], out var version, out _) && version!.ShowVersion, "Version rejected.");
});
Test("path errors reject devices and alternate data streams", () =>
{
    Check(!DailyOptions.TryParse(["--snapshot", Path.Combine(root, "NUL")], out _, out _), "Device accepted.");
    Check(!DailyOptions.TryParse(["--snapshot", Path.Combine(root, "file:stream")], out _, out _), "ADS accepted.");
});
Test("same normalized home owns one local instance and bounded activation", () =>
{
    var home = Path.Combine(root, "instance");
    using var primary = SingleInstanceCoordinator.Acquire(home);
    Check(primary.IsPrimary, "First instance was not primary.");
    var calls = 0;
    primary.StartListening(() => Interlocked.Increment(ref calls));
    using var secondary = SingleInstanceCoordinator.Acquire(Path.Combine(home, ".") + Path.DirectorySeparatorChar);
    Check(!secondary.IsPrimary, "Normalized home created another primary.");
    Check(secondary.TryActivateAsync(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult(), "Activation not acknowledged.");
    Check(Volatile.Read(ref calls) == 1, "Activation callback count wrong.");
    using var distinct = SingleInstanceCoordinator.Acquire(Path.Combine(root, "other-home"));
    Check(distinct.IsPrimary, "Distinct home collided.");
});
Test("primary exit releases ownership for next launch", () =>
{
    var home = Path.Combine(root, "reopen");
    using (var first = SingleInstanceCoordinator.Acquire(home)) Check(first.IsPrimary, "First primary failed.");
    using var second = SingleInstanceCoordinator.Acquire(home);
    Check(second.IsPrimary, "Disposed primary left ownership behind.");
});
Test("separate process activates the existing primary without duplicate ownership", () =>
{
    var home = Path.Combine(root, "cross-process");
    using var primary = SingleInstanceCoordinator.Acquire(home);
    Check(primary.IsPrimary, "Primary acquisition failed.");
    var calls = 0;
    primary.StartListening(() => Interlocked.Increment(ref calls));
    var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
    if (string.Equals(Path.GetFileNameWithoutExtension(Environment.ProcessPath), "dotnet", StringComparison.OrdinalIgnoreCase))
        start.ArgumentList.Add(typeof(PanelSettings).Assembly.Location);
    start.ArgumentList.Add("--activation-worker"); start.ArgumentList.Add(home.ToLowerInvariant());
    using var worker = Process.Start(start)!;
    Check(worker.WaitForExit(5000), "Secondary process did not return within bounded activation interval.");
    var diagnostics = $"exit={worker.ExitCode}; calls={Volatile.Read(ref calls)}; stdout={worker.StandardOutput.ReadToEnd()}; stderr={worker.StandardError.ReadToEnd()}";
    Check(worker.ExitCode == 0 && Volatile.Read(ref calls) == 1, "Separate process failed activation: " + diagnostics);
});
Test("nonlistening primary activation timeout is bounded", () =>
{
    var home = Path.Combine(root, "timeout");
    using var first = SingleInstanceCoordinator.Acquire(home);
    using var second = SingleInstanceCoordinator.Acquire(home);
    var timer = Stopwatch.StartNew();
    Check(!second.TryActivateAsync(TimeSpan.FromMilliseconds(200)).GetAwaiter().GetResult(), "Unserved activation acknowledged.");
    Check(timer.Elapsed < TimeSpan.FromSeconds(2), "Activation hung.");
});
Test("malformed and incomplete local requests do not activate and do not block later valid requests", () =>
{
    if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
    var home = Path.Combine(root, "malformed");
    using var identity = WindowsIdentity.GetCurrent();
    var endpoint = "CodexDaily-v2-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity.User!.Value + "\n" + home.ToUpperInvariant())));
    using var primary = SingleInstanceCoordinator.Acquire(home);
    var calls = 0;
    primary.StartListening(() => Interlocked.Increment(ref calls));
    using (var malformed = new NamedPipeClientStream(".", endpoint, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly))
    {
        malformed.Connect(2000);
        malformed.Write("shutdown\n"u8);
        malformed.Flush();
    }
    using (var incomplete = new NamedPipeClientStream(".", endpoint, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly))
    {
        incomplete.Connect(2000);
        incomplete.WriteByte((byte)'a'); incomplete.Flush();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var result = incomplete.ReadAsync(new byte[1], timeout.Token).AsTask().GetAwaiter().GetResult();
        Check(result == 0, "Incomplete peer was not disconnected.");
    }
    Check(Volatile.Read(ref calls) == 0, "Malformed command activated the app.");
    using var secondary = SingleInstanceCoordinator.Acquire(home);
    Check(secondary.TryActivateAsync(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult() && Volatile.Read(ref calls) == 1, "Malformed client blocked legitimate activation.");
});
Console.WriteLine($"RESULT {passed} passed; {failed} failed");
return failed == 0 ? 0 : 1;
