using System.Windows;
using System.Windows.Controls;
using JableLens.App.Controls;
using JableLens.App.Services;
using JableLens.Core.Models;
using JableLens.Core.Sources.Jable;

namespace JableLens.App.Pages;

public sealed record GridTab(
    string Name,
    Func<int, CancellationToken, Task<PageResult<CardItem>>> Loader,
    string Template = "VideoCardTemplate",
    double MinItemWidth = 230,
    string EmptyText = "没有内容");

/// <summary>标题 + 一排标签 + 卡片网格。首页、搜索、分类等都用它。</summary>
public class TabbedGridPage : PageBase
{
    private readonly string _title;
    private readonly IReadOnlyList<GridTab> _tabs;
    protected readonly CardGrid Grid = new();
    protected readonly StackPanel HeaderPanel = new() { Margin = new Thickness(Ui.Gutter, 20, Ui.Gutter, 22) };
    private int _selected;

    public TabbedGridPage(string title, IReadOnlyList<GridTab> tabs, int selected = 0, UIElement? extraHeader = null)
    {
        _title = title;
        _tabs = tabs;
        _selected = Math.Clamp(selected, 0, Math.Max(0, tabs.Count - 1));

        // Netflix 类别页：大标题，右边一个下拉切换（「最近更新 ▾」）
        var titleRow = new WrapPanel();
        titleRow.Children.Add(Ui.Text(title, "H1"));
        if (tabs.Count > 1)
        {
            var dd = Ui.Dropdown(tabs.Select(t => t.Name).ToArray(), _selected, Select, style: "TitleDropdownBtn");
            dd.Margin = new Thickness(16, 8, 0, 0);
            dd.VerticalAlignment = VerticalAlignment.Center;
            titleRow.Children.Add(dd);
        }
        HeaderPanel.Children.Add(titleRow);
        if (extraHeader is not null) HeaderPanel.Children.Add(extraHeader);

        var dock = new DockPanel();
        DockPanel.SetDock(HeaderPanel, Dock.Top);
        dock.Children.Add(HeaderPanel);
        Grid.Padding = Ui.GridPadding;
        dock.Children.Add(Grid);
        Content = dock;

        Shell.Wire(Grid);
        Loaded += OnFirstLoaded;
    }

    public override string Title => _title;

    private void OnFirstLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnFirstLoaded;
        if (_tabs.Count > 0) Select(_selected);
    }

    protected void Select(int index)
    {
        _selected = index;
        var tab = _tabs[index];
        Grid.ItemTemplate = (DataTemplate)Application.Current.Resources[tab.Template];
        Grid.MinItemWidth = tab.MinItemWidth;
        Grid.RowGap = tab.Template is "PerformerCardTemplate" or "TermCardTemplate" ? new Thickness(0) : new Thickness(0, 0, 0, tab.Template == "CategoryCardTemplate" ? 6 : 26);
        Grid.EmptyText = tab.EmptyText;
        Grid.Loader = tab.Loader;
        Grid.Reload();
    }

    protected void ReloadCurrent() => Select(_selected);

    public override void OnClosed()
    {
        base.OnClosed();
        Grid.Cancel();
    }

    // ---------- 常用的标签 ----------

    public static CardItem VideoItem(VideoCard v)
    {
        var h = AppServices.Library.GetHistory(v.PageUrl);
        return CardItem.From(v, progress: h?.IsUnfinished == true ? h.ProgressRatio : 0);
    }

    public static GridTab JableTab(string name, Func<int, string> url, string template = "VideoCardTemplate", double minWidth = 230) =>
        new(name, async (p, ct) =>
        {
            var r = await AppServices.Sites.JableListAsync(url(p), p, ct);
            return new PageResult<CardItem>(r.Items.Select(VideoItem).ToList(), r.HasMore);
        }, template, minWidth);


    public static GridTab LocalTab(string name, Func<IEnumerable<CardItem>> items, string emptyText, string template = "VideoCardTemplate") =>
        new(name, (_, _) => Task.FromResult(new PageResult<CardItem>(items().ToList(), false)), template, EmptyText: emptyText);

    public static TabbedGridPage ForJableList(string title, string listUrl) =>
        new(title, [JableTab(title, p => JableUrls.WithPage(listUrl, p))]);
}

public sealed class CoverWallPage : TabbedGridPage
{
    public CoverWallPage() : base("封面墙", [
        JableTab("最近更新", JableUrls.Latest, "WallCardTemplate", 340),
        JableTab("本周热门", JableUrls.Hot, "WallCardTemplate", 340),
        JableTab("新片上市", JableUrls.NewRelease, "WallCardTemplate", 340),
    ]) { }
}


public sealed class SearchPage : TabbedGridPage
{
    public SearchPage(string q) : base($"搜索：{q}", [
        JableTab("Jable", p => JableUrls.Search(q, p)),
        new GridTab("演员", async (p, ct) =>
        {
            var r = await AppServices.Netflav.SearchActressAsync(q, p, ct);
            return new PageResult<CardItem>(r.Items.Select(CardItem.From).ToList(), r.HasMore && r.Items.Count >= 40);
        }, "PerformerCardTemplate", 150, "没有找到演员"),
    ]) { }
}

public sealed class CategoriesPage : TabbedGridPage
{
    public CategoriesPage() : base("分类", [
        new GridTab("主题", async (_, ct) =>
            new PageResult<CardItem>((await AppServices.Sites.JableCategoriesAsync(ct)).Select(CardItem.From).ToList(), false),
            "CategoryCardTemplate", 300),
    ]) { }
}

public sealed class FavoritesPage : TabbedGridPage
{
    public FavoritesPage() : base("我的片单", [
        LocalTab("我的片单", () => AppServices.Library.Read(d => d.Favorites.ToList()).Select(VideoItem), "片单里还没有作品"),
    ]) { }

    public override void OnRevisited() => ReloadCurrent();
}

/// <summary>猜我喜欢：根据观看记录和片单在本机算出来的推荐。</summary>
public sealed class GuessPage : TabbedGridPage
{
    public GuessPage() : base("猜我喜欢", [
        new GridTab("猜我喜欢", async (_, ct) =>
            new PageResult<CardItem>((await Recommendations.GuessAsync(120, ct)).Select(VideoItem).ToList(), false),
            EmptyText: "先看几部或加几部到片单，这里就会按你的口味推荐"),
    ]) { }
}

public sealed class HistoryPage : TabbedGridPage
{
    public HistoryPage() : base("观看记录", [
        LocalTab("没看完的", () => AppServices.Library.RecentHistory(true).Select(h => VideoItem(h.Card)), "没有没看完的作品"),
        LocalTab("全部历史", () => AppServices.Library.RecentHistory().Select(h => VideoItem(h.Card)), "还没有观看记录"),
    ], extraHeader: ClearButton()) { }

    private static UIElement ClearButton()
    {
        var b = Ui.Button("清空观看记录", (s, _) =>
        {
            if (MessageBox.Show("确定清空全部观看记录和播放进度吗？", "Jable", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
            AppServices.Library.Mutate(d => d.History.Clear());
            if (Shell.Host.Content is HistoryPage hp) hp.ReloadCurrent();
        });
        b.HorizontalAlignment = HorizontalAlignment.Left;
        b.Margin = new Thickness(0, 14, 0, 0);
        return b;
    }

    public override void OnRevisited() => ReloadCurrent();
}
