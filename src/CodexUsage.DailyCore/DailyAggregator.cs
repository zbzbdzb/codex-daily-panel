using CodexUsage.Core;

namespace CodexUsage.Daily;

public static class DailyAggregator
{
    private static readonly TimeSpan RecentObservation = TimeSpan.FromMinutes(15);

    public static DailySnapshot Summarize(IEnumerable<StatsEvent> events, DateOnly day, TimeZoneInfo zone,
        DateTimeOffset now, bool partial = false)
    {
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(zone);
        var rows = events.ToArray();
        var issues = new HashSet<string>(StringComparer.Ordinal);
        if (partial) issues.Add("来源仍在读取或部分来源不可用");
        var lower = DayBoundary(day, zone);
        var upper = DayBoundary(day.AddDays(1), zone);
        bool InDay(DateTimeOffset value) => value != DateTimeOffset.MinValue && value >= lower && value < upper;
        bool Relevant(StatsEvent value) => value.Timestamp == DateTimeOffset.MinValue || InDay(value.Timestamp);

        // Metadata is projected independently of input order. A late child marker must
        // exclude earlier rows too; replayed metadata never changes a root's totals.
        var children = new HashSet<string>(StringComparer.Ordinal);
        var roots = new HashSet<string>(StringComparer.Ordinal);
        var metadataConflicts = new HashSet<string>(StringComparer.Ordinal);
        foreach (var group in rows.OfType<MetadataEvent>().GroupBy(x => x.SessionKey))
        {
            var lastTime = group.Max(x => x.Timestamp);
            var latest = group.Where(x => x.Timestamp == lastTime).Distinct().ToArray();
            if (latest.Select(x => (x.IsChild, x.ParentKey)).Distinct().Count() > 1)
                metadataConflicts.Add(group.Key);
            else if (latest[0].IsChild) children.Add(group.Key);
            else roots.Add(group.Key);
        }
        bool Root(StatsEvent value) => roots.Contains(value.SessionKey);
        if (rows.Any(x => metadataConflicts.Contains(x.SessionKey) && Relevant(x))) issues.Add("会话关系冲突");
        if (rows.Any(x => x is not MetadataEvent && x is not QuotaEvent && Relevant(x)
            && !roots.Contains(x.SessionKey) && !children.Contains(x.SessionKey) && !metadataConflicts.Contains(x.SessionKey)))
            issues.Add("缺少会话元数据，无法确认根会话范围");
        var usable = rows.Where(Root).Where(x => x is not MetadataEvent && x is not QuotaEvent).ToArray();
        if (usable.Any(x => x.Timestamp == DateTimeOffset.MinValue)) issues.Add("统计事件缺少时间");
        if (usable.Any(x => x.Timestamp > now && InDay(x.Timestamp))) issues.Add("统计事件时间超前");
        var observed = usable.Where(x => x.Timestamp != DateTimeOffset.MinValue && x.Timestamp <= now).ToArray();
        var today = observed.Where(x => InDay(x.Timestamp)).ToArray();

        var rejectedTurns = new HashSet<(string Session, string Turn)>();
        var accepted = new List<AcceptedRequest>();
        var modern = usable.OfType<UsageEvent>().Where(x => !x.Legacy).ToArray();
        foreach (var missing in modern.Where(x => string.IsNullOrWhiteSpace(x.RequestKey)
            || string.IsNullOrWhiteSpace(x.SessionKey) || string.IsNullOrWhiteSpace(x.TurnKey)
            || string.IsNullOrWhiteSpace(x.RootTurnKey)))
        {
            if (Relevant(missing)) issues.Add("请求身份缺失");
            if (missing.TurnKey is { } turn) rejectedTurns.Add((missing.SessionKey, turn));
        }
        foreach (var group in modern.Where(x => !string.IsNullOrWhiteSpace(x.RequestKey)
            && !string.IsNullOrWhiteSpace(x.SessionKey) && !string.IsNullOrWhiteSpace(x.TurnKey)
            && !string.IsNullOrWhiteSpace(x.RootTurnKey)).GroupBy(x => x.RequestKey!, StringComparer.Ordinal))
        {
            var versions = group.Distinct().OrderBy(x => x.Timestamp).ThenBy(x => x.Ordinal).ToArray();
            var conflict = RequestConflict(versions);
            var invalid = versions.Any(x => x.Usage is null || !x.Usage.IsValid);
            var missingTime = versions.Any(x => x.Timestamp == DateTimeOffset.MinValue);
            if (conflict || invalid || missingTime)
            {
                if (versions.Any(Relevant)) issues.Add(conflict ? "请求修订冲突" : invalid ? "请求 token 字段无效" : "统计事件缺少时间");
                foreach (var value in versions) rejectedTurns.Add((value.SessionKey, value.TurnKey!));
                continue;
            }
            var selected = versions[^1];
            // Do not fall back to an older revision when the latest one is from the future.
            if (selected.Timestamp > now) { rejectedTurns.Add((selected.SessionKey, selected.TurnKey!)); continue; }
            // A request's first valid occurrence is lifecycle evidence. Revision
            // arrival time must neither move it to another day nor outside its turn.
            // Keep latest counters separately, with their original revision ordering.
            var evidence = versions[0];
            var coverageEvidence = versions[0];
            var coverageHistory = new List<UsageEvent> { versions[0] };
            foreach (var version in versions.Skip(1))
            {
                // Exact replays must not promote an intermediate cumulative snapshot
                // above a later request's final snapshot. A real counter/snapshot
                // correction advances evidence order; a thread-only change does not.
                if (version.Usage != evidence.Usage || version.TurnSnapshot != evidence.TurnSnapshot)
                    evidence = version;
                // Aggregate legacy coverage also depends on the thread snapshot.
                // Keep its revision order separate from unchanged TPS evidence.
                if (!SamePayload(version, coverageEvidence))
                {
                    coverageEvidence = version;
                    coverageHistory.Add(version);
                }
            }
            accepted.Add(new(selected, versions[0].Timestamp, evidence, coverageEvidence, versions[0].Ordinal, [.. coverageHistory]));
        }

        var dayRequests = accepted.Where(x => InDay(x.OccurredAt)).ToArray();
        TokenVector? totals = dayRequests.Length == 0 ? null : Sum(dayRequests);
        if (dayRequests.Any(x => !x.Latest.Usage!.IsComplete)) issues.Add("部分 token 字段未提供");
        if (totals is { IsComplete: false } && dayRequests.All(x => x.Latest.Usage!.IsComplete)) issues.Add("累计数值超出范围");
        var legacy = today.OfType<UsageEvent>().Where(x => x.Legacy).ToArray();
        if (legacy.Length > 0)
        {
            var covered = BuildLegacyCoverage(accepted);
            if (legacy.Any(x => !LegacyCovered(x, covered)))
                issues.Add("旧格式快照覆盖关系未知，无法还原全部今日用量");
        }
        // Revision arrival alone is not a new daily chat or turn. Lifecycle rows
        // and legacy snapshots establish observed chat/turn presence on their own
        // event dates; modern usage follows its request occurrence. Legacy presence
        // never supplies token totals or invents a missing turn identity.
        var dailyObservations = today.Where(x => x is not UsageEvent { Legacy: false }
            && x is not DailyToolEvent && x is not DailyTimingUnavailableEvent).ToArray();
        var chats = dailyObservations.Select(x => x.SessionKey).Concat(dayRequests.Select(x => x.Latest.SessionKey))
            .Distinct(StringComparer.Ordinal).Count();
        var turnKeys = dailyObservations.Select(TurnKey).Where(x => x.HasValue).Select(x => x!.Value)
            .Concat(dayRequests.Select(x => (x.Latest.SessionKey, x.Latest.TurnKey!))).Distinct().Count();

        // Inspect timing evidence before discarding future or timestamp-less rows.
        // An invalid row must make its scope unavailable, never masquerade as no wait.
        var toolRows = usable.OfType<DailyToolEvent>().Distinct().ToArray();
        var timingUnavailable = usable.OfType<DailyTimingUnavailableEvent>().Distinct().ToArray();
        var untimedSessions = timingUnavailable.Where(x => string.IsNullOrWhiteSpace(x.TurnKey)).Select(x => x.SessionKey)
            .Concat(toolRows.Where(x => string.IsNullOrWhiteSpace(x.TurnKey)).Select(x => x.SessionKey))
            .ToHashSet(StringComparer.Ordinal);
        var untimedTurns = timingUnavailable.Where(x => !string.IsNullOrWhiteSpace(x.TurnKey))
            .Select(x => (Session: x.SessionKey, Turn: x.TurnKey!)).ToHashSet();
        // A call identity crossing turn boundaries is ambiguous even when both
        // sides carry turn keys. Do not independently pair a replay under a new turn.
        foreach (var group in toolRows.Where(x => !string.IsNullOrWhiteSpace(x.CallKey))
            .GroupBy(x => (x.SessionKey, x.CallKey)))
        {
            if (group.Select(x => x.TurnKey).Distinct(StringComparer.Ordinal).Count() <= 1) continue;
            foreach (var row in group.Where(x => !string.IsNullOrWhiteSpace(x.TurnKey)))
                untimedTurns.Add((row.SessionKey, row.TurnKey!));
        }
        var toolsByTurn = toolRows.Where(x => !string.IsNullOrWhiteSpace(x.TurnKey))
            .GroupBy(x => (x.SessionKey, Turn: x.TurnKey!)).ToDictionary(x => x.Key, x => x.ToArray());

        var turns = new Dictionary<(string Session, string Turn), Turn>();
        Turn Get(string session, string key)
        {
            var identity = (session, key);
            if (!turns.TryGetValue(identity, out var value)) turns.Add(identity, value = new Turn(session, key));
            return value;
        }
        foreach (var row in observed)
        {
            switch (row)
            {
                case StartEvent start: Get(start.SessionKey, start.TurnKey).Starts.Add(start.Timestamp); break;
                case CompletionEvent completion: Get(completion.SessionKey, completion.TurnKey).Completions.Add(completion); break;
                case DailyTurnAbortedEvent abort: Get(abort.SessionKey, abort.TurnKey).Aborts.Add(abort.Timestamp); break;
            }
        }
        foreach (var request in accepted) Get(request.Latest.SessionKey, request.Latest.TurnKey!).Requests.Add(request);
        var lastStarts = observed.OfType<StartEvent>().GroupBy(x => x.SessionKey)
            .ToDictionary(x => x.Key, x =>
            {
                var timestamp = x.Max(start => start.Timestamp);
                var keys = x.Where(start => start.Timestamp == timestamp).Select(start => start.TurnKey).Distinct().ToArray();
                if (keys.Length > 1 && InDay(timestamp)) issues.Add("当前回合启动关系冲突");
                return (At: timestamp, Key: keys.Length == 1 ? keys[0] : null);
            }, StringComparer.Ordinal);
        var intervals = new List<(DateTimeOffset Start, DateTimeOffset End)>();
        void AddInterval(DateTimeOffset start, DateTimeOffset finish)
        {
            if (finish < start || finish < lower || start >= upper || start > now) return;
            var clippedStart = start < lower ? lower : start;
            var clippedEnd = finish > upper ? upper : finish;
            if (clippedEnd > now) clippedEnd = now;
            if (clippedEnd >= clippedStart) intervals.Add((clippedStart, clippedEnd));
        }
        decimal processingMs = 0;
        decimal excludedWaitMs = 0;
        decimal matchedOutput = 0;
        var timedTurns = 0;
        var unavailableTiming = 0;
        var active = 0;
        foreach (var turn in turns.Values)
        {
            var starts = turn.Starts.Distinct().Order().ToArray();
            var completions = turn.Completions.Distinct().ToArray();
            var aborts = turn.Aborts.Distinct().Order().ToArray();
            var relevantTurn = starts.Any(InDay) || completions.Any(x => InDay(x.Timestamp))
                || aborts.Any(InDay) || turn.Requests.Any(x => InDay(x.OccurredAt));
            var timingConflict = starts.Length > 1 || completions.Length > 1 || aborts.Length > 1
                || (completions.Length > 0 && aborts.Length > 0);
            if (timingConflict)
            {
                if (relevantTurn) issues.Add("回合时间事件冲突");
                if (completions.Any(x => InDay(x.Timestamp))) unavailableTiming++;
                continue;
            }
            DateTimeOffset? explicitStart = starts.Length == 1 ? starts[0] : null;
            if (aborts.Length == 1)
            {
                if (explicitStart is { } began) AddInterval(began, aborts[0]);
                continue;
            }
            if (completions.Length == 0)
            {
                // A log start is only an observation, not proof the process is alive.
                // Require the latest start in its root session and a short age limit.
                if (explicitStart is { } began && InDay(now) && now - began <= RecentObservation
                    && lastStarts.TryGetValue(turn.Session, out var latest) && began == latest.At && turn.Key == latest.Key)
                {
                    active++;
                    AddInterval(began, now);
                }
                continue;
            }

            var completed = completions[0];
            var duration = completed.DurationMs is > 0 ? completed.DurationMs : null;
            DateTimeOffset? durationStart = null;
            if (duration is { } millis)
            {
                try { durationStart = completed.Timestamp.AddMilliseconds(-millis); }
                catch (ArgumentOutOfRangeException) { }
            }
            var beganAt = explicitStart ?? durationStart;
            if (beganAt is { } intervalStart) AddInterval(intervalStart, completed.Timestamp);
            if (!InDay(completed.Timestamp)) continue;
            // The completion's declared duration and an explicit start/end pair are
            // independent evidence for the same turn. Allow modest write/scheduling
            // skew, but never use a grossly shorter duration to inflate throughput.
            // The larger of one second and five percent is deliberately symmetric.
            var durationMismatch = explicitStart is { } loggedStart && duration is { } declared
                && Math.Abs((completed.Timestamp - loggedStart).TotalMilliseconds - declared)
                    > Math.Max(1000d, declared * .05d);
            if (durationMismatch) issues.Add("回合声明耗时与起止时间不一致");
            var reconciliation = Reconcile(turn.Requests, rejectedTurns.Contains((turn.Session, turn.Key)));
            if (!reconciliation && relevantTurn) issues.Add("完成回合快照缺失或对账不一致");
            var sameDay = durationStart is { } inferred && InDay(inferred) && beganAt is { } beganSameDay
                && InDay(beganSameDay) && beganSameDay <= completed.Timestamp
                && turn.Requests.All(x => InDay(x.OccurredAt) && x.OccurredAt >= beganSameDay && x.OccurredAt <= completed.Timestamp);
            if (!reconciliation || duration is null || !sameDay || durationMismatch)
            {
                unavailableTiming++;
                continue;
            }
            if (completed.TimeToFirstTokenMs is not >= 0 || completed.TimeToFirstTokenMs >= duration)
            {
                unavailableTiming++;
                continue;
            }
            if (untimedSessions.Contains(turn.Session) || untimedTurns.Contains((turn.Session, turn.Key))
                || !TryExcludedWait(durationStart!.Value, explicitStart, completed.Timestamp,
                    completed.TimeToFirstTokenMs.Value,
                    toolsByTurn.GetValueOrDefault((turn.Session, turn.Key)) ?? [], now, out var waitMs)
                || duration.Value - waitMs <= 0)
            {
                unavailableTiming++;
                continue;
            }
            processingMs += duration.Value - waitMs;
            excludedWaitMs += waitMs;
            matchedOutput += Sum(turn.Requests).Output!.Value;
            timedTurns++;
        }

        var status = dayRequests.Length > 0 ? "本地日志 · 根会话" : "暂无今日可核验 token 记录";
        if (active > 0) status += $"；观察到 {active} 个运行中回合（最近15分钟启动）";
        if (timedTurns > 0) status += "；平均 TPS 为扣除首字与工具等待后的估算";
        if (unavailableTiming > 0)
            status += timedTurns > 0
                ? $"；平均 TPS 仅含 {timedTurns} 个可核验完成回合，另有 {unavailableTiming} 个回合缺少同日可靠耗时、首字、工具配对或对账"
                : "；完成回合缺少同日可靠耗时、首字、工具配对或对账，处理时长与 TPS 暂不可用";
        if (issues.Count > 0) status += "；统计不完整：" + string.Join("、", issues.Order(StringComparer.Ordinal));
        var weekly = LatestWeekly(rows.OfType<QuotaEvent>(), now, out var multipleWeekly);
        if (multipleWeekly) status += "；日志报告多个独立周额度窗口，未合并显示";
        return new(day, now, totals?.Input, totals?.CachedInput, totals?.Output, totals?.ReasoningOutput,
            timedTurns > 0 ? (double)(processingMs / 1000m) : null, UnionSeconds(intervals),
            timedTurns > 0 ? (double)(matchedOutput / (processingMs / 1000m)) : null,
            chats, turnKeys, active, 0, issues.Count > 0, status, weekly)
        {
            TimedTurnCount = timedTurns,
            ExcludedTimingTurnCount = unavailableTiming,
            ExcludedWaitSeconds = timedTurns > 0 ? (double)(excludedWaitMs / 1000m) : null
        };
    }

