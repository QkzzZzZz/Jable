using System.Windows;
using System.Windows.Controls;
using JableLens.App.Controls;
using JableLens.App.Services;
using JableLens.Core.Http;
using JableLens.Core.Sources.Jable;

namespace JableLens.App.Pages;

/// <summary>首页：Netflix 式——顶部整屏大图（本周热门），下面一行行横向翻页的分类（含本周热门 TOP 10，卡片左上角标名次）。</summary>
public sealed class HomePage : PageBase
{
    private readonly HeroCarousel _hero = new();
    private readonly StackPanel _rows = new();
    private readonly List<MediaRow> _rowList = [];

    public HomePage()
    {
        var root = new StackPanel();
        root.Children.Add(_hero);
        root.Children.Add(_rows);
        // 第一行压在大图的下沿上
        _hero.SizeChanged += (_, _) => _rows.Margin = new Thickness(0, _hero.Visibility == Visibility.Visible ? -_hero.Overlap : 76, 0, 56);
        var sv = new ScrollViewer { Content = root, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Focusable = false };
        SmoothScroll.SetIsEnabled(sv, true);
        Content = sv;

        var history = AppServices.Library.RecentHistory(unfinishedOnly: true);
        if (history.Count > 0)
            AddRow(new MediaRow("继续观看",
                _ => Task.FromResult<IReadOnlyList<CardItem>>(AppServices.Library.RecentHistory(true).Take(20).Select(h => TabbedGridPage.VideoItem(h.Card)).ToList()),
                () => Shell.Navigate(new HistoryPage())));

        // 有观看记录或片单才有得猜；一个都猜不出来时这一行自己隐藏
        if (Recommendations.HasSeeds)
            AddRow(new MediaRow("猜我喜欢",
                async ct => (await Recommendations.GuessAsync(30, ct)).Select(TabbedGridPage.VideoItem).ToList(),
                () => Shell.Navigate(new GuessPage())));

        AddRow(JableRow("最近更新", JableUrls.Latest));
        AddRow(new MediaRow("本周热门 TOP 10",
            async ct => (await AppServices.Sites.JableListAsync(JableUrls.Hot(), 1, ct)).Items.Take(10)
                .Select((v, i) => new CardItem
                {
                    Title = HeroCarousel.TitleOnly(v), Meta = v.Duration is { Length: > 0 } d ? $"{v.Code}  ·  {d}" : v.Code,
                    Code = v.Code, CoverUrl = Covers.HiRes(v) ?? v.CoverUrl, Duration = v.Duration,
                    PreviewUrl = v.PreviewUrl, Payload = v, Rank = i + 1,
                }).ToList(),
            null));

        var newWorks = AppServices.Library.Read(d => d.Follows.SelectMany(f => f.NewWorks.Select(w => CardItem.From(w, "新作"))).Take(20).ToList());
        if (newWorks.Count > 0)
            AddRow(new MediaRow("关注演员的新作", _ => Task.FromResult<IReadOnlyList<CardItem>>(newWorks), () => Shell.Navigate(new FollowsPage()), "PosterCardTemplate"));

        AddRow(JableRow("新片上市", JableUrls.NewRelease));
        foreach (var tag in AppServices.Library.Read(d => d.PinnedTags.ToList()))
            if (tag.PageUrl is { } url)
                AddRow(new MediaRow(tag.Name,
                    async ct => (await AppServices.Sites.JableListAsync(url, 1, ct)).Items.Select(TabbedGridPage.VideoItem).ToList(),
                    () => Shell.Navigate(new TagsPage(tag))));
        AddRow(JableRow("本周热门", JableUrls.Hot));

        Loaded += OnFirstLoaded;
    }

    public override string Title => "首页";

    public override bool FullBleed => true;

    private void AddRow(MediaRow row)
    {
        _rowList.Add(row);
        _rows.Children.Add(row);
    }

    private static MediaRow JableRow(string title, Func<int, string> url) =>
        new(title, async ct => (await AppServices.Sites.JableListAsync(url(1), 1, ct)).Items.Select(TabbedGridPage.VideoItem).ToList(),
            () => Shell.Navigate(new TabbedGridPage(title, [TabbedGridPage.JableTab(title, url)])));


    private void OnFirstLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnFirstLoaded;
        var ct = Life.Token;
        _ = LoadHeroAsync(ct);
        // 各行同时开始读（每个站一个浏览器，fetch 可以并发）
        foreach (var row in _rowList) _ = row.LoadAsync(ct);
    }

    private async Task LoadHeroAsync(CancellationToken ct)
    {
        try
        {
            var hot = await AppServices.Sites.JableListAsync(JableUrls.Hot(), 1, ct);
            _hero.SetItems(hot.Items.Take(6).ToList());
        }
        catch (OperationCanceledException) { }
        catch (SourceException)
        {
            _hero.Visibility = Visibility.Collapsed;
            _rows.Margin = new Thickness(0, 76, 0, 56);
        }
    }
}
