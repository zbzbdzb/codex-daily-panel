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
    // Evidence belongs to _day, independently of the currently discovered paths.
    // Only hashes survive withdrawal; retired events never supply subtotal values.
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
                ClearSources();
                _coverageLosses.Clear();
                _coverageEvidenceCount = 0;
                _unprovableCoverage = false;
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
                    // A transient I/O failure does not require throwing away its cursor,
                    // parser context, format diagnostics or historical occurrence dates.
                    if (!existing.Reader.IsStopped) continue;
                    var coverage = existing.Reader.Coverage;
                    // An initial denied open has not withdrawn any observed data.
                    // Its still-discovered path can recover by a complete first read.
                    if (coverage.Stamp.Bytes > 0 || coverage.ObservedLength > 0) RecordLoss(existing, coverage);
                    existing.Dispose();
                    _sources.Remove(path);
                }
                try { _sources.Add(path, new Source(path, day, _zone, () => _retainedEvents < MaximumRetainedEvents,
                    delta => _retainedEvents += delta, RecordLoss, CoverageTargets)); }
                catch (ArgumentException) { discoveryPartial = true; }
            }
            foreach (var stamp in CoverageTargets())
                foreach (var source in _sources.Values) source.Reader.RequestCoverageReplay(stamp);
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
                    source.VerifiedThisRefresh = false;
                }
                catch (UnauthorizedAccessException)
                {
                    var pending = source.Reader.DrainPendingResult();
                    bytes += pending.BytesRead;
                    source.Apply(pending);
                    source.ReadFailed = true;
                    source.VerifiedThisRefresh = false;
                }
                catch (OperationCanceledException) { source.Apply(source.Reader.DrainPendingResult()); throw; }
            }
            ReconcileCoverage();
            IsLoading = _sources.Values.Any(source => source.HasMore);
            var coverageLost = _unprovableCoverage || _coverageLosses.Count > 0;
            var partial = coverageLost || discoveryPartial || IsLoading || _sources.Values.Any(source => source.BadStatistics || source.ReadFailed || source.PendingLine);
            var events = _sources.Values.SelectMany(source => source.SummaryEvents(now));
            var snapshot = DailyAggregator.Summarize(events, day, _zone, now, partial) with { SourceCount = _sources.Count };
            var reasons = new List<string>();
            if (!directoryFound) reasons.Add("未找到本机 Codex sessions 日志目录");
            if (IsLoading) reasons.Add("正在读取今日统计，请稍候…");
            if (_lostOversizedLine || _sources.Values.Any(source => source.OversizedLine) || _coverageLosses.Any(loss => loss.OversizedLine))
                reasons.Add("部分统计或未知日志行超过 4 MiB 读取上限；当前为可读取范围");
            if (_lostBadStatistics || _lostReadFailed || discoveryPartial || _sources.Values.Any(source => source.ReadFailed || source.BadStatistics)
                || _coverageLosses.Any(loss => loss.BadStatistics || loss.ReadFailed))
                reasons.Add("部分日志不可读取或格式不完整；当前为可读取范围");
            if (coverageLost) reasons.Add("日志截断、替换或删除后，当日覆盖无法确认；仅显示当前可核验小计");
            reasons.Add(snapshot.Status);
            return snapshot with { Status = string.Join("；", reasons) };
        }
        finally { _gate.Release(); }
    }

    private DailyCoverageStamp[] CoverageTargets() => _coverageLosses.Where(x => x.RawPrefix.HasValue)
        .Select(x => x.RawPrefix!.Value).Distinct().ToArray();

    private void RecordLoss(Source source, DailyCoverageState coverage)
    {
        // Use the same day/root/request-occurrence rules as the actual statistics.
        // A prior-day replay, closed historical turn or confirmed child alone is
        // not missing today's root-session coverage.
        var required = new HashSet<StatsEvent>();
        DailyAggregator.Summarize(_sources.Values.SelectMany(x => x.Events), _day!.Value, _zone, _refreshAt,
            coverageProof: required);
        var hashes = source.Events.Where(required.Contains).Select(EventDigest).ToHashSet(StringComparer.Ordinal);
        if (hashes.Count == 0 && !source.BadStatistics && !source.PendingLine && !source.HasMore && !source.ReadFailed && !coverage.Incomplete) return;
        // Unknown/unread bytes cannot be reconstructed from the remaining valid events.
        // An exact consumed prefix is required for malformed/half-line/timing input.
        var opaque = source.BadStatistics || coverage.Incomplete || source.PendingLine || source.TimingUnavailable || source.UnassociatedTiming
            || source.ReadFailed && hashes.Count == 0;
        var loss = new CoverageLoss(hashes, opaque ? coverage.Stamp : null,
            !coverage.Stable || coverage.ObservedLength > coverage.Stamp.Bytes
                || coverage.Stamp.Bytes == 0 && (source.ReadFailed || source.HasMore),
            source.BadStatistics, source.OversizedLine, source.ReadFailed);
        if (_coverageLosses.Any(x => x.RawPrefix == loss.RawPrefix && x.Unprovable == loss.Unprovable
            && x.BadStatistics == loss.BadStatistics && x.OversizedLine == loss.OversizedLine
            && x.ReadFailed == loss.ReadFailed && x.Events.SetEquals(hashes))) return;
        // Repeated generations must not circumvent the existing bounded retention.
        if (_coverageLosses.Count >= MaximumRetainedEvents || _coverageEvidenceCount + hashes.Count > MaximumRetainedEvents)
        {
            _unprovableCoverage = true;
            _lostBadStatistics |= loss.BadStatistics;
            _lostOversizedLine |= loss.OversizedLine;
            _lostReadFailed |= loss.ReadFailed;
            return;
        }
        _coverageLosses.Add(loss);
        _coverageEvidenceCount += hashes.Count;
    }

    private void ReconcileCoverage()
    {
        if (_coverageLosses.Count == 0) return;
        var drained = _sources.Values.Where(x => x.VerifiedThisRefresh && !x.HasMore && !x.PendingLine && !x.ReadFailed && x.Reader.Coverage.Stable).ToArray();
        var hashes = drained.Where(x => !x.BadStatistics).SelectMany(x => x.Events).Where(e => e is not QuotaEvent)
            .Select(EventDigest).ToHashSet(StringComparer.Ordinal);
        foreach (var loss in _coverageLosses.ToArray())
        {
            if (loss.Unprovable || !loss.Events.IsSubsetOf(hashes)) continue;
            if (loss.RawPrefix is { } prefix && !drained.Any(x => x.Reader.Proves(prefix))) continue;
            _coverageLosses.Remove(loss);
            _coverageEvidenceCount -= loss.Events.Count;
        }
    }

    private static string EventDigest(StatsEvent item) => item.GetType().FullName + ":"
        + Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(item, item.GetType())));

    private sealed record CoverageLoss(HashSet<string> Events, DailyCoverageStamp? RawPrefix, bool Unprovable,
        bool BadStatistics, bool OversizedLine, bool ReadFailed);

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
            };
        }

        public void Apply(DailyReadBatchResult result)
        {
            HasMore = result.HasMore;
            PendingLine = result.WaitingForCompleteLine;
            ReadFailed = result.Missing || result.AccessDenied;
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
            if (HasMore || PendingLine || ReadFailed || BadStatistics || TimingUnavailable || _unassociatedTiming)
                foreach (var session in Events.Select(e => e.SessionKey).Distinct(StringComparer.Ordinal))
                    yield return new DailyTimingUnavailableEvent(session, null, now);
        }
        private static bool Unique(JsonElement node) => node.ValueKind == JsonValueKind.Object && node.EnumerateObject().Select(p => p.Name).Distinct(StringComparer.Ordinal).Count() == node.EnumerateObject().Count();
        private void ClearEvents() { _changeCount(-Events.Count); Events.Clear(); }
        public void Dispose() { ClearEvents(); Reader.Dispose(); }
    }
}