    private sealed class Turn(string session, string key)
    {
        public string Session { get; } = session;
        public string Key { get; } = key;
        public List<DateTimeOffset> Starts { get; } = [];
        public List<CompletionEvent> Completions { get; } = [];
        public List<DateTimeOffset> Aborts { get; } = [];
        public List<AcceptedRequest> Requests { get; } = [];
    }

    private sealed record AcceptedRequest(UsageEvent Latest, DateTimeOffset OccurredAt, UsageEvent SnapshotEvidence,
        UsageEvent CoverageEvidence, long? FirstOrdinal, UsageEvent[] CoverageHistory);

    private sealed record LegacyCheckpoint(UsageEvent Evidence, long? PrefixMaxKnownOrdinal);

    private sealed class LegacyCheckpoints
    {
        public LegacyCheckpoint? KnownOrdinal { get; private set; }
        public LegacyCheckpoint? UnknownOrdinal { get; private set; }

        public void Include(UsageEvent checkpoint, long? prefixMaxKnownOrdinal)
        {
            // Both position and the prefix's known ordinal bound must survive a
            // later missing ordinal. Missing evidence never erases a contradiction.
            if (checkpoint.Ordinal.HasValue) KnownOrdinal ??= new(checkpoint, prefixMaxKnownOrdinal);
            else UnknownOrdinal ??= new(checkpoint, prefixMaxKnownOrdinal);
        }

