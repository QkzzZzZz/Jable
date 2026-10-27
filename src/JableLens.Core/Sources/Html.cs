using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;

namespace JableLens.Core.Sources;

internal static partial class Html
{
    private static readonly HtmlParser Parser = new();

    public static IDocument Parse(string html) => Parser.ParseDocument(html);

    public static string? Text(this IElement? e)
    {
        var t = e?.TextContent;
        if (t is null) return null;
        t = Spaces().Replace(t, " ").Trim();
        return t.Length == 0 ? null : t;
    }

    public static string? Attr(this IElement? e, params string[] names)
    {
        if (e is null) return null;
        foreach (var n in names)
        {
            var v = e.GetAttribute(n);
            if (!string.IsNullOrWhiteSpace(v) && !v.StartsWith("data:", StringComparison.Ordinal)) return v.Trim();
        }
        return null;
    }

    /// <summary>「1 680 部影片」「18 部影片」→ 数字。</summary>
    public static int? Count(string? s)
    {
        if (s is null) return null;
        var digits = new string(s.Where(char.IsAsciiDigit).ToArray());
        return int.TryParse(digits, out var n) ? n : null;
    }

    public static string Absolute(string baseUrl, string href) =>
        Uri.TryCreate(new Uri(baseUrl), href, out var u) ? u.ToString() : href;

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();
}
