using System.Collections.Frozen;
using System.Reflection;

namespace JableLens.Core.Text;

/// <summary>
/// 简繁转换，逐字转换（不处理词组，用于人名、标签足够）。
/// 对照表是从 Windows 的 LCMapStringEx 导出的（zh-t2s.txt / zh-s2t.txt，每两个字一组：原字、目标字），
/// 内嵌在程序里，Windows 和安卓结果一致。
/// </summary>
public static class ChineseConverter
{
    private static readonly Lazy<FrozenDictionary<char, char>> T2S = new(() => Load("zh-t2s.txt"));
    private static readonly Lazy<FrozenDictionary<char, char>> S2T = new(() => Load("zh-s2t.txt"));

    public static string ToSimplified(string s) => Map(s, T2S.Value);

    public static string ToTraditional(string s) => Map(s, S2T.Value);

    /// <summary>原文 + 简体 + 繁体，去重。</summary>
    public static IReadOnlyList<string> Variants(string s) =>
        new[] { s, ToSimplified(s), ToTraditional(s) }.Distinct().ToArray();

    private static string Map(string s, FrozenDictionary<char, char> table)
    {
        if (string.IsNullOrEmpty(s)) return s;
        return string.Create(s.Length, (s, table), static (span, st) =>
        {
            for (var i = 0; i < span.Length; i++)
                span[i] = st.table.TryGetValue(st.s[i], out var c) ? c : st.s[i];
        });
    }

    private static FrozenDictionary<char, char> Load(string name)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("JableLens.Core.Text." + name)!;
        using var reader = new StreamReader(stream);
        var text = reader.ReadToEnd();
        var map = new Dictionary<char, char>(text.Length / 2);
        for (var i = 0; i + 1 < text.Length; i += 2) map[text[i]] = text[i + 1];
        return map.ToFrozenDictionary();
    }
}
