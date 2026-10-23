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
Test("copy includes exact total without adding cached or reasoning subsets", () =>
{
    var text = DailyFormatting.CopyText(Snapshot(120_989_545, 639_259), false);
    if (!text.Split('\n').Any(line => line.TrimEnd('\r') == "Total tokens: 121,628,804"))
        throw new Exception("exact total line is missing or double-counted subsets");
});
Test("copy preserves unknown total when one component is missing", () =>
{
    var text = DailyFormatting.CopyText(Snapshot(null, 639_259), false);
    if (!text.Contains("Total tokens: 暂无数据", StringComparison.Ordinal))
        throw new Exception("unknown total must not become partial or zero");
});
Test("copy preserves unknown total on overflow", () =>
{
    var text = DailyFormatting.CopyText(Snapshot(long.MaxValue, 1), true);
    if (!text.Contains("Total tokens: 暂无数据", StringComparison.Ordinal))
        throw new Exception("overflow must not wrap or become a count");
});
Test("copy preserves known partial totals and an explicit legacy incompleteness reason", () =>
{
    const string reason = "旧格式统计无法还原今日请求（synthetic coverage fixture）";
    var snapshot = Snapshot(100, 40) with
    {
        CachedInputTokens = 60, ReasoningTokens = 10, IsPartial = true, Status = reason
    };
    var text = DailyFormatting.CopyText(snapshot, false);
    foreach (var line in new[] { "Total input: 100", "Total output: 40", "Total tokens: 140",
        "Cached input: 60", "Reasoning output: 10", "Status: " + reason + " (partial data)" })
    {
        if (!text.Split('\n').Any(actual => actual.TrimEnd('\r') == line))
            throw new Exception("copy omitted known subtotal or partial reason: " + line);
    }
});
Console.WriteLine($"{checks - failures}/{checks} passed");
return failures == 0 ? 0 : 1;
