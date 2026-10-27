using JableLens.Core.Library;
using JableLens.Core.Models;

namespace JableLens.Core.Tests;

public class RecommenderTests
{
    private static VideoCard Card(string code) => new(VideoSource.Jable, $"https://jable.tv/videos/{code.ToLowerInvariant()}/", code, code, null, null, null);

    private static readonly TagLink Tag = new("巨乳", "https://jable.tv/tags/big-tits/");

    private static LibraryData Data()
    {
        var now = DateTimeOffset.Now;
        var d = new LibraryData();
        foreach (var code in new[] { "AAA-001", "AAA-002" })
        {
            var c = Card(code);
            d.History[c.PageUrl] = new HistoryEntry { Card = c, Position = 3000, Duration = 3600, UpdatedAt = now };
        }
        d.Profiles[Card("AAA-001").PageUrl] = new WorkProfile(Card("AAA-001").PageUrl, [Tag], [], [Card("BBB-001"), Card("AAA-002"), Card("CCC-001")], now);
        d.Profiles[Card("AAA-002").PageUrl] = new WorkProfile(Card("AAA-002").PageUrl, [Tag], [], [Card("CCC-001"), Card("DDD-001")], now);
        return d;
    }

    [Fact]
    public void RecommendedByMoreSeedsRanksFirst_AndWatchedIsExcluded()
    {
        var d = Data();
        var seeds = Recommender.Seeds(d, DateTimeOffset.Now);
        var list = Recommender.Rank(d, seeds, []);
        Assert.Equal("CCC-001", list[0].Code);
        Assert.DoesNotContain(list, c => c.Code == "AAA-002");
        Assert.Equal(3, list.Count);
    }

    [Fact]
    public void TasteKeepsTagsSeenInSeveralWorks()
    {
        var d = Data();
        var taste = Recommender.BuildTaste(d, Recommender.Seeds(d, DateTimeOffset.Now));
        Assert.Equal(Tag.PageUrl, Assert.Single(taste.Tags).Tag.PageUrl);
    }

    [Fact]
    public void PortraitCountsTagsAndWatchTime()
    {
        var d = Data();
        var p = Portrait.Build(d, DateTimeOffset.Now);
        Assert.Equal("巨乳", p.Tags[0].Name);
        Assert.Equal(2, p.Tags[0].Count);
        Assert.Equal(6000, p.WatchedSeconds);
        Assert.Equal(0, p.Finished); // 3000/3600 不到 90%
        Assert.Equal(6000, p.Weeks[^1].Seconds);
        Assert.Equal(12, p.Weeks.Count);
    }

    [Fact]
    public void WeekStartsOnMonday() =>
        Assert.Equal(new DateOnly(2026, 10, 19), Portrait.WeekStart(new DateOnly(2026, 10, 25)));

    [Fact]
    public void OldHistoryWeighsLessThanFavorites()
    {
        var d = Data();
        foreach (var h in d.History.Values) h.UpdatedAt = DateTimeOffset.Now.AddDays(-90);
        d.Favorites.Add(Card("EEE-001"));
        var seeds = Recommender.Seeds(d, DateTimeOffset.Now);
        Assert.Equal(Card("EEE-001").PageUrl, seeds[0].PageUrl);
    }
}
