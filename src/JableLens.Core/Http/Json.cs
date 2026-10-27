using System.Globalization;
using System.Text.Json.Nodes;

namespace JableLens.Core.Http;

/// <summary>宽松读取 JSON 字段：站点常把数字写成字符串、或字段缺失。</summary>
internal static class Json
{
    public static string? Str(this JsonNode? n, string key)
    {
        var v = n?[key];
        if (v is not JsonValue jv) return null;
        var s = jv.TryGetValue<string>(out var str) ? str : jv.ToJsonString();
        return string.IsNullOrWhiteSpace(s) ? null : s.Trim();
    }

    public static int? Int(this JsonNode? n, string key)
    {
        var v = n?[key];
        if (v is not JsonValue jv) return null;
        if (jv.TryGetValue<int>(out var i)) return i;
        if (jv.TryGetValue<double>(out var d)) return (int)d;
        if (jv.TryGetValue<string>(out var s))
        {
            var digits = new string(s.TakeWhile(char.IsAsciiDigit).ToArray());
            if (int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out i)) return i;
        }
        return null;
    }

    public static IReadOnlyList<string> Strings(this JsonNode? n, string key) =>
        n?[key] is JsonArray arr
            ? arr.Select(x => x is JsonValue v && v.TryGetValue<string>(out var s) ? s : null).OfType<string>().ToArray()
            : [];

    /// <summary>「2026-10-16」或 ISO 时间 → 日期（取字符串里的日期部分，不做时区换算）。</summary>
    public static DateOnly? Date(this JsonNode? n, string key)
    {
        var s = n.Str(key);
        return s is { Length: >= 10 } && DateOnly.TryParseExact(s[..10], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
            ? d
            : null;
    }
}
