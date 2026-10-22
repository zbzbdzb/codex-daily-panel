using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CodexUsage.Core;
using CodexUsage.Daily;

namespace CodexUsage.DailyPanel;

public sealed class SessionLogMonitor : IDisposable
{
    private const int MaximumFiles = 32;
    private const int MaximumEventsPerFile = 25000;
    private const int MaximumRetainedEvents = 50000;
    private const long ReadBudget = 64L * 1024 * 1024;
    private readonly string _home;
    private readonly TimeZoneInfo _zone;
    private readonly Dictionary<string, Source> _sources = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private DateOnly? _day;
    private DateTimeOffset _refreshAt;
    private bool _disposed;
    private int _retainedEvents;
    // Daily coverage is independent of the currently discovered paths. Withdrawn
    // sources retain hashes and classification facts, never subtotal counters.
    private readonly List<CoverageLoss> _coverageLosses = [];
    private bool _unprovableCoverage;
    private int _coverageEvidenceCount;
    private bool _lostBadStatistics, _lostOversizedLine, _lostReadFailed;

    public bool IsLoading { get; private set; }

    public SessionLogMonitor(string homeDirectory, TimeZoneInfo zone)
    {
        _home = Path.GetFullPath(homeDirectory);
        _zone = zone;
    }

    public async Task<DailySnapshot> RefreshAsync(DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var day = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, _zone).DateTime);
            _refreshAt = now;
            if (_day != day)
            {
                // Daily numeric gaps expire. Missing classification facts remain
                // conditional: a later replay must not erase a known earlier day,
                // child relationship or shared-call ambiguity at midnight.
                // A still-live source may disappear between the last poll of
                // yesterday and this one. Capture its new-day classification
                // dependencies before discarding the previous day's cache.
                var exclusions = new HashSet<StatsEvent>();
                DailyAggregator.Summarize(_sources.Values.SelectMany(x => x.Events).Concat(ChildEvidence()), day, _zone, now,
                    exclusionProof: exclusions, requestOrigins: RequestOrigins(), startHistory: StartHistory(), closureHistory: ClosureHistory());
                var guards = Guards().Concat(exclusions.Select(CreateGuard).OfType<ExclusionGuard>())
                    .Distinct().Take(MaximumRetainedEvents + 1).ToList();
                var overflow = guards.Count > MaximumRetainedEvents;
                if (overflow) guards.RemoveAt(MaximumRetainedEvents);
                ClearSources();
                _coverageLosses.Clear();
                if (guards.Count > 0)
                    _coverageLosses.Add(new CoverageLoss([], null, false, false, false, false, guards, [], []) { PositiveResolved = true });
                _coverageEvidenceCount = guards.Count;
                _unprovableCoverage = overflow;
                _lostBadStatistics = _lostOversizedLine = _lostReadFailed = false;
                _day = day;
            }
            var localStart = day.ToDateTime(TimeOnly.MinValue);
            // Some zones skip midnight. Advance to the first existing local minute;
            // for an ambiguous midnight choose its earliest UTC occurrence.
            while (_zone.IsInvalidTime(localStart)) localStart = localStart.AddMinutes(1);
            var startOffset = _zone.IsAmbiguousTime(localStart) ? _zone.GetAmbiguousTimeOffsets(localStart).Max() : _zone.GetUtcOffset(localStart);
            var midnight = new DateTimeOffset(localStart, startOffset).UtcDateTime;
            var paths = Discover(midnight, out var discoveryPartial, out var directoryFound);
            var retained = paths.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var removed = _sources.Keys.Where(path => !retained.Contains(path)).ToArray();
            foreach (var old in removed) RecordLoss(_sources[old], _sources[old].Reader.Coverage);
            foreach (var old in removed)
            {
                _sources[old].Dispose();
                _sources.Remove(old);
            }
            foreach (var path in paths)
            {
                if (_sources.TryGetValue(path, out var existing))
                {
                    // Retry the same identity/cursor, including a denied half-line.
                    // Only a successful EOF can settle its uncertain tail. A changed
                    // identity or prefix still captures loss before the normal reset.
                    if (existing.Reader.IsStopped) existing.Reader.RetryAccessDenied();
                    continue;
                }
                try { _sources.Add(path, new Source(path, day, _zone, () => _retainedEvents < MaximumRetainedEvents,
                    delta => _retainedEvents += delta, RecordLoss, CoverageTargets)); }
                catch (ArgumentException) { discoveryPartial = true; }
            }
            foreach (var stamp in CoverageTargets())
                foreach (var source in _sources.Values)
                    // Settle an interrupted original read before any optional replay
                    // withdraws its cache. EOF recovery and a physical reset must not
                    // be confused with a subsequent proof-only replay.
                    if (!source.ReadFailed && !source.UncertainTail) source.Reader.RequestCoverageReplay(stamp);
            foreach (var source in _sources.Values) source.VerifiedThisRefresh = false;
            long bytes = 0;
            foreach (var source in _sources.Values)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (bytes >= ReadBudget) break;
                try
                {
                    DailyReadBatchResult result;
                    do
                    {
                        result = await source.Reader.ReadAvailableAsync(source.AddLine, cancellationToken).ConfigureAwait(false);
                        bytes += result.BytesRead;
                        source.Apply(result);
                        source.VerifiedThisRefresh = true;
                    } while (result.HasMore && bytes < ReadBudget);
                }
                catch (IOException)
                {
                    var pending = source.Reader.DrainPendingResult();
                    bytes += pending.BytesRead;
                    source.Apply(pending);
                    source.ReadFailed = true;
                    source.UncertainTail = true;
                    source.VerifiedThisRefresh = false;
                }
                catch (UnauthorizedAccessException)
                {
                    var pending = source.Reader.DrainPendingResult();
                    bytes += pending.BytesRead;
                    source.Apply(pending);
                    source.ReadFailed = true;
                    source.UncertainTail = true;
                    source.VerifiedThisRefresh = false;
                }
                catch (OperationCanceledException) { source.Apply(source.Reader.DrainPendingResult()); throw; }
            }
            ReconcileCoverage();
            IsLoading = _sources.Values.Any(source => source.HasMore);
            var current = _sources.Values.SelectMany(source => source.Events).ToArray();
            var view = _coverageLosses.Count > 0 || _unprovableCoverage ? CoverageViewFor(current) : null;
            var activeLosses = view is null ? [] : _coverageLosses.Where(x => !x.PositiveResolved || x.Guards.Any(g => GuardActive(g, view))).ToArray();
            var coverageLost = _unprovableCoverage || activeLosses.Length > 0;
            var partial = coverageLost || discoveryPartial || IsLoading || _sources.Values.Any(source => source.BadStatistics || source.ReadFailed || source.UncertainTail || source.PendingLine);
            var events = _sources.Values.SelectMany(source => source.SummaryEvents(now))
                .Concat(ChildEvidence()).Concat(view is null ? [] : MissingTiming(view, activeLosses, now));
            var snapshot = DailyAggregator.Summarize(events, day, _zone, now, partial,
                requestOrigins: RequestOrigins(), startHistory: StartHistory(), closureHistory: ClosureHistory()) with { SourceCount = _sources.Count };
            var reasons = new List<string>();
            if (!directoryFound) reasons.Add("未找到本机 Codex sessions 日志目录");
            if (IsLoading) reasons.Add("正在读取今日统计，请稍候…");
            if (_lostOversizedLine || _sources.Values.Any(source => source.OversizedLine) || activeLosses.Any(loss => !loss.PositiveResolved && loss.OversizedLine))
                reasons.Add("部分统计或未知日志行超过 4 MiB 读取上限；当前为可读取范围");
            if (_lostBadStatistics || _lostReadFailed || discoveryPartial || _sources.Values.Any(source => source.ReadFailed || source.UncertainTail || source.BadStatistics)
                || activeLosses.Any(loss => !loss.PositiveResolved && (loss.BadStatistics || loss.ReadFailed)))
                reasons.Add("部分日志不可读取或格式不完整；当前为可读取范围");
            if (coverageLost) reasons.Add("日志截断、替换或删除后，当日覆盖无法确认；仅显示当前可核验小计");
            reasons.Add(snapshot.Status);
            return snapshot with { Status = string.Join("；", reasons) };
        }
        finally { _gate.Release(); }
    }

    private DailyCoverageStamp[] CoverageTargets() => _coverageLosses.Where(x => !x.PositiveResolved && x.RawPrefix.HasValue)
        .Select(x => x.RawPrefix!.Value).Distinct().ToArray();

    private void RecordLoss(Source source, DailyCoverageState coverage)
    {
        // Use the same day/root/request-occurrence rules as the actual statistics.
        // A prior-day replay, closed historical turn or confirmed child alone is
        // not missing today's root-session coverage.
        var required = new HashSet<StatsEvent>();
        var exclusions = new HashSet<StatsEvent>();
        DailyAggregator.Summarize(_sources.Values.SelectMany(x => x.Events).Concat(ChildEvidence()), _day!.Value, _zone, _refreshAt,
            coverageProof: required, exclusionProof: exclusions, requestOrigins: RequestOrigins(), startHistory: StartHistory(), closureHistory: ClosureHistory());
        // Child metadata is a conditional decision fact, rather than a daily
        // contribution. It is needed only while its session still has candidates.
        var hashes = source.Events.Where(x => required.Contains(x) && x is not MetadataEvent { IsChild: true })
            .Select(EventDigest).ToHashSet(StringComparer.Ordinal);
        var guards = source.Events.Where(exclusions.Contains).Select(CreateGuard).Where(x => x is not null)
            .Cast<ExclusionGuard>().Distinct().ToList();
        if (hashes.Count == 0 && guards.Count == 0 && !source.BadStatistics && !source.PendingLine && !source.HasMore
            && !source.ReadFailed && !source.UncertainTail && !coverage.Incomplete) return;
        // Unknown/unread bytes cannot be reconstructed from the remaining valid events.
        // An exact consumed prefix is required for malformed/half-line/timing input.
        var opaque = source.BadStatistics || coverage.Incomplete || source.PendingLine || source.TimingUnavailable || source.UnassociatedTiming
            || source.ReadFailed && hashes.Count == 0;
        var loss = new CoverageLoss(hashes, opaque ? coverage.Stamp : null,
            source.ReadFailed || source.UncertainTail || !coverage.Stable || coverage.ObservedLength > coverage.Stamp.Bytes
                || coverage.Stamp.Bytes == 0 && (source.ReadFailed || source.HasMore),
            source.BadStatistics, source.OversizedLine, source.ReadFailed || source.UncertainTail,
            guards, source.Events.Select(x => x.SessionKey).Distinct(StringComparer.Ordinal).ToArray(),
            source.Events.Where(required.Contains).Select(TimingTurn).Where(x => x.HasValue).Select(x => x!.Value).Distinct().ToArray());
        if (_coverageLosses.Any(x => !x.PositiveResolved && x.RawPrefix == loss.RawPrefix && x.Unprovable == loss.Unprovable
            && x.BadStatistics == loss.BadStatistics && x.OversizedLine == loss.OversizedLine
            && x.ReadFailed == loss.ReadFailed && x.Events.SetEquals(hashes) && x.Guards.ToHashSet().SetEquals(guards))) return;
        // Repeated generations must not circumvent the existing bounded retention.
        if (_coverageLosses.Count >= MaximumRetainedEvents || _coverageEvidenceCount + loss.EvidenceCount > MaximumRetainedEvents)
        {
            _unprovableCoverage = true;
            _lostBadStatistics |= loss.BadStatistics;
            _lostOversizedLine |= loss.OversizedLine;
            _lostReadFailed |= loss.ReadFailed;
            return;
        }
        _coverageLosses.Add(loss);
        _coverageEvidenceCount += loss.EvidenceCount;
    }

    private void ReconcileCoverage()
    {
        if (_coverageLosses.Count == 0) return;
        var drained = _sources.Values.Where(x => x.VerifiedThisRefresh && !x.HasMore && !x.PendingLine && !x.ReadFailed && !x.UncertainTail && x.Reader.Coverage.Stable).ToArray();
        var hashes = drained.Where(x => !x.BadStatistics).SelectMany(x => x.Events).Where(e => e is not QuotaEvent)
            .Select(EventDigest).ToHashSet(StringComparer.Ordinal);
        foreach (var loss in _coverageLosses.ToArray())
        {
            var proven = loss.Guards.RemoveAll(x => hashes.Contains(x.Digest));
            _coverageEvidenceCount -= proven;
            if (!loss.Unprovable && loss.Events.IsSubsetOf(hashes)
                && (loss.RawPrefix is not { } prefix || drained.Any(x => x.Reader.Proves(prefix)))) loss.PositiveResolved = true;
            if (!loss.PositiveResolved || loss.Guards.Count > 0) continue;
            _coverageLosses.Remove(loss);
            _coverageEvidenceCount -= loss.EvidenceCount;
        }
    }

    private static string EventDigest(StatsEvent item) => item.GetType().FullName + ":"
        + Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(item, item.GetType())));

    private sealed record CoverageLoss(HashSet<string> Events, DailyCoverageStamp? RawPrefix, bool Unprovable,
        bool BadStatistics, bool OversizedLine, bool ReadFailed, List<ExclusionGuard> Guards, string[] Sessions,
        (string Session, string Turn)[] Turns)
    {
        public bool PositiveResolved { get; set; }
        public int EvidenceCount => Events.Count + Guards.Count + Sessions.Length + Turns.Length;
    }

    // No retired UsageEvent or token counter is retained in a guard.
    private abstract record ExclusionGuard(string Digest, string Session);
    private sealed record OriginGuard(string Hash, string Key, string Request, DateTimeOffset At) : ExclusionGuard(Hash, Key);
    private sealed record ChildGuard(string Hash, MetadataEvent Relation) : ExclusionGuard(Hash, Relation.SessionKey);
    private sealed record TimingGuard(string Hash, StatsEvent Evidence) : ExclusionGuard(Hash, Evidence.SessionKey);
    private sealed record StartOrderGuard(string Hash, StartEvent Evidence) : ExclusionGuard(Hash, Evidence.SessionKey);
    private sealed record ClosureGuard(string Hash, string Key, string Turn, DateTimeOffset At, bool Aborted) : ExclusionGuard(Hash, Key);

    private static ExclusionGuard? CreateGuard(StatsEvent row) => row switch
    {
        UsageEvent { RequestKey: { } request } usage => new OriginGuard(EventDigest(row), usage.SessionKey, request, usage.Timestamp),
        MetadataEvent { IsChild: true } metadata => new ChildGuard(EventDigest(row), metadata),
        DailyToolEvent or DailyTimingUnavailableEvent => new TimingGuard(EventDigest(row), row),
        StartEvent start => new StartOrderGuard(EventDigest(row), start),
        CompletionEvent end => new ClosureGuard(EventDigest(row), end.SessionKey, end.TurnKey, end.Timestamp, false),
        DailyTurnAbortedEvent abort => new ClosureGuard(EventDigest(row), abort.SessionKey, abort.TurnKey, abort.Timestamp, true),
        _ => null
    };

    private IEnumerable<ExclusionGuard> Guards() => _coverageLosses.SelectMany(x => x.Guards);
    private IEnumerable<MetadataEvent> ChildEvidence() => Guards().OfType<ChildGuard>().Select(x => x.Relation).Distinct();
    private IEnumerable<StartEvent> StartHistory() => Guards().OfType<StartOrderGuard>().Select(x => x.Evidence).Distinct();
    private IEnumerable<(string Session, string Turn, DateTimeOffset At)> ClosureHistory() => Guards().OfType<ClosureGuard>()
        .Select(x => (x.Session, x.Turn, x.At)).Distinct();
    private Dictionary<(string Session, string Request), DateTimeOffset> RequestOrigins() => Guards().OfType<OriginGuard>()
        .GroupBy(x => (x.Session, x.Request)).ToDictionary(x => x.Key, x => x.Min(g => g.At));
    private bool InDay(DateTimeOffset at) => at != DateTimeOffset.MinValue
        && DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(at, _zone).DateTime) == _day;

    private static (string Session, string Turn)? TimingTurn(StatsEvent row) => row switch
    {
        UsageEvent { TurnKey: { } turn } x => (x.SessionKey, turn),
        StartEvent x => (x.SessionKey, x.TurnKey),
        CompletionEvent x => (x.SessionKey, x.TurnKey),
        DailyTurnAbortedEvent x => (x.SessionKey, x.TurnKey),
        DailyToolEvent { TurnKey: { } turn } x => (x.SessionKey, turn),
        DailyTimingUnavailableEvent { TurnKey: { } turn } x => (x.SessionKey, turn),
        _ => null
    };

    private HashSet<(string Session, string Turn)> CandidateTurns(StatsEvent[] rows)
    {
        // Guard relevance must be decided from raw candidates. A retired origin
        // fact must not hide the candidate that still needs a missing child fact.
        var result = rows.Where(x => x is not UsageEvent && InDay(x.Timestamp)).Select(TimingTurn)
            .Where(x => x.HasValue).Select(x => x!.Value).ToHashSet();
        foreach (var group in rows.OfType<UsageEvent>().Where(x => !x.Legacy && x.RequestKey is not null).GroupBy(x => x.RequestKey!))
        {
            var first = group.Min(x => x.Timestamp);
            if (InDay(first)) foreach (var row in group) if (TimingTurn(row) is { } turn) result.Add(turn);
        }
        var closed = rows.Where(x => x.Timestamp <= _refreshAt && x is CompletionEvent or DailyTurnAbortedEvent)
            .Select(TimingTurn).Where(x => x.HasValue).Select(x => x!.Value).ToHashSet();
        foreach (var start in rows.OfType<StartEvent>().Where(x => x.Timestamp <= _refreshAt && _refreshAt - x.Timestamp <= TimeSpan.FromMinutes(15)))
            if (!closed.Contains((start.SessionKey, start.TurnKey))) result.Add((start.SessionKey, start.TurnKey));
        return result;
    }

    private sealed record CoverageView(StatsEvent[] Rows, HashSet<(string Session, string Turn)> Turns,
        HashSet<string> Roots, HashSet<(string Session, string Request)> TodayRequests, HashSet<string> CandidateSessions,
        Dictionary<string, DateTimeOffset> MetadataTimes,
        Dictionary<(string Session, string Call), (string Session, string Turn)[]> Calls);

    private CoverageView CoverageViewFor(StatsEvent[] rows)
    {
        var turns = CandidateTurns(rows);
        var roots = rows.OfType<MetadataEvent>().Concat(ChildEvidence()).GroupBy(x => x.SessionKey)
            .Select(group =>
            {
                var at = group.Max(x => x.Timestamp);
                return (Session: group.Key, Latest: group.Where(x => x.Timestamp == at).ToArray());
            })
            .Where(x => x.Latest.All(m => !m.IsChild) && x.Latest.Select(m => m.ParentKey).Distinct().Count() == 1)
            .Select(x => x.Session).ToHashSet(StringComparer.Ordinal);
        var todayRequests = rows.OfType<UsageEvent>().Where(x => !x.Legacy && x.RequestKey is not null && InDay(x.Timestamp))
            .Select(x => (x.SessionKey, x.RequestKey!)).ToHashSet();
        var sessions = turns.Select(x => x.Session).Concat(rows.Where(x => x is not (MetadataEvent or QuotaEvent or DailyToolEvent or DailyTimingUnavailableEvent)
            && (InDay(x.Timestamp) || x.Timestamp == DateTimeOffset.MinValue)
            && (x is not UsageEvent || x is UsageEvent { Legacy: true }
                || x is UsageEvent usage && (string.IsNullOrWhiteSpace(usage.RequestKey) || string.IsNullOrWhiteSpace(usage.TurnKey)
                    || string.IsNullOrWhiteSpace(usage.RootTurnKey))))
            .Select(x => x.SessionKey)).ToHashSet(StringComparer.Ordinal);
        var times = rows.OfType<MetadataEvent>().GroupBy(x => x.SessionKey).ToDictionary(x => x.Key, x => x.Max(m => m.Timestamp));
        var calls = rows.OfType<DailyToolEvent>().Where(x => x.CallKey is not null && TimingTurn(x) is { } turn && turns.Contains(turn))
            .GroupBy(x => (Session: x.SessionKey, Call: x.CallKey!))
            .ToDictionary(x => x.Key, x => x.Select(TimingTurn).Select(t => t!.Value).Distinct().ToArray());
        return new(rows, turns, roots, todayRequests, sessions, times, calls);
    }

    private static bool GuardActive(ExclusionGuard guard, CoverageView view) => guard switch
    {
        OriginGuard origin => view.Roots.Contains(origin.Session) && view.TodayRequests.Contains((origin.Session, origin.Request)),
        ChildGuard child => view.CandidateSessions.Contains(child.Session)
            && (!view.MetadataTimes.TryGetValue(child.Session, out var latest) || latest <= child.Relation.Timestamp),
        TimingGuard timing => view.Roots.Contains(timing.Session) && (TimingTurn(timing.Evidence) is { } own && view.Turns.Contains(own)
            || timing.Evidence is DailyToolEvent { CallKey: { } call } && view.Calls.ContainsKey((timing.Session, call))
            || TimingTurn(timing.Evidence) is null && view.CandidateSessions.Contains(timing.Session)),
        // Activity qualification is compared in Summarize itself, using the same
        // closure/latest/recent rules that decide ActiveTurns and ActiveSeconds.
        StartOrderGuard or ClosureGuard => false,
        _ => false
    };

    private IEnumerable<StatsEvent> MissingTiming(CoverageView view, CoverageLoss[] losses, DateTimeOffset now)
    {
        var scopes = new HashSet<(string Session, string? Turn)>();
        var calls = new HashSet<(string Session, string Call)>();
        if (_unprovableCoverage)
            foreach (var session in view.Rows.Select(x => x.SessionKey).Distinct(StringComparer.Ordinal))
                scopes.Add((session, null));
        foreach (var loss in losses)
        {
            if (!loss.PositiveResolved)
            {
                foreach (var turn in loss.Turns) scopes.Add((turn.Session, turn.Turn));
                if (loss.RawPrefix.HasValue || loss.Unprovable || loss.Turns.Length == 0)
                    foreach (var session in loss.Sessions.Length > 0 ? loss.Sessions : view.Rows.Select(x => x.SessionKey).Distinct(StringComparer.Ordinal))
                        scopes.Add((session, null));
            }
            foreach (var guard in loss.Guards.OfType<TimingGuard>())
            {
                if (!view.Roots.Contains(guard.Session)) continue;
                if (TimingTurn(guard.Evidence) is { } turn && view.Turns.Contains(turn)) scopes.Add((turn.Session, turn.Turn));
                if (guard.Evidence is DailyToolEvent { CallKey: { } call }) calls.Add((guard.Session, call));
                if (TimingTurn(guard.Evidence) is null && view.CandidateSessions.Contains(guard.Session)) scopes.Add((guard.Session, null));
            }
        }
        foreach (var call in calls)
            foreach (var turn in view.Calls.GetValueOrDefault(call) ?? []) scopes.Add((turn.Session, turn.Turn));
        return scopes.Select(x => new DailyTimingUnavailableEvent(x.Session, x.Turn, now));
    }

    private List<string> Discover(DateTime midnightUtc, out bool partial, out bool found)
    {
        partial = false;
        found = false;
        var paths = new List<string>();
        foreach (var name in new[] { "sessions", "archived_sessions" })
        {
            var root = Path.Combine(_home, name);
            if (!Directory.Exists(root)) continue;
            found = true;
            try
            {
                if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0) { partial = true; continue; }
                // Ignore links. A resumed conversation may be in an old date folder;
                // select by file modification time, then filter each event by local day.
                var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = false, AttributesToSkip = FileAttributes.ReparsePoint };
                foreach (var path in Directory.EnumerateFiles(root, "*.jsonl", options))
                {
                    var info = new FileInfo(path);
                    if (info.LastWriteTimeUtc < midnightUtc && !_sources.ContainsKey(path)) continue;
                    if (paths.Count >= MaximumFiles) { partial = true; break; }
                    paths.Add(path);
                }
            }
            catch (IOException) { partial = true; }
            catch (UnauthorizedAccessException) { partial = true; }
        }
        return paths;
    }

    private void ClearSources()
    {
        foreach (var source in _sources.Values) source.Dispose();
        _sources.Clear();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        ClearSources();
        _gate.Dispose();
    }

    private sealed class Source : IDisposable
    {
        private readonly string _key;
        private readonly DateOnly _day;
        private readonly TimeZoneInfo _zone;
        private readonly EventParser _parser = new();
        private readonly Func<bool> _canRetain;
        private readonly Action<int> _changeCount;
        private SourceContext _context;
        private DailyToolTimingParser _timing;
        private bool _unassociatedTiming;
        public DailyLogReader Reader { get; }
        public List<StatsEvent> Events { get; } = [];
        public bool HasMore { get; set; } = true;
        public bool VerifiedThisRefresh { get; set; }
        public bool PendingLine { get; set; }
        public bool ReadFailed { get; set; }
        public bool UncertainTail { get; set; }
        public bool BadStatistics { get; set; }
        public bool OversizedLine { get; set; }
        public bool TimingUnavailable { get; set; }
        public bool UnassociatedTiming => _unassociatedTiming;

        public Source(string path, DateOnly day, TimeZoneInfo zone, Func<bool> canRetain, Action<int> changeCount,
            Action<Source, DailyCoverageState> recordLoss, Func<DailyCoverageStamp[]> coverageTargets)
        {
            _key = EventParser.Key("daily-source", path);
            _day = day;
            _zone = zone;
            _canRetain = canRetain;
            _changeCount = changeCount;
            _context = new SourceContext(_key);
            _timing = new DailyToolTimingParser(_key);
            Reader = new DailyLogReader(path, coverageTargets);
            Reader.ResetDetected += () =>
            {
                recordLoss(this, Reader.CoverageBeforeReset);
                ClearEvents();
                _context = new SourceContext(_key);
                _timing = new DailyToolTimingParser(_key);
                _unassociatedTiming = false;
                BadStatistics = false;
                OversizedLine = false;
                TimingUnavailable = false;
                ReadFailed = false;
                UncertainTail = false;
            };
        }

        public void Apply(DailyReadBatchResult result)
        {
            HasMore = result.HasMore;
            PendingLine = result.WaitingForCompleteLine;
            ReadFailed = result.Missing || result.AccessDenied;
            if (ReadFailed) UncertainTail = true;
            else if (!result.HasMore && !result.WaitingForCompleteLine && Reader.Coverage.Stable) UncertainTail = false;
            BadStatistics |= result.InvalidUtf8 || result.OversizedLine;
            OversizedLine |= result.OversizedLine;
            TimingUnavailable |= result.TimingUnavailable;
        }

        public void AddLine(string line)
        {
            var parsed = _parser.Parse(line, _context);
            BadStatistics |= parsed.Malformed || parsed.Unsupported;
            var timing = _timing.Parse(line, parsed.Events);
            if (timing.Any(e => e is DailyTimingUnavailableEvent && e.SessionKey == _key)) _unassociatedTiming = true;
            foreach (var item in parsed.Events.Concat(timing))
            {
                if (item.Timestamp == DateTimeOffset.MinValue) BadStatistics = true;
                // Retain bounded modern request and lifecycle history together. Late revisions
                // keep their original day, and a start before midnight needs its completion
                // or abort retained as well to distinguish a clipped interval from an active turn.
                if (item is not (MetadataEvent or QuotaEvent or StartEvent or CompletionEvent or DailyTurnAbortedEvent or DailyToolEvent or DailyTimingUnavailableEvent or UsageEvent { Legacy: false })
                    && DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(item.Timestamp, _zone).DateTime) != _day) continue;
                if (item is QuotaEvent quota)
                {
                    // Keep only the latest per bucket in each file, preserving sample time.
                    var old = Events.FindLastIndex(e => e is QuotaEvent existing && existing.LimitKey == quota.LimitKey);
                    if (old >= 0)
                    {
                        if (Events[old].Timestamp < quota.Timestamp) { Events[old] = quota; continue; }
                        if (Events[old] == quota || Events[old].Timestamp > quota.Timestamp) continue;
                    }
                }
                if (Events.Count >= MaximumEventsPerFile || !_canRetain()) { BadStatistics = true; return; }
                Events.Add(item);
                _changeCount(1);
            }
            if (line.Contains("task_aborted", StringComparison.Ordinal) || line.Contains("turn_aborted", StringComparison.Ordinal))
                AddAbort(line);
        }

        private void AddAbort(string line)
        {
            try
            {
                using var document = JsonDocument.Parse(line, new JsonDocumentOptions { MaxDepth = 64 });
                var root = document.RootElement;
                if (!Unique(root) || Field(root, "type").GetString() != "event_msg") return;
                var payload = Field(root, "payload");
                if (!Unique(payload)) return;
                var kind = Field(payload, "type");
                if (kind.ValueKind != JsonValueKind.String || kind.GetString() is not ("task_aborted" or "turn_aborted")) return;
                var turn = Field(payload, "turn_id");
                var timestamp = Field(root, "timestamp");
                if (turn.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(turn.GetString())
                    || !timestamp.TryGetDateTimeOffset(out var time)) return;
                if (Events.Count < MaximumEventsPerFile && _canRetain())
                {
                    Events.Add(new DailyTurnAbortedEvent(_context.SessionKey, EventParser.Key("turn", turn.GetString()), time));
                    _changeCount(1);
                }
                else BadStatistics = true;
            }
            catch (JsonException) { BadStatistics = true; }
            catch (InvalidOperationException) { BadStatistics = true; }
        }

        private static JsonElement Field(JsonElement node, string name) => node.ValueKind == JsonValueKind.Object && node.TryGetProperty(name, out var value) ? value : default;
        public IEnumerable<StatsEvent> SummaryEvents(DateTimeOffset now)
        {
            if (!ReadFailed) foreach (var item in Events) yield return item;
            // This evidence is transient for a half-line/backlog, and persistent for damaged or
            // capped input. Token totals keep their existing partial semantics.
            if (HasMore || PendingLine || ReadFailed || UncertainTail || BadStatistics || TimingUnavailable || _unassociatedTiming)
                foreach (var session in Events.Select(e => e.SessionKey).Distinct(StringComparer.Ordinal))
                    yield return new DailyTimingUnavailableEvent(session, null, now);
        }
        private static bool Unique(JsonElement node) => node.ValueKind == JsonValueKind.Object && node.EnumerateObject().Select(p => p.Name).Distinct(StringComparer.Ordinal).Count() == node.EnumerateObject().Count();
        private void ClearEvents() { _changeCount(-Events.Count); Events.Clear(); }
        public void Dispose() { ClearEvents(); Reader.Dispose(); }
    }
}