        public bool Covers(UsageEvent legacy)
        {
            bool Matches(LegacyCheckpoint? checkpoint) => checkpoint is { } value
                && !(value.PrefixMaxKnownOrdinal > legacy.Ordinal)
                && AtOrBefore(value.Evidence.Timestamp, value.Evidence.Ordinal, legacy.Timestamp, legacy.Ordinal) == true;
            return Matches(KnownOrdinal) || Matches(UnknownOrdinal);
        }
    }

    private static bool LegacyCovered(UsageEvent legacy,
        Dictionary<(string Session, string Turn, TokenVector Snapshot), LegacyCheckpoints> covered)
    {
        // The frozen parser supplies no native legacy request/turn identity. A
        // contextual turn constrains scope; it does not turn last_token_usage
        // into a request. Only a fully reconciled cumulative checkpoint can
        // prove aggregate coverage, and no legacy value enters the sum.
        if (string.IsNullOrWhiteSpace(legacy.TurnKey)
            || legacy.Usage is not { IsComplete: true, IsValid: true } last
            || legacy.ThreadSnapshot is not { IsComplete: true, IsValid: true } snapshot
            || last.Input > snapshot.Input || last.CachedInput > snapshot.CachedInput
            || last.Output > snapshot.Output || last.ReasoningOutput > snapshot.ReasoningOutput
            || last.Total > snapshot.Total) return false;

        return covered.TryGetValue((legacy.SessionKey, legacy.TurnKey, snapshot), out var checkpoints)
            && checkpoints.Covers(legacy);
    }

