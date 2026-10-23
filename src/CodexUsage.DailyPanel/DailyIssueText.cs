using System.Collections.Generic;
using CodexUsage.Daily;

namespace CodexUsage.DailyPanel;

internal static class DailyIssueText
{
    internal static string Format(DailyIssue issue, DailyStrings text)
    {
        if (issue.Code.StartsWith("settings.", System.StringComparison.Ordinal)) return text["SettingsProblem"];
        if (!Messages.TryGetValue(issue.Code, out var message)) return text.Format("UnknownIssue", issue.Code);
        var value = text.Language == LanguagePreference.ZhCN ? message.Zh : message.En;
        foreach (var parameter in issue.Parameters) value = value.Replace("{" + parameter.Key + "}", parameter.Value, System.StringComparison.Ordinal);
        return value;
    }
    private static readonly IReadOnlyDictionary<string, (string En, string Zh)> Messages = new Dictionary<string, (string, string)>
    {
        [DailyIssueCodes.SourcePartial] = ("Sources are still loading or some sources are unavailable.", "来源仍在读取或部分来源不可用。"),
        [DailyIssueCodes.MetadataConflict] = ("Session relationships conflict.", "会话关系冲突。"),
        [DailyIssueCodes.MetadataMissing] = ("Missing session metadata prevents confirming root-session scope.", "缺少会话元数据，无法确认根会话范围。"),
        [DailyIssueCodes.TimestampMissing] = ("A statistics event has no timestamp.", "统计事件缺少时间。"),
        [DailyIssueCodes.TimestampFuture] = ("A statistics event is dated in the future.", "统计事件时间超前。"),
        [DailyIssueCodes.RequestIdentityMissing] = ("Request identity is missing.", "请求身份缺失。"),
        [DailyIssueCodes.RequestRevisionConflict] = ("Request revisions conflict.", "请求修订冲突。"),
        [DailyIssueCodes.RequestTokensInvalid] = ("Request token fields are invalid.", "请求 token 字段无效。"),
        [DailyIssueCodes.TokenFieldsMissing] = ("Some token fields were not provided.", "部分 token 字段未提供。"),
        [DailyIssueCodes.TokenOverflow] = ("Accumulated values exceed the supported range.", "累计数值超出范围。"),
        [DailyIssueCodes.LegacyCoverageUnknown] = ("Legacy snapshot coverage is unknown; the full daily usage cannot be reconstructed.", "旧格式快照覆盖关系未知，无法还原全部今日用量。"),
        [DailyIssueCodes.CurrentStartConflict] = ("Current turn-start relationships conflict.", "当前回合启动关系冲突。"),
        [DailyIssueCodes.TurnTimeConflict] = ("Turn timing events conflict.", "回合时间事件冲突。"),
        [DailyIssueCodes.HistoricalClosureMissing] = ("Historical turn-closure evidence is missing; activity cannot be confirmed.", "历史回合闭合依据缺失，活动状态无法确认。"),
        [DailyIssueCodes.HistoricalStartMissing] = ("Historical turn-start evidence is missing; activity cannot be confirmed.", "历史回合启动依据缺失，活动状态无法确认。"),
        [DailyIssueCodes.DurationMismatch] = ("Reported turn duration disagrees with start and end timestamps.", "回合声明耗时与起止时间不一致。"),
        [DailyIssueCodes.CompletionReconciliation] = ("A completed turn snapshot is missing or does not reconcile.", "完成回合快照缺失或对账不一致。"),
        [DailyIssueCodes.LocalRootScope] = ("Local logs; root sessions only.", "本地日志，仅根会话。"),
        [DailyIssueCodes.NoTokens] = ("No verifiable token records for this day.", "暂无当日可核验 token 记录。"),
        [DailyIssueCodes.ObservedActive] = ("Observed {count} active turns started within the last 15 minutes.", "观察到 {count} 个最近 15 分钟启动的运行中回合。"),
        [DailyIssueCodes.EstimatedTps] = ("Average TPS is estimated after excluding TTFT and observed tool waits.", "平均 TPS 为扣除首 token 与工具等待后的估算。"),
        [DailyIssueCodes.TimingCoveragePartial] = ("Timing coverage: {included} included and {excluded} excluded completed turns.", "耗时覆盖：纳入 {included} 个，排除 {excluded} 个完成回合。"),
        [DailyIssueCodes.TimingUnavailable] = ("No completed turn has eligible timing evidence.", "暂无具备有效耗时依据的完成回合。"),
        [DailyIssueCodes.MultipleWeekly] = ("Multiple independent weekly windows were reported; they are not combined.", "日志报告多个独立周额度窗口，未合并显示。"),
        [DailyIssueCodes.SourceNotFound] = ("No sessions directory was found in the selected Codex home.", "在所选 Codex 目录中未找到 sessions 日志目录。"),
        [DailyIssueCodes.SourceLoading] = ("Reading local statistics; more source data remains.", "正在读取本地统计，仍有待处理数据。"),
        [DailyIssueCodes.SourceOversized] = ("Some statistics or unknown log lines exceed the {limitMiB} MiB read limit.", "部分统计或未知日志行超过 {limitMiB} MiB 读取上限。"),
        [DailyIssueCodes.SourceUnreadable] = ("Some logs cannot be read or are incomplete; showing the readable scope.", "部分日志不可读取或格式不完整，显示当前可读取范围。"),
        [DailyIssueCodes.CoverageLost] = ("Daily coverage cannot be confirmed after truncation, replacement or deletion; showing the verifiable subtotal.", "日志截断、替换或删除后，当日覆盖无法确认；仅显示可核验小计。"),
        [DailyIssueCodes.RefreshFailed] = ("The last refresh failed; any retained snapshot keeps its original date and time.", "最近一次刷新失败；保留快照仍使用原日期与时间。"),
        [DailyIssueCodes.StaleDay] = ("The retained snapshot belongs to {day}, not the current day.", "保留的快照属于 {day}，并非今日数据。"),
        [DailyIssueCodes.WeeklyExpired] = ("The weekly log snapshot has expired.", "周额度日志快照已过期。"),
        [DailyIssueCodes.ActivityObservationExpired] = ("The activity observation is too old to describe current running turns.", "活动记录已过期，不能据此判断当前运行中回合。"),
        [DailyIssueCodes.SnapshotTimeFuture] = ("The snapshot timestamp is in the future; current activity and quota are unavailable.", "快照时间超前，当前活动与额度不可用。"),
        [DailyIssueCodes.WeeklyObservationFuture] = ("The weekly observation is dated in the future and cannot be shown as valid.", "周额度记录时间超前，不能显示为有效快照。")
    };
}
