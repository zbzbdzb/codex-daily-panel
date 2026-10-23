using System;
using System.IO;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
namespace CodexUsage.DailyPanel;
public sealed class SingleInstanceCoordinator : IDisposable
{
    private static readonly byte[] Activation = "activate\n"u8.ToArray();
    private static readonly byte[] Acknowledgement = "ok\n"u8.ToArray();
    private readonly string _pipeName;
    private readonly NamedPipeServerStream? _server;
    private readonly CancellationTokenSource _lifetime = new();
    private Task? _listener;
    private bool _disposed;
    public bool IsPrimary => _server is not null;

    private SingleInstanceCoordinator(string pipeName, NamedPipeServerStream? server) { _pipeName = pipeName; _server = server; }
    public static SingleInstanceCoordinator Acquire(string home)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("CodexDaily requires Windows.");
        using var identity = WindowsIdentity.GetCurrent();
        var user = identity.User?.Value ?? throw new InvalidOperationException("Current user is unavailable.");
        var normalized = DailyOptions.NormalizePath(home).ToUpperInvariant();
        var name = "CodexDaily-v2-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(user + "\n" + normalized)));
        NamedPipeServerStream? server;
        try
        {
            // One server handle owns the endpoint throughout its lifetime, including
            // disconnect/reconnect. CurrentUserOnly enforces identity at the OS boundary.
            server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly, 64, 64);
        }
        catch (IOException) { server = null; }
        return new(name, server);
    }

    public void StartListening(Action activate)
    {
        ArgumentNullException.ThrowIfNull(activate);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!IsPrimary || _listener is not null) throw new InvalidOperationException("Only a new primary may listen.");
        _listener = ListenAsync(activate);
    }

    private async Task ListenAsync(Action activate)
    {
        while (!_lifetime.IsCancellationRequested)
        {
            try
            {
                await _server!.WaitForConnectionAsync(_lifetime.Token).ConfigureAwait(false);
                using var request = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
                request.CancelAfter(TimeSpan.FromMilliseconds(750));
                var input = new byte[Activation.Length];
                await _server.ReadExactlyAsync(input, request.Token).ConfigureAwait(false);
                if (input.AsSpan().SequenceEqual(Activation))
                {
                    activate();
                    await _server.WriteAsync(Acknowledgement, request.Token).ConfigureAwait(false);
                    await _server.FlushAsync(request.Token).ConfigureAwait(false);
                    // Disconnect can discard unread pipe bytes. Wait for the client's
                    // bounded receipt so a successful callback is also acknowledged.
                    await _server.ReadExactlyAsync(new byte[1], request.Token).ConfigureAwait(false);
                }
            }
            catch (Exception e) when (e is IOException or OperationCanceledException or ObjectDisposedException or InvalidOperationException) { }
            finally
            {
                try { if (_server!.IsConnected) _server.Disconnect(); }
                catch (Exception e) when (e is IOException or ObjectDisposedException or InvalidOperationException) { }
            }
            if (!_lifetime.IsCancellationRequested)
                try { await Task.Delay(50, _lifetime.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { }
        }
    }

    public async Task<bool> TryActivateAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (IsPrimary || timeout <= TimeSpan.Zero || timeout > TimeSpan.FromSeconds(5)) return false;
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        lifetime.CancelAfter(timeout);
        try
        {
            using var pipe = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await pipe.ConnectAsync(lifetime.Token).ConfigureAwait(false);
            await pipe.WriteAsync(Activation, lifetime.Token).ConfigureAwait(false);
            await pipe.FlushAsync(lifetime.Token).ConfigureAwait(false);
            var reply = new byte[Acknowledgement.Length];
            await pipe.ReadExactlyAsync(reply, lifetime.Token).ConfigureAwait(false);
            if (!reply.AsSpan().SequenceEqual(Acknowledgement)) return false;
            await pipe.WriteAsync(new byte[] { 1 }, lifetime.Token).ConfigureAwait(false);
            await pipe.FlushAsync(lifetime.Token).ConfigureAwait(false);
            return true;
        }
        catch (Exception e) when (e is IOException or OperationCanceledException or UnauthorizedAccessException) { return false; }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _lifetime.Cancel();
        _server?.Dispose();
        // No UI callback is awaited here. RequestShow posts to the dispatcher.
        _listener?.GetAwaiter().GetResult();
        _lifetime.Dispose();
    }
}
