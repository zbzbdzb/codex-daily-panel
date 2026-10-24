using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using CodexUsage.Daily;

namespace CodexUsage.DailyPanel;

internal static class DailyPresentationFormatting
{
    internal static string Exact(long? value, DailyStrings strings) => value is >= 0 ? value.Value.ToString("N0", CultureInfo.InvariantCulture) : strings["Unavailable"];
    internal static string Seconds(double? value, DailyStrings strings) => value is >= 0 && double.IsFinite(value.Value)
        ? strings.Format("Seconds", value.Value.ToString("N2", CultureInfo.InvariantCulture)) : strings["Unavailable"];
    internal static string Stamp(DateTimeOffset at, TimeZoneInfo zone) => TimeZoneInfo.ConvertTime(at, zone).ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture);
    internal static string Zone(DateOnly day, TimeZoneInfo zone, DailyStrings strings)
    {
        var offset = zone.GetUtcOffset(day.ToDateTime(TimeOnly.MinValue));
        return strings["LocalTime"] + " · UTC" + (offset < TimeSpan.Zero ? "−" : "+") + offset.Duration().ToString(@"hh\:mm", CultureInfo.InvariantCulture);
    }
    internal static string Quality(DailyPresentationProjection view, DailyStrings strings)
    {
        var values = new List<string>();
        if (view.IsStale) values.Add(strings["Stale"]);
        if (view.Snapshot?.IsPartial == true) values.Add(strings["Partial"]);
        if (view.Snapshot is not { } snapshot || snapshot.SourceCount == 0 && snapshot.InputTokens is null && snapshot.OutputTokens is null)
            values.Add(strings["NoData"]);
        else if (snapshot.InputTokens is null && snapshot.OutputTokens is null) values.Add(strings["Unavailable"]);
        return values.Count == 0 ? strings["Local"] : string.Join(" · ", values);
    }
    internal static string Activity(DailyPresentationProjection view, DailyStrings strings, bool demo)
    {
        var values = new List<string>();
        if (view.State.IsExiting) values.Add(strings["Exiting"]);
        if (view.State.IsPaused) values.Add(strings["Paused"]);
        if (view.State.IsBusy) values.Add(strings["Refreshing"]);
        if (demo) values.Add(strings["Demo"]);
        if (view.DisplayActiveTurns is > 0) values.Add(strings.Format("Running", view.DisplayActiveTurns.Value));
        return string.Join(" · ", values);
    }
    internal static string Reasons(DailyPresentationProjection view, DailyStrings strings)
    {
        var messages = new List<string>();
        if (view.State.IsPaused) messages.Add(strings["PausedNote"]);
        if (view.IsStale && view.Snapshot is { } retained) messages.Add(strings.Format("Retained", retained.Day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)));
        if (view.Snapshot?.IsPartial == true) messages.Add(strings["PartialNote"]);
        // Compatibility for external callers predating structured issues. Do not
        // reverse-translate legacy text or use it to determine statistics quality.
        if (view.Snapshot is { IsPartial: true, Issues.Count: 0 } legacy && !string.IsNullOrWhiteSpace(legacy.Status))
            messages.Add(strings.Format("LegacyDiagnostic", legacy.Status));
        foreach (var issue in view.Issues)
        {
            // Activity in old snapshots cannot be promoted to a present-tense claim.
            if (issue.Code == DailyIssueCodes.ObservedActive && view.DisplayActiveTurns is null) continue;
            if (issue.Code is DailyIssueCodes.LocalRootScope or DailyIssueCodes.EstimatedTps) continue;
            messages.Add(DailyIssueText.Format(issue, strings));
        }
        return string.Join(Environment.NewLine, messages.Distinct(StringComparer.Ordinal));
    }
    internal static string Copy(DailyPresentationProjection view, DailyStrings strings, TimeZoneInfo zone, bool demo)
    {
        var value = view.Snapshot;
        if (value is null) return strings["Title"] + Environment.NewLine + strings["NoData"] + Environment.NewLine + Reasons(view, strings);
        var text = new StringBuilder();
        text.AppendLine(strings["Title"] + (demo ? " — " + strings["Demo"] : ""));
        text.AppendLine(strings["SnapshotDate"] + ": " + value.Day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + " · " + Zone(value.Day, zone, strings));
        void Line(string key, string val) => text.AppendLine(strings[key] + ": " + val);
        Line("Input", Exact(value.InputTokens, strings)); Line("Output", Exact(value.OutputTokens, strings));
        Line("Total", Exact(DailyFormatting.TotalTokens(value.InputTokens, value.OutputTokens), strings));
        Line("CachedInput", Exact(value.CachedInputTokens, strings)); Line("Cache", DailyFormatting.CacheRate(value.InputTokens, value.CachedInputTokens));
        Line("Uncached", Exact(DailyFormatting.Uncached(value.InputTokens, value.CachedInputTokens), strings)); Line("Reasoning", Exact(value.ReasoningTokens, strings));
        Line("Speed", DailyFormatting.Tps(value.AverageTps) is "—" ? strings["Unavailable"] : DailyFormatting.Tps(value.AverageTps) + " tok/s");
        Line("NetTime", Seconds(value.ProcessingSeconds, strings));
        text.AppendLine(strings.Format("Coverage", value.TimedTurnCount, value.ExcludedTimingTurnCount));
        Line("WaitUnion", Seconds(value.ExcludedWaitSeconds, strings)); Line("ActiveTime", Seconds(value.ActiveSeconds, strings));
        Line("Chats", Exact(value.ChatCount, strings)); Line("Turns", Exact(value.TurnCount, strings));
        Line("ActiveTurns", Exact(view.DisplayActiveTurns, strings)); Line("Sources", Exact(value.SourceCount, strings));
        if (view.DisplayWeekly is { } weekly && double.IsFinite(weekly.UsedPercent) && weekly.UsedPercent is >= 0 and <= 100)
        {
            Line("Weekly", strings.Format("Remaining", (100 - weekly.UsedPercent).ToString("0.#", CultureInfo.InvariantCulture)));
            text.AppendLine(strings.Format("Observed", Stamp(weekly.ObservedAt, zone)));
            text.AppendLine(strings.Format("Reset", weekly.ResetsAt is { } resets ? Stamp(resets, zone) : strings["Unavailable"]));
        }
        else Line("Weekly", strings["NoWeekly"]);
        text.AppendLine(strings["NotLive"]); text.AppendLine(strings["Scope"]); text.AppendLine(strings["Subsets"]);
        text.AppendLine(strings["TpsExplanation"]); text.AppendLine(strings["TpsLimit"]);
        Line("Status", string.Join(" · ", new[] { Activity(view, strings, demo), Quality(view, strings) }.Where(x => x.Length > 0)));
        var reasons = Reasons(view, strings); if (reasons.Length > 0) text.AppendLine(reasons);
        text.Append(strings.Format("Updated", Stamp(value.UpdatedAt, zone)));
        return text.ToString();
    }
}
