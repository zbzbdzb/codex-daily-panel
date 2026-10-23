using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace CodexUsage.Core;

public static class OfficialCodexCli
{
    public static string? FindExecutable()
    {
        if (!OperatingSystem.IsWindows()) return null;
        var roots = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Trim().Trim('"')).Where(Path.IsPathFullyQualified).ToList();
        // Explorer-launched apps may inherit PATH from before npm was installed.
        roots.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm"));
        var architecture = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture == System.Runtime.InteropServices.Architecture.Arm64 ? "arm64" : "x64";
        var triple = architecture == "arm64" ? "aarch64-pc-windows-msvc" : "x86_64-pc-windows-msvc";
        foreach (var root in roots.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            foreach (var candidate in new[]
            {
                Path.Combine(root, "codex.exe"),
                Path.Combine(root, "node_modules", "@openai", "codex", "node_modules", "@openai", $"codex-win32-{architecture}", "vendor", triple, "bin", "codex.exe"),
                Path.Combine(root, "node_modules", "@openai", "codex", "vendor", triple, "bin", "codex.exe")
            })
                if (File.Exists(candidate)) return Path.GetFullPath(candidate);
        }
        return null;
    }

    public static IAccountRpcSession Start() => StartFor(false);
    public static IAccountRpcSession StartThreadUsage() => StartFor(true);
    private static IAccountRpcSession StartFor(bool threadReads)
    {
        var executable = FindExecutable() ?? throw new AccountRpcException(AccountQuotaStatus.CliNotFound);
        var info = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var argument in new[] { "-s", "read-only", "-a", "never", "app-server" }) info.ArgumentList.Add(argument);
        try { return new OfficialAccountRpcSession(Process.Start(info) ?? throw new AccountRpcException(AccountQuotaStatus.StartupFailed), threadReads); }
        catch (Win32Exception e) { throw new AccountRpcException(e.NativeErrorCode == 5 ? AccountQuotaStatus.PermissionDenied : AccountQuotaStatus.StartupFailed); }
    }
}

