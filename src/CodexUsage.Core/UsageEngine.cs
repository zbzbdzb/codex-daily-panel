namespace CodexUsage.Core;

// Bounded in-memory statistics. No raw events, session IDs, paths or conversation content persist.
public sealed class UsageEngine
{
    public const int MaximumSessions = 128;
    public const int MaximumRequestsPerSession = 20000;
    public const int MaximumTurnsPerSession = 2000;
    private readonly Dictionary<string, State> sessions = [];
    public bool CapacityExceeded { get; private set; }
    private sealed class State(string key)
    {
        public string Key { get; } = key;
        public bool Child; public string? Parent; public bool LegacySeen; public DateTimeOffset? MetadataTime;
        public Dictionary<string,UsageEvent> Requests { get; } = [];
        public Dictionary<string,(TokenVector Tokens,DateTimeOffset Time,int Priority,long? Ordinal)> TurnSnapshots { get; } = [];
        public Dictionary<string,CompletionEvent> Completions { get; } = [];
        public (TokenVector Tokens,DateTimeOffset Time,int Priority,long? Ordinal)? ThreadSnapshot;
        public Dictionary<string,DateTimeOffset> TurnTimes { get; } = [];
        public Dictionary<string,DateTimeOffset> TurnStarts { get; } = [];
        public Dictionary<string,DateTimeOffset> TurnFirstUsage { get; } = [];
        public Dictionary<string,DateTimeOffset> TurnFirstCompletion { get; } = [];
        public HashSet<string> Issues { get; } = [];
        public QuotaEvent? Quota; public DateTimeOffset? LastEventTimestamp;
    }
    public void MarkIncomplete(string sessionKey,string issue)
    {
        var s=GetState(sessionKey); s?.Issues.Add(issue);
    }
    private State? GetState(string key)
    {
        if(sessions.TryGetValue(key,out var s)) return s;
        if(sessions.Count>=MaximumSessions) { CapacityExceeded=true; return null; }
        s=new(key); sessions.Add(key,s); return s;
    }
    public void Apply(StatsEvent ev)
    {
        var s=GetState(ev.SessionKey); if(s is null) return;
        if(ev is not MetadataEvent && ev.Timestamp!=DateTimeOffset.MinValue && (s.LastEventTimestamp is null || ev.Timestamp>s.LastEventTimestamp)) s.LastEventTimestamp=ev.Timestamp;
        switch(ev)
        {
            case MetadataEvent m:
                if(s.MetadataTime is { } mt && m.Timestamp<mt) break;
                if(s.MetadataTime==m.Timestamp && (s.Child!=m.IsChild || s.Parent!=m.ParentKey)) { s.Issues.Add("同一时间的会话关系冲突"); break; }
                s.MetadataTime=m.Timestamp; s.Child=m.IsChild; s.Parent=m.ParentKey; break;
            case QuotaEvent q: if(s.Quota is null || q.Timestamp>=s.Quota.Timestamp) s.Quota=q; break;
            case StartEvent start:
                if(s.TurnTimes.Count>=MaximumTurnsPerSession && !s.TurnTimes.ContainsKey(start.TurnKey)) s.Issues.Add("达到回合容量上限；请重新选择日志");
                else { SetTurnTime(s,start.TurnKey,start.Timestamp); SetEarliestTime(s.TurnStarts,start.TurnKey,start.Timestamp); }
                break;
            case CompletionEvent c:
                if (!s.TurnTimes.ContainsKey(c.TurnKey) && s.TurnTimes.Count>=MaximumTurnsPerSession) { s.Issues.Add("达到回合容量上限；请重新选择日志"); break; }
                SetEarliestTime(s.TurnFirstCompletion,c.TurnKey,c.Timestamp);
                if(s.Completions.TryGetValue(c.TurnKey,out var old) && c.Timestamp==old.Timestamp && (c.DurationMs!=old.DurationMs || c.TimeToFirstTokenMs!=old.TimeToFirstTokenMs)) { s.Issues.Add("同一时间的完成事件冲突"); break; }
                if(old is null || c.Timestamp>=old.Timestamp) s.Completions[c.TurnKey]=c;
                SetTurnTime(s,c.TurnKey,c.Timestamp); break;
            case UsageEvent u:
                if (!u.Legacy && u.RequestKey is { } candidate && s.Requests.TryGetValue(candidate,out var sameOrder) && sameOrder.Timestamp==u.Timestamp && sameOrder.Ordinal==u.Ordinal && (sameOrder.Usage!=u.Usage || sameOrder.TurnSnapshot!=u.TurnSnapshot || sameOrder.ThreadSnapshot!=u.ThreadSnapshot || sameOrder.TurnKey!=u.TurnKey || sameOrder.RootTurnKey!=u.RootTurnKey))
                { s.Issues.Add("同一顺序的请求修订冲突"); break; }
                if(!u.Legacy && u.RequestKey is { } ambiguousKey && s.Requests.TryGetValue(ambiguousKey,out var ambiguous) && ambiguous.Timestamp==u.Timestamp && ambiguous.Ordinal.HasValue!=u.Ordinal.HasValue && (ambiguous.Usage!=u.Usage || ambiguous.TurnSnapshot!=u.TurnSnapshot || ambiguous.ThreadSnapshot!=u.ThreadSnapshot || ambiguous.TurnKey!=u.TurnKey || ambiguous.RootTurnKey!=u.RootTurnKey)) s.Issues.Add("缺少 ordinal 的同时间修订顺序不明确");
                if(!u.Legacy && u.RequestKey is { } orderedKey && s.Requests.TryGetValue(orderedKey,out var ordered) && ordered.Ordinal.HasValue && u.Ordinal.HasValue && ((u.Timestamp<ordered.Timestamp && u.Ordinal>ordered.Ordinal) || (u.Timestamp>ordered.Timestamp && u.Ordinal<ordered.Ordinal))) s.Issues.Add("请求时间戳与 ordinal 顺序矛盾");
                if (u.Timestamp==DateTimeOffset.MinValue) s.Issues.Add("统计事件采样时间缺失");
                if (u.Legacy) s.LegacySeen=true;
                if(u.Usage is { IsValid:false } || u.TurnSnapshot is { IsValid:false } || u.ThreadSnapshot is { IsValid:false }) { s.Issues.Add("token 字段无效"); break; }
                if(u.ThreadSnapshot is { } thread && Newer(u.Timestamp,u.Legacy?1:2,u.Ordinal,s.ThreadSnapshot)) s.ThreadSnapshot=(thread,u.Timestamp,u.Legacy?1:2,u.Ordinal);
                if(u.TurnKey is { } turn)
                {
                    if (!s.TurnTimes.ContainsKey(turn) && s.TurnTimes.Count>=MaximumTurnsPerSession) { s.Issues.Add("达到回合容量上限；请重新选择日志"); break; }
                    SetTurnTime(s,turn,u.Timestamp);
                    if(!u.Legacy && u.RequestKey is not null && u.Usage is not null) SetEarliestTime(s.TurnFirstUsage,turn,u.Timestamp);
                    if(u.TurnSnapshot is { } snapshot && (!s.TurnSnapshots.TryGetValue(turn,out var current) || Newer(u.Timestamp,u.Legacy?1:2,u.Ordinal,current))) s.TurnSnapshots[turn]=(snapshot,u.Timestamp,u.Legacy?1:2,u.Ordinal);
                }
                if(u.Legacy) break;
                if(u.RequestKey is null || u.TurnKey is null || u.Usage is null) { s.Issues.Add("请求身份或 usage 缺失"); break; }
                if(s.Requests.TryGetValue(u.RequestKey,out var previous))
                {
                    if(CompareOrder(u.Timestamp,u.Ordinal,previous.Timestamp,previous.Ordinal)<0) break;
                    if(previous.TurnKey!=u.TurnKey) s.Issues.Add("请求回合身份发生变化");
                    s.Requests[u.RequestKey]=u;
                }
                else if(s.Requests.Count<MaximumRequestsPerSession) s.Requests.Add(u.RequestKey,u);
                else s.Issues.Add("达到请求容量上限；请重新选择日志");
                break;
        }
    }
    private static void SetTurnTime(State s,string turn,DateTimeOffset time) { if(!s.TurnTimes.TryGetValue(turn,out var old) || time>old) s.TurnTimes[turn]=time; }
    private static void SetEarliestTime(Dictionary<string,DateTimeOffset> values,string key,DateTimeOffset time) { if(time!=DateTimeOffset.MinValue && (!values.TryGetValue(key,out var old) || time<old)) values[key]=time; }
    private static DateTimeOffset? LifecycleTime(State s,string key)
    {
        DateTimeOffset? earliest=null;
        if(s.TurnStarts.TryGetValue(key,out var started)) earliest=started;
        if(s.TurnFirstUsage.TryGetValue(key,out var first) && (earliest is null || first<earliest)) earliest=first;
        if(s.TurnFirstCompletion.TryGetValue(key,out var completed) && (earliest is null || completed<earliest)) earliest=completed;
        // Lifecycle evidence only moves earlier. Latest completion timing revisions
        // remain separate, so late usage or completion cannot revive an old turn.
        return earliest;
    }
    private static int CompareOrdinal(long? value,long? previous) => value.HasValue==previous.HasValue ? Nullable.Compare(value,previous) : value.HasValue?1:-1;
    private static int CompareOrder(DateTimeOffset time,long? ordinal,DateTimeOffset previousTime,long? previousOrdinal) => time!=previousTime ? time.CompareTo(previousTime) : CompareOrdinal(ordinal,previousOrdinal);
    private static bool Newer(DateTimeOffset time,int priority,long? ordinal,(TokenVector Tokens,DateTimeOffset Time,int Priority,long? Ordinal)? previous) => previous is null || time>previous.Value.Time || (time==previous.Value.Time && (priority>previous.Value.Priority || (priority==previous.Value.Priority && CompareOrdinal(ordinal,previous.Value.Ordinal)>=0)));
    private static TokenVector Sum(IEnumerable<UsageEvent> events) => events.Aggregate(TokenVector.Zero,(sum,x)=>sum+x.Usage!);
    private static DateTimeOffset? KnownLatestTime(IEnumerable<UsageEvent> events) { var time=events.Select(e=>e.Timestamp).DefaultIfEmpty(DateTimeOffset.MinValue).Max(); return time==DateTimeOffset.MinValue?null:time; }
    private static bool Mismatch(TokenVector sum,TokenVector snapshot) => !sum.IsComplete || !snapshot.IsComplete || sum!=snapshot;
    public IReadOnlyList<SessionView> GetSessions()
    {
        return sessions.Values.Select(s=>
        {
            var issues=new HashSet<string>(s.Issues);
            var requests=s.Requests.Values.ToArray(); var total=requests.Length==0 ? TokenVector.Unknown : Sum(requests);
            if(!total.IsComplete || requests.Any(x=>!x.Usage!.IsComplete)) issues.Add("token 字段缺失");
            if(requests.Length==0 && s.LegacySeen) issues.Add("旧格式缺少请求身份；仅有累计快照");
            if(requests.Length>0 && s.ThreadSnapshot is null) issues.Add("线程累计快照缺失；无法对账");
            if(s.ThreadSnapshot is { } thread && Mismatch(total,thread.Tokens)) issues.Add("线程累计对账不一致");
            var grouped=requests.GroupBy(x=>x.TurnKey!).ToDictionary(x=>x.Key,x=>x.ToArray());
            var turns=s.TurnTimes.OrderByDescending(x=>LifecycleTime(s,x.Key) ?? x.Value).ThenBy(x=>x.Key,StringComparer.Ordinal).Select(pair=>
            {
                var turnRequests=grouped.TryGetValue(pair.Key,out var groupedRequests)?groupedRequests:[];
                var tokens=turnRequests.Length==0 ? TokenVector.Unknown : Sum(turnRequests);
                TokenVector? snapshot=s.TurnSnapshots.TryGetValue(pair.Key,out var snap)?snap.Tokens:null;
                var completed=s.Completions.TryGetValue(pair.Key,out var completion);
                var incomplete=!tokens.IsComplete || snapshot is null || Mismatch(tokens,snapshot) || turnRequests.Any(x=>!x.Usage!.IsComplete);
                if(incomplete && (turnRequests.Length>0 || completed)) issues.Add("回合累计对账不一致或字段缺失");
                return new TurnView(pair.Key,tokens,snapshot,completion?.DurationMs,completion?.TimeToFirstTokenMs,completed,incomplete,pair.Value==DateTimeOffset.MinValue?null:pair.Value,completion is null || completion.Timestamp==DateTimeOffset.MinValue?null:completion.Timestamp,KnownLatestTime(turnRequests));
            }).ToArray();
            if(issues.Count>0) turns=turns.Select(t=>t with { Incomplete=true }).ToArray();
            var latest=requests.MaxBy(x=>(x.Timestamp,x.Ordinal.HasValue,x.Ordinal ?? 0));
            var label=(s.Child ? "子代理 " : "会话 ")+s.Key[..Math.Min(10,s.Key.Length)];
            return new SessionView(s.Key,label,s.Child,s.Parent,latest is null?null:new(latest.Usage!,latest.Timestamp),turns.FirstOrDefault(),total,s.ThreadSnapshot?.Tokens,requests.Length,issues.Count>0,issues.Order().ToArray(),s.Quota,turns,s.LastEventTimestamp,KnownLatestTime(requests));
        }).ToArray();
    }
}
