using System.Diagnostics;

[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("CodexUsage.Tests")]

namespace CodexUsage.Core;

public sealed record SourceStatus(string Alias,long Offset,long Lines,long MalformedLines,long UnsupportedEvents,string? Issue,bool IsStopped,bool IsLoading=false,bool WaitingForCompleteLine=false);

/// <summary>Monitors explicitly selected sources only. All stored data are projected statistics and safe status counts.</summary>
public sealed class LogMonitor : IDisposable
{
    // Anonymous identity diagnostics only; no file reads or changes to aggregation.
    // The UI takes this bounded projection after a completed polling batch.
    public IReadOnlySet<string> SourceDerivedSessionKeys
    {
        get
        {
            var keys = new HashSet<string>(StringComparer.Ordinal);
            var visible = Sessions.Select(session => session.Key).ToHashSet(StringComparer.Ordinal);
            foreach (var source in sources)
                foreach (var key in source.DerivedKeys)
                    if (visible.Contains(key)) keys.Add(key);
            return keys;
        }
    }
    private readonly List<Source> sources=[];
    private readonly UsageEngine engine=new();
    private readonly EventParser parser=new();
    private readonly SemaphoreSlim gate=new(1,1);
    private readonly CancellationTokenSource shutdown=new();
    private readonly Action<int>? beforeSourceRead;
    private readonly Action? afterCompleteLine;
    private bool disposed; private bool viewsDirty;
    private sealed class Source : IDisposable
    {
        public required string Alias; public required IncrementalLogReader Reader; public required SourceContext Context; public required string ContextKey;
        public FileSystemWatcher? Watcher; public int Dirty=1; public long LastPoll;
        public long Lines; public long Malformed; public long Unsupported;
        public HashSet<string> Sessions { get; }=[];
        public HashSet<string> DerivedKeys { get; }=[];
        public string? DerivedContextKey;
        public string? Issue; public bool Stopped; public bool Loading=true; public bool WaitingForCompleteLine; public bool Interrupted; public bool TransientIssue; public string? LastPersistentIssue;
        public long Generation;
        public HashSet<string> StatisticalIssues { get; }=[];
        public void Dispose() { Watcher?.Dispose(); Reader.Dispose(); }
    }
    public LogMonitor(IEnumerable<string> paths) : this(paths,null) { }
    internal LogMonitor(IEnumerable<string> paths,Action<int>? beforeSourceRead,Action? afterCompleteLine=null)
    {
        this.beforeSourceRead=beforeSourceRead;
        this.afterCompleteLine=afterCompleteLine;
        var configured=paths.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).Take(17).ToArray();
        if(configured.Length>16) throw new ArgumentException("Select at most sixteen log files.",nameof(paths));
        try
        {
            foreach(var path in configured)
            {
                var source=new Source { Alias=$"来源 {sources.Count+1}",Reader=new(path),Context=new(EventParser.Key("source",path)),ContextKey=EventParser.Key("source",path) };
                source.DerivedContextKey = source.ContextKey;
                sources.Add(source);
                source.Reader.ResetDetected+=()=> {
                    SetIssue(source,"文件截断、替换或轮转；请核对累计快照");
                    source.Sessions.Clear();
                    source.Generation++;
                    source.DerivedContextKey=EventParser.Key("source-generation",source.ContextKey,source.Generation.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    source.Context=new(source.DerivedContextKey);
                };
                try
                {
                    source.Watcher=new(Path.GetDirectoryName(path)!,Path.GetFileName(path)) { NotifyFilter=NotifyFilters.LastWrite|NotifyFilters.Size|NotifyFilters.FileName };
                    source.Watcher.Changed+=(_,_)=>Interlocked.Exchange(ref source.Dirty,1);
                    source.Watcher.Created+=(_,_)=>Interlocked.Exchange(ref source.Dirty,1);
                    source.Watcher.Deleted+=(_,_)=>Interlocked.Exchange(ref source.Dirty,1);
                    source.Watcher.Renamed+=(_,_)=>Interlocked.Exchange(ref source.Dirty,1);
                    source.Watcher.Error+=(_,_)=>Interlocked.Exchange(ref source.Dirty,1);
                    source.Watcher.EnableRaisingEvents=true;
                }
                catch(IOException) { source.Watcher?.Dispose(); source.Watcher=null; }
                catch(UnauthorizedAccessException) { source.Watcher?.Dispose(); source.Watcher=null; }
                catch(ArgumentException) { source.Watcher?.Dispose(); source.Watcher=null; }
            }
        }
        catch { Dispose(); throw; }
        UpdateViews();
    }
    public IReadOnlyList<SessionView> Sessions { get; private set; }=[];
    public IReadOnlyList<SourceStatus> Sources { get; private set; }=[];
    public bool CapacityExceeded => engine.CapacityExceeded;
    public string Summary => CapacityExceeded ? "统计不完整：达到会话容量上限；请重新配置" : Sources.Count==0 ? "请选择统计日志；不会自动扫描历史" : string.Join("；",Sources.Select(s=>$"{s.Alias} · {s.Lines:N0} 行 · {s.Offset:N0} 字节"+(s.IsLoading?" · 正在读取，统计暂不完整":"")+(s.Issue is null?"":$" · {s.Issue}")));
    public async Task<bool> PollAsync(bool force=false,CancellationToken cancellationToken=default)
    {
        ObjectDisposedException.ThrowIf(disposed,this);
        using var linked=CancellationTokenSource.CreateLinkedTokenSource(shutdown.Token,cancellationToken);
        await gate.WaitAsync(linked.Token).ConfigureAwait(false);
        try { return await Task.Run(()=>DrainAsync(force,linked.Token),linked.Token).ConfigureAwait(false); }
        finally { gate.Release(); }
    }
    private async Task<bool> DrainAsync(bool force,CancellationToken cancellationToken)
    {
        var changed=false;
        for(var sourceIndex=0;sourceIndex<sources.Count;sourceIndex++)
        {
            var source=sources[sourceIndex];
            beforeSourceRead?.Invoke(sourceIndex);
            cancellationToken.ThrowIfCancellationRequested();
            if(source.Stopped) continue;
            var now=Stopwatch.GetTimestamp();
            if(!force && Volatile.Read(ref source.Dirty)==0 && Stopwatch.GetElapsedTime(source.LastPoll,now)<TimeSpan.FromSeconds(1)) continue;
            Interlocked.Exchange(ref source.Dirty,0); source.LastPoll=now;
            try
            {
                var result=await source.Reader.ReadAvailableAsync(line=>
                {
                    if(!string.IsNullOrWhiteSpace(line)) {
                    var parsed=parser.Parse(line,source.Context);
                    if(parsed.Malformed) { source.Malformed++; SetIssue(source,"存在损坏的 JSON 行"); }
                    if(parsed.Unsupported) { source.Unsupported++; SetIssue(source,"存在不支持的统计格式"); }
                    foreach(var ev in parsed.Events)
                    {
                        if(source.Sessions.Count<UsageEngine.MaximumSessions || source.Sessions.Contains(ev.SessionKey))
                        {
                            source.Sessions.Add(ev.SessionKey);
                            if (ev.SessionKey == source.DerivedContextKey
                                && source.DerivedKeys.Count < UsageEngine.MaximumSessions)
                                source.DerivedKeys.Add(ev.SessionKey);
                        }
                        viewsDirty=true;
                        engine.Apply(ev);
                        foreach(var issue in source.StatisticalIssues) engine.MarkIncomplete(ev.SessionKey,issue);
                    }
                    }
                    afterCompleteLine?.Invoke();
                },cancellationToken).ConfigureAwait(false);
                changed|=CommitReadResult(source,result);
            }
            catch(IOException) { changed|=CommitReadResult(source,source.Reader.DrainPendingResult(),true); changed=true; SetIssue(source,"读取暂时失败；等待重试",false); }
            catch(ObjectDisposedException) when(disposed) { PublishInterrupted(source); throw new OperationCanceledException(cancellationToken); }
            catch { PublishInterrupted(source); throw; }
        }
        changed|=viewsDirty;
        if(changed) { UpdateViews(); viewsDirty=false; }
        return changed;
    }
    private bool CommitReadResult(Source source,ReadBatchResult result,bool interrupted=false)
    {
        var changed=source.Interrupted!=interrupted; source.Interrupted=interrupted;
        source.Lines+=result.LinesRead; var wasLoading=source.Loading; source.Loading=result.HasMore; changed|=wasLoading!=source.Loading;
        var wasWaiting=source.WaitingForCompleteLine; source.WaitingForCompleteLine=result.WaitingForCompleteLine; changed|=wasWaiting!=source.WaitingForCompleteLine;
        changed|=result.BytesRead>0 || result.LinesRead>0 || result.ResetDetected || result.AccessDenied || result.Missing || result.InvalidUtf8 || result.OversizedLine;
        if(result.HasMore) Interlocked.Exchange(ref source.Dirty,1);
        if(result.ResetDetected) SetIssue(source,"文件截断、替换或轮转；请核对累计快照");
        if(result.InvalidUtf8) SetIssue(source,"存在无效 UTF-8 行");
        if(result.OversizedLine) SetIssue(source,"已跳过超过 4 MiB 的行");
        if(result.Missing) SetIssue(source,"文件暂不存在；等待重新出现",false);
        if(!interrupted && !result.Missing && !result.AccessDenied && source.TransientIssue) { source.TransientIssue=false; source.Issue=source.LastPersistentIssue; changed=true; }
        viewsDirty|=changed;
        if(result.AccessDenied) { source.Stopped=true; SetIssue(source,"权限拒绝：已停止读取，请重新配置"); }
        return changed;
    }
    private void PublishInterrupted(Source source)
    {
        CommitReadResult(source,source.Reader.DrainPendingResult(),true);
        UpdateViews(); viewsDirty=false;
    }
    private void SetIssue(Source source,string issue,bool persistent=true)
    {
        viewsDirty=true; source.Issue=issue; source.TransientIssue=!persistent;
        if(!persistent) return;
        source.LastPersistentIssue=issue; source.StatisticalIssues.Add(issue);
        foreach(var session in source.Sessions) engine.MarkIncomplete(session,issue);
    }
    private void UpdateViews()
    {
        Sessions=engine.GetSessions().Select(session=> {
            var provisional=sources.Any(source=>source.Sessions.Contains(session.Key) && (source.Loading || source.WaitingForCompleteLine || source.Interrupted || source.TransientIssue));
            if(!provisional && !CapacityExceeded) return session;
            var issue=CapacityExceeded ? "达到会话容量上限；统计不完整" : "来源正在读取或暂不可用；累计暂不完整";
            return session with { Incomplete=true, Issues=session.Issues.Append(issue).Distinct().ToArray(), CurrentTurn=session.CurrentTurn is { } turn ? turn with { Incomplete=true }:null, Turns=session.Turns.Select(t=>t with {Incomplete=true}).ToArray() };
        }).ToArray();
        Sources=sources.Select(s=>new SourceStatus(s.Alias,s.Reader.Offset,s.Lines,s.Malformed,s.Unsupported,s.Issue,s.Stopped,s.Loading||s.Interrupted,s.WaitingForCompleteLine)).ToArray();
    }
    public void Dispose()
    {
        if(disposed) return; disposed=true; shutdown.Cancel();
        foreach(var source in sources) source.Dispose();
        // Gate/CTS remain alive until in-flight reads observe cancellation.
    }
}
