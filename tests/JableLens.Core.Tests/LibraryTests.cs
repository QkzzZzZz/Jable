using System.Net;
using System.Text;
using JableLens.Core.Library;
using JableLens.Core.Models;
using JableLens.Core.Sources.Netflav;

namespace JableLens.Core.Tests;

public class LibraryTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "jablelens-test-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
    }

    private sealed class FixtureHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(ParserTests.Fixture("nf_all.html"), Encoding.UTF8, "text/html"),
            });
    }

    private static VideoCard Card(string code) =>
        new(VideoSource.Jable, $"https://jable.tv/videos/{code.ToLowerInvariant()}/", code, code, null, null, null);

    [Fact]
    public async Task Progress_Favorites_Offsets_Persist()
    {
        var path = Path.Combine(_dir, "library.json");
        var lib = await UserLibrary.LoadAsync(path);
        lib.RecordProgress(Card("MFYD-202"), 600, 9600);
        Assert.True(lib.ToggleFavorite(Card("MFYD-202")));
        lib.SetSubtitleOffset("mfyd-202", 12.5);
        await lib.FlushAsync();

        var again = await UserLibrary.LoadAsync(path);
        var h = again.GetHistory("https://jable.tv/videos/mfyd-202/")!;
        Assert.True(h.IsUnfinished);
        Assert.Equal(600, h.Position);
        Assert.True(again.IsFavorite("https://jable.tv/videos/mfyd-202/"));
        Assert.Equal(12.5, again.SubtitleOffset("MFYD-202"));
        Assert.False(again.ToggleFavorite(Card("MFYD-202")));
    }

    [Fact]
    public void HistoryEntry_FinishedRules()
    {
        Assert.False(new HistoryEntry { Card = Card("A-1"), Position = 5, Duration = 100 }.IsUnfinished);
        Assert.True(new HistoryEntry { Card = Card("A-1"), Position = 50, Duration = 100 }.IsUnfinished);
        Assert.False(new HistoryEntry { Card = Card("A-1"), Position = 99, Duration = 100 }.IsUnfinished);
    }

    [Fact]
    public async Task Checker_FindsOnlyUnknownCodes_AndOnlyOnce()
    {
        var lib = await UserLibrary.LoadAsync(Path.Combine(_dir, "library.json"));
        var netflav = new NetflavClient(new HttpClient(new FixtureHandler()));
        var page = NetflavParser.ParseActressPage(ParserTests.Fixture("nf_all.html"), 1);
        // 关注时假装还不知道最新的两部
        var known = page.Works.Items.Select(w => w.Code).Where(c => c is not "MFYD-202" and not "PBD-524");
        lib.ToggleFollow("和香なつき", null, known);

        var checker = new NewWorkChecker(netflav, lib);
        var found = await checker.CheckAllAsync();
        Assert.Equal(["MFYD-202", "PBD-524"], found.Select(f => f.Work.Code).Order());
        Assert.Equal(2, lib.Read(d => d.Follows[0].NewWorks.Count));
        Assert.NotNull(lib.Read(d => d.Follows[0].LastChecked));

        Assert.Empty(await checker.CheckAllAsync());
    }

    [Fact]
    public async Task Following_RecordsCurrentWorksAsKnown()
    {
        var lib = await UserLibrary.LoadAsync(Path.Combine(_dir, "library.json"));
        Assert.True(lib.ToggleFollow("X", null, ["MFYD-202"]));
        Assert.True(lib.IsFollowing("X"));
        Assert.Contains("MFYD202", lib.Read(d => d.Follows[0].KnownCodes));
        Assert.False(lib.ToggleFollow("X", null, []));
        Assert.False(lib.IsFollowing("X"));
    }
}
