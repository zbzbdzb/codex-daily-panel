using System.Text;
using System.Text.Json;
using CodexUsage.DailyPanel;

internal static class EnvelopeValidatorTests
{
    public static void Run()
    {
        string[] valid = [
            "{\"type\":\"response_item\"}",
            "{\"payload\":{\"type\":\"token_usage_record\",\"text\":\"compaction summary\"},\"type\":\"compacted\"}",
            " { \"payload\": [null,true,false,{},[],0,-0,12,-13.45,1e2,2E-3,3e+4], \"type\":\"response_item\" }\r",
            "{\"type\":\"turn_context\",\"payload\":\"汉字😀\\uD83D\\uDE00\\n\\t\\\\\\\"\\/\\b\\f\\r\"}",
            "{\"ty\\u0070e\":\"response_item\",\"\\u0061\":1,\"汉字😀\":2}",
            "{\"type\":\"response_item\",\"payload\":{\"type\":\"token_usage_record\",\"duplicate\":1,\"duplicate\":2}}",
            "{\"type\":\"response_item\",\"payload\":" + new string('[', 63) + "null" + new string(']', 63) + "}"
        ];
        string[] invalid = [
            "", "{}", "[]", "\"response_item\"", "null", "{\"type\":\"unknown\"}",
            "{\"type\":\"response_item\",\"type\":\"response_item\"}",
            "{\"type\":\"response_item\",\"a\":1,\"\\u0061\":2}",
            "{\"type\":\"response_item\",\"payload\":truex}",
            "{\"type\":\"response_item\",\"payload\":+1}",
            "{\"type\":\"response_item\",\"payload\":01}",
            "{\"type\":\"response_item\",\"payload\":-}",
            "{\"type\":\"response_item\",\"payload\":1.}",
            "{\"type\":\"response_item\",\"payload\":1e}",
            "{\"type\":\"response_item\",\"payload\":1e+}",
            "{\"type\":\"response_item\",\"payload\":tru}",
            "{\"type\":\"response_item\",\"payload\":nUll}",
            "{\"type\":\"response_item\",\"payload\":\"\\uD800\"}",
            "{\"type\":\"response_item\",\"payload\":\"\\uDC00\"}",
            "{\"type\":\"response_item\",\"payload\":\"\\uD800\\n\"}",
            "{\"type\":\"response_item\",\"payload\":\"\\u12zz\"}",
            "{\"type\":\"response_item\",\"payload\":\"\t\"}",
            "{\"type\":\"response_item\",}",
            "{\"type\":\"response_item\",\"payload\":[1,]}",
            "{\"type\":\"response_item\",\"payload\":[,1]}",
            "{\"type\":\"response_item\",\"payload\":{1:2}}",
            "{\"type\":\"response_item\"}{\"type\":\"response_item\"}",
            "{\"type\":\"response_item\",\"payload\":{}",
            "{\"type\":\"response_item\",\"payload\":" + new string('[', 64) + "null" + new string(']', 64) + "}",
            "{\"type\":\"response_item\",\"" + new string('a', 257) + "\":0}",
            "{\"type\":\"response_item\"," + string.Join(',', Enumerable.Range(0, 128).Select(i => $"\"p{i}\":0")) + "}"
        ];
        foreach (string json in valid)
        {
            using var document = JsonDocument.Parse(json);
            CheckAllBoundaries(Encoding.UTF8.GetBytes(json), true);
        }
        foreach (string json in invalid) CheckAllBoundaries(Encoding.UTF8.GetBytes(json), false);
        foreach (byte[] invalidUtf8 in new byte[][] { [0xc0, 0xaf], [0xed, 0xa0, 0x80], [0xf4, 0x90, 0x80, 0x80], [0xe0, 0x80, 0x80], [0x80], [0xc2] })
        {
            byte[] bytes = Encoding.UTF8.GetBytes("{\"type\":\"response_item\",\"payload\":\"").Concat(invalidUtf8).Concat(Encoding.UTF8.GetBytes("\"}")).ToArray();
            CheckAllBoundaries(bytes, false);
        }
        Console.WriteLine($"PASS: bounded envelope grammar and UTF8 across every split ({valid.Length + invalid.Length + 6} cases)");
    }

    private static void CheckAllBoundaries(byte[] bytes, bool expected)
    {
        for (int split = 0; split <= bytes.Length; split++)
        {
            var validator = new OversizedEnvelopeValidator();
            validator.Append(bytes.AsSpan(0, split));
            validator.Append(bytes.AsSpan(split));
            if (validator.IsKnownNonStatistics() != expected)
                throw new InvalidOperationException($"FAIL: envelope grammar at split {split} / {bytes.Length}, expected {expected}");
        }
    }
}
