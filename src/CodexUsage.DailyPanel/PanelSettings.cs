using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
namespace CodexUsage.DailyPanel;
public enum ThemePreference { System, Light, Dark }
public enum LanguagePreference { System, ZhCN, EnUS }
public sealed record PanelSettings(int SchemaVersion = 1, ThemePreference Theme = ThemePreference.System, LanguagePreference Language = LanguagePreference.System);
public sealed record PanelSettingsIssue(string Code);
public sealed record PanelSettingsResult(PanelSettings Settings, PanelSettingsIssue? Issue = null);
public sealed class PanelSettingsStore
{
    private const int MaximumBytes = 16 * 1024;
    public string DirectoryPath { get; }
    public static string DefaultDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodexDaily");
    public PanelSettingsStore(string directory) => DirectoryPath = Path.GetFullPath(directory);

    public PanelSettingsResult Load()
    {
        try
        {
            using var stream = new FileStream(Path.Combine(DirectoryPath, "settings.json"), FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length > MaximumBytes) return Failure("settings.too_large");
            var bytes = new byte[MaximumBytes + 1];
            var length = 0;
            int read;
            while (length < bytes.Length && (read = stream.Read(bytes, length, bytes.Length - length)) != 0) length += read;
            if (length > MaximumBytes) return Failure("settings.too_large");
            using var document = JsonDocument.Parse(bytes.AsMemory(0, length), new JsonDocumentOptions { MaxDepth = 4 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return Failure("settings.invalid");
            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in root.EnumerateObject())
                if (!keys.Add(property.Name) || property.Name is not ("schemaVersion" or "theme" or "language")) return Failure("settings.invalid");
            if (!root.TryGetProperty("schemaVersion", out var version) || !version.TryGetInt32(out var schema)) return Failure("settings.invalid");
            if (schema != 1) return Failure("settings.unsupported_version");
            if (!root.TryGetProperty("theme", out var theme) || theme.ValueKind != JsonValueKind.String
                || !TryNamedEnum(theme.GetString(), out ThemePreference selectedTheme)
                || !root.TryGetProperty("language", out var language) || language.ValueKind != JsonValueKind.String
                || !TryNamedEnum(language.GetString(), out LanguagePreference selectedLanguage)) return Failure("settings.invalid");
            return new(new(schema, selectedTheme, selectedLanguage));
        }
        catch (FileNotFoundException) { return new(new()); }
        catch (DirectoryNotFoundException) { return new(new()); }
        catch (JsonException) { return Failure("settings.invalid"); }
        catch (InvalidOperationException) { return Failure("settings.invalid"); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        { return Failure("settings.read_failed"); }
    }

    public PanelSettingsResult Save(PanelSettings settings)
    {
        if (settings.SchemaVersion != 1 || !Enum.IsDefined(settings.Theme) || !Enum.IsDefined(settings.Language))
            return new(settings, new("settings.invalid"));
        string? temporary = null;
        try
        {
            Directory.CreateDirectory(DirectoryPath);
            temporary = Path.Combine(DirectoryPath, ".settings-" + Guid.NewGuid().ToString("N") + ".tmp");
            var bytes = JsonSerializer.SerializeToUtf8Bytes(new { schemaVersion = settings.SchemaVersion, theme = settings.Theme.ToString(), language = settings.Language.ToString() });
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            // The temporary and destination files are in the same directory/volume.
            // Rename replaces the complete document; never truncate the old settings.
            File.Move(temporary, Path.Combine(DirectoryPath, "settings.json"), overwrite: true);
            return new(settings);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        { return new(settings, new("settings.write_failed")); }
        finally
        {
            if (temporary is not null)
                try { File.Delete(temporary); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
        }
    }

    private static PanelSettingsResult Failure(string code) => new(new(), new(code));
    private static bool TryNamedEnum<T>(string? value, out T result) where T : struct, Enum
        => Enum.TryParse(value, ignoreCase: false, out result) && Enum.IsDefined(result) && result.ToString() == value;
}