    private static Dictionary<(string Session, string Turn, TokenVector Snapshot), LegacyCheckpoints> BuildLegacyCoverage(
        List<AcceptedRequest> accepted)
    {
        var covered = new Dictionary<(string Session, string Turn, TokenVector Snapshot), LegacyCheckpoints>();
        foreach (var session in accepted.GroupBy(x => x.Latest.SessionKey))
        {
            // Sweep final meaningful revisions once. A still-pending request
            // whose first occurrence belongs to this prefix makes it unknown;
            // its revised counters must not be backdated into the checkpoint.
            var ordered = session.OrderBy(x => x.CoverageEvidence.Timestamp).ThenBy(x => x.CoverageEvidence.Ordinal).ToArray();
            var pendingFirst = new AcceptedRequest?[ordered.Length + 1];
            var pendingOrdinal = new long?[ordered.Length + 1];
            for (var i = ordered.Length - 1; i >= 0; i--)
            {
                var next = pendingFirst[i + 1];
                var request = ordered[i];
                pendingFirst[i] = next is null || request.OccurredAt < next.OccurredAt
                    || (request.OccurredAt == next.OccurredAt && Nullable.Compare(request.FirstOrdinal, next.FirstOrdinal) < 0)
                    ? request : next;
                pendingOrdinal[i] = request.FirstOrdinal is { } ordinal
                    ? Math.Min(ordinal, pendingOrdinal[i + 1] ?? long.MaxValue) : pendingOrdinal[i + 1];
            }
            var firstTimes = session.GroupBy(x => x.OccurredAt)
                .ToDictionary(x => x.Key, x => (Count: x.Count(), MissingOrdinal: x.Count(r => r.FirstOrdinal is null)));
            var revisionTimes = session.GroupBy(x => x.CoverageEvidence.Timestamp)
                .ToDictionary(x => x.Key, x => (Count: x.Count(), MissingOrdinal: x.Count(r => r.CoverageEvidence.Ordinal is null)));
            // Preserve ordering evidence from every meaningful version, including
            // intermediate corrections. Replays do not become new positions.
            var history = session.SelectMany(x => x.CoverageHistory)
                .OrderBy(x => x.Timestamp).ThenBy(x => x.Ordinal).ToArray();
            var historyIndex = 0;
            long? maxHistoryOrdinal = null;
            var historyConflict = false;
            var total = TokenVector.Zero;
            long? maxFirstOrdinal = null, maxRevisionOrdinal = null;
            for (var index = 0; index < ordered.Length;)
            {
                var evidence = ordered[index].CoverageEvidence;
                var end = index;
                while (end < ordered.Length && ordered[end].CoverageEvidence.Timestamp == evidence.Timestamp
                    && ordered[end].CoverageEvidence.Ordinal == evidence.Ordinal)
                {
                    var request = ordered[end++];
                    total += request.Latest.Usage!;
                    if (request.FirstOrdinal is { } first) maxFirstOrdinal = Math.Max(first, maxFirstOrdinal ?? long.MinValue);
                    if (request.CoverageEvidence.Ordinal is { } revision) maxRevisionOrdinal = Math.Max(revision, maxRevisionOrdinal ?? long.MinValue);
                }
                while (historyIndex < history.Length && (history[historyIndex].Timestamp < evidence.Timestamp
                    || (history[historyIndex].Timestamp == evidence.Timestamp
                        && Nullable.Compare(history[historyIndex].Ordinal, evidence.Ordinal) <= 0)))
                {
                    if (history[historyIndex++].Ordinal is not { } ordinal) continue;
                    historyConflict |= maxHistoryOrdinal > ordinal;
                    maxHistoryOrdinal = Math.Max(ordinal, maxHistoryOrdinal ?? long.MinValue);
                }
                var pending = pendingFirst[end];
                var completePrefix = total.IsComplete && total.IsValid && !historyConflict
                    && (pending is null || AtOrBefore(pending.OccurredAt, pending.FirstOrdinal, evidence.Timestamp, evidence.Ordinal) == false)
                    && !(pendingOrdinal[end] < maxHistoryOrdinal)
                    && !(evidence.Ordinal is { } boundary && (maxFirstOrdinal > boundary
                        || maxRevisionOrdinal > boundary || pendingOrdinal[end] < boundary));
                var revisionsAtTime = revisionTimes[evidence.Timestamp];
                completePrefix &= revisionsAtTime.Count == 1 || revisionsAtTime.MissingOrdinal == 0;
                for (var i = index; completePrefix && i < end; i++)
                {
                    var request = ordered[i];
                    var checkpoint = request.CoverageEvidence;
                    var firstAtTime = firstTimes.GetValueOrDefault(evidence.Timestamp);
                    if (evidence.Ordinal.HasValue ? firstAtTime.MissingOrdinal > 0
                        : firstAtTime.Count > (request.OccurredAt == evidence.Timestamp ? 1 : 0)) continue;
                    if (checkpoint.ThreadSnapshot is { IsComplete: true, IsValid: true } snapshot && snapshot == total
                        && !string.IsNullOrWhiteSpace(checkpoint.TurnKey))
                    {
                        var key = (session.Key, checkpoint.TurnKey, snapshot);
                        if (!covered.TryGetValue(key, out var checkpoints)) covered.Add(key, checkpoints = new());
                        checkpoints.Include(checkpoint, maxHistoryOrdinal);
                    }
                }
                index = end;
            }
        }
        return covered;
    }

