using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
    private bool _disposed;
    private int _retainedEvents;

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
            if (_day != day) { ClearSources(); _day = day; }
            var localStart = day.ToDateTime(TimeOnly.MinValue);
            // Some zones skip midnight. Advance to the first existing local minute;
            // for an ambiguous midnight choose its earliest UTC occurrence.
            while (_zone.IsInvalidTime(localStart)) localStart = localStart.AddMinutes(1);
            var startOffset = _zone.IsAmbiguousTime(localStart) ? _zone.GetAmbiguousTimeOffsets(localStart).Max() : _zone.GetUtcOffset(localStart);
            var midnight = new DateTimeOffset(localStart, startOffset).UtcDateTime;
            var paths = Discover(midnight, out var discoveryPartial, out var directoryFound);
            var retained = paths.ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var old in _sources.Keys.Where(path => !retained.Contains(path)).ToArray())
            {
                _sources[old].Dispose();
                _sources.Remove(old);
            }
            foreach (var path in paths)
            {
                if (_sources.TryGetValue(path, out var existing))
                {
                    if (!existing.ReadFailed && !existing.Reader.IsStopped) continue;
                    existing.Dispose();
                    _sources.Remove(path);
                }
                try { _sources.Add(path, new Source(path, day, _zone, () => _retainedEvents < MaximumRetainedEvents, delta => _retainedEvents += delta)); }
                catch (ArgumentException) { discoveryPartial = true; }
            }
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
                        source.HasMore = result.HasMore;
                        source.PendingLine = result.WaitingForCompleteLine;
                        source.ReadFailed = result.Missing || result.AccessDenied;
                        source.BadStatistics |= result.InvalidUtf8 || result.OversizedLine;
                        source.OversizedLine |= result.OversizedLine;
                        source.TimingUnavailable |= result.TimingUnavailable;
                    } while (result.HasMore && bytes < ReadBudget);
                }
                catch (IOException) { source.ReadFailed = true; }
                catch (UnauthorizedAccessException) { source.ReadFailed = true; }
            }
            IsLoading = _sources.Values.Any(source => source.HasMore);
            var partial = discoveryPartial || IsLoading || _sources.Values.Any(source => source.BadStatistics || source.ReadFailed || source.PendingLine);
            var events = _sources.Values.SelectMany(source => source.SummaryEvents(now));
            var snapshot = DailyAggregator.Summarize(events, day, _zone, now, partial) with { SourceCount = _sources.Count };
            var status = !directoryFound ? "未找到本机 Codex sessions 日志目录"
                : IsLoading ? "正在读取今日统计，请稍候…"
                : _sources.Values.Any(source => source.OversizedLine) ? "部分统计或未知日志行超过 4 MiB 读取上限；当前为可读取范围"
                : discoveryPartial || _sources.Values.Any(source => source.ReadFailed || source.BadStatistics) ? "部分日志不可读取或格式不完整；当前为可读取范围"
                : snapshot.Status;
            return snapshot with { Status = status };
        }
        finally { _gate.Release(); }
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
        public bool PendingLine { get; set; }
        public bool ReadFailed { get; set; }
        public bool BadStatistics { get; set; }
        public bool OversizedLine { get; set; }
        public bool TimingUnavailable { get; set; }

        public Source(string path, DateOnly day, TimeZoneInfo zone, Func<bool> canRetain, Action<int> changeCount)
        {
            _key = EventParser.Key("daily-source", path);
            _day = day;
            _zone = zone;
            _canRetain = canRetain;
            _changeCount = changeCount;
            _context = new SourceContext(_key);
            _timing = new DailyToolTimingParser(_key);
            Reader = new DailyLogReader(path);
            Reader.ResetDetected += () =>
            {
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
