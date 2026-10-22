using System.Runtime.InteropServices;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace CodexUsage.Core;

/// <summary>
/// BytesRead counts new cursor bytes, excluding fixed-size identity probes. LinesRead counts delivered valid lines.
/// HasMore requests a further byte drain; WaitingForCompleteLine independently marks an unfinished row at EOF.
/// </summary>
public readonly record struct ReadBatchResult(long BytesRead, int LinesRead, bool ResetDetected, bool AccessDenied, bool Missing, bool InvalidUtf8, bool OversizedLine, bool HasMore = false, bool WaitingForCompleteLine = false);

/// <summary>
/// Reads one explicitly configured log without modifying it. Instances start at byte zero;
/// callers reconcile replayed events by request identity. A denied instance must be replaced by reconfiguration.
/// Head/boundary fingerprints detect common truncate-and-regrow operations, not arbitrary interior edits.
/// </summary>
public sealed class IncrementalLogReader : IDisposable
{
    private const int MaximumLineBytes = 4 * 1024 * 1024;
    private const int ReadBudgetBytes = 4 * 1024 * 1024;
    private const int ChunkBytes = 64 * 1024;
    private const int ProbeBytes = 128;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly char[] SensitiveNameSeparators = ['.', '_', '-'];
    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private byte[] _line = [];
    private int _lineLength;
    private bool _skippingOversizedLine;
    private bool _firstLine = true;
    private volatile bool _disposed;
    private FileIdentity? _identity;
    private byte[]? _fingerprint;
    private long _fingerprintOffset;
    private bool _pendingReset;
    private bool _hasMore;
    private bool _sourceMissing;
    private readonly object _pendingResultGate = new();
    private ReadBatchResult _pendingResult;

