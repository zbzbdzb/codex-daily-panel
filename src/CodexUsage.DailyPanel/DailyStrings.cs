using System;
using System.Collections.Generic;
using System.Globalization;

namespace CodexUsage.DailyPanel;

public sealed class DailyStrings
{
    public LanguagePreference Language { get; }
    public CultureInfo Culture => CultureInfo.GetCultureInfo(Language == LanguagePreference.ZhCN ? "zh-CN" : "en-US");
    public DailyStrings(LanguagePreference preference, string? systemCulture = null)
    {
        Language = preference == LanguagePreference.System
            ? ((systemCulture ?? CultureInfo.CurrentUICulture.Name).StartsWith("zh", StringComparison.OrdinalIgnoreCase)
                ? LanguagePreference.ZhCN : LanguagePreference.EnUS) : preference;
    }
    public string this[string key] => Texts.TryGetValue(key, out var pair)
        ? Language == LanguagePreference.ZhCN ? pair.Zh : pair.En : key;
    public string Format(string key, params object?[] values) => string.Format(Culture, this[key], values);

    private static readonly IReadOnlyDictionary<string, (string En, string Zh)> Texts = new Dictionary<string, (string, string)>(StringComparer.Ordinal)
    {
        ["Title"]=("Codex Usage", "Codex 用量"), ["Today"]=("Today", "今日"),
        ["LocalTime"]=("Local time", "本地时间"), ["Input"]=("Total input", "总输入"),
        ["Output"]=("Total output", "总输出"), ["Total"]=("Total tokens", "总 token"),
        ["Speed"]=("Average speed · estimate", "平均速度 · 估算"), ["NetTime"]=("Net processing time", "净处理时间"),
        ["Weekly"]=("Weekly log snapshot", "周额度日志快照"), ["Remaining"]=("{0}% remaining", "剩余 {0}%"),
        ["Observed"]=("Observed: {0}", "记录时间：{0}"), ["Reset"]=("Resets: {0}", "重置时间：{0}"),
        ["NoWeekly"]=("No valid weekly snapshot", "暂无有效周额度快照"),
        ["NotLive"]=("Local log snapshot; live account allowance was not queried.", "来自本地日志；未查询实时账号额度。"),
        ["Details"]=("Breakdown and scope", "明细与统计范围"), ["Cache"]=("Cache hit rate", "缓存命中率"),
        ["CachedInput"]=("Cached input", "缓存输入"), ["Uncached"]=("Uncached input", "非缓存输入"),
        ["Reasoning"]=("Reasoning output", "推理输出"), ["ActiveTime"]=("Active time (interval union)", "活动时长（区间并集）"),
        ["Chats"]=("Chats", "会话"), ["Turns"]=("Turns", "回合"), ["ActiveTurns"]=("Active turns", "运行中回合"),
        ["Sources"]=("Sources", "日志来源"), ["Running"]=("{0} running", "{0} 个运行中"),
        ["Coverage"]=("TPS: {0} included / {1} excluded", "TPS：纳入 {0} / 排除 {1}"),
        ["Scope"]=("Local root sessions only; subagents and cloud tasks excluded.", "仅本机根会话；不计子代理与云任务。"),
        ["Subsets"]=("Input includes cached input; output includes reasoning. Do not add these subsets again.", "输入包含缓存输入，输出包含推理输出；请勿重复相加。"),
        ["TpsExplanation"]=("Estimated TPS uses output and net time from the same eligible completed turns. TTFT and observed tool waits are merged before subtraction.", "估算 TPS 使用同一组可核验完成回合的输出与净时间；首 token 等待和工具等待合并后扣除。"),
        ["TpsLimit"]=("Whole-turn TTFT is not necessarily visible-text latency. Remaining time may include reasoning, network and scheduling; this is not pure generation TPS.", "整轮首 token 不一定等于可见首字；剩余时间可能包含推理、网络与调度，并非纯生成 TPS。"),
        ["WaitUnion"]=("Excluded TTFT and tool wait union", "已扣除的首 token 与工具等待并集"),
        ["Copy"]=("Copy stats", "复制统计"), ["Copied"]=("Copied", "已复制"), ["CopyFailed"]=("Copy failed; try again", "复制失败，请重试"),
        ["Refresh"]=("Refresh statistics", "刷新统计"), ["Folder"]=("Open log folder", "打开日志目录"),
        ["RefreshPending"]=("Request another refresh; repeated requests are combined", "请求后续刷新；重复请求会合并"),
        ["LegacyDiagnostic"]=("Legacy diagnostic (original text): {0}", "旧版诊断（原文）：{0}"),
        ["Exit"]=("Exit panel", "退出面板"), ["Hide"]=("Hide to tray (Esc)", "隐藏至托盘（Esc）"),
        ["Open"]=("Open daily statistics", "打开今日统计"), ["Pause"]=("Pause automatic refresh", "暂停自动刷新"),
        ["Resume"]=("Resume automatic refresh", "恢复自动刷新"), ["Paused"]=("Paused", "已暂停"),
        ["Refreshing"]=("Refreshing", "正在读取"), ["Exiting"]=("Exiting", "正在退出"), ["Waiting"]=("Waiting", "等待数据"),
        ["Local"]=("Local", "本地"), ["Partial"]=("Partial", "统计不完整"), ["Stale"]=("Stale", "数据已过期"),
        ["NoData"]=("No data", "暂无数据"), ["Unavailable"]=("Unavailable", "不可用"), ["Demo"]=("Demo", "演示"),
        ["Updated"]=("Updated: {0}", "更新于：{0}"), ["NotUpdated"]=("Not updated yet", "尚未更新"),
        ["Retained"]=("Showing the last verified snapshot from {0}. These are not current statistics.", "显示 {0} 的上次可核验快照，并非当前统计。"),
        ["PausedNote"]=("Automatic refresh is paused. In-flight reads can finish; manual refresh still works.", "已暂停自动刷新；在途读取可完成，仍可手动刷新。"),
        ["RefreshFailed"]=("The last refresh failed. The dated snapshot is retained; retry when the source is available.", "最近一次刷新失败，保留带日期的原快照；来源可用后可重试。"),
        ["PartialNote"]=("Some source coverage is unavailable; only the verifiable subtotal is shown.", "部分来源覆盖无法确认，仅显示可核验小计。"),
        ["Settings"]=("Appearance and language", "外观与语言"), ["Theme"]=("Theme", "主题"), ["Language"]=("Language", "语言"),
        ["System"]=("System", "跟随系统"), ["Light"]=("Light", "浅色"), ["Dark"]=("Dark", "深色"),
        ["English"]=("English", "English"), ["Chinese"]=("简体中文", "简体中文"),
        ["SettingsProblem"]=("Preferences could not be read or saved. This session can continue with the selected defaults.", "偏好设置无法读取或保存；本次仍可使用当前选择或默认设置。"),
        ["FolderMissing"]=("The selected log folder does not exist yet.", "所选日志目录尚不存在。"),
        ["FolderFailed"]=("The log folder could not be opened.", "无法打开日志目录。"),
        ["DemoFolder"]=("Demo mode does not access real logs.", "演示模式不访问真实日志。"),
        ["Status"]=("Status", "状态"), ["SnapshotDate"]=("Snapshot date", "快照日期"),
        ["Seconds"]=("{0} seconds", "{0} 秒"), ["UnknownIssue"]=("Diagnostic: {0}", "诊断：{0}"),
        ["TrayTotal"]=("Daily total {0} tokens", "今日总 token {0}"), ["TraySnapshot"]=("Snapshot {0}: {1} tokens", "{0} 快照：{1} token"),
        ["CopyAccessible"]=("Copy exact statistics, date, scope and data quality", "复制精确统计、日期、范围与数据状态"),
        ["OperationFailed"]=("This operation could not be completed. Please refresh or restart the panel.", "这次操作未能完成，请刷新或重新启动面板。"),
        ["StartupFailed"]=("The panel could not start. Check the selected paths and required Windows Desktop Runtime, or use the self-contained package.", "面板无法启动。请检查所选路径和 Windows Desktop Runtime，或使用自包含版本。")
    };
}
