using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using JableLens.App.Controls;
using JableLens.App.Services;
using JableLens.Core.Http;
using JableLens.Core.Models;
using JableLens.Core.Sources.Av01;

namespace JableLens.App.Pages;

/// <summary>
/// 作品详情（Netflix 的预览弹窗）：顶部大图（随后静音播放预览）+ 番号 + 播放 / ＋片单；
/// 下面左栏是日期时长、标题、简介，右栏是演员、标签；再往下是剧照、「更多类似内容」、「关于」。
/// 先显示卡片上已有的信息，再并行补全（站点详情、AV01 简介、R18 资料）。
/// </summary>
public sealed class VideoDetailPage : PageBase
{
    private const double Pad = 56;

    private readonly VideoCard? _card;
    private readonly string _code;
    private readonly CodedWork? _work;
    private VideoDetail? _detail;
    private Av01Video? _av01;

    private readonly Grid _backdrop = new() { ClipToBounds = true, Height = 480 };
    private readonly Image _cover = new() { Stretch = Stretch.UniformToFill };

    /// <summary>顶部大图区域和其中的封面（卡片展开 / 缩回动画的终点和起点）。</summary>
    public FrameworkElement Hero => _backdrop;
    public ImageSource? HeroImage => _cover.Source;

    /// <summary>高清大图还没到时，先用卡片上的封面顶着（展开动画开头和卡片一模一样，高清图到了直接换上）。</summary>
    public void ShowPlaceholder(ImageSource image)
    {
        if (_cover.Source is null) _cover.Source = image;
    }
    private readonly Grid _videoHost = new() { IsHitTestVisible = false };
    private readonly TextBlock _logo = new() { FontFamily = (FontFamily)Application.Current.Resources["LogoFont"], FontSize = 60, Foreground = Brushes.White };
    private readonly StackPanel _buttons = new() { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 18, 0, 0) };
    private readonly StackPanel _progress = new() { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 14, 0, 0), Visibility = Visibility.Collapsed };

    private readonly WrapPanel _metaLine = new();
    private readonly TextBlock _title = new() { FontSize = 20, FontWeight = Weights.SemiBold, LineHeight = 30, TextWrapping = TextWrapping.Wrap, Foreground = Brushes.White, Margin = new Thickness(0, 22, 0, 0), MaxWidth = 860, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly TextBlock _description = new()
    {
        FontSize = 14.5, LineHeight = 26, TextWrapping = TextWrapping.Wrap, Foreground = new SolidColorBrush(Color.FromRgb(0xD6, 0xD6, 0xD6)), Margin = new Thickness(0, 14, 0, 0),
        MaxWidth = 860, MaxHeight = 104, TextTrimming = TextTrimming.CharacterEllipsis, HorizontalAlignment = HorizontalAlignment.Left, Visibility = Visibility.Collapsed,
    };
    private readonly Button _expand = new() { Content = "展开简介", Visibility = Visibility.Collapsed, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 6, 0, 0) };
    private readonly TextBlock _status = new() { FontSize = 14, Foreground = Ui.Res("Fg2"), Margin = new Thickness(0, 12, 0, 0), TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
    private readonly StackPanel _gallery = new();
    private readonly StackPanel _more = new();
    /// <summary>资料卡：演员 / 类型 / 标签 / 上市 / 片长 / 厂牌……一行一项（原来右栏和底部「关于」重复显示的内容合在这里）。</summary>
    private readonly Grid _facts2 = new();
    private MediaElement? _player;

    public VideoDetailPage(VideoCard card) : this(card.Code, card, null) { }

    private VideoDetailPage(string code, VideoCard? card, CodedWork? work)
    {
        _code = code;
        _card = card;
        _work = work;
        Background = Ui.Res("Panel");
        BuildLayout();
        ShowBasic();
        Loaded += OnFirstLoaded;
    }

    public static VideoDetailPage ForCode(string code, CodedWork? work) => new(code, null, work);

    public override string Title => _code;

    private void OnFirstLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnFirstLoaded;
        _ = LoadAsync();
        _ = PlayPreviewAsync();
    }

    public override void OnRevisited() => RenderButtons();

    public override void OnClosed()
    {
        base.OnClosed();
        StopPreview();
    }

    // ---------------- 布局 ----------------

    private void BuildLayout()
    {
        ImageLoader.SetDecodeWidth(_cover, 1400);
        // 大图下半截渐隐成透明，和下面的玻璃面板接上（不是渐变到一块实色）
        var media = new Grid
        {
            OpacityMask = new LinearGradientBrush(new GradientStopCollection { new(Colors.Black, 0.5), new(Colors.Transparent, 1) }, new Point(0, 0), new Point(0, 1)),
        };
        media.Children.Add(_cover);
        media.Children.Add(_videoHost);
        _backdrop.Children.Add(media);
        _backdrop.Children.Add(new Rectangle
        {
            IsHitTestVisible = false, // 底部压暗一点，番号和按钮在亮图上也清楚
            Fill = new LinearGradientBrush(new GradientStopCollection { new(Color.FromArgb(0, 0, 0, 0), 0.4), new(Color.FromArgb(0x66, 0, 0, 0), 0.85), new(Color.FromArgb(0, 0, 0, 0), 1) }, new Point(0, 0), new Point(0, 1)),
        });
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(Pad, 0, Pad, 44) };
        text.Children.Add(_logo);
        text.Children.Add(_buttons);
        text.Children.Add(_progress);
        _backdrop.Children.Add(text);
        _backdrop.SizeChanged += (_, e) => { if (e.WidthChanged) _backdrop.Height = Math.Round(_backdrop.ActualWidth * 0.5625); };

        // 下面一栏到底：日期 · 时长 · HD；标题；简介（可展开）；资料卡
        var info = new StackPanel { Margin = new Thickness(Pad, 36, Pad, 0) };
        info.Children.Add(_metaLine);
        info.Children.Add(_title);
        info.Children.Add(_description);
        _expand.Style = (Style)Application.Current.Resources["GhostBtn"];
        _expand.Foreground = Ui.Res("Fg2");
        _expand.Click += (_, _) =>
        {
            var open = double.IsInfinity(_description.MaxHeight);
            _description.MaxHeight = open ? 104 : double.PositiveInfinity;
            _expand.Content = open ? "展开简介" : "收起";
        };
        info.Children.Add(_expand);
        info.Children.Add(_status);
        var card = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0x0F, 255, 255, 255)), BorderBrush = new SolidColorBrush(Color.FromArgb(0x14, 255, 255, 255)),
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(16), Padding = new Thickness(24, 20, 24, 10), Margin = new Thickness(0, 28, 0, 0),
            Child = _facts2,
        };
        _facts2.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(64) });
        _facts2.ColumnDefinitions.Add(new ColumnDefinition());
        info.Children.Add(card);

        var root = new StackPanel();
        root.Children.Add(_backdrop);
        root.Children.Add(info);
        root.Children.Add(_gallery);
        root.Children.Add(_more);
        root.Children.Add(new Border { Height = 48 });
        Content = root;
    }

    private void ShowBasic()
    {
        _title.Text = _card is not null ? HeroCarousel.TitleOnly(_card) : _work?.Title ?? "";
        if (_card is not null) ImageLoader.SetSource(_cover, Covers.HiRes(_card) ?? _card.CoverUrl);
        else if (_work?.CoverUrl is { } w) ImageLoader.SetSource(_cover, w);
        RenderMeta();
        RenderButtons();
        RenderFacts();
    }

    // ---------------- 读取 ----------------

    private async Task LoadAsync()
    {
        var ct = Life.Token;
        var site = LoadSiteAsync(ct);
        var av01 = LoadAv01Async(ct);
        await Task.WhenAll(site, av01);
        if (ct.IsCancellationRequested) return;
        await LoadR18Async(ct);
    }

    private async Task LoadSiteAsync(CancellationToken ct)
    {
        try
        {
            _detail = _card is not null
                ? await AppServices.Sites.DetailAsync(_card.Source, _card.PageUrl, ct)
                : await AppServices.Sites.FindByCodeAsync(_code, ct);
            if (ct.IsCancellationRequested) return;
            if (_detail is null)
            {
                _status.Text = "暂无片源";
                _status.Visibility = Visibility.Visible;
                RenderButtons();
                return;
            }
            if (_card is null)
            {
                _title.Text = _detail.Title.StartsWith(_detail.Code, StringComparison.OrdinalIgnoreCase) ? _detail.Title[_detail.Code.Length..].Trim() : _detail.Title;
                ImageLoader.SetSource(_cover, Covers.HiRes(PlayableCard()!) ?? _detail.CoverUrl);
            }
            AppServices.Library.RememberProfile(_detail);
            if (_detail.ReleaseDate is { } rd) _facts.TryAdd("上市", rd.ToString("yyyy-MM-dd"));
            RenderMeta();
            RenderButtons();
            RenderFacts();
            RenderMore();
        }
        catch (OperationCanceledException) { }
        catch (SourceException e)
        {
            _status.Text = "读取失败：" + e.Message;
            _status.Visibility = Visibility.Visible;
        }
    }

    private async Task LoadAv01Async(CancellationToken ct)
    {
        try
        {
            _av01 = await AppServices.Av01.FindByCodeAsync(_code, ct);
            if (_av01 is null || ct.IsCancellationRequested) return;
            if (_av01.Maker is { } maker) _facts["厂牌"] = maker;
            if (_av01.DurationSeconds is { } secs) _facts["片长"] = $"{secs / 60} 分钟";
            if (_av01.PublishedDate is { } d) _facts["上市"] = d.ToString("yyyy-MM-dd");
            if (!string.IsNullOrWhiteSpace(_av01.DescriptionCn))
            {
                _description.Text = _av01.DescriptionCn;
                _description.Visibility = Visibility.Visible;
                _expand.Visibility = _av01.DescriptionCn.Length > 160 ? Visibility.Visible : Visibility.Collapsed;
            }
            RenderMeta();
            RenderFacts();
        }
        catch (OperationCanceledException) { }
        catch (SourceException) { } // 简介是锦上添花，读不到就不显示
    }

    private async Task LoadR18Async(CancellationToken ct)
    {
        var date = _av01?.PublishedDate ?? _detail?.ReleaseDate;
        if (date is null) return;
        try
        {
            // R18 只有发行日完全一致才采用；Jable 显示的日期有时差一天，两个都试
            var d = await AppServices.R18.LookupAsync(_code, date.Value, ct);
            if (d is null && _detail?.ReleaseDate is { } jd && jd != date) d = await AppServices.R18.LookupAsync(_code, jd, ct);
            if (d is null || ct.IsCancellationRequested) return;
            if (d.Directors.FirstOrDefault()?.NameKanji is { } dir) _facts.TryAdd("导演", dir);
            if (d.Label is { } label && label != d.Maker) _facts.TryAdd("系列", label);
            if (d.Maker is { } m) _facts.TryAdd("厂牌", m);
            if (d.RuntimeMinutes is { } rt) _facts.TryAdd("片长", $"{rt} 分钟");
            if (d.Categories.Count > 0) _facts.TryAdd("分类", string.Join("、", d.Categories.Take(8)));
            RenderFacts();
            if (d.Gallery.Count > 0) RenderGallery(d.Gallery);
        }
        catch (OperationCanceledException) { }
        catch (SourceException) { }
    }

    /// <summary>Netflix 的预览窗打开一会儿就开始放预告：这里放站点的预览短片（静音循环）。</summary>
    private async Task PlayPreviewAsync()
    {
        if (_card?.PreviewUrl is not { } url) return;
        await Task.Delay(1200);
        if (Life.IsCancellationRequested) return;
        var file = await HoverPreview.DownloadAsync(url);
        if (file is null || Life.IsCancellationRequested) return;
        var player = new MediaElement
        {
            LoadedBehavior = MediaState.Manual, UnloadedBehavior = MediaState.Close, IsMuted = true,
            Stretch = Stretch.UniformToFill, Opacity = 0, Source = new Uri(file),
        };
        player.MediaOpened += (_, _) => player.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(600)));
        player.MediaEnded += (_, _) => { player.Position = TimeSpan.Zero; player.Play(); };
        player.MediaFailed += (_, _) => StopPreview();
        _videoHost.Children.Add(player);
        _player = player;
        player.Play();
    }

    private void StopPreview()
    {
        if (_player is null) return;
        _player.Stop();
        _player.Close();
        _videoHost.Children.Remove(_player);
        _player = null;
    }

    // ---------------- 渲染 ----------------

    private readonly Dictionary<string, string> _facts = [];

    private void RenderMeta()
    {
        _logo.Text = _detail?.Code ?? _card?.Code ?? _code;
        _metaLine.Children.Clear();
        var date = _av01?.PublishedDate ?? _detail?.ReleaseDate ?? _work?.Date;
        if (date is not null) _metaLine.Children.Add(Pill($"{date:yyyy-MM-dd}"));
        if (_card?.Duration is { } dur) _metaLine.Children.Add(Pill(dur));
        else if (_av01?.DurationSeconds is { } s) _metaLine.Children.Add(Pill($"{s / 60} 分钟"));
        _metaLine.Children.Add(Pill("HD"));
        if (_detail is not null) _metaLine.Children.Add(Pill("Jable"));
    }

    private static Border Pill(string s) => new()
    {
        Background = new SolidColorBrush(Color.FromArgb(0x1A, 255, 255, 255)), CornerRadius = new CornerRadius(12), Padding = new Thickness(11, 3, 11, 4),
        Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center,
        Child = new TextBlock { Text = s, FontSize = 13, Foreground = new SolidColorBrush(Color.FromRgb(0xD0, 0xD0, 0xD0)) },
    };

    private VideoCard? PlayableCard()
    {
        if (_detail is not null)
            return new VideoCard(_detail.Source, _detail.PageUrl, _detail.Code, _detail.Title, _card?.CoverUrl ?? _detail.CoverUrl, _card?.PreviewUrl, _card?.Duration);
        return _card;
    }

    private void RenderButtons()
    {
        _buttons.Children.Clear();
        var card = PlayableCard();
        var history = card is null ? null : AppServices.Library.GetHistory(card.PageUrl);
        var resume = history?.IsUnfinished == true;
        var play = new Button { Content = resume ? "   继续播放" : "   播放", Style = (Style)Application.Current.Resources["HeroBtn"], IsEnabled = card is not null };
        play.Click += (_, _) => Play(resume: true);
        _buttons.Children.Add(play);
        if (resume)
        {
            var restart = RoundButton("", "从头播放");
            restart.Click += (_, _) => Play(resume: false);
            _buttons.Children.Add(restart);
        }
        if (card is not null)
        {
            var fav = AppServices.Library.IsFavorite(card.PageUrl);
            var add = RoundButton(fav ? "" : "", fav ? "从我的片单移除" : "加入我的片单");
            add.Click += (_, _) =>
            {
                AppServices.Library.ToggleFavorite(card);
                if (_detail is not null) AppServices.Library.RememberProfile(_detail);
                RenderButtons();
            };
            _buttons.Children.Add(add);
            var queued = AppServices.Downloads.Find(card) is not null;
            var dl = RoundButton(queued ? "" : "", queued ? "已在下载列表（点开查看）" : "下载");
            dl.Click += (_, _) =>
            {
                if (AppServices.Downloads.Find(card) is null) { AppServices.Downloads.Enqueue(card); RenderButtons(); }
                else Shell.NavigateTo("downloads");
            };
            _buttons.Children.Add(dl);
        }

        // 进度：灰底红条 + 「已看 x / 共 y」
        _progress.Children.Clear();
        _progress.Visibility = resume ? Visibility.Visible : Visibility.Collapsed;
        if (resume && history is not null)
        {
            var bar = new Grid { Width = 220, Height = 3, Background = new SolidColorBrush(Color.FromRgb(0x4D, 0x4D, 0x4D)), VerticalAlignment = VerticalAlignment.Center };
            bar.Children.Add(new Border { Background = Ui.Res("Accent"), HorizontalAlignment = HorizontalAlignment.Left, Width = 220 * history.ProgressRatio });
            _progress.Children.Add(bar);
            _progress.Children.Add(new TextBlock
            {
                Text = $"已看 {Clock(history.Position)} / 共 {Clock(history.Duration)}", FontSize = 13.5, Foreground = Ui.Res("Fg2"),
                Margin = new Thickness(14, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
            });
        }
    }

    private static string Clock(double seconds) => TimeSpan.FromSeconds(seconds).ToString(seconds >= 3600 ? @"h\:mm\:ss" : @"m\:ss");

    private static Button RoundButton(string glyph, string tip) => new Button
    {
        Content = glyph, Style = (Style)Application.Current.Resources["RoundBtn"], Width = 44, Height = 44, FontSize = 16, ToolTip = tip,
        Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center,
    }.WithName(tip);

    private void Play(bool resume)
    {
        var card = PlayableCard();
        if (card is null) return;
        if (!resume) AppServices.Library.RecordProgress(card, 0, AppServices.Library.GetHistory(card.PageUrl)?.Duration ?? 0);
        if (_detail is not null) AppServices.Library.RememberProfile(_detail);
        StopPreview();
        MainWindow.Play(card);
    }

    // ---- 演员 / 标签 ----

    private IReadOnlyList<(string Name, string? Alt)> Performers()
    {
        var list = _detail?.Performers.Select(p => p.Name).ToList()
                   ?? _av01?.Actresses.Select(a => a.Name).ToList()
                   ?? _work?.Actors.ToList() ?? [];
        return list.Select(name =>
        {
            var tr = _av01?.Actresses.FirstOrDefault(a => a.AllNames.Contains(name))?.Translations.GetValueOrDefault("cn");
            return (name, tr is not null && tr != name ? tr : null);
        }).ToList();
    }

    private IReadOnlyList<TagLink> Types() => _detail is null ? [] : WorkInfo.Split(_detail).Types;

    private IReadOnlyList<TagLink> Tags() => _detail is null ? [] : WorkInfo.Split(_detail).Tags;

    /// <summary>资料卡：左边灰色字段名，右边内容；演员 / 类型 / 标签是可点的胶囊。每项只出现一次。</summary>
    private void RenderFacts()
    {
        _facts2.Children.Clear();
        _facts2.RowDefinitions.Clear();
        var row = 0;
        void Add(string label, FrameworkElement content)
        {
            _facts2.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var l = new TextBlock { Text = label, FontSize = 13.5, Foreground = Ui.Res("Fg3"), Margin = new Thickness(0, 6, 0, 0), VerticalAlignment = VerticalAlignment.Top };
            Grid.SetRow(l, row);
            _facts2.Children.Add(l);
            content.Margin = new Thickness(0, 0, 0, 10);
            Grid.SetRow(content, row);
            Grid.SetColumn(content, 1);
            _facts2.Children.Add(content);
            row++;
        }
        FrameworkElement Chips(IEnumerable<(string Text, Action Click)> items)
        {
            var w = new WrapPanel();
            foreach (var (text, click) in items)
            {
                var b = new Button { Content = text, Style = (Style)Application.Current.Resources["ChipBtn"] };
                b.Click += (_, _) => click();
                w.Children.Add(b);
            }
            return w;
        }
        FrameworkElement Plain(string s) => new TextBlock { Text = s, FontSize = 14, TextWrapping = TextWrapping.Wrap, Padding = new Thickness(0, 5, 0, 6), Foreground = new SolidColorBrush(Color.FromRgb(0xE0, 0xE0, 0xE0)) };

        var performers = Performers();
        if (performers.Count > 0)
            Add("演员", Chips(performers.Select(p => (p.Alt is null ? p.Name : $"{p.Name}（{p.Alt}）", (Action)(() => OpenPerformer(p.Name, performers.Count))))));
        var types = Types();
        if (types.Count > 0) Add("类型", Chips(types.Select(t => (t.Name, (Action)(() => OpenTag(t))))));
        var tags = Tags();
        if (tags.Count > 0) Add("标签", Chips(tags.Select(t => (t.Name, (Action)(() => OpenTag(t))))));
        foreach (var key in new[] { "上市", "片长", "厂牌", "系列", "导演", "分类" })
            if (_facts.GetValueOrDefault(key) is { } value) Add(key, Plain(value));
        ((FrameworkElement)_facts2.Parent).Visibility = row == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private async void OpenPerformer(string name, int count)
    {
        try
        {
            var match = await AppServices.Names.ResolveAsync(name, _code, count, Life.Token);
            Shell.OpenPerformer(match?.NetflavName ?? name);
        }
        catch (OperationCanceledException) { }
    }

    private void OpenTag(TagLink tag)
    {
        if (tag.PageUrl is null || _detail is null) return;
        Shell.Navigate(tag.PageUrl.Contains("/tags/") ? new TagsPage(tag) : TabbedGridPage.ForJableList(tag.Name, tag.PageUrl));
    }

    // ---- 剧照 ----

    private void RenderGallery(IReadOnlyList<string> urls)
    {
        _gallery.Children.Clear();
        var title = Ui.Text("剧照", "H2");
        title.Margin = new Thickness(Pad, 48, Pad, 16);
        _gallery.Children.Add(title);
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(Pad, 0, Pad, 0) };
        for (var i = 0; i < urls.Count; i++)
        {
            var idx = i;
            var img = new Image { Height = 130, Stretch = Stretch.Uniform };
            ImageLoader.SetDecodeWidth(img, 300);
            ImageLoader.SetSource(img, urls[i]);
            var g = new Grid { Background = Ui.Res("Card"), MinWidth = 90, Cursor = Cursors.Hand, Margin = new Thickness(0, 0, 8, 0), Children = { img } };
            RoundClip.SetRadius(g, 10);
            g.MouseLeftButtonUp += (_, _) => new GalleryWindow(urls, idx) { Owner = Application.Current.MainWindow }.Show();
            row.Children.Add(g);
        }
        _gallery.Children.Add(new ScrollViewer
        {
            Content = row, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, Padding = new Thickness(0, 0, 0, 8),
        });
    }

    // ---- 更多类似内容：三列卡片，先显示 9 个，底部圆按钮展开 ----

    private void RenderMore()
    {
        _more.Children.Clear();
        if (_detail is null || _detail.Related.Count == 0) return;
        var title = Ui.Text("更多类似内容", "H2");
        title.Margin = new Thickness(Pad, 48, Pad, 18);
        _more.Children.Add(title);
        var all = _detail.Related.Take(24).ToList();
        var grid = new UniformGrid { Columns = 3, Margin = new Thickness(Pad - 8, 0, Pad - 8, 0) };
        void Fill(int n)
        {
            grid.Children.Clear();
            foreach (var v in all.Take(n)) grid.Children.Add(MoreCard(v));
        }
        Fill(9);
        _more.Children.Add(grid);
        if (all.Count > 9)
        {
            var expander = new Grid { Margin = new Thickness(Pad, 4, Pad, 0), Height = 44 };
            expander.Children.Add(new Border { Height = 2, Background = new SolidColorBrush(Color.FromRgb(0x40, 0x40, 0x40)), VerticalAlignment = VerticalAlignment.Center });
            var toggle = RoundButton("", "显示更多");
            toggle.Margin = new Thickness(0);
            toggle.HorizontalAlignment = HorizontalAlignment.Center;
            toggle.Background = new SolidColorBrush(Color.FromArgb(0xE6, 0x2A, 0x2A, 0x2A));
            var open = false;
            toggle.Click += (_, _) =>
            {
                open = !open;
                Fill(open ? all.Count : 9);
                toggle.Content = open ? "" : "";
                toggle.ToolTip = open ? "收起" : "显示更多";
            };
            expander.Children.Add(toggle);
            _more.Children.Add(expander);
        }
    }

    private Border MoreCard(VideoCard v)
    {
        var img = new Image { Stretch = Stretch.UniformToFill };
        ImageLoader.SetDecodeWidth(img, 420);
        ImageLoader.SetSource(img, v.CoverUrl);
        var top = new Grid { Children = { new AspectBox { Ratio = 0.5625, Child = img } } };
        if (v.Duration is { } d)
            top.Children.Add(new TextBlock { Text = d, FontSize = 14, FontWeight = Weights.SemiBold, Foreground = Brushes.White, Margin = new Thickness(0, 8, 10, 0), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 4, ShadowDepth = 0, Opacity = 0.9 } });

        var head = new DockPanel { LastChildFill = false };
        var fav = AppServices.Library.IsFavorite(v.PageUrl);
        var add = RoundButton(fav ? "" : "", fav ? "从我的片单移除" : "加入我的片单");
        add.Width = add.Height = 36;
        add.FontSize = 13;
        add.Margin = new Thickness(0);
        add.Click += (_, _) =>
        {
            var now = AppServices.Library.ToggleFavorite(v);
            add.Content = now ? "" : "";
        };
        DockPanel.SetDock(add, Dock.Right);
        head.Children.Add(add);
        var left = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        left.Children.Add(new TextBlock { Text = v.Code, FontSize = 15, FontWeight = Weights.Bold, Foreground = Brushes.White });
        head.Children.Add(left);

        var body = new StackPanel { Margin = new Thickness(14, 14, 14, 16) };
        body.Children.Add(head);
        body.Children.Add(new TextBlock
        {
            Text = HeroCarousel.TitleOnly(v), FontSize = 13.5, LineHeight = 20, MaxHeight = 80, TextWrapping = TextWrapping.Wrap,
            TextTrimming = TextTrimming.CharacterEllipsis, Foreground = new SolidColorBrush(Color.FromRgb(0xD2, 0xD2, 0xD2)), Margin = new Thickness(0, 10, 0, 0),
        });

        var stack = new StackPanel { Children = { top, body } };
        var clip = new Grid { Background = Ui.Res("Card"), Children = { stack } };
        RoundClip.SetRadius(clip, 12);
        var card = new Border { Child = clip, Margin = new Thickness(8, 0, 8, 16), Cursor = Cursors.Hand };
        card.MouseLeftButtonUp += (_, e) =>
        {
            if (e.OriginalSource is DependencyObject o && IsInButton(o)) return;
            Shell.OpenCard(CardItem.From(v));
        };
        return card;
    }

    private static bool IsInButton(DependencyObject d)
    {
        for (var cur = d; cur is not null; cur = Tree.Parent(cur))
            if (cur is ButtonBase) return true;
        return false;
    }
}