    private static bool? AtOrBefore(DateTimeOffset at, long? ordinal, DateTimeOffset boundary, long? boundaryOrdinal)
    {
        if (at == boundary)
            return ordinal is { } same && boundaryOrdinal is { } other ? same <= other : null;
        if (ordinal is { } known && boundaryOrdinal is { } limit
            && (at < boundary ? known > limit : known < limit)) return null;
        return at < boundary;
    }

    private static (string Session, string Turn)? TurnKey(StatsEvent row) => row switch
    {
        StartEvent value => (value.SessionKey, value.TurnKey),
        CompletionEvent value => (value.SessionKey, value.TurnKey),
        DailyTurnAbortedEvent value => (value.SessionKey, value.TurnKey),
        UsageEvent { TurnKey: { } key } value => (value.SessionKey, key),
        _ => null
    };

    private static TokenVector Sum(IEnumerable<AcceptedRequest> values) =>
        values.Aggregate(TokenVector.Zero, (sum, value) => sum + value.Latest.Usage!);

    private static bool SamePayload(UsageEvent a, UsageEvent b) => a.SessionKey == b.SessionKey
        && a.TurnKey == b.TurnKey && a.RootTurnKey == b.RootTurnKey && a.Usage == b.Usage
        && a.TurnSnapshot == b.TurnSnapshot && a.ThreadSnapshot == b.ThreadSnapshot;

