using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Animation;
using JableLens.App.Controls;
using JableLens.App.Services;
using JableLens.Core.Http;
using JableLens.Core.Library;
using JableLens.Core.Models;

namespace JableLens.App.Pages;

/// <summary>
/// 我的画像：口味（类型 / 标签 / 演员）、演员外表、关注、收藏类型、观看统计、片单与下载。
/// 全部用本机记录算；只有「演员外表」要去 Netflav 读演员资料（和打开演员页一样）。
/// </summary>
public sealed class PortraitPage : PageBase
{
    private const double BarLabelWidth = 120;
    private static readonly Dictionary<string, PerformerProfile?> LooksCache = [];

    private readonly StackPanel _root = new() { Margin = new Thickness(Ui.Gutter, 20, Ui.Gutter, 56), MaxWidth = 1180, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly StackPanel _looks = new();

    public PortraitPage()
    {
        var sv = new ScrollViewer { Content = _root, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        SmoothScroll.SetIsEnabled(sv, true);
        Content = sv;
        Loaded += OnFirstLoaded;
    }

    public override string Title => "我的画像";

    private async void OnFirstLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnFirstLoaded;
        Render(Build());
        // 看过但还没记下标签的作品，补读一部分后再画一次
        var ct = Life.Token;
        try
        {
            var seeds = AppServices.Library.Read(d => Recommender.Seeds(d, DateTimeOffset.Now, 30));
            if (seeds.Any(s => !AppServices.Library.HasProfile(s.PageUrl)))
            {
                await Recommendations.FillProfilesAsync(seeds, 12, ct);
                Render(Build());
            }
            await LoadLooksAsync(ct);
        }
        catch (OperationCanceledException) { }
    }

    private static Portrait Build() => AppServices.Library.Read(d => Portrait.Build(d, DateTimeOffset.Now));

    private Portrait? _p;

    private void Render(Portrait p)
    {
        _p = p;
        _root.Children.Clear();
        _root.Children.Add(Ui.Text("我的画像", "H1"));
        var note = Ui.Text(Summary(p), fg: Ui.Res("Fg2"), size: 15);
        note.Margin = new Thickness(0, 6, 0, 0);
        note.TextWrapping = TextWrapping.Wrap;
        _root.Children.Add(note);
        var privacy = Ui.Text("只根据这台电脑上的观看记录、片单、关注计算，不会上传。", fg: Ui.Res("Fg3"), size: 12.5);
        privacy.Margin = new Thickness(0, 4, 0, 0);
        _root.Children.Add(privacy);

        // ---- 观看统计 ----
        Section("观看统计");
        var tiles = new WrapPanel();
        tiles.Children.Add(Tile(Hours(p.WatchedSeconds), "累计观看"));
        tiles.Children.Add(Tile($"{p.Works}", "看过的作品"));
        tiles.Children.Add(Tile(p.Works == 0 ? "—" : $"{p.FinishRate:P0}", "看完率"));
        tiles.Children.Add(Tile(p.Works == 0 ? "—" : Minutes(p.WatchedSeconds / p.Works), "平均每部"));
        _root.Children.Add(Stagger(tiles));
        _root.Children.Add(WeekChart(p.Weeks));

        // ---- 口味偏好 ----
        Section("口味偏好");
        if (p.ProfiledWorks == 0)
            _root.Children.Add(Empty("还没有可以分析的作品。看几部或往片单里加几部，这里就会出现你常看的类型和标签。"));
        else
        {
            var cols = new Grid();
            cols.ColumnDefinitions.Add(new ColumnDefinition());
            cols.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(48) });
            cols.ColumnDefinitions.Add(new ColumnDefinition());
            var left = Column("常看类型", Bars(p.Types.Take(8).ToList(), OpenTag));
            var right = Column("常看标签", Bars(p.Tags.Take(8).ToList(), OpenTag));
            Grid.SetColumn(right, 2);
            cols.Children.Add(left);
            cols.Children.Add(right);
            _root.Children.Add(cols);
        }

