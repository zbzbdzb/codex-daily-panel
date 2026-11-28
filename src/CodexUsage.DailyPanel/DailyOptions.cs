using System;
using System.IO;
using System.Linq;
namespace CodexUsage.DailyPanel;
public sealed record DailyOptions(bool Demo, string? RenderPath, string? SnapshotPath, string Home, string? SettingsDirectory, bool ShowHelp, bool ShowVersion)
{
    public const string HelpText = "CodexDaily [--codex-home <directory>] [--settings-dir <directory>]\n"
        + "  --demo                 Show synthetic demo data\n"
        + "  --render <file.png>    Render demo data and exit\n"
        + "  --snapshot <file.json> Export a local snapshot (or --demo data)\n"
        + "  --codex-home <dir>     Read only this Codex log root\n"
        + "  --settings-dir <dir>   Store theme/language in this directory\n"
        + "  --help                Show usage without reading logs or settings\n"
        + "  --version             Show build version and exit\n"
        + "Exit codes: 0 success; 1 operation failed; 2 invalid arguments; 3 snapshot still loading.";

    public static bool TryParse(string[] args, out DailyOptions? options, out string? error)
    {
        options = null;
        error = null;
        if (args.Length > 64) { error = "Too many arguments."; return false; }
        var demo = false;
        var help = false;
        var version = false;
        string? render = null, snapshot = null, home = null, settings = null;
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--demo": demo = true; break;
                case "--help": help = true; break;
                case "--version": version = true; break;
                case "--render": case "--snapshot": case "--codex-home": case "--settings-dir":
                    var key = args[i];
                    if (++i == args.Length || args[i].StartsWith("--", StringComparison.Ordinal)) { error = "Missing value for " + key + "."; return false; }
                    if (key == "--render") { render = args[i]; demo = true; }
                    else if (key == "--snapshot") snapshot = args[i];
                    else if (key == "--codex-home") home = args[i];
                    else settings = args[i];
                    break;
                default: error = "Unknown argument. Use --help for usage."; return false;
            }
        }
        // Information-only invocations do not resolve paths or consult the environment.
        if (help || version) { options = new(false, null, null, "", null, help, version); return true; }
        if (render is not null && snapshot is not null) { error = "--render and --snapshot cannot be combined."; return false; }
        if (home is null)
        {
            home = Environment.GetEnvironmentVariable("CODEX_HOME");
            if (string.IsNullOrWhiteSpace(home)) home = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
        }
        try
        {
            home = NormalizePath(home);
            render = render is null ? null : NormalizePath(render);
            snapshot = snapshot is null ? null : NormalizePath(snapshot);
            settings = settings is null ? null : NormalizePath(settings);
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or IOException or UnauthorizedAccessException or System.Security.SecurityException)
        { error = "Invalid path. Supply a normal file or directory path, without device names or streams."; return false; }
        options = new(demo, render, snapshot, home, settings, false, false);
        return true;
    }

    public static string NormalizePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 32760 || path.Any(char.IsControl)
            || path.StartsWith("\\\\?\\", StringComparison.Ordinal) || path.StartsWith("\\\\.\\", StringComparison.Ordinal))
            throw new ArgumentException("Invalid path.", nameof(path));
        var full = Path.GetFullPath(path);
        if (full.StartsWith("\\\\?\\", StringComparison.Ordinal) || full.StartsWith("\\\\.\\", StringComparison.Ordinal)) throw new ArgumentException("Device path.", nameof(path));
        var root = Path.GetPathRoot(full)!;
        foreach (var part in full[root.Length..].Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
        {
            if (part.IndexOfAny(['<', '>', ':', '"', '|', '?', '*']) >= 0 || part.EndsWith(' ') || part.EndsWith('.')) throw new ArgumentException("Invalid component.", nameof(path));
            var name = part.Split('.')[0].ToUpperInvariant();
            if (name is "CON" or "PRN" or "AUX" or "NUL" || name.Length == 4 && (name.StartsWith("COM", StringComparison.Ordinal) || name.StartsWith("LPT", StringComparison.Ordinal)) && name[3] is >= '1' and <= '9')
                throw new ArgumentException("Device path.", nameof(path));
        }
        return Path.TrimEndingDirectorySeparator(full);
    }
}