    private static bool RequestConflict(UsageEvent[] versions)
    {
        if (versions.Select(x => (x.SessionKey, x.TurnKey, x.RootTurnKey)).Distinct().Count() != 1) return true;
        long? greatestEarlierOrdinal = null;
        foreach (var sameTime in versions.GroupBy(x => x.Timestamp).OrderBy(x => x.Key))
        {
            var values = sameTime.ToArray();
            foreach (var sameOrdinal in values.GroupBy(x => x.Ordinal))
            {
                var first = sameOrdinal.First();
                if (sameOrdinal.Any(x => !SamePayload(first, x))) return true;
            }
            if (values.Any(x => x.Ordinal is null) && values.Any(x => x.Ordinal is not null)
                && values.Any(x => !SamePayload(values[0], x))) return true;
            var knownOrdinals = values.Where(x => x.Ordinal.HasValue).Select(x => x.Ordinal!.Value).ToArray();
            if (greatestEarlierOrdinal is { } prior && knownOrdinals.Any(x => x < prior)) return true;
            if (knownOrdinals.Length > 0) greatestEarlierOrdinal = Math.Max(greatestEarlierOrdinal ?? long.MinValue, knownOrdinals.Max());
        }
        return false;
    }

    private static bool Reconcile(List<AcceptedRequest> requests, bool rejected)
    {
        if (rejected || requests.Count == 0 || requests.Any(x => x.Latest.Usage is not { IsComplete: true, IsValid: true })) return false;
        var total = Sum(requests);
        if (!total.IsComplete || !total.IsValid) return false;
        var snapshots = requests.Select(x => x.SnapshotEvidence).ToArray();
        var lastTime = snapshots.Max(x => x.Timestamp);
        var sameTime = snapshots.Where(x => x.Timestamp == lastTime).ToArray();
        if (sameTime.Any(x => x.Ordinal is null) && sameTime.Any(x => x.Ordinal.HasValue)
            && sameTime.Select(x => x.TurnSnapshot).Distinct().Count() > 1) return false;
        var lastOrdinal = sameTime.Max(x => x.Ordinal);
        var last = sameTime.Where(x => x.Ordinal == lastOrdinal).Select(x => x.TurnSnapshot).Distinct().ToArray();
        return last.Length == 1 && last[0] is { IsComplete: true, IsValid: true } snapshot && snapshot == total;
    }

