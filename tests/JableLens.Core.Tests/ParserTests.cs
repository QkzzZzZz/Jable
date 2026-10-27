using JableLens.Core.Sources.Av01;
using JableLens.Core.Sources.Netflav;
using JableLens.Core.Sources.R18;
using JableLens.Core.Subtitles;

namespace JableLens.Core.Tests;

/// <summary>用 2026-10-23 抓到的真实响应做样本；站点改版后这些测试会先挂。</summary>
public class ParserTests
{
    internal static string Fixture(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    [Fact]
    public void NetflavActressPage_ParsesWorks_AndMissingProfile()
    {
        var page = NetflavParser.ParseActressPage(Fixture("nf_all.html"), 1);

        Assert.Equal(20, page.Works.Items.Count);
        Assert.Null(page.Profile); // 和香なつき 在 Netflav 没有资料对象
        var first = page.Works.Items[0];
        Assert.Equal("MFYD-202", first.Code);
        Assert.Equal(["和香なつき"], first.Actors);
        Assert.Equal(new DateOnly(2026, 10, 16), first.Date);
        Assert.Contains("蕩婦", first.Tags);
        Assert.DoesNotContain(first.Tags, t => t.StartsWith("zh:"));
    }

    [Fact]
    public void NetflavActressPage_WithoutNextData_GivesClearError()
    {
        var e = Assert.Throws<Http.SourceException>(() => NetflavParser.ParseActressPage("<html>挑战页</html>", 1));
        Assert.Contains("__NEXT_DATA__", e.Message);
    }

    [Fact]
    public void NetflavProfile_ParsesNumbersWrittenAsStrings()
    {
        var node = System.Text.Json.Nodes.JsonNode.Parse(
            """{"name":"测试","birthday":"1995-03-04T00:00:00.000Z","height":"158cm","cup":"E","breast":86,"waist":"58","hip":85,"videoCount":12}""");
        var p = NetflavParser.ParseProfile(node)!;
        Assert.Equal(new DateOnly(1995, 3, 4), p.Birthday);
        Assert.Equal(158, p.Height);
        Assert.Equal(58, p.Waist);
        Assert.Equal(31, p.AgeOn(new DateOnly(2026, 10, 23)));
        Assert.Equal(30, p.AgeOn(new DateOnly(2026, 3, 3)));
    }

    [Fact]
    public void NetflavActors_PicksJapaneseNames()
    {
        Assert.Equal(["森沢かな（飯岡かなこ）", "和香なつき"],
            NetflavParser.JapaneseActors(["jp:森沢かな（飯岡かなこ）", "jp:和香なつき", "和香なつき", "en:Waka natuki", "Waka natuki"]));
        Assert.Equal(["和香なつき"], NetflavParser.JapaneseActors(["和香なつき", "zh:和香なつき", "Waka Natsuki", "en:Waka Natsuki"]));
    }

    [Fact]
    public void NetflavDirectory_ParsesSummaries()
    {
        var (docs, _, _) = NetflavParser.ParseApiResult(Fixture("nf_dir.json"));
        var list = docs.Select(NetflavParser.ParseSummary).OfType<Models.PerformerSummary>().ToList();
        Assert.NotEmpty(list);
        Assert.All(list, a => Assert.False(string.IsNullOrEmpty(a.Name)));
        Assert.Contains(list, a => a.Name == "小野六花" && a.NameEn == "Rikka Ono");
    }

    [Fact]
    public void Av01Search_ParsesActressTranslations()
    {
        var v = Assert.Single(Av01Client.ParseSearch(Fixture("av01_search.json")));
        Assert.Equal("MFYD-202", v.Code);
        Assert.Equal("mfyd00202", v.DmmId);
        Assert.Equal(new DateOnly(2026, 10, 16), v.PublishedDate);
        var a = Assert.Single(v.Actresses);
        Assert.Equal("和香なつき", a.Name);
        Assert.Equal("和香夏树", a.Translations["cn"]);
        Assert.Equal("和香夏樹", a.Translations["tw"]);
    }

    [Fact]
    public void R18_ParsesDetail()
    {
        var d = R18Client.Parse(Fixture("r18.json"))!;
        Assert.Equal("mfyd00202", d.ContentId);
        Assert.Equal(new DateOnly(2026, 10, 16), d.ReleaseDate);
        Assert.Equal(158, d.RuntimeMinutes);
        Assert.Equal("和香なつき", Assert.Single(d.Actresses).NameKanji);
        Assert.Equal(11, d.Gallery.Count);
        Assert.StartsWith("https://pics.dmm.co.jp/", d.Gallery[0]);
        Assert.Contains("熟女", d.Categories);
    }

    [Fact]
    public void SubtitleCat_SearchAndTracks()
    {
        var hit = Assert.Single(SubtitleCatClient.ParseSearch(Fixture("sc_search.html"), "MFYD-202"));
        Assert.Equal("MFYD-202.zh-cn(by transub)", hit.Title);
        Assert.Empty(SubtitleCatClient.ParseSearch(Fixture("sc_search.html"), "MFYD-20"));

        var tracks = SubtitleCatClient.ParseTracks(Fixture("sc_page.html"), hit.Title);
        Assert.Equal(13, tracks.Count);
        var zh = tracks.Single(t => t.LanguageCode == "zh-CN");
        Assert.Equal("Chinese (Simplified)", zh.DisplayName);
        Assert.Equal("https://www.subtitlecat.com/subs/1735/MFYD-202.zh-cn%28by%20transub%29-zh-CN.srt", zh.Url.AbsoluteUri);
    }

    [Fact]
    public void SubtitleCat_DoesNotDoubleEncode()
    {
        Assert.Equal("https://www.subtitlecat.com/subs/1/A%20B.srt",
            SubtitleCatClient.ToAbsolute("/subs/1/A%20B.srt").AbsoluteUri);
    }
}
