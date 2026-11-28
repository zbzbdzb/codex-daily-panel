using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using CodexUsage.Daily;

namespace CodexUsage.DailyPanel;

/// <summary>Independent UI activity and last successful data. The snapshot is never relabelled or rewritten.</summary>
public sealed record DailyPresentationState
{
    public DailySnapshot? Snapshot { get; init; }
    public bool IsPaused { get; init; }
    public bool IsBusy { get; init; }
    public bool IsExiting { get; init; }
    public bool RefreshFailed { get; init; }
    public DateTimeOffset? LastAttemptAt { get; init; }
    public IReadOnlyList<DailyIssue> Issues { get; init; } = [];

    public DailyPresentationProjection Project(DateTimeOffset now, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(zone);
        var day = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, zone).DateTime);
        var differentDay = Snapshot is { } value && value.Day != day;
        var futureSnapshot = Snapshot is { } dated && dated.UpdatedAt > now;
        var stale = Snapshot is not null && (RefreshFailed || differentDay || futureSnapshot);
        // UpdatedAt is only an upper bound for start observation age. Never
        // extend activity from it; beyond this bound no previously active start
        // can still satisfy the aggregator's existing 15-minute age contract.
        var activityExpired = Snapshot is { } active && now - active.UpdatedAt > TimeSpan.FromMinutes(15);
        var issues = new List<DailyIssue>();
        if (Snapshot is { } snapshot) issues.AddRange(snapshot.Issues);
        issues.AddRange(Issues);
        if (RefreshFailed) issues.Add(new(DailyIssueCodes.RefreshFailed));
        if (differentDay) issues.Add(DailyIssue.With(DailyIssueCodes.StaleDay,
            ("day", Snapshot!.Day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))));
        if (futureSnapshot) issues.Add(new(DailyIssueCodes.SnapshotTimeFuture));
        if (activityExpired) issues.Add(new(DailyIssueCodes.ActivityObservationExpired));

        var weekly = Snapshot?.Weekly;
        // Same expiry contract as DailyAggregator.LatestWeekly. Re-evaluate with
        // current time, even when paused or a failed refresh retains UpdatedAt.
        var weeklyExpired = weekly is not null && (weekly.ResetsAt is { } resets
            ? resets <= now : now - weekly.ObservedAt > TimeSpan.FromMinutes(15));
        var futureWeekly = weekly is not null && weekly.ObservedAt > now;
        if (weeklyExpired) issues.Add(new(DailyIssueCodes.WeeklyExpired));
        if (futureWeekly) issues.Add(new(DailyIssueCodes.WeeklyObservationFuture));
        return new(this, day, stale, differentDay,
            stale || IsPaused || IsExiting || activityExpired ? null : Snapshot?.ActiveTurns,
            stale || weeklyExpired || futureWeekly ? null : weekly, issues.AsReadOnly());
    }
}

public sealed record DailyPresentationProjection(DailyPresentationState State, DateOnly CurrentDay,
    bool IsStale, bool IsDifferentDay, int? DisplayActiveTurns, WeeklyAllowance? DisplayWeekly,
    IReadOnlyList<DailyIssue> Issues)
{
    public DailySnapshot? Snapshot => State.Snapshot;
}