// Read JSONL with explicit bounds before allocating a whole line; no raw output is logged.
public sealed class AccountRpcLineReader(TextReader reader)
{
    public const int MaximumLineChars = 1024 * 1024;
    private readonly char[] _buffer = new char[4096];
    private int _position, _length, _total;
    public async Task<string?> ReadAsync(CancellationToken cancellationToken)
    {
        var line = new StringBuilder();
        while (true)
        {
            if (_position == _length)
            {
                _length = await reader.ReadAsync(_buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
                _position = 0;
                _total += _length;
                if (_total > 4 * MaximumLineChars) throw new AccountRpcException(AccountQuotaStatus.ProtocolError);
                if (_length == 0) return line.Length == 0 ? null : throw new AccountRpcException(AccountQuotaStatus.ProtocolError);
            }
            var next = _buffer[_position++];
            if (next == '\n') return line.ToString().TrimEnd('\r');
            if (line.Length >= MaximumLineChars) throw new AccountRpcException(AccountQuotaStatus.ProtocolError);
            line.Append(next);
        }
    }
}

internal sealed class OfficialAccountRpcSession : IAccountRpcSession
{
    private readonly Process _process;
    private readonly AccountRpcLineReader _output;
    private readonly CancellationTokenSource _drainStop = new();
    private readonly Task _stderrTask;
    private volatile bool _permissionFailure;
    private int _id;
    private readonly bool _threadReads;
    public bool CleanupSucceeded { get; private set; }
    public OfficialAccountRpcSession(Process process) : this(process, false) { }
    public OfficialAccountRpcSession(Process process, bool threadReads)
    {
        _process = process;
        _threadReads = threadReads;
        _output = new(process.StandardOutput);
        _stderrTask = DrainErrorsAsync();
    }
    private async Task DrainErrorsAsync()
    {
        var buffer = new char[2048];
        var tail = "";
        try
        {
            int count;
            while ((count = await _process.StandardError.ReadAsync(buffer.AsMemory(), _drainStop.Token).ConfigureAwait(false)) > 0)
            {
                var text = tail + new string(buffer, 0, count);
                // Generic arg0 cleanup warnings are nonfatal. Only use stderr when stdout closes.
                if (text.Contains("Access is denied", StringComparison.OrdinalIgnoreCase) || text.Contains("permission denied", StringComparison.OrdinalIgnoreCase)) _permissionFailure = true;
                tail = text.Length > 64 ? text[^64..] : text;
            }
        }
        catch (OperationCanceledException) { }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
    }
    private async Task SendAsync(object message, CancellationToken cancellationToken)
    {
        await _process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(message).AsMemory(), cancellationToken).ConfigureAwait(false);
        await _process.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);
    }
    public Task NotifyInitializedAsync(CancellationToken cancellationToken) => SendAsync(new { method = "initialized" }, cancellationToken);
    public async Task<JsonElement> RequestAsync(string method, object parameters, CancellationToken cancellationToken)
    {
        var allowed = _threadReads ? method is "initialize" or "thread/list" or "account/usage/read"
            : method is "initialize" or "account/read" or "account/rateLimits/read";
        if (!allowed) throw new AccountRpcException(AccountQuotaStatus.UnsupportedProtocol);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(method == "initialize" ? 10 : 15));
        var id = ++_id;
        try
        {
            await SendAsync(new { id, method, @params = parameters }, deadline.Token).ConfigureAwait(false);
            for (var count = 0; count < 64; count++)
            {
                var line = await _output.ReadAsync(deadline.Token).ConfigureAwait(false);
                if (line is null)
                {
                    if (_process.HasExited) await _stderrTask.WaitAsync(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
                    throw new AccountRpcException(_permissionFailure ? AccountQuotaStatus.PermissionDenied : id == 1 ? AccountQuotaStatus.StartupFailed : AccountQuotaStatus.ProtocolError);
                }
                if (string.IsNullOrWhiteSpace(line)) continue;
                using var document = JsonDocument.Parse(line, new JsonDocumentOptions { MaxDepth = 32 });
                var message = document.RootElement;
                AccountQuotaParser.ValidateObject(message);
                if (!message.TryGetProperty("id", out var responseId))
                {
                    if (!message.TryGetProperty("method", out _)) throw new AccountRpcException(AccountQuotaStatus.ProtocolError);
                    continue;
                }
                if (responseId.ValueKind != JsonValueKind.Number || !responseId.TryGetInt32(out var number) || number != id)
                    throw new AccountRpcException(AccountQuotaStatus.ProtocolError);
                if (message.TryGetProperty("error", out var error)) throw new AccountRpcException(ClassifyError(error));
                if (!message.TryGetProperty("result", out var result) || result.ValueKind != JsonValueKind.Object) throw new AccountRpcException(AccountQuotaStatus.ProtocolError);
                return result.Clone();
            }
            throw new AccountRpcException(AccountQuotaStatus.ProtocolError);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { throw new AccountRpcException(AccountQuotaStatus.Timeout); }
        catch (TimeoutException) { throw new AccountRpcException(AccountQuotaStatus.Timeout); }
    }
    internal static AccountQuotaStatus ClassifyError(JsonElement error)
    {
        if (error.ValueKind != JsonValueKind.Object) return AccountQuotaStatus.ProtocolError;
        var code = error.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.Number && c.TryGetInt32(out var i) ? i : 0;
        var message = error.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() ?? "" : "";
        if (code == -32601) return AccountQuotaStatus.UnsupportedProtocol;
        if (code == 401 || message.Contains("unauthorized", StringComparison.OrdinalIgnoreCase) || message.Contains("not logged", StringComparison.OrdinalIgnoreCase)
            || message.Contains("sign in", StringComparison.OrdinalIgnoreCase) || message.Contains("authentication", StringComparison.OrdinalIgnoreCase)) return AccountQuotaStatus.LoginRequired;
        if (code == 429 || message.Contains("too many requests", StringComparison.OrdinalIgnoreCase)) return AccountQuotaStatus.RateLimited;
        if (message.Contains("connect", StringComparison.OrdinalIgnoreCase) || message.Contains("network", StringComparison.OrdinalIgnoreCase)
            || message.Contains("dns", StringComparison.OrdinalIgnoreCase) || message.Contains("request", StringComparison.OrdinalIgnoreCase) && message.Contains("send", StringComparison.OrdinalIgnoreCase)) return AccountQuotaStatus.NetworkError;
        return AccountQuotaStatus.ServiceError;
    }
    public async ValueTask DisposeAsync()
    {
        try
        {
            _process.StandardInput.Close();
            try { await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
            catch (TimeoutException)
            {
                _process.Kill(entireProcessTree: true);
                await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
            }
            CleanupSucceeded = _process.HasExited;
        }
        catch (Exception e) when (e is InvalidOperationException or Win32Exception or IOException or TimeoutException) { CleanupSucceeded = _process.HasExited; }
        finally
        {
            _drainStop.Cancel();
            try { await _stderrTask.ConfigureAwait(false); } catch (Exception) { /* Never expose raw CLI diagnostics. */ }
            _drainStop.Dispose();
            _process.Dispose();
        }
    }
}