        // ---- 喜欢的演员 + 外表 ----
        Section("喜欢的演员");
        _root.Children.Add(p.Performers.Count == 0
            ? Empty("看过的作品里还没有演员信息。")
            : Bars(p.Performers.Take(8).ToList(), w => OpenPerformer(w.Name), alwaysClickable: true));
        Section("演员外表画像");
        _root.Children.Add(_looks);
        if (_looks.Children.Count == 0) _looks.Children.Add(Empty("正在读取喜欢的演员的资料…"));

        // ---- 关注 ----
        Section("关注");
        var (follows, pinned) = AppServices.Library.Read(d => (d.Follows.Select(f => f.Name).ToList(), d.PinnedTags.ToList()));
        UIElement followRow = follows.Count == 0
            ? Empty("还没有关注演员（在演员页点「关注」）")
            : Chips(follows.Select(n => (n, (Action)(() => Shell.OpenPerformer(n)))));
        UIElement pinnedRow = pinned.Count == 0
            ? Empty("还没有固定到首页的标签（在标签页点「固定到首页」）")
            : Chips(pinned.Select(t => (t.Name, (Action)(() => OpenTag(new WeightedName(t.Name, t.PageUrl, 0, 0))))));
        _root.Children.Add(Row("关注的演员", followRow));
        _root.Children.Add(Row("关注的类型", pinnedRow));

        // ---- 收藏类型 ----
        Section("收藏类型");
        _root.Children.Add(p.FavoriteTypes.Count == 0
            ? Empty(p.Favorites == 0 ? "片单是空的。" : "片单里的作品还没读到标签，打开一下它们的详情就有了。")
            : Bars(p.FavoriteTypes.Take(8).ToList(), OpenTag, unit: "部"));

