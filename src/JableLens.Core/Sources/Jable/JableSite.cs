using System.Globalization;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using JableLens.Core.Http;
using JableLens.Core.Models;

namespace JableLens.Core.Sources.Jable;

/// <summary>Jable 网址。列表分页统一是 /栏目/页码/。</summary>
public static class JableUrls
{
    public const string Base = "https://jable.tv";
    public const string Referer = "https://jable.tv/";

    public static string Latest(int page = 1) => Paged("/latest-updates/", page);
    public static string Hot(int page = 1) => Paged("/hot/", page);
    public static string NewRelease(int page = 1) => Paged("/new-release/", page);
    public static string Search(string keyword, int page = 1) => Paged($"/search/{Uri.EscapeDataString(keyword.Trim())}/", page);
    public static string Categories => Base + "/categories/";
    public static string Models(int page = 1) => Paged("/models/", page);

    /// <summary>列表缩略图 …/62857/320x180/1.jpg → 高清大图 …/62857/preview.jpg（播放页的海报就是它）。</summary>
    public static string? HiResCover(string? coverUrl) =>
        coverUrl is null ? null : Regex.Replace(coverUrl, @"/\d+x\d+/\d+\.jpg$", "/preview.jpg");

    /// <summary>番号 → 播放页（Jable 的地址就是小写番号）。</summary>
    public static string Video(string code) => $"{Base}/videos/{code.Trim().ToLowerInvariant()}/";

    /// <summary>给任意列表地址（分类、标签、女优页）加页码。</summary>
    public static string WithPage(string listUrl, int page)
    {
        var u = new Uri(listUrl);
        var path = Regex.Replace(u.AbsolutePath, @"/\d+/$", "/");
        if (!path.EndsWith('/')) path += "/";
        return Paged(path, page);
    }

    private static string Paged(string path, int page) => page <= 1 ? Base + path : $"{Base}{path}{page}/";
}

/// <summary>Jable 页面解析（输入是 WebView2 拿到的完整 HTML）。</summary>
public static partial class JableParser
{
    public const string Source = "Jable";

    [GeneratedRegex(@"var\s+hlsUrl\s*=\s*'([^']+)'")]
    private static partial Regex HlsUrl();

    [GeneratedRegex(@"\d{4}-\d{2}-\d{2}")]
    private static partial Regex IsoDate();

    [GeneratedRegex(@"^([A-Za-z0-9]+-[A-Za-z0-9]+(?:-[A-Za-z0-9]+)?)\b")]
    private static partial Regex LeadingCode();

    [GeneratedRegex(@"^\s*(?:\[[^\]]*\]|【[^】]*】|\s)+")]
    private static partial Regex LeadingBrackets();

    public static IReadOnlyList<VideoCard> ParseList(string html)
    {
        var doc = Html.Parse(html);
        return Cards(doc.QuerySelectorAll(".video-img-box"));
    }

    /// <summary>列表页是否还有下一页（看分页里有没有比当前大的页码）。</summary>
    public static bool HasNextPage(string html, int currentPage)
    {
        var doc = Html.Parse(html);
        return doc.QuerySelectorAll(".pagination .page-link")
            .Select(a => Html.Count(a.Text()) ?? 0)
            .Any(n => n > currentPage);
    }

