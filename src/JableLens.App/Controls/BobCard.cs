using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Threading;
using JableLens.App.Services;
using JableLens.Core.Http;
using JableLens.Core.Models;

namespace JableLens.App.Controls;

/// <summary>
/// 悬停卡（Netflix 那种）：鼠标在作品卡片上停 0.5 秒，卡片放大弹出。
/// 上面是大图（番号压在左下角，随后静音播放预览短片）；下面是「播放」「＋片单」「详情」、
/// 上市日 / 时长、标题，以及从详情页读来的类型、演员、标签。鼠标离开就收起。
/// 全局只有一张，挂在需要的容器上（行、网格）。
/// </summary>
public static class BobCard
{
    private const double Shadow = 28;   // 弹出层四周留给阴影的边
    private const double Scale = 1.45;  // 相对原卡片的放大倍数

    private static readonly Popup Popup = new() { AllowsTransparency = true, Placement = PlacementMode.Relative, StaysOpen = true, Focusable = false };
    private static readonly DispatcherTimer OpenTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private static readonly DispatcherTimer CloseTimer = new() { Interval = TimeSpan.FromMilliseconds(120) };
    private static readonly DispatcherTimer PreviewTimer = new() { Interval = TimeSpan.FromMilliseconds(700) };
    private static readonly Brush Muted = Freeze(new SolidColorBrush(Color.FromRgb(0x8C, 0x8C, 0x8C)));
    private static readonly Brush Soft = Freeze(new SolidColorBrush(Color.FromRgb(0xD9, 0xD9, 0xD9)));

    private static FrameworkElement? _pending;
    private static FrameworkElement? _anchor;
    private static CardItem? _item;
    private static Border? _frame;
    private static Grid? _media;
    private static MediaElement? _player;
    private static StackPanel? _meta;
    private static StackPanel? _info;
    private static CancellationTokenSource? _cts;
    private static int _outside;

