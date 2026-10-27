using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using JableLens.App.Controls;
using JableLens.App.Services;
using JableLens.Core.Http;
using JableLens.Core.Models;
using JableLens.Core.Text;

namespace JableLens.App.Pages;

/// <summary>
/// 演员页（Netflix 式全幅）：最新作品的封面铺满顶部，左下是名字、资料、「关注」；
/// 下面一排下拉（排序、年份、标签）+ 关键词，然后是作品海报网格。
/// 作品逐页读完（以「某页为空」为结束），筛选都在本地完成。
/// </summary>
public sealed class PerformerPage : PageBase
{
    private const int MaxPages = 30;

    private readonly string _name;
    private readonly string? _iconUrl;
    // 横幅要铺满整宽：网格本身不留边，卡片行自己留
    private readonly CardGrid _grid = new()
    {
        MinItemWidth = 180, Padding = new Thickness(0),
        RowGap = new Thickness(Ui.Gutter - Ui.CardInset, 0, Ui.Gutter - Ui.CardInset, 22),
    };
    private readonly StackPanel _header = new() { Margin = new Thickness(-Ui.CardInset, 0, -Ui.CardInset, 0) };
    private readonly TextBlock _profile = new() { FontSize = 15, Foreground = Brushes.White, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0) };
    private readonly TextBlock _altNames = new() { FontSize = 15, Foreground = new SolidColorBrush(Color.FromRgb(0xBC, 0xBC, 0xBC)), Margin = new Thickness(0, 6, 0, 0) };
    private readonly StackPanel _actions = new() { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 22, 0, 0) };
    private readonly WrapPanel _filters = new() { Margin = new Thickness(Ui.Gutter, 8, Ui.Gutter, 14) };
    private readonly TextBlock _count = new() { FontSize = 14, Foreground = Ui.Res("Fg2"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 8) };
    private readonly TextBox _keyword = new() { Width = 200, Height = 32, Padding = new Thickness(12, 0, 12, 0), FontSize = 13.5, VerticalContentAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 8) };
    private readonly Image _banner = new() { Stretch = Stretch.UniformToFill, HorizontalAlignment = HorizontalAlignment.Right };
    private readonly Grid _bannerBox = new() { ClipToBounds = true, Height = 480 };

    private readonly List<CodedWork> _works = [];
    private PerformerProfile? _profileData;
    private bool _loadingDone;
    private int _sort; // 0 最新 1 最早
    private int? _year;
    private string? _tag;

    public PerformerPage(string name, string? iconUrl)
    {
        _name = name;
        _iconUrl = iconUrl;
        _grid.ItemTemplate = (DataTemplate)Application.Current.Resources["PosterCardTemplate"];
        _grid.EmptyText = "";
        System.Windows.Automation.AutomationProperties.SetName(_keyword, "筛选作品");
        _keyword.TextChanged += (_, _) => RenderGrid();
        BuildHeader();
        _grid.Header = _header;
        Shell.Wire(_grid);
        Content = _grid;
        Loaded += OnFirstLoaded;
    }

    public override string Title => _name;

    public override bool FullBleed => true;

    private void OnFirstLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnFirstLoaded;
        _grid.SetItems([]);
        _ = LoadAsync();
    }

    public override void OnRevisited() => RenderActions();

    private void BuildHeader()
    {
        var bg = (Color)Application.Current.Resources["BgColor"];
        ImageLoader.SetDecodeWidth(_banner, 1600);
        _bannerBox.Children.Add(new Border { Background = Ui.Res("Panel") });
        _bannerBox.Children.Add(_banner);
        // 左边压暗（文字区）+ 底部融进页面
        _bannerBox.Children.Add(new Rectangle
        {
            Fill = new LinearGradientBrush(new GradientStopCollection
            {
                new(Color.FromArgb(0xF2, bg.R, bg.G, bg.B), 0), new(Color.FromArgb(0xB3, bg.R, bg.G, bg.B), 0.35), new(Color.FromArgb(0x1A, bg.R, bg.G, bg.B), 0.8),
            }, new Point(0, 0), new Point(1, 0)),
        });
        _bannerBox.Children.Add(new Rectangle
        {
            Fill = new LinearGradientBrush(new GradientStopCollection
            {
                new(Color.FromArgb(0, bg.R, bg.G, bg.B), 0.6), new(Color.FromArgb(0xFF, bg.R, bg.G, bg.B), 1),
            }, new Point(0, 0), new Point(0, 1)),
        });

        var avatar = new Grid { Width = 72, Height = 72, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 16) };
        avatar.Children.Add(new Ellipse { Fill = Ui.Res("Card") });
        avatar.Children.Add(new TextBlock { Text = _name[..1], FontSize = 30, Foreground = Ui.Res("Fg3"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center });
        var avatarImg = new Image { Stretch = Stretch.UniformToFill, Clip = new EllipseGeometry(new Point(36, 36), 36, 36) };
        ImageLoader.SetDecodeWidth(avatarImg, 160);
        if (_iconUrl is not null) ImageLoader.SetSource(avatarImg, _iconUrl);
        avatar.Children.Add(avatarImg);

        var info = new StackPanel { VerticalAlignment = VerticalAlignment.Bottom, HorizontalAlignment = HorizontalAlignment.Left, MaxWidth = 760, Margin = new Thickness(Ui.Gutter, 0, Ui.Gutter, 44) };
        info.Children.Add(avatar);
        info.Children.Add(new TextBlock { Text = _name, FontSize = 54, FontWeight = Weights.Bold, Foreground = Brushes.White, TextWrapping = TextWrapping.Wrap });
        info.Children.Add(_altNames);
        info.Children.Add(_profile);
        info.Children.Add(_actions);
        _bannerBox.Children.Add(info);
        _bannerBox.SizeChanged += (_, e) => { if (e.WidthChanged) _bannerBox.Height = Math.Clamp(_bannerBox.ActualWidth * 0.36, 400, 600); };

        _header.Children.Add(_bannerBox);
        _header.Children.Add(_filters);
        RenderActions();
    }

    private void RenderActions()
    {
        _actions.Children.Clear();
        var following = AppServices.Library.IsFollowing(_name);
        var follow = new Button
        {
            Content = following ? "   已关注" : "   关注", Style = (Style)Application.Current.Resources[following ? "HeroBtnGlass" : "HeroBtn"],
            IsEnabled = following || _loadingDone,
            ToolTip = !_loadingDone && !following ? "作品列表读完后才能关注（用来区分哪些是新作）" : null,
        };
        follow.Click += (_, _) =>
        {
            AppServices.Library.ToggleFollow(_name, _iconUrl, _works.Select(w => w.Code));
            RenderActions();
        };
        _actions.Children.Add(follow);
        var search = new Button { Content = "   在 Jable 搜索", Style = (Style)Application.Current.Resources["HeroBtnGlass"] };
        search.Click += (_, _) => Shell.Navigate(new SearchPage(_name));
        _actions.Children.Add(search);
    }

    private async Task LoadAsync()
    {
        var ct = Life.Token;
        try
        {
            for (var page = 1; page <= MaxPages; page++)
            {
                var p = await AppServices.Netflav.GetActressPageAsync(_name, page, ct);
                if (page == 1) ShowProfile(p.Profile);
                if (!p.Works.HasMore) break;
                var known = _works.Select(w => VideoCode.Compact(w.Code)).ToHashSet();
                // 同一番号会重复上架（如 [中字] 版），只保留一条
                _works.AddRange(p.Works.Items.Where(w => known.Add(VideoCode.Compact(w.Code))));
                if (page == 1 && _works.FirstOrDefault(w => w.CoverUrl is not null) is { } first) ImageLoader.SetSource(_banner, first.CoverUrl);
                RenderFilters();
                RenderGrid();
            }
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (SourceException e)
        {
            _profile.Text = "读取失败：" + e.Message;
        }
        _loadingDone = true;
        if (_works.Count == 0) _grid.EmptyText = "没有找到作品";
        RenderActions();
        RenderFilters();
        RenderGrid();
    }

    private void ShowProfile(PerformerProfile? p)
    {
        _profileData = p;
        if (p is null) return;
        var alt = new[] { p.NameJa, p.NameZh }.Where(n => !string.IsNullOrEmpty(n) && n != _name).Distinct();
        _altNames.Text = string.Join(" / ", alt);
        var parts = new List<string>();
        if (p.Birthday is { } b)
        {
            var age = p.AgeOn(DateOnly.FromDateTime(DateTime.Today));
            parts.Add(age is null ? $"{b:yyyy-MM-dd}" : $"{age} 岁");
        }
        if (p.Height is { } h) parts.Add($"{h} cm");
        if (!string.IsNullOrEmpty(p.Cup)) parts.Add($"{p.Cup} 罩杯");
        if (p.Bust is not null || p.Waist is not null || p.Hip is not null) parts.Add($"B{p.Bust?.ToString() ?? "?"} W{p.Waist?.ToString() ?? "?"} H{p.Hip?.ToString() ?? "?"}");
        if (p.VideoCount is { } n) parts.Add($"{n} 部作品");
        _profile.Text = string.Join("   ·   ", parts);
    }

    private void RenderFilters()
    {
        _filters.Children.Clear();
        _filters.Children.Add(Ui.Dropdown(["最新", "最早"], _sort, i => { _sort = i; RenderGrid(); }, "排序"));

        var years = _works.Where(w => w.Date is not null).Select(w => w.Date!.Value.Year).Distinct().OrderDescending().ToList();
        if (years.Count > 1)
        {
            var labels = years.Select(y => y.ToString()).Prepend("全部").ToList();
            var sel = _year is { } y0 ? years.IndexOf(y0) + 1 : 0;
            _filters.Children.Add(Ui.Dropdown(labels, sel, i => { _year = i == 0 ? null : years[i - 1]; RenderGrid(); }, "年份"));
        }

        var tags = _works.SelectMany(w => w.Tags).Where(t => t.Length is > 0 and < 10)
            .GroupBy(t => t).OrderByDescending(g => g.Count()).Take(30).Select(g => g.Key).ToList();
        if (tags.Count > 0)
        {
            var labels = tags.Prepend("全部").ToList();
            var sel = _tag is null ? 0 : tags.IndexOf(_tag) + 1;
            _filters.Children.Add(Ui.Dropdown(labels, Math.Max(0, sel), i => { _tag = i == 0 ? null : tags[i - 1]; RenderGrid(); }, "标签"));
        }

        if (_keyword.Parent is Panel old) old.Children.Remove(_keyword);
        _filters.Children.Add(_keyword);
        if (_count.Parent is Panel oldc) oldc.Children.Remove(_count);
        _filters.Children.Add(_count);
    }

    private void RenderGrid()
    {
        IEnumerable<CodedWork> q = _works;
        if (_year is { } y) q = q.Where(w => w.Date?.Year == y);
        if (_tag is { } t) q = q.Where(w => w.Tags.Contains(t));
        var kw = _keyword.Text.Trim();
        if (kw.Length > 0) q = q.Where(w => w.Code.Contains(kw, StringComparison.OrdinalIgnoreCase) || w.Title.Contains(kw, StringComparison.OrdinalIgnoreCase));
        q = _sort == 0 ? q.OrderByDescending(w => w.Date) : q.OrderBy(w => w.Date);
        var list = q.ToList();

        var newCodes = AppServices.Library.Read(d => d.Follows.FirstOrDefault(f => f.Name == _name)?.NewWorks.Select(w => VideoCode.Compact(w.Code)).ToHashSet()) ?? [];
        _count.Text = _loadingDone ? $"共 {list.Count} 部" : $"{_works.Count} 部…";
        _grid.SetItems(list.Select(w => CardItem.From(w, newCodes.Contains(VideoCode.Compact(w.Code)) ? "新作" : null)));
    }
}
