using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Threading;
using JableLens.App.Pages;
using JableLens.App.Services;
using JableLens.Core.Models;

namespace JableLens.App.Controls;

/// <summary>
/// Netflix 首页顶部的大图（Billboard）：整屏宽的高清封面，左下角是番号（当片名）、TOP 10 标、简介、
/// 「播放」「更多信息」；右边是 18+ 分级标。停几秒后静音播放预览短片，播完换下一部。
/// </summary>
public sealed class HeroCarousel : UserControl
{
    private readonly Image _a = new() { Stretch = Stretch.UniformToFill };
    private readonly Image _b = new() { Stretch = Stretch.UniformToFill, Opacity = 0 };
    private readonly Grid _videoHost = new() { IsHitTestVisible = false };
    private readonly TextBlock _rank = new() { FontSize = 17, FontWeight = Weights.Bold, Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
    private readonly TextBlock _code = new() { FontFamily = (FontFamily)Application.Current.Resources["LogoFont"], FontSize = 86, Foreground = Brushes.White, Margin = new Thickness(0, 10, 0, 0) };
    /// <summary>番号下面一行小标签：时长 · HD · 18+（Netflix 大图上的那行）。</summary>
    private readonly StackPanel _meta = new() { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0) };
    private readonly TextBlock _synopsis = new() { FontSize = 18, LineHeight = 27, MaxHeight = 54, Foreground = Brushes.White, TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 12, 0, 24) };
    private readonly StackPanel _text = new() { VerticalAlignment = VerticalAlignment.Bottom, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly Border _rating = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(10) };
    private readonly DispatcherTimer _previewDelay = new() { Interval = TimeSpan.FromSeconds(2.5) };
    private MediaElement? _player;
    private IReadOnlyList<VideoCard> _cards = [];
    private int _index = -1;
    private bool _aOnTop = true;

    public HeroCarousel()
    {
        ImageLoader.SetDecodeWidth(_a, 1920);
        ImageLoader.SetDecodeWidth(_b, 1920);
        var bg = (Color)Application.Current.Resources["BgColor"];

        // 左侧斜向压暗（文字可读）+ 底部融进页面底色
        var left = new Rectangle
        {
            IsHitTestVisible = false,
            Fill = new LinearGradientBrush(new GradientStopCollection
            {
                new(Color.FromArgb(0xB3, 0, 0, 0), 0), new(Color.FromArgb(0x59, 0, 0, 0), 0.4), new(Color.FromArgb(0, 0, 0, 0), 0.85),
            }, new Point(0, 0.6), new Point(1, 0.3)),
        };
        // 底部长渐隐（从 60% 开始，中段已有一半底色），内容行像是从画面里浮出来
        var bottom = new Rectangle
        {
            IsHitTestVisible = false,
            Fill = new LinearGradientBrush(new GradientStopCollection
            {
                new(Color.FromArgb(0, bg.R, bg.G, bg.B), 0.6), new(Color.FromArgb(0x80, bg.R, bg.G, bg.B), 0.82), new(Color.FromArgb(0xFF, bg.R, bg.G, bg.B), 1),
            }, new Point(0, 0), new Point(0, 1)),
        };
        // 顶部渐暗：玻璃导航栏压在亮图上也看得清
        var top = new Rectangle
        {
            IsHitTestVisible = false, VerticalAlignment = VerticalAlignment.Top, Height = 180,
            Fill = new LinearGradientBrush(Color.FromArgb(0x8C, 0, 0, 0), Color.FromArgb(0, 0, 0, 0), 90),
        };

        // TOP 10 标：红底小方块 + 「本周热门第 N 名」
        var top10 = new Border
        {
            Width = 30, Height = 32, Background = (Brush)Application.Current.Resources["Accent"], CornerRadius = new CornerRadius(2),
            Child = new StackPanel
            {
                VerticalAlignment = VerticalAlignment.Center,
                Children =
                {
                    new TextBlock { Text = "TOP", FontSize = 8, FontWeight = Weights.Black, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center },
                    new TextBlock { Text = "10", FontSize = 15, FontWeight = Weights.Black, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, -4, 0, 0) },
                },
            },
        };
        var badge = new StackPanel { Orientation = Orientation.Horizontal };
        badge.Children.Add(top10);
        badge.Children.Add(_rank);

        var play = new Button { Content = "   播放", Style = (Style)Application.Current.Resources["HeroBtn"] };
        var info = new Button { Content = "   更多信息", Style = (Style)Application.Current.Resources["HeroBtnGlass"] };
        play.Click += (_, _) => { if (Current is { } c) MainWindow.Play(c); };
        info.Click += (_, _) => { if (Current is { } c) Shell.OpenCard(CardItem.From(c)); };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        buttons.Children.Add(play);
        buttons.Children.Add(info);

        _text.Children.Add(badge);
        _text.Children.Add(_code);
        _text.Children.Add(_meta);
        _text.Children.Add(_synopsis);
        _text.Children.Add(buttons);
        _text.Effect = new DropShadowEffect { BlurRadius = 8, ShadowDepth = 1, Opacity = 0.6, Color = Colors.Black, RenderingBias = RenderingBias.Performance };

        // 右侧分级标：左边一道浅色竖线 + 18+
        _rating.Child = new TextBlock { Text = "18+", FontSize = 17, Foreground = Brushes.White, Margin = new Thickness(12, 4, 40, 5) };
        _rating.Background = new SolidColorBrush(Color.FromArgb(0x99, 0x33, 0x33, 0x33));
        _rating.BorderBrush = new SolidColorBrush(Color.FromRgb(0xDC, 0xDC, 0xDC));
        _rating.BorderThickness = new Thickness(3, 0, 0, 0);
        _rating.HorizontalAlignment = HorizontalAlignment.Right;
        _rating.VerticalAlignment = VerticalAlignment.Bottom;

        var root = new Grid { ClipToBounds = true, Background = (Brush)Application.Current.Resources["Bg"] };
        root.Children.Add(_a);
        root.Children.Add(_b);
        root.Children.Add(_videoHost);
        root.Children.Add(left);
        root.Children.Add(bottom);
        root.Children.Add(top);
        root.Children.Add(_text);
        Content = root;

        _timer.Tick += (_, _) => Next();
        _previewDelay.Tick += async (_, _) => { _previewDelay.Stop(); await PlayPreviewAsync(); };
        SizeChanged += (_, _) => Relayout();
        Unloaded += (_, _) => { _timer.Stop(); _previewDelay.Stop(); StopPreview(); };
        Loaded += (_, _) => { if (_cards.Count > 1) _timer.Start(); };
        Height = 560;
    }

    private static MainWindow Shell => (MainWindow)Application.Current.MainWindow;

    private VideoCard? Current => _index >= 0 && _index < _cards.Count ? _cards[_index] : null;

    /// <summary>行要往上叠进大图底部多少（Netflix 的第一行压在大图下沿上）。</summary>
    public double Overlap => Math.Round(Height * 0.2);

    private void Relayout()
    {
        var h = Math.Clamp(ActualWidth * 0.5625, 420, 860);
        if (Math.Abs(h - Height) > 1) Height = h;
        var bottomGap = Overlap + 40;
        _text.Margin = new Thickness(Ui.Gutter, 0, 0, bottomGap);
        _text.Width = Math.Max(380, ActualWidth * 0.38);
        _rating.Margin = new Thickness(0, 0, 0, bottomGap + 6);
    }

    public void SetItems(IReadOnlyList<VideoCard> cards)
    {
        _cards = cards;
        if (cards.Count > 0) Show(0);
        // 预先把后面几张的大图读进缓存，切换时不闪
        foreach (var c in cards.Skip(1)) Warm(Covers.HiRes(c));
    }

    private static void Warm(string? url)
    {
        if (url is null) return;
        var tmp = new Image();
        ImageLoader.SetDecodeWidth(tmp, 1920);
        ImageLoader.SetSource(tmp, url);
    }

    private void Next()
    {
        if (_cards.Count > 1) Show((_index + 1) % _cards.Count);
    }

    private void Show(int i)
    {
        if (_cards.Count == 0) return;
        StopPreview();
        _index = i;
        var card = _cards[i];
        var (front, back) = _aOnTop ? (_b, _a) : (_a, _b);
        _aOnTop = !_aOnTop;
        // 两张图都在渐变和文字下面，新的一张在旧的上面淡入
        Panel.SetZIndex(front, -1);
        Panel.SetZIndex(back, -2);
        ImageLoader.SetSource(front, Covers.HiRes(card));
        front.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(800)));

        _rank.Text = $"本周热门第 {i + 1} 名";
        _code.Text = card.Code;
        RenderMeta(card);
        _synopsis.Text = TitleOnly(card);
        Motion.FadeIn(_text, 600);

        _timer.Stop();
        _timer.Start();
        _previewDelay.Stop();
        if (card.PreviewUrl is not null) _previewDelay.Start();
        AppServices.Sites.Prefetch(card.PageUrl);
    }

    /// <summary>时长（白字）+ HD、18+ 两个细框小标，和详情弹窗的那行一致。</summary>
    private void RenderMeta(VideoCard card)
    {
        _meta.Children.Clear();
        if (card.Duration is { Length: > 0 } d)
            _meta.Children.Add(new TextBlock { Text = d, FontSize = 15, FontWeight = Weights.SemiBold, Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) });
        foreach (var tag in new[] { "HD", "18+" })
            _meta.Children.Add(new Border
            {
                BorderBrush = new SolidColorBrush(Color.FromArgb(0xB3, 255, 255, 255)), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4),
                Padding = new Thickness(6, 0, 6, 1), Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock { Text = tag, FontSize = 12, FontWeight = Weights.SemiBold, Foreground = Brushes.White },
            });
    }

    /// <summary>预告片：图静止几秒后，静音播放预览短片盖在图上，播完换下一部。</summary>
    private async Task PlayPreviewAsync()
    {
        var card = Current;
        if (card?.PreviewUrl is null || !IsVisible) return;
        var file = await HoverPreview.DownloadAsync(card.PreviewUrl);
        if (file is null || card != Current || !IsVisible) return;
        _timer.Stop();
        var player = new MediaElement
        {
            LoadedBehavior = MediaState.Manual, UnloadedBehavior = MediaState.Close, IsMuted = true,
            Stretch = Stretch.UniformToFill, Opacity = 0, Source = new Uri(file),
        };
        player.MediaOpened += (_, _) => player.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(600)));
        player.MediaEnded += (_, _) => Next();
        player.MediaFailed += (_, _) => { StopPreview(); _timer.Start(); };
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

    /// <summary>标题去掉开头的番号（番号单独显示）。</summary>
    internal static string TitleOnly(VideoCard c) =>
        c.Title.StartsWith(c.Code, StringComparison.OrdinalIgnoreCase) ? c.Title[c.Code.Length..].Trim(' ', '-', '　') : c.Title;
}

/// <summary>封面地址的高清版本。</summary>
public static class Covers
{
    public static string? HiRes(VideoCard c) => c.Source == VideoSource.Jable
        ? Core.Sources.Jable.JableUrls.HiResCover(c.CoverUrl)
        : c.CoverUrl?.Replace("cover-t.jpg", "cover-n.jpg");
}
