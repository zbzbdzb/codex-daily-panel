using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodexUsage.Core;

public sealed record LocalDisplaySettings(bool HudTopmost = true, double HudScale = 1,
    int HistoricalMinutes = 15, bool ShowHud = false, bool Paused = false);
public sealed record LocalSourceEntry(string Path, string SourceKey, bool RequiresReselection = false);
public sealed record LocalSourceIndex(LocalSourceEntry[] Sources, string? SelectedSessionKey);
public sealed record LocalAppState(int SchemaVersion, LocalDisplaySettings Settings, LocalSourceIndex Index)
{
    public static LocalAppState Default => new(1, new(), new([], null));
}
public enum LocalStateStatus { Missing, Loaded, Saved, Recovered, CorruptReset, Busy, Unavailable, InvalidState, UnsupportedVersion, ClearIncomplete }
public sealed record LocalStateLoadResult(LocalAppState State, LocalStateStatus Status);
public sealed record LocalStateSaveResult(bool Saved, LocalStateStatus Status);

public sealed class LocalStateStore
{
    private const int MaximumBytes = 64 * 1024;
    private const string CurrentName = "local-state.json", BackupName = "local-state.backup.json", LockName = "local-state.lock";
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 16
    };
    private readonly string? _directory;

    public LocalStateStore(string directory)
    {
        try { if (!string.IsNullOrWhiteSpace(directory) && Path.IsPathFullyQualified(directory)) _directory = Path.GetFullPath(directory); }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or IOException) { }
    }

    public LocalStateLoadResult Load()
    {
        if (_directory is null) return Default(LocalStateStatus.Unavailable);
        try
        {
            using var held = Lock();
            var current = Read(CurrentName); var backup = Read(BackupName);
            if (current.Kind == FileKind.Unsupported || backup.Kind == FileKind.Unsupported) return Default(LocalStateStatus.UnsupportedVersion);
            if (current.Kind == FileKind.Unavailable || backup.Kind == FileKind.Unavailable) return Default(LocalStateStatus.Unavailable);
            if (current.Kind == FileKind.Valid) return new(current.State!, LocalStateStatus.Loaded);
            if (backup.Kind == FileKind.Valid) return new(backup.State!, LocalStateStatus.Recovered);
            return Default(current.Kind == FileKind.Missing && backup.Kind == FileKind.Missing ? LocalStateStatus.Missing : LocalStateStatus.CorruptReset);
        }
        catch (IOException exception) { return Default(IoStatus(exception)); }
        catch (Exception exception) when (exception is UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Security.SecurityException)
        { return Default(LocalStateStatus.Unavailable); }
    }

    public LocalStateSaveResult Save(LocalAppState state, bool discardBackup = false)
    {
        if (!TryNormalize(state, out var snapshot) || (discardBackup && (snapshot.Index.Sources.Length != 0 || snapshot.Index.SelectedSessionKey is not null)))
            return new(false, LocalStateStatus.InvalidState);
        if (_directory is null) return new(false, LocalStateStatus.Unavailable);
        var bytes = Encode(snapshot);
        if (bytes.Length > MaximumBytes) return new(false, LocalStateStatus.InvalidState);
        var ownedTemporaries = new List<string>();
        var clearCurrentCommitted = false;
        try
        {
            using var held = Lock();
            var current = Read(CurrentName); var backup = Read(BackupName);
            if (current.Kind == FileKind.Unsupported || backup.Kind == FileKind.Unsupported) return new(false, LocalStateStatus.UnsupportedVersion);
            if (current.Kind == FileKind.Unavailable || backup.Kind == FileKind.Unavailable) return new(false, LocalStateStatus.Unavailable);
            var temporary = WriteTemporary(bytes, ownedTemporaries);
            if (discardBackup)
            {
                // Keep the old recovery copy until a valid empty current has committed.
                File.Move(temporary, FilePath(CurrentName), overwrite: true);
                clearCurrentCommitted = true;
                File.Move(WriteTemporary(bytes, ownedTemporaries), FilePath(BackupName), overwrite: true);
            }
            else if (current.Kind == FileKind.Valid)
                File.Replace(temporary, FilePath(CurrentName), FilePath(BackupName), ignoreMetadataErrors: true);
            else
            {
                // Never promote a corrupt current file over a known-good backup.
                if (backup.Kind != FileKind.Valid)
                    File.Move(WriteTemporary(bytes, ownedTemporaries), FilePath(BackupName), overwrite: true);
                File.Move(temporary, FilePath(CurrentName), overwrite: true);
            }
            return new(true, LocalStateStatus.Saved);
        }
        catch (IOException exception) { return new(false, clearCurrentCommitted ? LocalStateStatus.ClearIncomplete : IoStatus(exception)); }
        catch (Exception exception) when (exception is UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Security.SecurityException)
        { return new(false, clearCurrentCommitted ? LocalStateStatus.ClearIncomplete : LocalStateStatus.Unavailable); }
        finally
        {
            // Only paths created by this Save are eligible; prior orphan files remain untouched.
            foreach (var path in ownedTemporaries)
                try { File.Delete(path); }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
        }
    }

    private FileStream Lock()
    {
        NoReparse(_directory!); Directory.CreateDirectory(_directory!);
        foreach (var name in new[] { CurrentName, BackupName, LockName }) NoReparse(FilePath(name));
        return new(FilePath(LockName), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }
    private string FilePath(string name) => Path.Combine(_directory!, name);
    private string WriteTemporary(byte[] bytes, List<string> ownedTemporaries)
    {
        var path = FilePath(".local-state-" + Guid.NewGuid().ToString("N") + ".tmp");
        using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        ownedTemporaries.Add(path);
        file.Write(bytes); file.Flush(flushToDisk: true);
        return path;
    }
    private FileRead Read(string name)
    {
        byte[] bytes;
        try
        {
            using var file = new FileStream(FilePath(name), FileMode.Open, FileAccess.Read, FileShare.Read);
            if (file.Length is <= 0 or > MaximumBytes) return new(FileKind.Invalid, null);
            bytes = new byte[(int)file.Length]; file.ReadExactly(bytes);
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException) { return new(FileKind.Missing, null); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException) { return new(FileKind.Unavailable, null); }
        try
        {
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 16 });
            var root = document.RootElement;
            if (Future(root, "version")) return new(FileKind.Unsupported, null);
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("payload", out var futurePayload) && Future(futurePayload, "schemaVersion"))
                return new(FileKind.Unsupported, null);
            if (!Fields(root, "version", "checksum", "payload") || !root.GetProperty("version").TryGetInt32(out var version) || version != 1) return new(FileKind.Invalid, null);
            var payload = root.GetProperty("payload"); var checksum = root.GetProperty("checksum");
            if (checksum.ValueKind != JsonValueKind.String || !HexKey(checksum.GetString()) || !PayloadShape(payload)) return new(FileKind.Invalid, null);
            var payloadBytes = Encoding.UTF8.GetBytes(payload.GetRawText());
            if (!CryptographicOperations.FixedTimeEquals(Convert.FromHexString(checksum.GetString()!), SHA256.HashData(payloadBytes))) return new(FileKind.Invalid, null);
            var state = JsonSerializer.Deserialize<LocalAppState>(payloadBytes, Json);
            return TryNormalize(state, out var normalized) ? new(FileKind.Valid, normalized) : new(FileKind.Invalid, null);
        }
        catch (Exception exception) when (exception is JsonException or FormatException or InvalidOperationException or ArgumentException or NotSupportedException)
        { return new(FileKind.Invalid, null); }
    }
    private static byte[] Encode(LocalAppState state)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(state, Json);
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject(); writer.WriteNumber("version", 1);
            writer.WriteString("checksum", Convert.ToHexString(SHA256.HashData(payload)));
            writer.WritePropertyName("payload"); writer.WriteRawValue(payload); writer.WriteEndObject();
        }
        return buffer.ToArray();
    }
    private static bool TryNormalize(LocalAppState? state, out LocalAppState normalized)
    {
        normalized = LocalAppState.Default;
        if (state is null || state.SchemaVersion != 1 || state.Settings is not { } settings || state.Index is not { Sources: { } sources } index
            || settings.HudScale is not (0.8 or 1 or 1.2 or 1.4) || settings.HistoricalMinutes is not (5 or 15 or 60)
            || sources.Length > 16 || (index.SelectedSessionKey is not null && !HexKey(index.SelectedSessionKey))) return false;
        var copied = sources.ToArray(); var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < copied.Length; i++)
        {
            var entry = copied[i];
            if (entry is null || string.IsNullOrWhiteSpace(entry.Path) || entry.Path.Length > 4096 || !Path.IsPathFullyQualified(entry.Path)
                || entry.Path.Any(character => character < 32 || character is '*' or '?' or '"' or '<' or '>' or '|')) return false;
            try
            {
                var path = Path.GetFullPath(entry.Path); var extension = Path.GetExtension(path);
                if (path.Length > 4096 || !(extension.Equals(".jsonl", StringComparison.OrdinalIgnoreCase) || extension.Equals(".log", StringComparison.OrdinalIgnoreCase))
                    || !paths.Add(path) || !string.Equals(entry.SourceKey, EventParser.Key("source", path), StringComparison.Ordinal)) return false;
                copied[i] = new(path, entry.SourceKey, entry.RequiresReselection);
            }
            catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException) { return false; }
        }
        normalized = new(1, settings, new(copied, index.SelectedSessionKey?.ToUpperInvariant()));
        return true;
    }
    private static bool PayloadShape(JsonElement payload)
    {
        if (!Fields(payload, "schemaVersion", "settings", "index")) return false;
        var settings = payload.GetProperty("settings"); var index = payload.GetProperty("index");
        if (!Fields(settings, "hudTopmost", "hudScale", "historicalMinutes", "showHud", "paused") || !Fields(index, "sources", "selectedSessionKey")) return false;
        var sources = index.GetProperty("sources");
        return sources.ValueKind == JsonValueKind.Array && sources.GetArrayLength() <= 16
            && sources.EnumerateArray().All(entry => Fields(entry, "path", "sourceKey", "requiresReselection"));
    }
    private static bool Fields(JsonElement element, params string[] names)
    {
        if (element.ValueKind != JsonValueKind.Object) return false;
        var remaining = new HashSet<string>(names, StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject()) if (!remaining.Remove(property.Name)) return false;
        return remaining.Count == 0;
    }
    private static bool Future(JsonElement element, string name) => element.ValueKind == JsonValueKind.Object
        && element.EnumerateObject().Any(property => property.Name == name && property.Value.ValueKind == JsonValueKind.Number
            && property.Value.TryGetDouble(out var version) && version > 1);
    private static bool HexKey(string? value) => value is { Length: 64 } && value.All(character => character is >= '0' and <= '9' or >= 'A' and <= 'F' or >= 'a' and <= 'f');
    private static void NoReparse(string path)
    {
        for (var part = path; part is not null; part = Path.GetDirectoryName(part))
            if ((File.Exists(part) || Directory.Exists(part)) && (File.GetAttributes(part) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Local state storage is unavailable.");
    }
    private static LocalStateStatus IoStatus(IOException exception) => (exception.HResult & 0xffff) is 32 or 33 ? LocalStateStatus.Busy : LocalStateStatus.Unavailable;
    private static LocalStateLoadResult Default(LocalStateStatus status) => new(LocalAppState.Default, status);
    private enum FileKind { Missing, Valid, Invalid, Unsupported, Unavailable }
    private sealed record FileRead(FileKind Kind, LocalAppState? State);
}
