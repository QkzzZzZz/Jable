using System.Net;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using JableLens.Core.Http;
using JableLens.Core.Models;

namespace JableLens.Core.Sources.Netflav;

/// <summary>演员作品页解析结果。Profile 为 null 表示 Netflav 没收录该演员资料（正常情况，不是错误）。</summary>
public sealed record NetflavActressPage(Page<CodedWork> Works, PerformerProfile? Profile);

/// <summary>Netflav 响应解析（纯函数，不联网，方便单元测试）。</summary>
public static partial class NetflavParser
{
    public const string Source = "Netflav";

    [GeneratedRegex("""<script id="__NEXT_DATA__"[^>]*>(.*?)</script>""", RegexOptions.Singleline)]
    private static partial Regex NextData();

    /// <summary>解析 https://netflav.com/all?actress=名&amp;page=n 的 HTML。</summary>
    public static NetflavActressPage ParseActressPage(string html, int page)
    {
        var m = NextData().Match(html);
        if (!m.Success) throw new SourceException(Source, "演员页里找不到 __NEXT_DATA__，页面结构可能变了");
        JsonNode? all;
        try
        {
            all = JsonNode.Parse(WebUtility.HtmlDecode(m.Groups[1].Value))?["props"]?["initialState"]?["all"];
        }
        catch (System.Text.Json.JsonException e)
        {
            throw new SourceException(Source, "演员页 JSON 无法解析", e);
        }
        if (all is null) throw new SourceException(Source, "演员页 JSON 里没有 initialState.all");

        var works = (all["docs"] as JsonArray ?? []).Select(ParseWork).OfType<CodedWork>().ToArray();
        return new NetflavActressPage(
            new Page<CodedWork>(works, page, all.Int("pages"), all.Int("total")),
            ParseProfile(all["actress"]));
    }

    public static PerformerProfile? ParseProfile(JsonNode? a)
    {
        if (a is not JsonObject) return null;
        var name = a.Str("name") ?? a.Str("name_ja");
        if (name is null) return null;
        return new PerformerProfile(
            name, a.Str("name_ja"), a.Str("name_zh"), a.Date("birthday"),
            a.Int("height"), a.Str("cup"), a.Int("breast"), a.Int("waist"), a.Int("hip"), a.Int("videoCount"));
    }

    public static CodedWork? ParseWork(JsonNode? d)
    {
        var code = d.Str("code") ?? d.Str("uCode");
        if (code is null) return null;
        return new CodedWork(
            code,
            d.Str("title_zh") ?? d.Str("title") ?? d.Str("title_en") ?? code,
            d.Str("preview_hp") ?? d.Str("preview"),
            d.Date("sourceDate") ?? d.Date("videoDate"),
            JapaneseActors(d.Strings("actors")),
            ChineseTags(d.Strings("tags")));
    }

    /// <summary>从混合语言的 actors 数组里挑日文名：优先「jp:」前缀，没有就用无前缀的。</summary>
    public static IReadOnlyList<string> JapaneseActors(IReadOnlyList<string> actors)
    {
        var jp = actors.Where(a => a.StartsWith("jp:", StringComparison.Ordinal)).Select(a => a[3..]).ToList();
        if (jp.Count == 0)
            jp = actors.Where(a => !HasLangPrefix(a) && a.Any(c => c > 0x2E80)).ToList();
        return jp.Distinct().ToArray();
    }

    public static IReadOnlyList<string> ChineseTags(IReadOnlyList<string> tags)
    {
        var zh = tags.Where(t => t.StartsWith("zh:", StringComparison.Ordinal)).Select(t => t[3..]).Distinct().ToArray();
        return zh.Length > 0 ? zh : tags.Where(t => !HasLangPrefix(t)).Distinct().ToArray();
    }

    private static bool HasLangPrefix(string s) => s.Length > 3 && s[2] == ':' && char.IsAsciiLetterLower(s[0]);

    /// <summary>api98 接口的 { result: { docs, page, pages, total } }。</summary>
    public static (JsonArray Docs, int? Pages, int? Total) ParseApiResult(string json)
    {
        JsonNode? root;
        try { root = JsonNode.Parse(json); }
        catch (System.Text.Json.JsonException e) { throw new SourceException(Source, "接口返回的不是 JSON", e); }
        var result = root?["result"] ?? throw new SourceException(Source, "接口返回里没有 result");
        return (result["docs"] as JsonArray ?? [], result.Int("pages"), result.Int("total"));
    }

    public static PerformerSummary? ParseSummary(JsonNode? d)
    {
        var name = d.Str("name");
        if (name is null) return null;
        return new PerformerSummary(d.Str("_id") ?? name, name, d.Str("name_en"), d.Str("icon"), d.Str("type"), d.Int("videoCount") ?? 0);
    }
}
