using System.Text;
using System.Text.RegularExpressions;

namespace JableLens.Core.Text;

/// <summary>番号相关的小工具。</summary>
public static partial class VideoCode
{
    /// <summary>只留字母数字并转大写：「mfyd-202」→「MFYD202」。</summary>
    public static string Compact(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
            if (char.IsAsciiLetterOrDigit(c)) sb.Append(char.ToUpperInvariant(c));
        return sb.ToString();
    }

    /// <summary>文本里是否含有该番号；数字后面紧跟数字不算（MFYD-2021 不匹配 MFYD-202）。</summary>
    public static bool AppearsIn(string code, string text)
    {
        var needle = Compact(code);
        var hay = Compact(text);
        if (needle.Length == 0) return false;
        for (var i = hay.IndexOf(needle, StringComparison.Ordinal); i >= 0; i = hay.IndexOf(needle, i + 1, StringComparison.Ordinal))
        {
            var end = i + needle.Length;
            if (end >= hay.Length || !char.IsAsciiDigit(hay[end])) return true;
        }
        return false;
    }

    [GeneratedRegex(@"^\s*([A-Za-z]+)-?(\d+)\s*$")]
    private static partial Regex PrefixNumber();

    /// <summary>
    /// 番号 → DMM content_id 的候选，按可能性排序：新片「前缀+5位补零」，旧片「不补零」。
    /// 推不出来（形如 FC2、带数字前缀的厂牌）返回空。
    /// </summary>
    public static IReadOnlyList<string> DmmContentIdCandidates(string code)
    {
        var m = PrefixNumber().Match(code);
        if (!m.Success) return [];
        var prefix = m.Groups[1].Value.ToLowerInvariant();
        var num = int.Parse(m.Groups[2].Value);
        var padded = $"{prefix}{num:D5}";
        var plain = prefix + m.Groups[2].Value;
        return padded == plain ? [padded] : [padded, plain];
    }
}