    private static bool TryExcludedWait(DateTimeOffset declaredStart, DateTimeOffset? explicitStart,
        DateTimeOffset completedAt, long ttftMs, DailyToolEvent[] tools, DateTimeOffset now, out decimal waitMs)
    {
        waitMs = 0;
        var intervals = new List<(DateTimeOffset Start, DateTimeOffset End)>();
        if (ttftMs > 0) intervals.Add((declaredStart, declaredStart.AddMilliseconds(ttftMs)));
        foreach (var call in tools.GroupBy(x => x.CallKey, StringComparer.Ordinal))
        {
            if (string.IsNullOrWhiteSpace(call.Key)) return false;
            var rows = call.Distinct().ToArray();
            if (rows.Any(x => x.Phase is not DailyToolPhase.Call and not DailyToolPhase.Result
                || x.Timestamp == DateTimeOffset.MinValue || x.Timestamp > now
                || x.Timestamp < declaredStart || x.Timestamp > completedAt
                || explicitStart is { } began && x.Timestamp < began)) return false;
            var starts = rows.Where(x => x.Phase == DailyToolPhase.Call).ToArray();
            var ends = rows.Where(x => x.Phase == DailyToolPhase.Result).ToArray();
            if (starts.Length != 1 || ends.Length != 1 || ends[0].Timestamp <= starts[0].Timestamp) return false;
            intervals.Add((starts[0].Timestamp, ends[0].Timestamp));
        }
        if (intervals.Count == 0) return true;
        var ordered = intervals.OrderBy(x => x.Start).ThenBy(x => x.End).ToArray();
        var start = ordered[0].Start;
        var end = ordered[0].End;
        decimal ticks = 0;
        foreach (var interval in ordered.Skip(1))
        {
            if (interval.Start <= end) { if (interval.End > end) end = interval.End; }
            else { ticks += (end - start).Ticks; start = interval.Start; end = interval.End; }
        }
        ticks += (end - start).Ticks;
        waitMs = ticks / TimeSpan.TicksPerMillisecond;
        return true;
    }

