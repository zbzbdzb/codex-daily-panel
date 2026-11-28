using System.Reflection;
using CodexUsage.Daily;
using CodexUsage.DailyPanel;

var failures = 0;
var checks = 0;
void Test(string name, Action body)
{
    checks++;
    try { body(); Console.WriteLine("PASS " + name); }
    catch (Exception error) { failures++; Console.WriteLine("FAIL " + name + ": " + error.Message); }
}
void Equal<T>(T expected, T actual)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new Exception($"expected {expected}, got {actual}");
}
long? Total(long? input, long? output)
{
    var method = typeof(DailyFormatting).GetMethod("TotalTokens", BindingFlags.Static | BindingFlags.NonPublic)
        ?? throw new Exception("TotalTokens helper is missing");
    return (long?)method.Invoke(null, [input, output]);
}
DailySnapshot Snapshot(long? input, long? output) => new(new DateOnly(2026, 10, 21),
    DateTimeOffset.Parse("2026-10-21T05:00:00Z"), input, 110_000_000, output, 300_000,
    100, 90, 27, 2, 3, 0, 2, false, "Ready", null);
string Copy(DailySnapshot snapshot, LanguagePreference language)
{
    var view = new DailyPresentationState { Snapshot = snapshot }.Project(snapshot.UpdatedAt, TimeZoneInfo.Utc);
    return DailyPresentationFormatting.Copy(view, new DailyStrings(language), TimeZoneInfo.Utc, demo: false);
}
void HasLine(string text, string expected)
{
    if (!text.Split(Environment.NewLine).Contains(expected, StringComparer.Ordinal))
        throw new Exception("copy omitted expected line: " + expected);
}

foreach (var (input, output, expected, name) in new (long?, long?, long?, string)[]
{
    (120_989_545, 639_259, 121_628_804, "adds input and output"),
    (0, 0, 0, "keeps observed zero"),
    (long.MaxValue, 0, long.MaxValue, "accepts maximum representable total"),
    (long.MaxValue - 1, 1, long.MaxValue, "accepts boundary addition"),
    (null, 1, null, "preserves unknown input"),
    (1, null, null, "preserves unknown output"),
    (-1, 1, null, "rejects negative input"),
    (1, -1, null, "rejects negative output"),
    (long.MaxValue, 1, null, "rejects overflow"),
    (1, long.MaxValue, null, "rejects overflow in reverse order")
}) Test(name, () => Equal(expected, Total(input, output)));

Test("compact total rounds daily fixture to 121.63M", () =>
    Equal("121.63M", DailyFormatting.Tokens(Total(120_989_545, 639_259))));
const string legacyReason = "旧格式统计无法还原今日请求（synthetic coverage fixture）";
var legacyPartial = Snapshot(100, 40) with
{
    CachedInputTokens = 60, ReasoningTokens = 10, IsPartial = true, Status = legacyReason
};
foreach (var language in new[] { LanguagePreference.EnUS, LanguagePreference.ZhCN })
{
    var chinese = language == LanguagePreference.ZhCN;
    var total = chinese ? "总 token: " : "Total tokens: ";
    var input = chinese ? "总输入: " : "Total input: ";
    var output = chinese ? "总输出: " : "Total output: ";
    var cached = chinese ? "缓存输入: " : "Cached input: ";
    var reasoning = chinese ? "推理输出: " : "Reasoning output: ";
    var unavailable = chinese ? "不可用" : "Unavailable";
    var partial = chinese ? "状态: 统计不完整" : "Status: Partial";
    var limitReason = chinese ? "最多读取 32 个日志来源，仅显示可核验小计。"
        : "At most 32 log sources are read. Showing the verifiable subtotal.";
    var unreadableReason = chinese ? "部分日志不可读取或格式不完整，显示当前可读取范围。"
        : "Some logs cannot be read or are incomplete; showing the readable scope.";

    Test($"presentation copy preserves exact integers and token subsets ({language})", () =>
    {
        var text = Copy(Snapshot(120_989_545, 639_259), language);
        HasLine(text, total + "121,628,804");
        HasLine(text, cached + "110,000,000");
        HasLine(text, reasoning + "300,000");
    });
    Test($"presentation copy preserves an unknown component ({language})", () =>
    {
        var text = Copy(Snapshot(null, 639_259), language);
        HasLine(text, input + unavailable);
        HasLine(text, output + "639,259");
        HasLine(text, total + unavailable);
    });
    Test($"presentation copy checks the integer addition boundary ({language})", () =>
    {
        var maximum = "9,223,372,036,854,775,807";
        HasLine(Copy(Snapshot(long.MaxValue - 1, 1), language), total + maximum);
        var overflow = Copy(Snapshot(long.MaxValue, 1), language);
        HasLine(overflow, input + maximum);
        HasLine(overflow, output + "1");
        HasLine(overflow, total + unavailable);
    });
    Test($"presentation copy preserves partial subtotal and original legacy reason ({language})", () =>
    {
        var text = Copy(legacyPartial, language);
        foreach (var line in new[] { input + "100", output + "40", total + "140", cached + "60", reasoning + "10", partial })
            HasLine(text, line);
        HasLine(text, (chinese ? "旧版诊断（原文）：" : "Legacy diagnostic (original text): ") + legacyReason);
    });
    Test($"presentation copy distinguishes the source cap from a read failure ({language})", () =>
    {
        var limit = DailyIssue.With("source.limit_reached", ("limit", "32"));
        var capped = Snapshot(3_200, 800) with { IsPartial = true, SourceCount = 32, Issues = [limit] };
        var text = Copy(capped, language);
        HasLine(text, total + "4,000");
        HasLine(text, partial);
        HasLine(text, limitReason);
        Equal(false, text.Contains(unreadableReason, StringComparison.Ordinal));

        var locked = capped with { InputTokens = 3_100, OutputTokens = 775, Issues = [limit, new(DailyIssueCodes.SourceUnreadable)] };
        var combined = Copy(locked, language);
        HasLine(combined, total + "3,875");
        HasLine(combined, partial);
        HasLine(combined, limitReason);
        HasLine(combined, unreadableReason);
    });
}
Test("legacy copy compatibility still preserves original partial status", () =>
    HasLine(DailyFormatting.CopyText(legacyPartial, false), "Status: " + legacyReason + " (partial data)"));
Console.WriteLine($"{checks - failures}/{checks} passed");
return failures == 0 ? 0 : 1;
