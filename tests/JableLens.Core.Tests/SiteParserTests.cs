using JableLens.Core.Models;
using JableLens.Core.Sources.Jable;
using static JableLens.Core.Tests.ParserTests;

namespace JableLens.Core.Tests;

/// <summary>页面样本是 2026-10-24 用 WebView2 读到的渲染后 HTML。</summary>
public class SiteParserTests
{
    [Fact]
    public void JableList_ParsesCards()
    {
        var html = Fixture("jable_latest.html");
        var cards = JableParser.ParseList(html);
        Assert.Equal(24, cards.Count);
        var c = cards[0];
        Assert.Equal("https://jable.tv/videos/cjod-540/", c.PageUrl);
        Assert.Equal("CJOD-540", c.Code);
        Assert.StartsWith("CJOD-540 【待望】", c.Title);
        Assert.EndsWith("320x180/1.jpg", c.CoverUrl);
        Assert.EndsWith("_preview.mp4", c.PreviewUrl);
        Assert.Equal("1:58:00", c.Duration);
        Assert.True(JableParser.HasNextPage(html, 1));
    }

    [Fact]
    public void JableSearch_SingleResult_NoNextPage()
    {
        var html = Fixture("jable_search.html");
        var c = Assert.Single(JableParser.ParseList(html));
        Assert.Equal("MFYD-202", c.Code);
        Assert.False(JableParser.HasNextPage(html, 1));
        Assert.True(JableParser.HasNextPage(Fixture("jable_search2.html"), 1));
    }

    [Fact]
    public void JableVideo_ParsesDetail()
    {
        var d = JableParser.ParseVideo(Fixture("jable_video.html"), "https://jable.tv/videos/mfyd-202/");
        Assert.Equal("MFYD-202", d.Code);
        Assert.StartsWith("MFYD-202 元美人", d.Title);
        Assert.Equal(new DateOnly(2026, 10, 15), d.ReleaseDate);
        var p = Assert.Single(d.Performers);
        Assert.Equal("和香なつき", p.Name);
        Assert.Contains("/models/", p.PageUrl);
        Assert.Contains(d.Tags, t => t.Name == "中出");
        Assert.EndsWith(".m3u8", d.StreamUrl);
        Assert.EndsWith("preview.jpg", d.CoverUrl);
        Assert.NotEmpty(d.Related);
        Assert.DoesNotContain(d.Related, r => r.PageUrl == d.PageUrl);
    }

    [Fact]
    public void JableModelPage_TitleAndCards()
    {
        var html = Fixture("jable_model.html");
        Assert.Equal("和香なつき", JableParser.ParseListTitle(html));
        Assert.Equal(18, JableParser.ParseList(html).Count);
    }

    [Fact]
    public void JableCategoriesAndModels()
    {
        var cats = JableParser.ParseCategories(Fixture("jable_cats.html"));
        Assert.Contains(cats, c => c.Name == "主奴調教" && c.VideoCount == 5479);
        var groups = JableParser.ParseTagGroups(Fixture("jable_cats.html"));
        Assert.Equal("衣著", groups[0].Name);
        Assert.DoesNotContain(groups, g => g.Name == "選片");
        Assert.Contains(groups[0].Tags, t => t.Name == "黑絲" && t.PageUrl == "https://jable.tv/tags/black-pantyhose/");
        Assert.True(groups.Sum(g => g.Tags.Count) > 80);
        var models = JableParser.ParseModels(Fixture("jable_models.html"));
        Assert.Contains(models, m => m.Name == "仲間あずみ" && m.VideoCount == 1);
    }


    [Theory]
    [InlineData("[中字]PRED-816 标题", "https://jable.tv/videos/pred-816-c/", "PRED-816")]
    [InlineData("没有番号的标题", "https://jable.tv/videos/abc-123/", "ABC-123")]
    public void JableCodeFrom(string title, string url, string expected) =>
        Assert.Equal(expected, JableParser.CodeFrom(title, url));

    [Fact]
    public void JableUrls_Paging()
    {
        Assert.Equal("https://jable.tv/latest-updates/", JableUrls.Latest());
        Assert.Equal("https://jable.tv/hot/3/", JableUrls.Hot(3));
        Assert.Equal("https://jable.tv/tags/creampie/2/", JableUrls.WithPage("https://jable.tv/tags/creampie/", 2));
        Assert.Equal("https://jable.tv/tags/creampie/5/", JableUrls.WithPage("https://jable.tv/tags/creampie/2/", 5));
        Assert.Equal("https://jable.tv/videos/mfyd-202/", JableUrls.Video("MFYD-202"));
    }


    [Fact]
    public void JableHiResCover()
    {
        Assert.Equal("https://assets-cdn.jable.tv/contents/videos_screenshots/62000/62857/preview.jpg",
            JableUrls.HiResCover("https://assets-cdn.jable.tv/contents/videos_screenshots/62000/62857/320x180/1.jpg"));
        Assert.Equal("https://cdn.example.com/x/cover-t.jpg", JableUrls.HiResCover("https://cdn.example.com/x/cover-t.jpg")); // 别的站的地址原样返回
    }
}