    private static double? UnionSeconds(List<(DateTimeOffset Start, DateTimeOffset End)> intervals)
    {
        if (intervals.Count == 0) return null;
        var ordered = intervals.OrderBy(x => x.Start).ThenBy(x => x.End).ToArray();
        var began = ordered[0].Start;
        var ended = ordered[0].End;
        double total = 0;
        foreach (var interval in ordered.Skip(1))
        {
            if (interval.Start <= ended) { if (interval.End > ended) ended = interval.End; }
            else { total += (ended - began).TotalSeconds; began = interval.Start; ended = interval.End; }
        }
        return total + (ended - began).TotalSeconds;
    }

    private static DateTimeOffset DayBoundary(DateOnly day, TimeZoneInfo zone)
    {
        var local = day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
        // Some zones advance their clocks at midnight. Pick the first real instant
        // of that local date, and the earlier instant when midnight is ambiguous.
        while (zone.IsInvalidTime(local)) local = local.AddMinutes(1);
        var offset = zone.IsAmbiguousTime(local) ? zone.GetAmbiguousTimeOffsets(local).Max() : zone.GetUtcOffset(local);
        return new DateTimeOffset(local, offset);
    }

    private static WeeklyAllowance? LatestWeekly(IEnumerable<QuotaEvent> values, DateTimeOffset now, out bool multipleWindows)
    {
        multipleWindows = false;
        var candidates = new List<WeeklyAllowance>();
        foreach (var bucket in values.Where(x => x.Timestamp != DateTimeOffset.MinValue && x.Timestamp <= now)
            .GroupBy(x => x.LimitKey, StringComparer.Ordinal))
        {
            var timestamp = bucket.Max(x => x.Timestamp);
            // Identical account snapshots mirrored in separate chats are the same
            // allowance sample; SessionKey does not identify an account quota bucket.
            var latest = bucket.Where(x => x.Timestamp == timestamp)
                .Select(x => (x.Primary, x.Secondary)).Distinct().ToArray();
            if (latest.Length != 1) continue;
            var sample = latest[0];
            var windows = new[] { sample.Primary, sample.Secondary }.Where(x => x?.WindowMinutes == 10080).Distinct().ToArray();
            if (windows.Length != 1 || windows[0] is not { } window || !double.IsFinite(window.UsedPercent)
                || window.UsedPercent < 0 || window.UsedPercent > 100) continue;
            if (window.ResetsAt is { } resets ? resets <= now : now - timestamp > RecentObservation) continue;
            candidates.Add(new(window.UsedPercent, window.ResetsAt, timestamp));
        }
        if (candidates.Count == 0) return null;
        if (candidates.Count > 1) { multipleWindows = true; return null; }
        var mostRecent = candidates.Max(x => x.ObservedAt);
        var selected = candidates.Where(x => x.ObservedAt == mostRecent).Distinct().ToArray();
        return selected.Length == 1 ? selected[0] : null;
    }
}