    public static VideoDetail ParseVideo(string html, string pageUrl)
    {
        var doc = Html.Parse(html);
        var title = doc.QuerySelector(".header-left h4").Text()
                    ?? doc.QuerySelector("meta[property='og:title']").Attr("content")
                    ?? throw new SourceException(Source, "播放页里找不到标题，页面结构可能变了");
        var performers = doc.QuerySelectorAll(".header-left .models a.model")
            .Select(a => new PerformerLink(
                a.QuerySelector("[data-original-title]").Attr("data-original-title")
                ?? a.QuerySelector("[title]").Attr("title") ?? a.Text() ?? "",
                a.Attr("href")))
            .Where(p => p.Name.Length > 0)
            .ToArray();
        var tags = doc.QuerySelectorAll("h5.tags a")
            .Select(a => new TagLink(a.Text() ?? "", a.Attr("href")))
            .Where(t => t.Name.Length > 0)
            .ToArray();
        var dateText = doc.QuerySelector(".header-right .inactive-color").Text();
        var date = dateText is not null && IsoDate().Match(dateText) is { Success: true } m
            && DateOnly.TryParseExact(m.Value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : (DateOnly?)null;
        var stream = HlsUrl().Match(html) is { Success: true } h ? h.Groups[1].Value : null;
        var cover = doc.QuerySelector("video#player").Attr("poster") ?? doc.QuerySelector("meta[property='og:image']").Attr("content");

        return new VideoDetail(VideoSource.Jable, pageUrl, CodeFrom(title, pageUrl), title, cover, date,
            performers, tags, stream, Cards(doc.QuerySelectorAll(".video-img-box")).Where(c => c.PageUrl != pageUrl).ToArray());
    }

    public static IReadOnlyList<CategoryLink> ParseCategories(string html)
    {
        var doc = Html.Parse(html);
        return doc.QuerySelectorAll(".video-img-box a[href*='/categories/']")
            .Select(a => new CategoryLink(
                a.QuerySelector("h4").Text() ?? "",
                a.Attr("href")!,
                a.QuerySelector("img").Attr("data-src", "src"),
                Html.Count(a.QuerySelector(".label").Text())))
            .Where(c => c.Name.Length > 0)
            .DistinctBy(c => c.Url)
            .ToArray();
    }

    /// <summary>
    /// 标签分组：每个页面的导航菜单里都有（.title-box h2 是组名，紧跟的 .row 里是 a.tag）。
    /// 没有标签的组（如「選片」）跳过。
    /// </summary>
    public static IReadOnlyList<TagGroup> ParseTagGroups(string html)
    {
        var doc = Html.Parse(html);
        return doc.QuerySelectorAll(".title-box")
            .Select(box => new TagGroup(
                box.QuerySelector("h2").Text() ?? "",
                (box.NextElementSibling?.QuerySelectorAll("a.tag") ?? Enumerable.Empty<IElement>())
                    .Select(a => new TagLink(a.Text() ?? "", a.Attr("href")))
                    .Where(t => t.Name.Length > 0 && t.PageUrl?.Contains("/tags/") == true)
                    .DistinctBy(t => t.PageUrl)
                    .ToArray()))
            .Where(g => g.Name.Length > 0 && g.Tags.Count > 0)
            .DistinctBy(g => g.Name)
            .ToArray();
    }

    public static IReadOnlyList<SitePerformer> ParseModels(string html)
    {
        var doc = Html.Parse(html);
        return doc.QuerySelectorAll(".horizontal-img-box a[href*='/models/']")
            .Select(a => new SitePerformer(a.QuerySelector(".title").Text() ?? "", a.Attr("href")!, Html.Count(a.QuerySelector(".detail span").Text())))
            .Where(p => p.Name.Length > 0)
            .ToArray();
    }

    /// <summary>女优页 / 搜索页 / 分类页顶部的标题（如演员名）。</summary>
    public static string? ParseListTitle(string html) => Html.Parse(html).QuerySelector(".content-header h2").Text();

    private static IReadOnlyList<VideoCard> Cards(IEnumerable<IElement> boxes) =>
        boxes.Select(ParseCard).OfType<VideoCard>().DistinctBy(c => c.PageUrl).ToArray();

    private static VideoCard? ParseCard(IElement box)
    {
        var link = box.QuerySelector(".detail .title a");
        var href = link.Attr("href");
        if (href is null || !href.Contains("/videos/")) return null;
        var title = link.Text() ?? "";
        var img = box.QuerySelector(".img-box img");
        return new VideoCard(VideoSource.Jable, href, CodeFrom(title, href), title,
            img.Attr("data-src", "src"), img.Attr("data-preview"), box.QuerySelector(".absolute-bottom-right .label").Text());
    }

    /// <summary>番号优先取标题开头，没有就取网址里的 /videos/xxx/。</summary>
    internal static string CodeFrom(string title, string url)
    {
        var t = LeadingBrackets().Replace(title, "");
        if (LeadingCode().Match(t) is { Success: true } m) return m.Groups[1].Value.ToUpperInvariant();
        return (new Uri(url).AbsolutePath.Trim('/').Split('/').LastOrDefault() ?? t).ToUpperInvariant();
    }
}