    static BobCard()
    {
        OpenTimer.Tick += (_, _) =>
        {
            OpenTimer.Stop();
            if (_pending is not { IsMouseOver: true } p || !p.IsVisible) return;
            if (Motion.Settling) { OpenTimer.Start(); return; } // 卡片还在滑入，等它到位再弹
            Show(p);
        };
        CloseTimer.Tick += (_, _) =>
        {
            // 用光标位置判断：弹出层刚打开时鼠标没动过，IsMouseOver 还不准
            if (Contains(_frame) || Contains(_anchor)) { _outside = 0; return; }
            if (++_outside >= 2) Hide();
        };
        PreviewTimer.Tick += async (_, _) =>
        {
            PreviewTimer.Stop();
            var item = _item;
            var url = (item?.Payload as VideoCard)?.PreviewUrl;
            if (url is null || _media is null) return;
            var file = await HoverPreview.DownloadAsync(url);
            if (file is null || item != _item || _media is null) return;
            var player = new MediaElement
            {
                LoadedBehavior = MediaState.Manual, UnloadedBehavior = MediaState.Close, IsMuted = true,
                Stretch = Stretch.UniformToFill, Opacity = 0, IsHitTestVisible = false, Source = new Uri(file),
            };
            player.MediaOpened += (_, _) => player.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(300)));
            player.MediaEnded += (_, _) => { player.Position = TimeSpan.Zero; player.Play(); };
            player.MediaFailed += (_, _) => StopPreview();
            _media.Children.Insert(1, player); // 图之上，渐变和番号之下
            _player = player;
            player.Play();
        };
    }

    private static Brush Freeze(Brush b) { b.Freeze(); return b; }

    private static MainWindow Shell => (MainWindow)Application.Current.MainWindow;

    private static bool Contains(FrameworkElement? fe)
    {
        if (fe is null || !fe.IsVisible || PresentationSource.FromVisual(fe) is null) return false;
        var cursor = System.Windows.Forms.Cursor.Position;
        var p = fe.PointFromScreen(new Point(cursor.X, cursor.Y));
        return p.X >= 0 && p.Y >= 0 && p.X <= fe.ActualWidth && p.Y <= fe.ActualHeight;
    }

    /// <summary>让一个容器里的作品卡片支持悬停卡。</summary>
    public static void Attach(FrameworkElement container)
    {
        container.MouseMove += OnMove;
        container.MouseLeave += (_, _) => { _pending = null; OpenTimer.Stop(); };
        container.PreviewMouseWheel += (_, _) => { _pending = null; OpenTimer.Stop(); Hide(); };
    }

    private static void OnMove(object sender, MouseEventArgs e)
    {
        var cover = CardCover(e.OriginalSource as DependencyObject);
        if (cover == _pending) return;
        _pending = cover;
        OpenTimer.Stop();
        if (cover is not null) OpenTimer.Start();
    }

    /// <summary>鼠标下面那张作品卡片的封面（模板里带圆角裁剪的那个 Grid）。</summary>
    private static FrameworkElement? CardCover(DependencyObject? d)
    {
        FrameworkElement? root = null;
        for (; d is not null; d = Tree.Parent(d))
        {
            if (d is ButtonBase) return null;
            if (d is ContentPresenter) break;
            if (d is FrameworkElement { DataContext: CardItem } fe) root = fe;
            else if (root is not null) break;
        }
        if (root?.DataContext is not CardItem item || item.Title.Length == 0 || item.Payload is not (VideoCard or CodedWork)) return null;
        return FindCover(root) ?? root;
    }

    private static FrameworkElement? FindCover(DependencyObject root)
    {
        var queue = new Queue<DependencyObject>([root]);
        while (queue.Count > 0)
        {
            var n = queue.Dequeue();
            if (n is Grid g && RoundClip.GetRadius(g) > 0) return g;
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(n); i++) queue.Enqueue(VisualTreeHelper.GetChild(n, i));
        }
        return null;
    }

    public static void Hide()
    {
        OpenTimer.Stop();
        CloseTimer.Stop();
        PreviewTimer.Stop();
        _cts?.Cancel();
        StopPreview();
        Popup.IsOpen = false;
        Popup.Child = null;
        _frame = null;
        _media = null;
        _meta = null;
        _info = null;
        _item = null;
        _anchor = null;
    }

    private static void StopPreview()
    {
        if (_player is null) return;
        _player.Stop();
        _player.Close();
        _media?.Children.Remove(_player);
        _player = null;
    }

    private static void Show(FrameworkElement cover)
    {
        Hide();
        if (cover.DataContext is not CardItem item || cover.ActualWidth < 40) return;
        Tilt.Reset(cover); // 卡片先回正，弹出层才按它的真实位置定位
        _item = item;
        _anchor = cover;

        var width = Math.Clamp(cover.ActualWidth * Scale, 360, 520);
        _frame = Build(item, width);
        // 外面包一层不缩放的容器：弹出窗口打开时按子元素「缩放后」的大小定位，直接把放大动画加在子元素上，
        // 悬停卡会先出现在偏左上的位置，等内容变高重新定位时再跳回来
        Popup.Child = new Border { Child = _frame };
        Popup.PlacementTarget = cover;

        // 横向居中压在卡片上，靠近窗口两边时贴边（Netflix 第一张 / 最后一张就是这样）
        var window = Window.GetWindow(cover);
        var x = (cover.ActualWidth - width) / 2;
        if (window is not null)
        {
            var p = cover.TranslatePoint(new Point(0, 0), window);
            var min = Pages.Ui.Gutter - Shadow / 2;
            var max = window.ActualWidth - Pages.Ui.Gutter - width;
            x = Math.Clamp(p.X + x, min, Math.Max(min, max)) - p.X;
        }
        var imageH = width * 0.5625;
        Popup.HorizontalOffset = x - Shadow;
        Popup.VerticalOffset = Math.Min(0, (cover.ActualHeight - imageH) / 2) - Shadow;

        // 从原卡片大小放大出来
        var st = new ScaleTransform(1 / Scale, 1 / Scale);
        _frame.RenderTransformOrigin = new Point(0.5, 0.25);
        _frame.RenderTransform = st;
        st.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1, Motion.SpringTime) { EasingFunction = Motion.Spring });
        st.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1, Motion.SpringTime) { EasingFunction = Motion.Spring });
        _frame.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)) { EasingFunction = Motion.Ease });

        Popup.IsOpen = true;
        // 显示出来之前按卡片当前的真实位置再定位一次（偏移量变一下再改回去就会重新定位），万一打开那一刻算偏了也看不到跳动
        var frame = _frame;
        Popup.Dispatcher.BeginInvoke(() =>
        {
            if (_frame != frame || !Popup.IsOpen) return;
            var h = Popup.HorizontalOffset;
            Popup.HorizontalOffset = h + 1;
            Popup.HorizontalOffset = h;
        }, System.Windows.Threading.DispatcherPriority.Loaded);
        _outside = 0;
        CloseTimer.Start();
        if ((item.Payload as VideoCard)?.PreviewUrl is not null) PreviewTimer.Start();

        _cts = new CancellationTokenSource();
        switch (item.Payload)
        {
            case VideoCard v: _ = LoadDetailAsync(item, v, _cts.Token); break;
            case CodedWork w: FillInfo(w.Actors, [], w.Tags); break;
        }
    }

    // ---------------- 内容 ----------------

    private static Border Build(CardItem item, double width)
    {
        var res = Application.Current.Resources;
        var video = item.Payload as VideoCard;
        var panel = (SolidColorBrush)res["Panel"];

        // 大图：封面（之后叠预览短片）+ 底部渐变 + 左下角番号
        _media = new Grid { Background = (Brush)res["Card"], Cursor = Cursors.Hand };
        var img = new Image { Stretch = Stretch.UniformToFill };
        ImageLoader.SetDecodeWidth(img, 760);
        ImageLoader.SetSource(img, video is not null ? Covers.HiRes(video) ?? item.CoverUrl : item.CoverUrl);
        _media.Children.Add(img);
        _media.Children.Add(new Rectangle
        {
            IsHitTestVisible = false,
            Fill = new LinearGradientBrush(new GradientStopCollection
            {
                new(Color.FromArgb(0, panel.Color.R, panel.Color.G, panel.Color.B), 0.55), new(panel.Color, 1),
            }, new Point(0, 0), new Point(0, 1)),
        });
        if ((item.Code ?? video?.Code) is { } code)
            _media.Children.Add(new TextBlock
            {
                Text = code, FontFamily = (FontFamily)res["LogoFont"], FontSize = 30, Foreground = Brushes.White,
                Margin = new Thickness(18, 0, 18, 8), VerticalAlignment = VerticalAlignment.Bottom, IsHitTestVisible = false,
                Effect = new DropShadowEffect { BlurRadius = 8, ShadowDepth = 1, Opacity = 0.8, RenderingBias = RenderingBias.Performance },
            });
        var imageBox = new AspectBox { Ratio = 0.5625, Child = _media };
        imageBox.MouseLeftButtonUp += (_, _) => Open(item);

        // 按钮：白色「播放」药丸 + ＋片单；右边「详情」
        var buttons = new DockPanel { LastChildFill = false };
        var play = new Button
        {
            Content = video is null ? "   详细信息" : "   播放", Style = (Style)res["HeroBtn"],
            FontSize = 14, Padding = new Thickness(18, 7, 22, 7), Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center,
        };
        play.Click += (_, _) => { if (video is not null) MainWindow.Play(video); else Open(item); };
        buttons.Children.Add(play);
        if (video is not null)
        {
            var fav = RoundButton(AppServices.Library.IsFavorite(video.PageUrl) ? "" : "", "加入我的片单");
            fav.Click += (_, _) =>
            {
                var now = AppServices.Library.ToggleFavorite(video);
                fav.Content = now ? "" : "";
                fav.ToolTip = now ? "从我的片单移除" : "加入我的片单";
            };
            buttons.Children.Add(fav);
        }
        var more = RoundButton("", "详细信息");
        more.Margin = new Thickness(0);
        more.Click += (_, _) => Open(item);
        DockPanel.SetDock(more, Dock.Right);
        buttons.Children.Add(more);

        // 上市日 · 18+ · 时长 · HD
        _meta = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 16, 0, 0) };
        RenderMeta(item, null);

        var title = new TextBlock
        {
            Text = item.Title, FontSize = 15, FontWeight = Weights.SemiBold, LineHeight = 22, MaxHeight = 44,
            TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.CharacterEllipsis, Foreground = Brushes.White, Margin = new Thickness(0, 10, 0, 0),
        };

        // 类型 / 演员 / 标签（读详情时先放三条闪动的占位）
        _info = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
        foreach (var w in new[] { 0.55, 0.4, 0.75 })
        {
            var bar = new Border { Height = 10, CornerRadius = new CornerRadius(3), Background = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33)), Width = (width - 40) * w, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 4, 0, 6) };
            bar.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0.4, 1, TimeSpan.FromMilliseconds(700)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever });
            _info.Children.Add(bar);
        }

        var body = new StackPanel { Margin = new Thickness(20, 6, 20, 20) };
        body.Children.Add(buttons);
        body.Children.Add(_meta);
        body.Children.Add(title);
        body.Children.Add(_info);
        if (item.Progress > 0)
        {
            var bar = new Grid { Height = 3, Margin = new Thickness(0, 14, 0, 0), Background = new SolidColorBrush(Color.FromRgb(0x4D, 0x4D, 0x4D)) };
            bar.Children.Add(new Border { Background = (Brush)res["Accent"], HorizontalAlignment = HorizontalAlignment.Left, Width = (width - 40) * item.Progress });
            body.Children.Add(bar);
        }

        var stack = new StackPanel();
        stack.Children.Add(imageBox);
        stack.Children.Add(body);
        // 下半截是毛玻璃（和顶栏、菜单同一种），阴影单独一层
        var clip = new Grid { Children = { new GlassBackdrop { CornerRadius = 18 }, stack } };
        RoundClip.SetRadius(clip, 18);
        var layers = new Grid();
        layers.Children.Add(new Border
        {
            Background = panel, CornerRadius = new CornerRadius(18), IsHitTestVisible = false,
            Effect = new DropShadowEffect { BlurRadius = 30, ShadowDepth = 8, Direction = 270, Opacity = 0.7, Color = Colors.Black, RenderingBias = RenderingBias.Performance },
        });
        layers.Children.Add(clip);

        var frame = new Border { Width = width, Margin = new Thickness(Shadow), Child = layers };
        frame.SetValue(TextElement.FontFamilyProperty, res["UiFont"]);
        frame.PreviewMouseWheel += (_, _) => Hide();
        return frame;
    }

    /// <summary>「2026-09-18  2:04:00  HD」：上市日白色加粗，其余灰色 / 描边小框。</summary>
    private static void RenderMeta(CardItem item, DateOnly? date)
    {
        if (_meta is null) return;
        _meta.Children.Clear();
        date ??= (item.Payload as CodedWork)?.Date;
        if (date is { } d)
            _meta.Children.Add(new TextBlock { Text = $"{d:yyyy-MM-dd} 上市", FontSize = 13.5, FontWeight = Weights.SemiBold, Foreground = new SolidColorBrush(Color.FromRgb(0x46, 0xD3, 0x69)), Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center });
        if (item.Duration is { } dur) _meta.Children.Add(new TextBlock { Text = dur, FontSize = 13.5, Foreground = Soft, Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center });
        _meta.Children.Add(Box("HD"));
    }

    private static Border Box(string s) => new()
    {
        BorderBrush = new SolidColorBrush(Color.FromArgb(0x80, 255, 255, 255)), BorderThickness = new Thickness(1), Padding = new Thickness(5, 0, 5, 1),
        Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center,
        Child = new TextBlock { Text = s, FontSize = 11.5, Foreground = Soft },
    };

    private static async Task LoadDetailAsync(CardItem item, VideoCard v, CancellationToken ct)
    {
        try
        {
            // 详情页通常在悬停 0.3 秒时已经预读进缓存，这里基本是瞬间返回
            var d = await AppServices.Sites.DetailAsync(v.Source, v.PageUrl, ct);
            if (ct.IsCancellationRequested || item != _item) return;
            var (types, tags) = WorkInfo.Split(d);
            RenderMeta(item, d.ReleaseDate);
            FillInfo(d.Performers.Select(p => p.Name).ToList(), types.Select(t => t.Name).ToList(), tags.Select(t => t.Name).ToList());
        }
        catch (OperationCanceledException) { }
        catch (SourceException)
        {
            if (item == _item) FillInfo([], [], []);
        }
    }

    /// <summary>类型 / 演员 / 标签三行：左边灰色小标题对齐，右边白字，用「·」隔开。</summary>
    private static void FillInfo(IReadOnlyList<string> actors, IReadOnlyList<string> types, IReadOnlyList<string> tags)
    {
        if (_info is null) return;
        _info.Children.Clear();
        void Row(string label, IEnumerable<string> values, int max)
        {
            var list = values.Where(s => s.Length > 0).Distinct().Take(max).ToList();
            if (list.Count == 0) return;
            var g = new Grid { Margin = new Thickness(0, 0, 0, 7) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40) });
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.Children.Add(new TextBlock { Text = label, FontSize = 12.5, Foreground = Muted });
            // 每个词一个块，换行只发生在词与词之间（不会把「美腿」拆成两行）
            var v = new WrapPanel();
            for (var i = 0; i < list.Count; i++)
                v.Children.Add(new TextBlock { Text = i < list.Count - 1 ? list[i] + "  ·  " : list[i], FontSize = 12.5, LineHeight = 19, Foreground = Soft });
            Grid.SetColumn(v, 1);
            g.Children.Add(v);
            _info.Children.Add(g);
        }
        Row("类型", types, 4);
        Row("演员", actors, 4);
        Row("标签", tags, 8);
        if (_info.Children.Count > 0) Motion.FadeIn(_info, 250);
    }

    private static Button RoundButton(string glyph, string tip) => new()
    {
        Content = glyph, Style = (Style)Application.Current.Resources["RoundBtn"], ToolTip = tip, VerticalAlignment = VerticalAlignment.Center,
        Margin = new Thickness(0, 0, 8, 0),
    };

    private static void Open(CardItem item)
    {
        var anchor = _anchor; // 悬停卡下面那张原卡片：详情从它展开
        Hide();
        Shell.OpenCard(item, anchor);
    }

    /// <summary>鼠标下面那张作品卡片的封面（点卡片打开详情时，展开动画从这里开始）。</summary>
    public static FrameworkElement? CoverUnder(DependencyObject? d) => CardCover(d);
}
