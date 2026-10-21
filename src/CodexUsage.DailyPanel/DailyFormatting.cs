using System;
using System.Globalization;
using System.Text;
using CodexUsage.Daily;

namespace CodexUsage.DailyPanel;

internal static class DailyFormatting
{
    private static readonly CultureInfo English = CultureInfo.InvariantCulture;

    internal static string Tokens(long? value)
    {
        if (value is null or < 0) return "—";
        if (value >= 1_000_000_000) return (value.Value / 1_000_000_000d).ToString("0.##", English) + "B";
        if (value >= 1_000_000) return (value.Value / 1_000_000d).ToString("0.##", English) + "M";
        if (value >= 10_000) return (value.Value / 1_000d).ToString("0.#", English) + "K";
        return value.Value.ToString("N0", English);
    }

    internal static string Exact(long? value) => value is null or < 0 ? "暂无数据" : value.Value.ToString("N0", English);
    internal static string Tps(double? value) => value is { } number && double.IsFinite(number) && number >= 0 ? number.ToString("0.00", English) : "—";
    internal static long? TotalTokens(long? input, long? output) => input is >= 0 && output is >= 0
        && input.Value <= long.MaxValue - output.Value ? input.Value + output.Value : null;
    internal static long? Uncached(long? input, long? cached) => input is >= 0 && cached is >= 0 && input >= cached ? input - cached : null;
    internal static string CacheRate(long? input, long? cached) => input is > 0 && cached is >= 0 && cached <= input
        ? ((double)cached.Value / input.Value * 100).ToString("0.00", English) + "%" : "—";

    internal static string Duration(double? value)
    {
        if (value is not { } seconds || !double.IsFinite(seconds) || seconds < 0) return "—";
        if (seconds < 60) return seconds.ToString(seconds < 10 ? "0.0" : "0", English) + "s";
        var totalSeconds = Math.Floor(seconds);
        if (totalSeconds < 3600) return Math.Floor(totalSeconds / 60).ToString("0", English) + "m " + (totalSeconds % 60).ToString("00", English) + "s";
        return Math.Floor(totalSeconds / 3600).ToString("0", English) + "h " + Math.Floor(totalSeconds % 3600 / 60).ToString("00", English)
            + "m " + (totalSeconds % 60).ToString("00", English) + "s";
    }

    internal static string ExactDuration(double? seconds) => seconds is { } value && double.IsFinite(value) && value >= 0
        ? value.ToString("N2", English) + " seconds" : "Unavailable";

    internal static string ZoneLabel(DateOnly day)
    {
        var offset = TimeZoneInfo.Local.GetUtcOffset(day.ToDateTime(TimeOnly.MinValue));
        return "Local time · UTC" + (offset < TimeSpan.Zero ? "−" : "+") + offset.Duration().ToString(@"hh\:mm", English);
    }

    internal static string CopyText(DailySnapshot value, bool demo)
    {
        var text = new StringBuilder();
        text.AppendLine("Codex Usage" + (demo ? " — Demo" : ""));
        text.AppendLine(value.Day.ToString("yyyy-MM-dd", English) + " · " + ZoneLabel(value.Day));
        text.AppendLine("Total input: " + Exact(value.InputTokens));
        text.AppendLine("Total output: " + Exact(value.OutputTokens));
        text.AppendLine("Total tokens: " + Exact(TotalTokens(value.InputTokens, value.OutputTokens)));
        text.AppendLine("Cached input: " + Exact(value.CachedInputTokens));
        text.AppendLine("Cache hit rate (cached / total input): " + CacheRate(value.InputTokens, value.CachedInputTokens));
        text.AppendLine("Uncached input: " + Exact(Uncached(value.InputTokens, value.CachedInputTokens)));
        text.AppendLine("Reasoning output: " + Exact(value.ReasoningTokens));
        text.AppendLine("Average TPS (estimate; TTFT and observed tool waits excluded): " + (Tps(value.AverageTps) is "—" ? "Unavailable" : Tps(value.AverageTps) + " tok/s"));
        text.AppendLine("Net processing time: " + ExactDuration(value.ProcessingSeconds));
        text.AppendLine($"TPS coverage: {value.TimedTurnCount:N0} completed turns included; {value.ExcludedTimingTurnCount:N0} excluded");
        text.AppendLine("Excluded TTFT and tool wait union: " + ExactDuration(value.ExcludedWaitSeconds));
        text.AppendLine("Active time (interval union): " + ExactDuration(value.ActiveSeconds));
        text.AppendLine($"Chats: {value.ChatCount:N0}; turns: {value.TurnCount:N0}; active turns: {value.ActiveTurns:N0}; sources: {value.SourceCount:N0}");
        if (value.Weekly is { } weekly && double.IsFinite(weekly.UsedPercent) && weekly.UsedPercent is >= 0 and <= 100
            && (weekly.ResetsAt is null || weekly.ResetsAt > value.UpdatedAt))
        {
            text.AppendLine("Weekly allowance remaining: " + (100 - weekly.UsedPercent).ToString("0.#", English) + "% (local log snapshot)");
            text.AppendLine("Weekly snapshot observed: " + weekly.ObservedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz", English));
            text.AppendLine("Weekly reset: " + (weekly.ResetsAt?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz", English) ?? "Unavailable"));
        }
        else text.AppendLine("Weekly allowance: Unavailable (no valid local log snapshot)");
        text.AppendLine("Scope: local root sessions only; subagents and cloud tasks excluded.");
        text.AppendLine("Input includes cached input; output includes reasoning. Estimated TPS uses the same eligible complete turns for output and net time; TTFT and observed tool wait intervals are merged before subtraction.");
        text.AppendLine("TTFT is whole-turn first-token latency, not necessarily visible-text latency. Remaining time can include reasoning, network and scheduling; this is not pure generation TPS.");
        text.AppendLine("Status: " + value.Status + (value.IsPartial ? " (partial data)" : ""));
        text.Append("Updated: " + value.UpdatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz", English));
        return text.ToString();
    }
}