        // ---- 片单与下载 ----
        Section("片单与下载");
        var lib = new WrapPanel();
        lib.Children.Add(Tile($"{p.Favorites}", "片单作品"));
        lib.Children.Add(Tile($"{p.Downloads}", "已下载"));
        lib.Children.Add(Tile(Size(p.DownloadBytes), "下载占用"));
        _root.Children.Add(Stagger(lib));
    }

    private static string Summary(Portrait p)
    {
        if (p.Works == 0 && p.Favorites == 0) return "还没有观看记录。先看几部，画像会慢慢清晰起来。";
        var parts = new List<string>();
        var likes = p.Types.Take(2).Concat(p.Tags.Take(2)).Select(t => t.Name).ToList();
        if (likes.Count > 0) parts.Add("偏爱「" + string.Join("」「", likes) + "」");
        if (p.Performers.FirstOrDefault() is { } top) parts.Add($"最常看 {top.Name}");
        parts.Add($"一共看了 {Hours(p.WatchedSeconds)}");
        return string.Join("，", parts) + "。";
    }

    // ---------------- 演员外表 ----------------

    /// <summary>喜欢的演员（加权前 6 位）+ 关注的演员，读 Netflav 资料，按喜好程度加权平均。</summary>
    private async Task LoadLooksAsync(CancellationToken ct)
    {
        if (_p is null) return;
        var people = _p.Performers.Take(6).Select(w => (w.Name, Weight: w.Weight, Resolve: true)).ToList();
        var top = people.Count > 0 ? people.Max(x => x.Weight) : 1;
        foreach (var f in AppServices.Library.Read(d => d.Follows.Select(f => f.Name).ToList()))
            people.Add((f, top * 0.5, false));

        var found = new List<(PerformerProfile P, double W)>();
        foreach (var (name, weight, resolve) in people)
        {
            ct.ThrowIfCancellationRequested();
            var p = await LooksAsync(name, resolve, ct);
            if (p is not null && found.All(x => x.P.Name != p.Name)) found.Add((p, weight));
        }

        // Jable「身材」分组里的标签：用户常看的那几个
        IReadOnlyList<WeightedName> body = [];
        try
        {
            var groups = await AppServices.Sites.JableTagGroupsAsync(ct);
            var urls = groups.Where(g => g.Name.Contains("身材")).SelectMany(g => g.Tags).Select(t => t.PageUrl).OfType<string>().ToHashSet();
            body = _p.Tags.Where(t => t.Url is not null && urls.Contains(t.Url)).Take(6).ToList();
        }
        catch (SourceException) { }

        _looks.Children.Clear();
        if (found.Count == 0 && body.Count == 0)
        {
            _looks.Children.Add(Empty("还读不到喜欢的演员的身高、罩杯等资料（Netflav 上可能没有），多看几部再来。"));
            return;
        }

        var tiles = new WrapPanel();
        if (Avg(found, p => p.Height) is { } h) tiles.Children.Add(Tile($"{h:0} cm", "平均身高"));
        var cups = found.Where(x => !string.IsNullOrEmpty(x.P.Cup)).GroupBy(x => x.P.Cup!.Trim().ToUpperInvariant())
            .Select(g => (Cup: g.Key, W: g.Sum(x => x.W))).OrderByDescending(x => x.W).ToList();
        if (cups.Count > 0) tiles.Children.Add(Tile($"{cups[0].Cup} 罩杯", "最常见罩杯"));
        if (Avg(found, p => p.Bust) is { } b && Avg(found, p => p.Waist) is { } w && Avg(found, p => p.Hip) is { } hip)
            tiles.Children.Add(Tile($"B{b:0} W{w:0} H{hip:0}", "平均三围"));
        var today = DateOnly.FromDateTime(DateTime.Today);
        if (Avg(found, p => p.AgeOn(today)) is { } age) tiles.Children.Add(Tile($"{age:0} 岁", "平均年龄（现在）"));
        if (tiles.Children.Count > 0) _looks.Children.Add(Stagger(tiles));
        else if (found.Count > 0) _looks.Children.Add(Empty("这几位演员在 Netflav 上没有身高、罩杯、三围资料，下面先按身材标签看。"));

        if (cups.Count > 1)
        {
            var total = cups.Sum(c => c.W);
            _looks.Children.Add(Row("罩杯分布", Chips(cups.Take(6).Select(c => ($"{c.Cup}  {c.W / total:P0}", (Action)(() => { }))))));
        }
        if (body.Count > 0) _looks.Children.Add(Row("身材偏好", Chips(body.Select(t => (t.Name, (Action)(() => OpenTag(t)))))));
        _looks.Children.Add(Row("参考演员", Chips(found.OrderByDescending(x => x.W).Select(x => (x.P.Name, (Action)(() => Shell.OpenPerformer(x.P.Name)))))));
        Motion.FadeIn(_looks, 300);
    }

    private static async Task<PerformerProfile?> LooksAsync(string name, bool resolve, CancellationToken ct)
    {
        if (LooksCache.TryGetValue(name, out var hit)) return hit;
        PerformerProfile? p = null;
        try
        {
            var netflavName = resolve ? (await AppServices.Names.ResolveAsync(name, ct: ct))?.NetflavName : name;
            if (netflavName is not null) p = (await AppServices.Netflav.GetActressPageAsync(netflavName, 1, ct)).Profile;
        }
        catch (SourceException) { }
        catch (System.Net.Http.HttpRequestException) { }
        LooksCache[name] = p;
        return p;
    }

    private static double? Avg(List<(PerformerProfile P, double W)> list, Func<PerformerProfile, int?> value)
    {
        var xs = list.Where(x => value(x.P) is > 0).ToList();
        return xs.Count == 0 ? null : xs.Sum(x => value(x.P)!.Value * x.W) / xs.Sum(x => x.W);
    }

    // ---------------- 打开 ----------------

    private async void OpenPerformer(string name)
    {
        try
        {
            var match = await AppServices.Names.ResolveAsync(name, ct: Life.Token);
            Shell.OpenPerformer(match?.NetflavName ?? name);
        }
        catch (OperationCanceledException) { }
    }

    private void OpenTag(WeightedName t)
    {
        if (t.Url is not { } url) return;
        Shell.Navigate(url.Contains("/tags/") ? new TagsPage(new TagLink(t.Name, url)) : TabbedGridPage.ForJableList(t.Name, url));
    }

    // ---------------- 小部件 ----------------

    private void Section(string title)
    {
        var t = Ui.Text(title, "H2");
        t.Margin = new Thickness(0, 40, 0, 14);
        _root.Children.Add(t);
    }

    private static TextBlock Empty(string text) => new() { Text = text, Foreground = Ui.Res("Fg3"), FontSize = 13.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 6) };

    private static StackPanel Column(string title, UIElement body)
    {
        var s = new StackPanel();
        s.Children.Add(new TextBlock { Text = title, Foreground = Ui.Res("Fg2"), FontSize = 13.5, Margin = new Thickness(0, 0, 0, 10) });
        s.Children.Add(body);
        return s;
    }

    /// <summary>「名称  内容」一行，名称固定宽度对齐。</summary>
    private static UIElement Row(string caption, UIElement content)
    {
        var g = new Grid { Margin = new Thickness(0, 0, 0, 12) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(BarLabelWidth) });
        g.ColumnDefinitions.Add(new ColumnDefinition());
        g.Children.Add(new TextBlock { Text = caption, Foreground = Ui.Res("Fg2"), FontSize = 13.5, Margin = new Thickness(0, 6, 0, 0) });
        Grid.SetColumn(content, 1);
        g.Children.Add(content);
        return g;
    }

    private static WrapPanel Chips(IEnumerable<(string Text, Action Click)> items)
    {
        var w = new WrapPanel();
        foreach (var (text, click) in items)
        {
            var b = new Button { Content = text, Style = (Style)Application.Current.Resources["ChipBtn"], Margin = new Thickness(0, 0, 8, 8) };
            b.Click += (_, _) => click();
            w.Children.Add(b);
        }
        return w;
    }

    /// <summary>统计小卡：大数字 + 说明，玻璃质感的浅色底。</summary>
    private static Border Tile(string value, string label)
    {
        var s = new StackPanel();
        s.Children.Add(new TextBlock { Text = value, FontSize = 26, FontWeight = Weights.Bold, Foreground = Brushes.White });
        s.Children.Add(new TextBlock { Text = label, FontSize = 12.5, Foreground = Ui.Res("Fg3"), Margin = new Thickness(0, 4, 0, 0) });
        return new Border
        {
            Child = s, MinWidth = 168, Padding = new Thickness(20, 16, 20, 16), Margin = new Thickness(0, 0, 12, 12), CornerRadius = new CornerRadius(14),
            Background = new SolidColorBrush(Color.FromArgb(0x14, 255, 255, 255)), BorderBrush = new SolidColorBrush(Color.FromArgb(0x1A, 255, 255, 255)), BorderThickness = new Thickness(1),
        };
    }

    /// <summary>一排小卡依次弹出来。</summary>
    private static Panel Stagger(Panel p)
    {
        for (var i = 0; i < p.Children.Count; i++)
        {
            var fe = (FrameworkElement)p.Children[i];
            var t = new TranslateTransform(0, 14);
            fe.RenderTransform = t;
            fe.Opacity = 0;
            var delay = TimeSpan.FromMilliseconds(i * 50);
            fe.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(280)) { BeginTime = delay, EasingFunction = Motion.Ease });
            t.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(14, 0, Motion.SpringTime) { BeginTime = delay, EasingFunction = Motion.Spring });
        }
        return p;
    }

    /// <summary>横向权重条：名称 ━━━━━ N 部。条长按第一名归一，弹簧伸长出来。</summary>
    private static StackPanel Bars(IReadOnlyList<WeightedName> items, Action<WeightedName> open, string unit = "部", bool alwaysClickable = false)
    {
        var list = new StackPanel();
        var max = items.Count == 0 ? 1 : items.Max(x => x.Weight);
        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            var g = new Grid { Height = 30, Background = Brushes.Transparent, ToolTip = $"{item.Name}：{item.Count} {unit}" };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(BarLabelWidth) });
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(56) });
            g.Children.Add(new TextBlock { Text = item.Name, Foreground = Ui.Res("Fg"), FontSize = 13.5, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 0, 10, 0) });

            var track = new Border { Height = 8, CornerRadius = new CornerRadius(4), Background = new SolidColorBrush(Color.FromArgb(0x14, 255, 255, 255)), VerticalAlignment = VerticalAlignment.Center };
            var fill = new Border { Height = 8, CornerRadius = new CornerRadius(4), Background = Ui.Res("Accent"), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center };
            var cell = new Grid();
            cell.Children.Add(track);
            cell.Children.Add(fill);
            var ratio = Math.Max(0.03, item.Weight / max);
            cell.SizeChanged += (_, e) => fill.Width = e.NewSize.Width * ratio;
            var st = new ScaleTransform(0, 1);
            fill.RenderTransform = st;
            st.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0, 1, Motion.SpringTime) { BeginTime = TimeSpan.FromMilliseconds(80 + i * 45), EasingFunction = Motion.Spring });
            Grid.SetColumn(cell, 1);
            g.Children.Add(cell);

            var n = new TextBlock { Text = $"{item.Count} {unit}", Foreground = Ui.Res("Fg3"), FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right };
            Grid.SetColumn(n, 2);
            g.Children.Add(n);
            if (item.Url is not null || alwaysClickable)
            {
                g.MouseEnter += (_, _) => track.Background = new SolidColorBrush(Color.FromArgb(0x26, 255, 255, 255));
                g.MouseLeave += (_, _) => track.Background = new SolidColorBrush(Color.FromArgb(0x14, 255, 255, 255));
                g.MouseLeftButtonUp += (_, _) => open(item);
                g.Cursor = System.Windows.Input.Cursors.Hand;
            }
            list.Children.Add(g);
        }
        return list;
    }

    /// <summary>近 12 周每周观看时长：单色竖条（红），顶部圆角，悬停显示具体数字。</summary>
    private static UIElement WeekChart(IReadOnlyList<WeekWatch> weeks)
    {
        const double H = 140;
        var max = Math.Max(1, weeks.Max(w => w.Seconds));
        var box = new StackPanel { Margin = new Thickness(0, 18, 0, 0), MaxWidth = 760, HorizontalAlignment = HorizontalAlignment.Left };
        box.Children.Add(new TextBlock { Text = $"近 {weeks.Count} 周观看时长（最多一周 {Hours(max)}）", Foreground = Ui.Res("Fg2"), FontSize = 13.5, Margin = new Thickness(0, 0, 0, 12) });

        var bars = new UniformGrid { Rows = 1, Columns = weeks.Count, Height = H };
        var labels = new UniformGrid { Rows = 1, Columns = weeks.Count, Margin = new Thickness(0, 6, 0, 0) };
        for (var i = 0; i < weeks.Count; i++)
        {
            var w = weeks[i];
            var cell = new Grid { Background = Brushes.Transparent, ToolTip = $"{w.WeekStart:M月d日} 那周：{Hours(w.Seconds)}，{w.Works} 部" };
            var bar = new Border
            {
                Height = w.Seconds <= 0 ? 2 : Math.Max(4, H * w.Seconds / max), Margin = new Thickness(5, 0, 5, 0), VerticalAlignment = VerticalAlignment.Bottom,
                CornerRadius = new CornerRadius(4, 4, 0, 0), Background = w.Seconds <= 0 ? new SolidColorBrush(Color.FromArgb(0x1F, 255, 255, 255)) : Ui.Res("Accent"),
                RenderTransformOrigin = new Point(0.5, 1),
            };
            var st = new ScaleTransform(1, 0);
            bar.RenderTransform = st;
            st.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0, 1, Motion.SpringTime) { BeginTime = TimeSpan.FromMilliseconds(i * 30), EasingFunction = Motion.Spring });
            cell.MouseEnter += (_, _) => bar.Opacity = 0.8;
            cell.MouseLeave += (_, _) => bar.Opacity = 1;
            cell.Children.Add(bar);
            bars.Children.Add(cell);
            labels.Children.Add(new TextBlock
            {
                // 隔一周标一次，从「本周」往前数，免得最后两个挤在一起
                Text = (weeks.Count - 1 - i) % 2 != 0 ? "" : i == weeks.Count - 1 ? "本周" : $"{w.WeekStart:M/d}",
                Foreground = Ui.Res("Fg3"), FontSize = 11.5, HorizontalAlignment = HorizontalAlignment.Center,
            });
        }
        box.Children.Add(bars);
        box.Children.Add(new Border { Height = 1, Background = Ui.Res("Line") });
        box.Children.Add(labels);
        return box;
    }

    private static string Hours(double seconds) =>
        seconds < 3600 ? $"{seconds / 60:0} 分钟" : $"{seconds / 3600:0.#} 小时";

    private static string Minutes(double seconds) => $"{seconds / 60:0} 分钟";

    private static string Size(long bytes) =>
        bytes >= 1L << 30 ? $"{bytes / (double)(1L << 30):0.#} GB" : $"{bytes / (double)(1L << 20):0} MB";
}