    public IncrementalLogReader(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = Path.GetFullPath(path);
        string extension = Path.GetExtension(_path);
        if (!extension.Equals(".jsonl", StringComparison.OrdinalIgnoreCase) && !extension.Equals(".log", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Only explicitly selected .jsonl or .log files are supported.", nameof(path));
        string? previousToken = null;
        foreach (string component in _path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            foreach (string token in component.Split(SensitiveNameSeparators, StringSplitOptions.RemoveEmptyEntries))
            {
                bool sensitivePair = token.Equals("token", StringComparison.OrdinalIgnoreCase) &&
                    (string.Equals(previousToken, "access", StringComparison.OrdinalIgnoreCase) || string.Equals(previousToken, "refresh", StringComparison.OrdinalIgnoreCase)) ||
                    token.Equals("key", StringComparison.OrdinalIgnoreCase) && string.Equals(previousToken, "api", StringComparison.OrdinalIgnoreCase);
                if (token.StartsWith("auth", StringComparison.OrdinalIgnoreCase) ||
                    token.StartsWith("credential", StringComparison.OrdinalIgnoreCase) ||
                    token.StartsWith("cookie", StringComparison.OrdinalIgnoreCase) ||
                    token.StartsWith("secret", StringComparison.OrdinalIgnoreCase) || sensitivePair)
                    throw new ArgumentException("Credential and secret file paths are not allowed.", nameof(path));
                previousToken = token;
            }
        }
    }

    /// <summary>Number of consumed bytes in the current file identity, including an unfinished line.</summary>
    public long Offset { get; private set; }
    public bool IsStopped { get; private set; }
    /// <summary>Raised after resetting the cursor and before delivering any replacement-file lines.</summary>
    public event Action? ResetDetected;

    public async Task<ReadBatchResult> ReadAvailableAsync(Action<string> onCompleteLine, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(onCompleteLine);
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            cancellationToken.ThrowIfCancellationRequested();
            if (IsStopped)
                return ReportResult(CurrentResult(accessDenied: true));
            bool callbackFailed = false;
            long bytesRead = 0;
            int linesRead = 0;
            bool reset = false;
            bool invalidUtf8 = false;
            bool oversizedLine = false;
            bool rewrittenDuringRead = false;
            ReadBatchResult completedResult = default;
            Action<string> deliver = line =>
            {
                try { onCompleteLine(line); }
                catch { callbackFailed = true; throw; }
            };
            try
            {
                try
                {
                    await using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
                        ChunkBytes, FileOptions.Asynchronous | FileOptions.SequentialScan);
                    _sourceMissing = false;
                    FileIdentity identity = GetIdentity(stream);
                    // Sample before validating the previous cursor. A rewrite between the cursor check
                    // and a later baseline must not become the trusted new baseline for an old half-line.
                    long initialLength = stream.Length;
                    byte[] initialFingerprint = Fingerprint(stream, initialLength);
                    reset = _pendingReset || (_identity is not null && (_identity.Value != identity || initialLength < Offset || !FingerprintMatches(stream)));
                    if (reset)
                    {
                        ResetCursor();
                        _pendingReset = true;
                        try { ResetDetected?.Invoke(); }
                        catch { callbackFailed = true; throw; }
                        _pendingReset = false;
                        initialLength = stream.Length;
                        initialFingerprint = Fingerprint(stream, initialLength);
                    }
                    _identity = identity;
                    _hasMore = initialLength > Offset;
                    stream.Position = Offset;
                    // An idle poll needs only identity probes; avoid allocating 64 KiB for an unchanged file.
                    byte[] chunk = _hasMore ? new byte[ChunkBytes] : [];
                    bool readSucceeded = false;
                    try
                    {
                        while (bytesRead < ReadBudgetBytes && chunk.Length != 0)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            ObjectDisposedException.ThrowIf(_disposed, this);
                            int read = await stream.ReadAsync(chunk.AsMemory(0, (int)Math.Min(ChunkBytes, ReadBudgetBytes - bytesRead)), cancellationToken).ConfigureAwait(false);
                            if (read == 0)
                                break;
                            Consume(chunk.AsSpan(0, read), deliver, cancellationToken, ref bytesRead, ref linesRead, ref invalidUtf8, ref oversizedLine);
                        }
                        readSucceeded = true;
                    }
                    finally
                    {
                        chunk.AsSpan().Clear();
                        try
                        {
                            // Also revalidate on callback/cancellation failure. Such failures must not
                            // hide a concurrent rewrite after the cursor already consumed some bytes.
                            byte[] cursorFingerprint = Fingerprint(stream, Offset);
                            rewrittenDuringRead = stream.Length < initialLength ||
                                !initialFingerprint.AsSpan().SequenceEqual(Fingerprint(stream, initialLength));
                            if (rewrittenDuringRead)
                                _pendingReset = true;
                            else
                            {
                                _fingerprintOffset = Offset;
                                _fingerprint = cursorFingerprint;
                            }
                            _hasMore = stream.Length > Offset;
                        }
                        // Preserve the original callback/read exception if the cleanup probe also fails.
                        catch when (!readSucceeded) { _pendingReset = true; }
                        catch { _pendingReset = true; throw; }
                    }
                    completedResult = CurrentResult(bytesRead, linesRead, reset || rewrittenDuringRead, invalidUtf8: invalidUtf8, oversizedLine: oversizedLine);
                }
                catch (UnauthorizedAccessException) when (!callbackFailed)
                {
                    StopAfterAccessDenied();
                    return ReportResult(CurrentResult(bytesRead, linesRead, reset || rewrittenDuringRead, accessDenied: true, invalidUtf8: invalidUtf8, oversizedLine: oversizedLine));
                }
                catch (SecurityException) when (!callbackFailed)
                {
                    StopAfterAccessDenied();
                    return ReportResult(CurrentResult(bytesRead, linesRead, reset || rewrittenDuringRead, accessDenied: true, invalidUtf8: invalidUtf8, oversizedLine: oversizedLine));
                }
                catch (IOException exception) when (!callbackFailed && (exception.HResult & 0xFFFF) is 5 or 1314)
                {
                    StopAfterAccessDenied();
                    return ReportResult(CurrentResult(bytesRead, linesRead, reset || rewrittenDuringRead, accessDenied: true, invalidUtf8: invalidUtf8, oversizedLine: oversizedLine));
                }
                catch (FileNotFoundException) when (!callbackFailed)
                {
                    _hasMore = false;
                    _sourceMissing = true;
                    return ReportResult(CurrentResult(bytesRead, linesRead, reset || rewrittenDuringRead, missing: true, invalidUtf8: invalidUtf8, oversizedLine: oversizedLine));
                }
                catch (DirectoryNotFoundException) when (!callbackFailed)
                {
                    _hasMore = false;
                    _sourceMissing = true;
                    return ReportResult(CurrentResult(bytesRead, linesRead, reset || rewrittenDuringRead, missing: true, invalidUtf8: invalidUtf8, oversizedLine: oversizedLine));
                }
                // Commit only after FileStream disposal succeeded; disposal failure must keep
                // the previous undrained statistics as well as this read's consumed progress.
                return ReportResult(completedResult);
            }
            catch
            {
                // A callback or cancellation can interrupt Consume after it advanced the cursor.
                // Keep only pure statistics so Monitor can commit them despite the thrown exception.
                lock (_pendingResultGate)
                    _pendingResult = MergeResults(_pendingResult, CurrentResult(bytesRead, linesRead, reset || rewrittenDuringRead, invalidUtf8: invalidUtf8, oversizedLine: oversizedLine));
                throw;
            }
        }
        finally
        {
            if (_disposed)
            {
                _line.AsSpan().Clear();
                _line = [];
                _lineLength = 0;
                _skippingOversizedLine = false;
                _fingerprint = null;
            }
            _gate.Release();
        }
    }

    /// <summary>
    /// Commits consumed statistics after an interrupted read; repeated drains do not repeat counts or diagnostics.
    /// Successful reads merge undrained statistics automatically. Call after observing the read's completion.
    /// Backlog and half-line flags reflect current reader state, independently of the interruption itself.
    /// </summary>
    internal ReadBatchResult DrainPendingResult() => ReportResult(CurrentResult(accessDenied: IsStopped, missing: _sourceMissing));

    private ReadBatchResult CurrentResult(long bytesRead = 0, int linesRead = 0, bool resetDetected = false, bool accessDenied = false,
        bool missing = false, bool invalidUtf8 = false, bool oversizedLine = false) =>
        new(bytesRead, linesRead, resetDetected, accessDenied, missing, invalidUtf8, oversizedLine,
            !_disposed && !IsStopped && !_sourceMissing && (_hasMore || _pendingReset), !_disposed && !IsStopped && (_lineLength > 0 || _skippingOversizedLine));

    private ReadBatchResult ReportResult(ReadBatchResult current)
    {
        lock (_pendingResultGate)
        {
            ReadBatchResult result = MergeResults(_pendingResult, current);
            _pendingResult = default;
            return result;
        }
    }

    private static ReadBatchResult MergeResults(ReadBatchResult pending, ReadBatchResult current) =>
        new(pending.BytesRead + current.BytesRead, pending.LinesRead + current.LinesRead,
            pending.ResetDetected || current.ResetDetected, current.AccessDenied, current.Missing,
            pending.InvalidUtf8 || current.InvalidUtf8, pending.OversizedLine || current.OversizedLine,
            current.HasMore, current.WaitingForCompleteLine);

    private void Consume(ReadOnlySpan<byte> bytes, Action<string> onCompleteLine, CancellationToken cancellationToken, ref long bytesRead, ref int linesRead, ref bool invalidUtf8, ref bool oversizedLine)
    {
        while (!bytes.IsEmpty)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ObjectDisposedException.ThrowIf(_disposed, this);
            int newline = bytes.IndexOf((byte)'\n');
            int contentLength = newline < 0 ? bytes.Length : newline;
            AppendLineBytes(bytes[..contentLength], ref oversizedLine);
            int consumed = contentLength + (newline < 0 ? 0 : 1);
            Offset += consumed;
            bytesRead += consumed;
            bytes = bytes[consumed..];
            if (newline < 0)
                break;
            if (_skippingOversizedLine)
            {
                oversizedLine = true;
                _skippingOversizedLine = false;
                _lineLength = 0;
                _firstLine = false;
                continue;
            }
            int length = _lineLength;
            if (length > 0 && _line[length - 1] == (byte)'\r')
                length--;
            int start = _firstLine && length >= 3 && _line[0] == 0xEF && _line[1] == 0xBB && _line[2] == 0xBF ? 3 : 0;
            string? line;
            try { line = StrictUtf8.GetString(_line, start, length - start); }
            catch (DecoderFallbackException) { invalidUtf8 = true; line = null; }
            _line.AsSpan(0, _lineLength).Clear();
            _lineLength = 0;
            _firstLine = false;
            if (line is not null)
            {
                onCompleteLine(line);
                linesRead++;
            }
        }
    }

    private void AppendLineBytes(ReadOnlySpan<byte> bytes, ref bool oversizedLine)
    {
        if (_skippingOversizedLine)
        {
            oversizedLine = true;
            return;
        }
        if (bytes.Length > MaximumLineBytes - _lineLength)
        {
            _skippingOversizedLine = true;
            oversizedLine = true;
            _line.AsSpan().Clear();
            _lineLength = 0;
            _line = [];
            return;
        }
        int needed = _lineLength + bytes.Length;
        if (needed > _line.Length)
        {
            int capacity = Math.Min(MaximumLineBytes, Math.Max(needed, Math.Max(ChunkBytes, _line.Length * 2)));
            byte[] previous = _line;
            _line = new byte[capacity];
            previous.AsSpan(0, _lineLength).CopyTo(_line);
            previous.AsSpan().Clear();
        }
        bytes.CopyTo(_line.AsSpan(_lineLength));
        _lineLength = needed;
    }

    private bool FingerprintMatches(FileStream stream) => _fingerprint is null ||
        (_fingerprintOffset <= stream.Length && _fingerprint.AsSpan().SequenceEqual(Fingerprint(stream, _fingerprintOffset)));

    private static byte[] Fingerprint(FileStream stream, long offset)
    {
        // Fixed 256-byte probes, never a repeated historical scan. Store only a SHA-256 digest.
        byte[] samples = new byte[ProbeBytes * 2];
        try
        {
            int headLength = (int)Math.Min(ProbeBytes, offset);
            int tailLength = (int)Math.Min(ProbeBytes, offset);
            ReadProbe(stream.SafeFileHandle, samples.AsSpan(0, headLength), 0);
            ReadProbe(stream.SafeFileHandle, samples.AsSpan(ProbeBytes, tailLength), offset - tailLength);
            return SHA256.HashData(samples);
        }
        finally { samples.AsSpan().Clear(); }
    }

    private static void ReadProbe(SafeFileHandle handle, Span<byte> target, long offset)
    {
        int read = 0;
        while (read < target.Length)
        {
            int count = RandomAccess.Read(handle, target[read..], offset + read);
            if (count == 0)
                break;
            read += count;
        }
    }

    private static FileIdentity GetIdentity(FileStream stream)
    {
        if (OperatingSystem.IsWindows())
        {
            if (!GetFileInformationByHandle(stream.SafeFileHandle, out ByHandleFileInformation information))
            {
                int error = Marshal.GetLastWin32Error();
                throw new IOException("Unable to identify the selected log file.", unchecked((int)(0x80070000u | (uint)error)));
            }
            return new(information.VolumeSerialNumber, information.FileIndexHigh, information.FileIndexLow, 0);
        }
        // The supported product is Windows. This fallback keeps synthetic harnesses usable elsewhere.
        return new(0, 0, 0, File.GetCreationTimeUtc(stream.Name).Ticks);
    }

    private void StopAfterAccessDenied()
    {
        IsStopped = true;
        _sourceMissing = false;
        _line.AsSpan().Clear();
        _line = [];
        _lineLength = 0;
        _skippingOversizedLine = false;
        _hasMore = false;
    }

    private void ResetCursor()
    {
        Offset = 0;
        _line.AsSpan().Clear();
        _lineLength = 0;
        _skippingOversizedLine = false;
        _firstLine = true;
        _fingerprint = null;
        _fingerprintOffset = 0;
    }

    public void Dispose()
    {
        _disposed = true;
        // No open file is retained between reads. Keep the gate alive so already queued reads
        // can leave cleanly after observing disposal, instead of racing SemaphoreSlim.Dispose.
        if (_gate.Wait(0))
        {
            try { _line.AsSpan().Clear(); _line = []; _lineLength = 0; _skippingOversizedLine = false; _fingerprint = null; }
            finally { _gate.Release(); }
        }
    }

    private readonly record struct FileIdentity(uint Volume, uint High, uint Low, long FallbackCreationTime);

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out ByHandleFileInformation information);
}





