namespace JableLens.Core.Text;

/// <summary>演员名比较用的工具。</summary>
public static class NameText
{
    private static readonly char[] AliasOpen = ['(', '（'];
    private static readonly char[] AliasClose = [')', '）'];

    /// <summary>「森沢かな（飯岡かなこ）」→ [森沢かな, 飯岡かなこ]。</summary>
    public static IReadOnlyList<string> SplitAliases(string name)
    {
        var parts = new List<string>();
        var open = name.IndexOfAny(AliasOpen);
        if (open < 0) return [name.Trim()];
        parts.Add(name[..open].Trim());
        var close = name.IndexOfAny(AliasClose, open);
        var inner = close > open ? name[(open + 1)..close] : name[(open + 1)..];
        parts.AddRange(inner.Split(['、', ',', '，', '/'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return parts.Where(p => p.Length > 0).Distinct().ToArray();
    }

    /// <summary>比较用的规范形：去空白、转简体、小写。</summary>
    public static string Normalize(string name) =>
        ChineseConverter.ToSimplified(new string(name.Where(c => !char.IsWhiteSpace(c) && c != '・' && c != '·').ToArray()))
            .ToLowerInvariant();

    public static bool SameName(string a, string b)
    {
        var na = SplitAliases(a).Select(Normalize).ToHashSet();
        return SplitAliases(b).Select(Normalize).Any(na.Contains);
    }

    /// <summary>编辑距离相似度 0..1（1 = 相同）。</summary>
    public static double Similarity(string a, string b)
    {
        a = Normalize(a);
        b = Normalize(b);
        if (a.Length == 0 && b.Length == 0) return 1;
        var d = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++) d[j] = j;
        for (var i = 1; i <= a.Length; i++)
        {
            var prev = d[0];
            d[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var tmp = d[j];
                d[j] = Math.Min(Math.Min(d[j] + 1, d[j - 1] + 1), prev + (a[i - 1] == b[j - 1] ? 0 : 1));
                prev = tmp;
            }
        }
        return 1.0 - (double)d[b.Length] / Math.Max(a.Length, b.Length);
    }
}
