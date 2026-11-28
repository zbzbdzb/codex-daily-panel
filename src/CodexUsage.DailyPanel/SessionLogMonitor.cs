using System;
using System.Collections.Generic;
using System.Globalization;
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
    private readonly Func<DirectoryInfo, IEnumerable<FileSystemInfo>> _enumerateDirectory;
    private readonly Dictionary<string, Source> _sources = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private DateOnly? _day;
    private DateTimeOffset _refreshAt;
    private volatile bool _disposed;
    private int _retainedEvents;
    // Daily coverage is independent of the currently discovered paths. Withdrawn
    // sources retain hashes and classification facts, never subtotal counters.
    private readonly List<CoverageLoss> _coverageLosses = [];
    private readonly List<ExclusionGuard> _unreadableGuards = [];
    private readonly Dictionary<string, DateTimeOffset> _observedMetadataConflicts = new(StringComparer.Ordinal);
    private bool _metadataConflictHistoryOverflow;
    private bool _unprovableCoverage;
    private int _coverageEvidenceCount;
    private bool _lostBadStatistics, _lostOversizedLine, _lostReadFailed;

    public bool IsLoading { get; private set; }

    public SessionLogMonitor(string homeDirectory, TimeZoneInfo zone)
        : this(homeDirectory, zone, EnumerateDirectory) { }

    internal SessionLogMonitor(string homeDirectory, TimeZoneInfo zone,
        Func<DirectoryInfo, IEnumerable<FileSystemInfo>> enumerateDirectory)
    {
        _home = Path.GetFullPath(homeDirectory);
        _zone = zone;
        _enumerateDirectory = enumerateDirectory;
    }

    public async Task<DailySnapshot> RefreshAsync(DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            cancellationToken.ThrowIfCancellationRequested();
            var day = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, _zone).DateTime);
            _refreshAt = now;
            var rewindBudget = MaximumRetainedEvents - _sources.Values.Sum(source => source.RewoundEvidenceCount);
            foreach (var source in _sources.Values) source.SampleAt(now, ref rewindBudget);
            if (_day != day)
            {
                // Daily numeric gaps expire. Missing classification facts remain
                // conditional: a later replay must not erase a known earlier day,
                // child relationship or shared-call ambiguity at midnight.
                // A still-live source may disappear between the last poll of
                // yesterday and this one. Capture its new-day classification
                // dependencies before discarding the previous day's cache.
                var exclusions = new HashSet<StatsEvent>();
                var conflicts = new HashSet<(string Session, string Request)>();
                DailyAggregator.Summarize(_sources.Values.SelectMany(x => x.Events).Concat(ChildEvidence()), day, _zone, now,
                    exclusionProof: exclusions, requestOrigins: RequestOrigins(), startHistory: StartHistory(), closureHistory: ClosureHistory(), closureFacts: ClosureFacts(),
                    requestConflicts: RequestConflicts(), requestConflictProof: conflicts);
                var guardCapacity = MaximumRetainedEvents - _observedMetadataConflicts.Count;
                var guards = new List<ExclusionGuard>();
                var histories = new HashSet<ToolOwnershipHistory>();
                int guardCount = 0;
                bool overflow = _sources.Values.Any(source => source.HasUnretainedRewoundWait);
                foreach (var guard in Guards().Concat(_sources.Values.SelectMany(x => CaptureGuards(x, exclusions, conflicts))).Distinct())
                {
                    guardCount += 1 + (guard is TimingGuard { Ownership: { } history } && histories.Add(history) ? history.Count : 0);
                    if (guardCount > guardCapacity) { overflow = true; break; }
                    guards.Add(guard);
                }
                ClearSources();
                _coverageLosses.Clear();
                if (guards.Count > 0)
                    _coverageLosses.Add(new CoverageLoss([], null, false, false, false, false, guards, [], 0) { PositiveResolved = true });
                _coverageEvidenceCount = GuardEvidenceCount(guards) + _observedMetadataConflicts.Count;
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
            var paths = Discover(midnight, cancellationToken, out var discoveryPartial, out var directoryFound, out var sourceLimitReached);
            cancellationToken.ThrowIfCancellationRequested();
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
                try { _sources.Add(path, new Source(path, day, _zone, now, () => _retainedEvents < MaximumRetainedEvents,
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
            UpdateUnreadableGuards();
            var skippedSources = _sources.Values.Where(source => !source.VerifiedThisRefresh && !source.ReadFailed).ToArray();
            var verified = _coverageLosses.Count == 0 && skippedSources.Length == 0 ? null : ReconcileCoverage();
            RememberMetadataConflicts();
            IsLoading = skippedSources.Length > 0 || _sources.Values.Any(source => source.HasMore);
            var current = _sources.Values.SelectMany(source => source.Events).ToArray();
            var view = _coverageLosses.Count > 0 || _unprovableCoverage || _observedMetadataConflicts.Count > 0 || _metadataConflictHistoryOverflow
                ? CoverageViewFor(current) : null;
            var activeLosses = view is null ? [] : _coverageLosses.Where(x => !x.PositiveResolved || x.Guards.Any(g => GuardActive(g, view))).ToArray();
            var metadataTimingGaps = view is null ? [] : MetadataTimingGaps(view, verified);
            var coverageLost = _unprovableCoverage || _metadataConflictHistoryOverflow || activeLosses.Length > 0 || metadataTimingGaps.Length > 0;
            var partial = coverageLost || discoveryPartial || IsLoading || _sources.Values.Any(source => source.BadStatistics || source.ReadFailed || source.UncertainTail || source.PendingLine);
            var events = _sources.Values.SelectMany(source => source.SummaryEvents(now))
                .Concat(ChildEvidence()).Concat(view is null ? [] : MissingTiming(view, activeLosses, verified, metadataTimingGaps, now))
                .Concat(verified is null ? [] : UnverifiedTiming(skippedSources, verified, now));
            var snapshot = DailyAggregator.Summarize(events, day, _zone, now, partial,
                requestOrigins: RequestOrigins(), startHistory: StartHistory(), closureHistory: ClosureHistory(), closureFacts: ClosureFacts(),
                requestConflicts: RequestConflicts()) with { SourceCount = _sources.Count };
            var reasons = new List<string>();
            var diagnostics = new List<DailyIssue>();
            void Reason(string compatibilityReason, DailyIssue issue)
            {
                reasons.Add(compatibilityReason);
                diagnostics.Add(issue);
            }
            if (!directoryFound) Reason("未找到本机 Codex sessions 日志目录", new(DailyIssueCodes.SourceNotFound));
            if (sourceLimitReached)
            {
                var limit = MaximumFiles.ToString(CultureInfo.InvariantCulture);
                Reason($"最多读取 {limit} 个日志来源，仅显示可核验小计",
                    DailyIssue.With(DailyIssueCodes.SourceLimitReached, ("limit", limit)));
            }
            if (IsLoading) Reason("正在读取今日统计，请稍候…", new(DailyIssueCodes.SourceLoading));
            if (_lostOversizedLine || _sources.Values.Any(source => source.OversizedLine) || activeLosses.Any(loss => !loss.PositiveResolved && loss.OversizedLine))
                Reason("部分统计或未知日志行超过 4 MiB 读取上限；当前为可读取范围",
                    DailyIssue.With(DailyIssueCodes.SourceOversized, ("limitMiB", "4")));
            if (_lostBadStatistics || _lostReadFailed || discoveryPartial || _sources.Values.Any(source => source.ReadFailed || source.UncertainTail || source.BadStatistics)
                || activeLosses.Any(loss => !loss.PositiveResolved && (loss.BadStatistics || loss.ReadFailed)))
                Reason("部分日志不可读取或格式不完整；当前为可读取范围", new(DailyIssueCodes.SourceUnreadable));
            if (coverageLost) Reason("日志截断、替换或删除后，当日覆盖无法确认；仅显示当前可核验小计", new(DailyIssueCodes.CoverageLost));
            reasons.Add(snapshot.Status);
            diagnostics.AddRange(snapshot.Issues);
            foreach (var source in _sources.Values) source.RememberVerifiedSample();
            // The source cap limits coverage, independently of loading or read failures.
            return snapshot with
            {
                IsPartial = snapshot.IsPartial || sourceLimitReached,
                Status = string.Join("；", reasons),
                Issues = diagnostics.AsReadOnly()
            };
        }
        finally
        {
            _gate.Release();
            // A concurrent Dispose requests cleanup without clearing a source
            // while this refresh still uses it. Recheck after releasing the gate
            // so a disposal request in the release window cannot be missed.
            if (_disposed) Dispose();
        }
    }

    private DailyCoverageStamp[] CoverageTargets() => _coverageLosses.Where(x => !x.PositiveResolved && x.RawPrefix.HasValue)
        .Select(x => x.RawPrefix!.Value).Distinct().ToArray();

    private void RememberMetadataConflicts()
    {
        bool changed = false;
        foreach (var source in _sources.Values)
        {
            // A source skipped or denied this poll needs to participate again
            // when it is verified, even if its cached metadata didn't change.
            if (!source.CanObserveMetadata) source.MetadataNeedsObservation = true;
            else if (source.MetadataNeedsObservation)
            {
                changed = true;
                source.MetadataNeedsObservation = false;
            }
        }
        if (!changed || _metadataConflictHistoryOverflow) return;

        var metadata = _sources.Values.Where(x => x.CanObserveMetadata)
            .SelectMany(x => x.Events).OfType<MetadataEvent>().Concat(ChildEvidence());
        foreach (var group in metadata.GroupBy(x => (x.SessionKey, x.Timestamp)))
        {
            if (!group.Select(x => (x.IsChild, x.ParentKey)).Distinct().Skip(1).Any()) continue;
            var (session, at) = group.Key;
            if (_observedMetadataConflicts.TryGetValue(session, out var previous))
            {
                if (at > previous) _observedMetadataConflicts[session] = at;
                continue;
            }
            if (_coverageEvidenceCount >= MaximumRetainedEvents)
            {
                _metadataConflictHistoryOverflow = true;
                return;
            }
            // One timestamp per session preserves the joint observation.
            // A newer root can cover it for a refresh, but can't erase it.
            _observedMetadataConflicts.Add(session, at);
            _coverageEvidenceCount++;
        }
    }

    private void UpdateUnreadableGuards()
    {
        // Cached rows from an unreadable source cannot supply counters or timing.
        // Their observed classification facts still constrain the readable rows.
        // Rebuild this transient proof on every poll so successful recovery clears
        // it without creating an opaque, permanently unsettled coverage loss.
        _unreadableGuards.Clear();
        if (!_sources.Values.Any(x => x.ReadFailed)) return;
        var exclusions = new HashSet<StatsEvent>();
        var conflicts = new HashSet<(string Session, string Request)>();
        DailyAggregator.Summarize(_sources.Values.SelectMany(x => x.Events).Concat(ChildEvidence()), _day!.Value, _zone, _refreshAt,
            exclusionProof: exclusions, requestOrigins: RequestOrigins(), startHistory: StartHistory(), closureHistory: ClosureHistory(), closureFacts: ClosureFacts(),
            requestConflicts: RequestConflicts(), requestConflictProof: conflicts);
        _unreadableGuards.AddRange(_sources.Values.Where(x => x.ReadFailed)
            .SelectMany(x => CaptureGuards(x, exclusions, conflicts)).Distinct());
    }

    private void RecordLoss(Source source, DailyCoverageState coverage)
    {
        _unprovableCoverage |= source.HasUnretainedRewoundWait;
        // Use the same day/root/request-occurrence rules as the actual statistics.
        // A prior-day replay, closed historical turn or confirmed child alone is
        // not missing today's root-session coverage.
        var required = new HashSet<StatsEvent>();
        var exclusions = new HashSet<StatsEvent>();
        var conflicts = new HashSet<(string Session, string Request)>();
        var observed = _sources.Values.SelectMany(x => x.Events).Concat(ChildEvidence());
        DailyAggregator.Summarize(observed, _day!.Value, _zone, _refreshAt,
            coverageProof: required, exclusionProof: exclusions, requestOrigins: RequestOrigins(), startHistory: StartHistory(), closureHistory: ClosureHistory(), closureFacts: ClosureFacts(),
            requestConflicts: RequestConflicts(), requestConflictProof: conflicts);
        // Child metadata is a conditional decision fact, rather than a daily
        // contribution. It is needed only while its session still has candidates.
        var events = new HashSet<(string Digest, TimingScopeKey? Scope)>();
        foreach (var row in source.Evidence.Where(x => required.Contains(x.Event) && x.Event is not MetadataEvent { IsChild: true }))
            events.Add((CoverageDigest(row), TimingScope(row.Event)));

        var guards = CaptureGuards(source, exclusions, conflicts).Distinct().ToList();
        if (events.Count == 0 && guards.Count == 0 && !source.BadStatistics && !source.PendingLine && !source.HasMore
            && !source.ReadFailed && !source.UncertainTail && !coverage.Incomplete) return;
        // Unknown/unread bytes cannot be reconstructed from the remaining valid events.
        // An exact consumed prefix is required for malformed/half-line/timing input.
        var opaque = source.BadStatistics || coverage.Incomplete || source.PendingLine || source.TimingUnavailable || source.UnassociatedTiming
            || source.ReadFailed && events.Count == 0;
        var loss = new CoverageLoss(events, opaque ? coverage.Stamp : null,
            source.ReadFailed || source.UncertainTail || !coverage.Stable || coverage.ObservedLength > coverage.Stamp.Bytes
                || coverage.Stamp.Bytes == 0 && (source.ReadFailed || source.HasMore),
            source.BadStatistics, source.OversizedLine, source.ReadFailed || source.UncertainTail,
            guards, source.Events.Select(x => x.SessionKey).Distinct(StringComparer.Ordinal).ToArray(),
            events.Select(x => x.Scope).Where(x => x is { } scope && (scope.Turn is not null || scope.Start is not null)).Distinct().Count());
        if (_coverageLosses.Any(x => !x.PositiveResolved && x.RawPrefix == loss.RawPrefix && x.Unprovable == loss.Unprovable
            && x.BadStatistics == loss.BadStatistics && x.OversizedLine == loss.OversizedLine
            && x.ReadFailed == loss.ReadFailed && x.Events.SetEquals(events) && x.Guards.ToHashSet().SetEquals(guards))) return;
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

    private sealed record VerifiedTimingEvidence(HashSet<string> CoverageDigests,
        HashSet<(string Coverage, string Projection)> Projections, Dictionary<string, DateTimeOffset> RootTimes);

    private IEnumerable<Source> DrainedSources() => _sources.Values.Where(x => x.VerifiedThisRefresh && !x.HasMore
        && !x.PendingLine && !x.ReadFailed && !x.UncertainTail && x.Reader.Coverage.Stable);

    private VerifiedTimingEvidence ReconcileCoverage()
    {
        var drained = DrainedSources().ToArray();
        var checkedSources = drained.Where(x => !x.BadStatistics)
            .Select(source => (source.Reader, Facts: source.Evidence.Where(x => x.Event is not QuotaEvent).ToArray())).ToArray();
        var verified = checkedSources.SelectMany(x => x.Facts).ToArray();
        var current = verified.Select(x => x.Event).ToArray();
        var hashes = verified.Select(CoverageDigest).ToHashSet(StringComparer.Ordinal);
        var projections = verified.Select(ProjectionIdentity).ToHashSet();
        var timing = verified.Where(x => x.RawTimingDigest is not null)
            .ToLookup(x => x.RawTimingDigest!, x => x.Event, StringComparer.Ordinal);
        var starts = current.OfType<StartEvent>().Concat(StartHistory())
            .ToLookup(x => (x.SessionKey, x.TurnKey), x => x.Timestamp);
        bool ProvesGuard(ExclusionGuard guard) => hashes.Contains(guard.Digest)
            && (guard is not TimingGuard old || !timing.Contains(guard.Digest)
                || projections.Contains((guard.Digest, EventDigest(old.Evidence)))
                || timing[guard.Digest].OfType<DailyTimingUnavailableEvent>().Any(row => CoversTiming(row, old.Evidence, starts)));
        static bool HasOwnershipHistory(ExclusionGuard guard) => guard is TimingGuard { Ownership: not null };
        var ownershipGroups = _coverageLosses.SelectMany(loss => loss.Guards.Where(HasOwnershipHistory)
                .Cast<TimingGuard>().Select(guard => (Loss: loss, Guard: guard)))
            .ToLookup(x => x.Guard.Digest, StringComparer.Ordinal);
        var timingSources = ownershipGroups.Count > 0
            ? checkedSources.Where(source => source.Facts.Any(x => x.RawTimingDigest is not null))
                .Select(source => (source.Reader, Digests: source.Facts.Select(CoverageDigest).ToHashSet(StringComparer.Ordinal),
                    Timing: source.Facts.Where(x => x.RawTimingDigest is not null)
                        .ToLookup(x => x.RawTimingDigest!, x => x.Event, StringComparer.Ordinal),
                    Pairs: IndexPairs(SourceWaitPairs(source.Facts, _refreshAt)))).ToArray()
            : [];
        var completeSources = new Dictionary<CoverageLoss, HashSet<DailyLogReader>>(ReferenceEqualityComparer.Instance);
        foreach (var loss in _coverageLosses)
        {
            bool complete = !loss.Unprovable && loss.Events.All(x => hashes.Contains(x.Digest))
                && (loss.RawPrefix is not { } prefix || drained.Any(x => x.Reader.Proves(prefix)));
            if (!complete) continue;
            loss.PositiveResolved = true;
            if (loss.Guards.Any(HasOwnershipHistory))
                completeSources[loss] = timingSources.Where(source => loss.Events.All(x => source.Digests.Contains(x.Digest))
                    && (loss.RawPrefix is not { } stamp || source.Reader.Proves(stamp))).Select(x => x.Reader).ToHashSet();
        }

        // A raw row can project into both its parsed and event-time start groups.
        // One current reader must cover every constraint on that row before any
        // are removed, even when the constraints came from different losses.
        var historyMatches = new Dictionary<(DailyLogReader, ToolOwnershipHistory), bool>();
        var provenOwnership = new HashSet<string>(StringComparer.Ordinal);
        foreach (var group in ownershipGroups)
        {
            var anchor = group.First();
            // Distinct spans matter for timing. Equivalent spans retain the
            // original raw witness instead of replacing it with another row.
            var seen = group.Select(x => x.Guard.ObservedPair?.Window).OfType<DailyTimingWindow>().ToHashSet();
            var additions = timingSources.Select(source => source.Pairs.GetValueOrDefault(group.Key))
                .OfType<ObservedToolPair>().OrderBy(x => x.CallDigest, StringComparer.Ordinal)
                .ThenBy(x => x.ResultDigest, StringComparer.Ordinal).Where(pair => seen.Add(pair.Window))
                .Select(pair => (anchor.Loss, Guard: anchor.Guard with { ObservedPair = pair })).ToArray();
            var constraints = group.Concat(additions);
            foreach (var source in timingSources)
            {
                if (!source.Timing.Contains(group.Key)) continue;
                var rows = source.Timing[group.Key];
                bool owned = rows.Any(row => row is DailyToolEvent);
                var unavailable = rows.OfType<DailyTimingUnavailableEvent>().ToArray();
                bool ProvesConstraint((CoverageLoss Loss, TimingGuard Guard) item)
                {
                    var history = item.Guard.Ownership!;
                    var key = (source.Reader, history);
                    if (!historyMatches.TryGetValue(key, out var matches))
                        historyMatches[key] = matches = history.Facts.Split('\n').All(source.Digests.Contains);
                    return matches && (item.Guard.ObservedPair is not { } pair
                            || source.Pairs.ContainsKey(pair.CallDigest) && source.Pairs.ContainsKey(pair.ResultDigest))
                        && (owned && completeSources.TryGetValue(item.Loss, out var covered) && covered.Contains(source.Reader)
                        || unavailable.Any(row => CoversTiming(row, item.Guard.Evidence, starts)));
                }
                if (!constraints.All(ProvesConstraint)) continue;
                provenOwnership.Add(group.Key);
                break;
            }
            if (provenOwnership.Contains(group.Key)) continue;
            foreach (var (loss, guard) in additions)
            {
                if (_coverageEvidenceCount >= MaximumRetainedEvents)
                {
                    _unprovableCoverage = true;
                    break;
                }
                loss.Guards.Add(guard);
                _coverageEvidenceCount++;
            }
        }
        foreach (var loss in _coverageLosses.ToArray())
        {
            int before = GuardEvidenceCount(loss.Guards);
            loss.Guards.RemoveAll(guard => HasOwnershipHistory(guard)
                ? provenOwnership.Contains(guard.Digest) : ProvesGuard(guard));
            _coverageEvidenceCount -= before - GuardEvidenceCount(loss.Guards);
            if (!loss.PositiveResolved || loss.Guards.Count > 0) continue;
            _coverageLosses.Remove(loss);
            _coverageEvidenceCount -= loss.EvidenceCount;
        }
        return new(hashes, projections, RootTimes(current.OfType<MetadataEvent>().Concat(ChildEvidence())));
    }

    private static bool CoversTiming(DailyTimingUnavailableEvent current, StatsEvent previous,
        ILookup<(string Session, string Turn), DateTimeOffset> starts)
    {
        if (current.SessionKey != previous.SessionKey || TimingScope(previous) is not { } prior) return false;
        if (current.StartTimestamp is { } start)
            return prior.Start == start || prior.Turn is { } turn && starts[(prior.Session, turn)].Contains(start);
        return current.TurnKey is null || current.TurnKey == prior.Turn;
    }

    private static string EventDigest(StatsEvent item) => item.GetType().FullName + ":"
        + Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(item, item.GetType())));
    private static string CoverageDigest(DailySourceEvidence fact) => fact.RawTimingDigest ?? EventDigest(fact.Event);
    private static (string Coverage, string Projection) ProjectionIdentity(DailySourceEvidence fact)
    {
        var projection = EventDigest(fact.Event);
        return (fact.RawTimingDigest ?? projection, projection);
    }

    private sealed record CoverageLoss(HashSet<(string Digest, TimingScopeKey? Scope)> Events, DailyCoverageStamp? RawPrefix, bool Unprovable,
        bool BadStatistics, bool OversizedLine, bool ReadFailed, List<ExclusionGuard> Guards, string[] Sessions,
        int TimedScopeCount)
    {
        public bool PositiveResolved { get; set; }
        public int EvidenceCount => Events.Count + GuardEvidenceCount(Guards) + Sessions.Length + TimedScopeCount;
    }

    // No retired UsageEvent or token counter is retained in a guard.
    private abstract record ExclusionGuard(string Digest, string Session);
    private sealed record OriginGuard(string Hash, string Key, string Request, DateTimeOffset At) : ExclusionGuard(Hash, Key);
    private sealed record RequestConflictGuard(string Hash, string Key, string Request) : ExclusionGuard(Hash, Key);
    private sealed record ChildGuard(string Hash, MetadataEvent Relation) : ExclusionGuard(Hash, Relation.SessionKey);
    private sealed record ToolOwnershipHistory(string Facts, int Count)
    {
        // Multiple raw tools share one group's history.
        private readonly int _hash = StringComparer.Ordinal.GetHashCode(Facts);
        public override int GetHashCode() => _hash;
    }
    // Each observed alternative consumes an existing guard budget entry.
    // Bounds decide relevance; raw row identities decide restoration.
    private sealed record ObservedToolPair(string CallDigest, string ResultDigest, DailyTimingWindow Window);
    private readonly record struct RawToolEndpoint(string Digest, string Session, string Call, DailyToolEndpoint Endpoint, bool Uncertain);
    private sealed record TimingGuard(string Hash, StatsEvent Evidence, ToolOwnershipHistory? Ownership = null,
        ObservedToolPair? ObservedPair = null)
        : ExclusionGuard(Hash, Evidence.SessionKey);
    private sealed record StartOrderGuard(string Hash, StartEvent Evidence) : ExclusionGuard(Hash, Evidence.SessionKey);
    private sealed record ClosureGuard(string Hash, DailyClosureFact Fact) : ExclusionGuard(Hash, Fact.Session);

    private static ExclusionGuard? CreateGuard(DailySourceEvidence fact, ISet<(string Session, string Request)> conflicts) => fact.Event switch
    {
        UsageEvent { RequestKey: { } request } usage when conflicts.Contains((usage.SessionKey, request)) =>
            new RequestConflictGuard(EventDigest(usage), usage.SessionKey, request),
        UsageEvent { RequestKey: { } request } usage => new OriginGuard(EventDigest(usage), usage.SessionKey, request, usage.Timestamp),
        MetadataEvent { IsChild: true } metadata => new ChildGuard(EventDigest(metadata), metadata),
        DailyToolEvent or DailyTimingUnavailableEvent => new TimingGuard(CoverageDigest(fact), fact.Event),
        StartEvent start => new StartOrderGuard(EventDigest(start), start),
        CompletionEvent end => new ClosureGuard(EventDigest(end), DailyClosureFact.From(end)),
        DailyTurnAbortedEvent abort => new ClosureGuard(EventDigest(abort), DailyClosureFact.From(abort)),
        _ => null
    };

    private static int GuardEvidenceCount(IReadOnlyCollection<ExclusionGuard> guards) => guards.Count
        + guards.OfType<TimingGuard>().Select(x => x.Ownership).OfType<ToolOwnershipHistory>().Distinct().Sum(x => x.Count);

    private static RawToolEndpoint? ToolEndpoint(DailySourceEvidence fact) => fact.RawTimingDigest is { } digest
        ? fact.Event switch
        {
            DailyToolEvent { CallKey: { } key } tool =>
                new(digest, tool.SessionKey, key, new(tool.Phase, tool.Timestamp), false),
            DailyTimingUnavailableEvent { StartTimestamp: not null, ToolCallKey: { } key, ToolPhase: { } phase } row =>
                new(digest, row.SessionKey, key, new(phase, row.Timestamp), true),
            _ => null
        } : null;

    private static IEnumerable<(RawToolEndpoint Anchor, ObservedToolPair Pair)> SourceWaitPairs(
        IEnumerable<DailySourceEvidence> evidence, DateTimeOffset now)
    {
        var endpoints = evidence.Select(ToolEndpoint).OfType<RawToolEndpoint>()
            .Where(x => x.Endpoint.Timestamp != DateTimeOffset.MinValue && x.Endpoint.Timestamp <= now);
        foreach (var group in endpoints.GroupBy(x => (x.Session, x.Call)))
        {
            var raw = group.GroupBy(x => x.Digest, StringComparer.Ordinal)
                .Select(rows => rows.First() with { Uncertain = rows.Any(x => x.Uncertain) }).ToArray();
            RawToolEndpoint? firstCall = null;
            RawToolEndpoint? lastResult = null;
            foreach (var row in raw)
            {
                if (row.Endpoint.Phase == DailyToolPhase.Call
                    && (firstCall is null || row.Endpoint.Timestamp < firstCall.Value.Endpoint.Timestamp))
                    firstCall = row;
                if (row.Endpoint.Phase == DailyToolPhase.Result
                    && (lastResult is null || row.Endpoint.Timestamp > lastResult.Value.Endpoint.Timestamp))
                    lastResult = row;
            }
            if (firstCall is not { } earliest || lastResult is not { } latest) continue;
            // For a fixed raw endpoint the farthest valid mate is itself
            // observed in this source and covers all its shorter possibilities.
            foreach (var anchor in raw)
            {
                var call = anchor.Endpoint.Phase == DailyToolPhase.Call ? anchor : earliest;
                var result = anchor.Endpoint.Phase == DailyToolPhase.Result ? anchor : latest;
                if (DailyTimingWindow.FromOrderedEndpoints(call.Endpoint, result.Endpoint, now) is { } window)
                    yield return (anchor, new(call.Digest, result.Digest, window));
            }
        }
    }

    private static Dictionary<string, ObservedToolPair> IndexPairs(IEnumerable<(RawToolEndpoint Anchor, ObservedToolPair Pair)> pairs) =>
        pairs.ToDictionary(x => x.Anchor.Digest, x => x.Pair, StringComparer.Ordinal);

    private IEnumerable<ExclusionGuard> CaptureGuards(Source source, HashSet<StatsEvent> exclusions,
        ISet<(string Session, string Request)> conflicts)
    {
        var evidence = source.Evidence.ToArray();
        var guards = evidence.Where(x => exclusions.Contains(x.Event)).Select(x => CreateGuard(x, conflicts))
            .OfType<ExclusionGuard>().ToArray();
        return AttachToolHistory(guards, evidence, _refreshAt).Concat(source.WaitsBeyondSample());
    }

    private static IEnumerable<ExclusionGuard> AttachToolHistory(ExclusionGuard[] guards, DailySourceEvidence[] evidence,
        DateTimeOffset at)
    {
        var cohorts = guards.OfType<TimingGuard>().Select(x => x.Evidence).OfType<DailyTimingUnavailableEvent>()
            .Where(x => x.StartTimestamp.HasValue)
            .Select(x => (x.SessionKey, x.StartTimestamp!.Value)).ToHashSet();
        if (cohorts.Count == 0) return guards;
        var pairs = IndexPairs(SourceWaitPairs(evidence, at));
        // ProjectOwnership uses the starts in the tool's group and the latest
        // observed end per member. Save those exact dependencies, not all prior
        // history or the subset that contributes to today's counters.
        var ends = evidence.Select(x => x.Event).Where(x => x is CompletionEvent or DailyTurnAbortedEvent && x.Timestamp <= at)
            .Select(x => (Event: x, Turn: TimingTurn(x))).Where(x => x.Turn.HasValue).GroupBy(x => x.Turn!.Value)
            .ToDictionary(x => x.Key, x => x.MaxBy(y => y.Event.Timestamp).Event);
        var histories = evidence.Select(x => x.Event).OfType<StartEvent>().Where(x => cohorts.Contains((x.SessionKey, x.Timestamp)))
            .GroupBy(x => (x.SessionKey, x.Timestamp)).ToDictionary(group => group.Key, group =>
            {
                var facts = new HashSet<string>(StringComparer.Ordinal);
                foreach (var start in group)
                {
                    facts.Add(EventDigest(start));
                    if (ends.TryGetValue((start.SessionKey, start.TurnKey), out var end) && end.Timestamp >= start.Timestamp)
                        facts.Add(EventDigest(end));
                }
                return new ToolOwnershipHistory(string.Join('\n', facts.Order(StringComparer.Ordinal)), facts.Count);
            });
        return guards.Select(guard => guard is TimingGuard { Evidence: DailyTimingUnavailableEvent { StartTimestamp: { } start } } timing
            && histories.TryGetValue((timing.Session, start), out var ownership)
                ? timing with { Ownership = ownership, ObservedPair = pairs.GetValueOrDefault(timing.Digest) } : guard);
    }

    private IEnumerable<ExclusionGuard> Guards() => _coverageLosses.SelectMany(x => x.Guards).Concat(_unreadableGuards);
    private IEnumerable<MetadataEvent> ChildEvidence() => Guards().OfType<ChildGuard>().Select(x => x.Relation).Distinct();
    private IEnumerable<StartEvent> StartHistory() => Guards().OfType<StartOrderGuard>().Select(x => x.Evidence).Distinct();
    private IEnumerable<(string Session, string Turn, DateTimeOffset At)> ClosureHistory() => Guards().OfType<ClosureGuard>()
        .Select(x => (x.Fact.Session, x.Fact.Turn, x.Fact.At)).Distinct();
    private IEnumerable<DailyClosureFact> ClosureFacts() => Guards().OfType<ClosureGuard>().Select(x => x.Fact).Distinct();
    private Dictionary<(string Session, string Request), DateTimeOffset> RequestOrigins() => Guards().OfType<OriginGuard>()
        .GroupBy(x => (x.Session, x.Request)).ToDictionary(x => x.Key, x => x.Min(g => g.At));
    private HashSet<(string Session, string Request)> RequestConflicts() => Guards().OfType<RequestConflictGuard>()
        .Select(x => (x.Session, x.Request)).ToHashSet();
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

    // Metadata and unscoped legacy rows participate in coverage without supplying
    // a turn's timing. A timing row with no turn still constrains the session.
    private readonly record struct TimingScopeKey(string Session, string? Turn, DateTimeOffset? Start = null,
        DateTimeOffset? At = null)
    {
        public DailyTimingUnavailableEvent UnavailableAt(DateTimeOffset now) => new(Session, Turn, At ?? now, Start);
    }

    private static TimingScopeKey? TimingScope(StatsEvent row) => row switch
    {
        MetadataEvent or QuotaEvent or UsageEvent { Legacy: true, TurnKey: null } => null,
        DailyTimingUnavailableEvent { StartTimestamp: { } start } tool =>
            new(tool.SessionKey, tool.TurnKey, start, tool.Timestamp),
        _ => new(row.SessionKey, TimingTurn(row)?.Turn)
    };

    private static IEnumerable<DailyTimingUnavailableEvent> UnverifiedTiming(Source[] sources,
        VerifiedTimingEvidence verified, DateTimeOffset now)
    {
        // Keep cached rows for the existing partial subtotal, but don't time them
        // from this cache alone. A checked source can independently prove a row.
        var scopes = new HashSet<TimingScopeKey>();
        foreach (var fact in sources.SelectMany(source => source.Evidence))
        {
            var row = fact.Event;
            if (row is MetadataEvent metadata)
            {
                if (!verified.RootTimes.ContainsKey(metadata.SessionKey))
                    scopes.Add(new(metadata.SessionKey, null));
            }
            else if (TimingScope(row) is { } scope && !verified.Projections.Contains(ProjectionIdentity(fact)))
                scopes.Add(scope);
        }
        return scopes.Select(x => x.UnavailableAt(now));
    }

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
        Dictionary<(string Session, string Call), (string Session, string Turn)[]> Calls,
        HashSet<(string Session, DateTimeOffset Start)> Starts, ILookup<string, DailyTimingWindow> InferredWindows,
        DateTimeOffset SampleAt);

    private static IEnumerable<(string Session, DateTimeOffset At, MetadataEvent[] Rows)> LatestMetadata(IEnumerable<MetadataEvent> metadata) =>
        metadata.GroupBy(x => x.SessionKey).Select(group =>
        {
            var at = group.Max(x => x.Timestamp);
            return (group.Key, at, group.Where(x => x.Timestamp == at).ToArray());
        });

    private static Dictionary<string, DateTimeOffset> RootTimes(IEnumerable<MetadataEvent> metadata) =>
        LatestMetadata(metadata).Where(x => x.Rows.All(row => !row.IsChild) && x.Rows.Select(row => row.ParentKey).Distinct().Count() == 1)
            .ToDictionary(x => x.Session, x => x.At, StringComparer.Ordinal);

    private CoverageView CoverageViewFor(StatsEvent[] rows)
    {
        var turns = CandidateTurns(rows);
        var roots = RootTimes(rows.OfType<MetadataEvent>().Concat(ChildEvidence())).Keys.ToHashSet(StringComparer.Ordinal);
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
        var observedStarts = rows.OfType<StartEvent>().Where(x => x.Timestamp != DateTimeOffset.MinValue && x.Timestamp <= _refreshAt).ToArray();
        var starts = observedStarts.Concat(StartHistory()).Where(x => turns.Contains((x.SessionKey, x.TurnKey)))
            .Select(x => (x.SessionKey, x.Timestamp)).ToHashSet();
        // Retired starts constrain timing but cannot supply an explicit start.
        var startedTurns = observedStarts.Select(x => (x.SessionKey, x.TurnKey)).ToHashSet();
        var inferred = rows.OfType<CompletionEvent>().Where(x => InDay(x.Timestamp) && x.Timestamp <= _refreshAt
                && !startedTurns.Contains((x.SessionKey, x.TurnKey)))
            .Select(x => (x.SessionKey, Window: DailyTimingWindow.FromCompletion(x)))
            .Where(x => x.Window is { } window && InDay(window.Start))
            .ToLookup(x => x.SessionKey, x => x.Window!.Value, StringComparer.Ordinal);
        return new(rows, turns, roots, todayRequests, sessions, times, calls, starts, inferred, _refreshAt);
    }

    private static bool ScopeRelevant(TimingScopeKey scope, CoverageView view) => scope.Start is { } start
        ? view.Starts.Contains((scope.Session, start))
            || scope.At is { } time && WaitRelevant(scope.Session, new(time, time), view)
        : scope.Turn is { } turn ? view.Turns.Contains((scope.Session, turn)) : view.CandidateSessions.Contains(scope.Session);

    private static bool WaitRelevant(string session, DailyTimingWindow wait, CoverageView view) =>
        wait.IsObservedAt(view.SampleAt) && view.InferredWindows[session].Any(wait.Overlaps);

    private static DailyTimingWindow? RelevantRetainedWait(TimingGuard guard, CoverageView view)
    {
        if (guard.ObservedPair is not { Window: var wait } || wait.Start > view.SampleAt) return null;
        // A backwards sample still needs the already witnessed part of the
        // wait. Clip the exclusion only; restoration uses both original rows.
        if (wait.End > view.SampleAt) wait = wait with { End = view.SampleAt };
        return WaitRelevant(guard.Session, wait, view) ? wait : null;
    }

    private static bool GuardActive(ExclusionGuard guard, CoverageView view) => guard switch
    {
        OriginGuard origin => view.Roots.Contains(origin.Session) && view.TodayRequests.Contains((origin.Session, origin.Request)),
        RequestConflictGuard conflict => view.Roots.Contains(conflict.Session) && view.TodayRequests.Contains((conflict.Session, conflict.Request)),
        ChildGuard child => view.CandidateSessions.Contains(child.Session)
            && (!view.MetadataTimes.TryGetValue(child.Session, out var latest) || latest <= child.Relation.Timestamp),
        TimingGuard timing => view.Roots.Contains(timing.Session)
            && (TimingScope(timing.Evidence) is { } scope && ScopeRelevant(scope, view)
                || RelevantRetainedWait(timing, view).HasValue
                || timing.Evidence is DailyToolEvent { CallKey: { } call } && view.Calls.ContainsKey((timing.Session, call))),
        // Activity qualification is compared in Summarize itself, using the same
        // closure/latest/recent rules that decide ActiveTurns and ActiveSeconds.
        StartOrderGuard or ClosureGuard => false,
        _ => false
    };

    private string[] MetadataTimingGaps(CoverageView view, VerifiedTimingEvidence? verified)
    {
        if (_observedMetadataConflicts.Count == 0 || _metadataConflictHistoryOverflow) return [];
        var affected = view.CandidateSessions.Where(_observedMetadataConflicts.ContainsKey).ToArray();
        if (affected.Length == 0) return [];
        // Match the metadata actually projected into the subtotal. An unreadable
        // source's cached rows are omitted from the snapshot.
        var roots = RootTimes(_sources.Values.Where(x => !x.ReadFailed)
            .SelectMany(x => x.Events).OfType<MetadataEvent>().Concat(ChildEvidence()));
        var checkedRoots = verified?.RootTimes ?? RootTimes(DrainedSources().Where(x => !x.BadStatistics)
            .SelectMany(x => x.Events).OfType<MetadataEvent>().Concat(ChildEvidence()));
        return affected.Where(session => roots.ContainsKey(session)
            && (!checkedRoots.TryGetValue(session, out var at) || at <= _observedMetadataConflicts[session])).ToArray();
    }

    private IEnumerable<StatsEvent> MissingTiming(CoverageView view, CoverageLoss[] losses,
        VerifiedTimingEvidence? verified, string[] metadataTimingGaps, DateTimeOffset now)
    {
        var scopes = new HashSet<TimingScopeKey>();
        var waits = new HashSet<(string Session, DailyTimingWindow Wait)>();
        foreach (var session in metadataTimingGaps) scopes.Add(new(session, null));
        var calls = new HashSet<(string Session, string Call)>();
        if (_unprovableCoverage || _metadataConflictHistoryOverflow)
            foreach (var session in view.Rows.Select(x => x.SessionKey).Distinct(StringComparer.Ordinal))
                scopes.Add(new(session, null));
        foreach (var loss in losses)
        {
            if (!loss.PositiveResolved)
            {
                if (loss.RawPrefix.HasValue || loss.Unprovable)
                    foreach (var session in loss.Sessions.Length > 0 ? loss.Sessions : view.Rows.Select(x => x.SessionKey).Distinct(StringComparer.Ordinal))
                        scopes.Add(new(session, null));
                else
                    // A known lost row only disqualifies its own turn (or session if unassociated).
                    // Use current verified digests; cached rows from a blocked source prove nothing.
                    foreach (var (digest, scope) in loss.Events)
                        if (scope is { } timingScope && verified?.CoverageDigests.Contains(digest) != true) scopes.Add(timingScope);
            }
            foreach (var guard in loss.Guards.OfType<TimingGuard>())
            {
                if (!view.Roots.Contains(guard.Session)) continue;
                if (TimingScope(guard.Evidence) is { } timingScope && ScopeRelevant(timingScope, view)) scopes.Add(timingScope);
                if (RelevantRetainedWait(guard, view) is { } wait) waits.Add((guard.Session, wait));
                if (guard.Evidence is DailyToolEvent { CallKey: { } call }) calls.Add((guard.Session, call));
            }
        }
        foreach (var call in calls)
            foreach (var turn in view.Calls.GetValueOrDefault(call) ?? []) scopes.Add(new(turn.Session, turn.Turn));
        return scopes.Select(x => x.UnavailableAt(now)).Concat(waits.Select(x =>
            new DailyTimingUnavailableEvent(x.Session, null, x.Wait.Start) { ObservedToolWait = x.Wait }));
    }

    private List<string> Discover(DateTime midnightUtc, CancellationToken cancellationToken,
        out bool partial, out bool found, out bool sourceLimitReached)
    {
        partial = false;
        found = false;
        sourceLimitReached = false;
        var paths = new List<string>();
        foreach (var name in new[] { "sessions", "archived_sessions" })
        {
            cancellationToken.ThrowIfCancellationRequested();
            var root = Path.Combine(_home, name);
            if (!Directory.Exists(root)) continue;
            found = true;
            try
            {
                if (DiscoverUnderRoot(root, midnightUtc, paths, cancellationToken, ref sourceLimitReached)) partial = true;
            }
            catch (IOException) { partial = true; }
            catch (UnauthorizedAccessException) { partial = true; }
        }
        return paths;
    }

    internal static IEnumerable<FileSystemInfo> EnumerateDirectory(DirectoryInfo directory) =>
        directory.EnumerateFileSystemInfos("*", new EnumerationOptions
        {
            IgnoreInaccessible = false, AttributesToSkip = FileAttributes.ReparsePoint
        });

    // Reports enumeration problems separately from reaching the shared source limit.
    private bool DiscoverUnderRoot(string root, DateTime midnightUtc, List<string> paths,
        CancellationToken cancellationToken, ref bool sourceLimitReached)
    {
        var rootDirectory = new DirectoryInfo(root);
        var pending = new Queue<DirectoryInfo>();
        pending.Enqueue(rootDirectory);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var partial = false;
        while (pending.TryDequeue(out var directory))
        {
            var isRoot = ReferenceEquals(directory, rootDirectory);
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                // A queued path may have changed since enumeration. Never follow a directory link.
                var attributes = File.GetAttributes(directory.FullName);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    if (isRoot) return true;
                    continue;
                }
                if ((attributes & FileAttributes.Directory) == 0)
                {
                    partial = true;
                    continue;
                }
                cancellationToken.ThrowIfCancellationRequested();
                using var entries = _enumerateDirectory(directory).GetEnumerator();
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!entries.MoveNext()) break;
                    cancellationToken.ThrowIfCancellationRequested();
                    var entry = entries.Current;
                    if (entry is DirectoryInfo child)
                    {
                        pending.Enqueue(child);
                        continue;
                    }
                    if (!Path.GetExtension(entry.Name).Equals(".jsonl", comparison)) continue;
                    var path = entry.FullName;
                    // A conversation can resume in an old date folder. Select by file timestamp.
                    var info = new FileInfo(path);
                    if (info.LastWriteTimeUtc < midnightUtc && !_sources.ContainsKey(path)) continue;
                    if (paths.Count >= MaximumFiles)
                    {
                        sourceLimitReached = true;
                        return partial;
                    }
                    paths.Add(path);
                }
            }
            catch (Exception error) when (!isRoot && error is (IOException or UnauthorizedAccessException))
            {
                // Retain entries already discovered and continue with the queued directories.
                partial = true;
            }
        }
        return partial;
    }

    private void ClearSources()
    {
        foreach (var source in _sources.Values) source.Dispose();
        _sources.Clear();
        _unreadableGuards.Clear();
    }

    /// <summary>
    /// Rejects new and queued refreshes. An already running refresh may finish;
    /// its sources are released after it leaves the monitor gate.
    /// </summary>
    public void Dispose()
    {
        _disposed = true;
        if (!_gate.Wait(0)) return;
        try { ClearSources(); }
        finally { _gate.Release(); }
        // Keep the semaphore alive for already queued RefreshAsync calls. They
        // acquire it, observe disposal, and release it without touching sources.
        // It has no native wait handle unless AvailableWaitHandle is requested.
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
        private readonly List<DailySourceEvidence> _events = [];
        private IEnumerable<DailySourceEvidence>? _projectedEvents;
        private (DateTimeOffset At, int Count)? _verifiedSample;
        private Dictionary<string, TimingGuard[]>? _rewoundWaits;
        private DateTimeOffset? _unretainedWaitEnd;
        private DateTimeOffset _sampleAt;
        private DateTimeOffset? _nextTimingChange;
        private bool _unassociatedTiming;
        public DailyLogReader Reader { get; }
        public IEnumerable<DailySourceEvidence> Evidence => _projectedEvents ??=
            DailyToolTimingParser.ProjectOwnership(_events, _sampleAt, out _nextTimingChange);
        public IEnumerable<StatsEvent> Events => Evidence.Select(x => x.Event);
        public bool HasMore { get; set; } = true;
        public bool VerifiedThisRefresh { get; set; }
        public bool MetadataNeedsObservation { get; set; }
        public bool CanObserveMetadata => VerifiedThisRefresh && !ReadFailed && Reader.HasStablePrefix;
        public bool PendingLine { get; set; }
        public bool ReadFailed { get; set; }
        public bool UncertainTail { get; set; }
        public bool BadStatistics { get; set; }
        public bool OversizedLine { get; set; }
        public bool TimingUnavailable { get; set; }
        public bool UnassociatedTiming => _unassociatedTiming;
        public int RewoundEvidenceCount { get; private set; }
        public bool HasUnretainedRewoundWait => _unretainedWaitEnd > _sampleAt;

        public Source(string path, DateOnly day, TimeZoneInfo zone, DateTimeOffset now, Func<bool> canRetain, Action<int> changeCount,
            Action<Source, DailyCoverageState> recordLoss, Func<DailyCoverageStamp[]> coverageTargets)
        {
            _key = EventParser.Key("daily-source", path);
            _day = day;
            _zone = zone;
            _sampleAt = now;
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

        public void SampleAt(DateTimeOffset now, ref int rewindBudget)
        {
            if (_verifiedSample is { } sample && now < sample.At)
            {
                _verifiedSample = null;
                // Only the verified prefix can use the old clock. An append
                // read after the rewind must qualify at its own sample.
                var prefix = _events.Take(sample.Count).ToArray();
                var observed = DailyToolTimingParser.ProjectOwnership(prefix, sample.At, out _).ToArray();
                var anchors = observed.Where(fact => fact.RawTimingDigest is not null
                        && fact.Event is DailyTimingUnavailableEvent { StartTimestamp: not null, ToolPhase: not null })
                    .Select(fact => new TimingGuard(fact.RawTimingDigest!, fact.Event)).ToArray();
                Dictionary<string, TimingGuard[]>? updates = null;
                var latestEnd = _unretainedWaitEnd;
                foreach (var group in AttachToolHistory(anchors, observed, sample.At).OfType<TimingGuard>()
                    .Where(x => x.ObservedPair is { } pair && pair.Window.End > now).GroupBy(x => x.Digest, StringComparer.Ordinal))
                {
                    var guards = group.Distinct().ToArray();
                    // Windows sharing a raw endpoint are nested. Keep the
                    // widest witnessed pair and its source-local scopes.
                    var window = guards[0].ObservedPair!.Window;
                    if (_rewoundWaits?.TryGetValue(group.Key, out var previous) == true)
                    {
                        var retained = previous[0].ObservedPair!.Window;
                        if (retained.Start <= window.Start && retained.End >= window.End) continue;
                    }
                    updates ??= _rewoundWaits is null ? new(StringComparer.Ordinal) : new(_rewoundWaits, StringComparer.Ordinal);
                    updates[group.Key] = guards;
                    if (latestEnd is null || window.End > latestEnd) latestEnd = window.End;
                }
                if (updates is not null)
                {
                    var count = GuardEvidenceCount(updates.Values.SelectMany(guards => guards).ToArray());
                    var added = count - RewoundEvidenceCount;
                    if (added > rewindBudget)
                        _unretainedWaitEnd = latestEnd;
                    else
                    {
                        rewindBudget -= added;
                        RewoundEvidenceCount = count;
                        _rewoundWaits = updates;
                    }
                }
            }
            if (now < _sampleAt || _nextTimingChange is { } next && now >= next) _projectedEvents = null;
            _sampleAt = now;
        }

        public void RememberVerifiedSample()
        {
            if (VerifiedThisRefresh && !ReadFailed && Reader.HasStablePrefix)
                _verifiedSample = (_sampleAt, _events.Count);
        }

        public IEnumerable<TimingGuard> WaitsBeyondSample() => _rewoundWaits is null ? [] :
            _rewoundWaits.Values.SelectMany(guards => guards).Where(x => x.ObservedPair!.Window.End > _sampleAt);

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
            var timing = _timing.Parse(line, parsed.Events, _context.SessionKey, out var malformedAbort);
            BadStatistics |= parsed.Malformed || parsed.Unsupported || malformedAbort;
            if (timing.Any(e => e.SessionKey == _key && e is (DailyTimingUnavailableEvent
                or DailyToolTimingParser.InferredTool { TurnKey: null }))) _unassociatedTiming = true;
            foreach (var item in parsed.Events.Concat(timing))
            {
                if (item.Timestamp == DateTimeOffset.MinValue) BadStatistics = true;
                // Retain bounded modern request and lifecycle history together. Late revisions
                // keep their original day, and a start before midnight needs its completion
                // or abort retained as well to distinguish a clipped interval from an active turn.
                if (item is not (MetadataEvent or QuotaEvent or StartEvent or CompletionEvent or DailyTurnAbortedEvent
                    or DailyToolEvent or DailyTimingUnavailableEvent or DailyToolTimingParser.InferredTool or UsageEvent { Legacy: false })
                    && DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(item.Timestamp, _zone).DateTime) != _day) continue;
                if (item is QuotaEvent quota)
                {
                    // Keep only the latest per bucket in each file, preserving sample time.
                    var old = _events.FindLastIndex(e => e.Event is QuotaEvent existing && existing.LimitKey == quota.LimitKey);
                    if (old >= 0)
                    {
                        if (_events[old].Event.Timestamp < quota.Timestamp)
                        {
                            _events[old] = new(quota);
                            _projectedEvents = null;
                            continue;
                        }
                        if (_events[old].Event == quota || _events[old].Event.Timestamp > quota.Timestamp) continue;
                    }
                }
                if (_events.Count >= MaximumEventsPerFile || !_canRetain()) { BadStatistics = true; return; }
                // Coverage tracks the original timing row and its source-local
                // session, independently of the current ownership projection.
                var rawTiming = item is DailyToolEvent or DailyTimingUnavailableEvent or DailyToolTimingParser.InferredTool
                    ? EventParser.Key("daily-raw-timing", item.SessionKey, line) : null;
                _events.Add(new(item, rawTiming));
                _projectedEvents = null;
                if (item is MetadataEvent) MetadataNeedsObservation = true;
                _changeCount(1);
            }
        }
        public IEnumerable<StatsEvent> SummaryEvents(DateTimeOffset now)
        {
            if (!ReadFailed)
            {
                bool hasScopedTool = false;
                foreach (var item in Events)
                {
                    hasScopedTool |= item is DailyTimingUnavailableEvent { StartTimestamp: not null, ToolPhase: not null };
                    yield return item;
                }
                if (hasScopedTool)
                    foreach (var (session, window) in SourceWaitPairs(Evidence, now).Where(x => x.Anchor.Uncertain)
                                 .Select(x => (x.Anchor.Session, x.Pair.Window)).Distinct())
                        yield return new DailyTimingUnavailableEvent(session, null, window.Start) { ObservedToolWait = window };
            }
            // This evidence is transient for a half-line/backlog, and persistent for damaged or
            // capped input. Token totals keep their existing partial semantics.
            if (HasMore || PendingLine || ReadFailed || UncertainTail || BadStatistics || TimingUnavailable || _unassociatedTiming)
                foreach (var session in Events.Select(e => e.SessionKey).Distinct(StringComparer.Ordinal))
                    yield return new DailyTimingUnavailableEvent(session, null, now);
        }
        private void ClearEvents()
        {
            _changeCount(-_events.Count);
            _events.Clear();
            _projectedEvents = null;
            _verifiedSample = null;
            _rewoundWaits = null;
            _unretainedWaitEnd = null;
            RewoundEvidenceCount = 0;
            _nextTimingChange = null;
        }
        public void Dispose() { ClearEvents(); Reader.Dispose(); }
    }
}
