using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using JableLens.App.Pages;
using JableLens.Core.Http;

namespace JableLens.App.Controls;

/// <summary>
/// Netflix 的一行：标题（悬停时出现「查看全部 ›」）+ 右上角页码短横 + 可横向翻页的卡片。
/// 两侧箭头是贯穿整行高度的半透明条；右边总露出下一张的一截。没有内容时整行隐藏。
/// </summary>
public sealed class MediaRow : UserControl
{
    public static readonly DependencyProperty CardWidthProperty = DependencyProperty.Register(
        nameof(CardWidth), typeof(double), typeof(MediaRow), new PropertyMetadata(300.0));

    public double CardWidth
    {
        get => (double)GetValue(CardWidthProperty);
        set => SetValue(CardWidthProperty, value);
    }

    private readonly Func<CancellationToken, Task<IReadOnlyList<CardItem>>> _loader;
    private readonly ItemsControl _items = new() { Focusable = false };
    private readonly ScrollViewer _scroller = new()
    {
        HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
        VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
        CanContentScroll = false,
        Focusable = false,
    };
    private readonly Button _left = Arrow("", HorizontalAlignment.Left);
    private readonly Button _right = Arrow("", HorizontalAlignment.Right);
    private readonly TextBlock _status = new() { Foreground = (Brush)Application.Current.Resources["Fg3"], FontSize = 13, Margin = new Thickness(Ui.Gutter, 0, 0, 12), Visibility = Visibility.Collapsed };
    private readonly StackPanel _dots = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, Ui.Gutter, 6), Visibility = Visibility.Hidden };
    private readonly Button? _seeAll;
    private readonly string _template;
    private int _visible = 5;

    public MediaRow(string title, Func<CancellationToken, Task<IReadOnlyList<CardItem>>> loader, Action? seeAll = null, string template = "VideoCardTemplate")
    {
        _loader = loader;
        _template = template;

        var header = new Grid { Margin = new Thickness(0, 34, 0, 12) };
        var titleRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(Ui.Gutter, 0, 0, 0) };
        titleRow.Children.Add(new TextBlock { Text = title, FontSize = 20, FontWeight = Weights.Bold, Foreground = new SolidColorBrush(Color.FromRgb(0xE5, 0xE5, 0xE5)), VerticalAlignment = VerticalAlignment.Bottom });
        if (seeAll is not null)
        {
            _seeAll = new Button
            {
                Content = "查看全部  ", FontSize = 13, FontWeight = Weights.SemiBold, Foreground = (Brush)Application.Current.Resources["Link"],
                Style = (Style)Application.Current.Resources["GhostBtn"], Margin = new Thickness(14, 0, 0, 1), Padding = new Thickness(0, 2, 0, 2),
                VerticalAlignment = VerticalAlignment.Bottom, Opacity = 0,
            };
            _seeAll.Click += (_, _) => seeAll();
            titleRow.Children.Add(_seeAll);
        }
        header.Children.Add(titleRow);
        header.Children.Add(_dots);

        _items.ItemTemplate = (DataTemplate)Application.Current.Resources[template];
        var panel = new FrameworkElementFactory(typeof(StackPanel));
        panel.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal);
        // 首尾留出边距：内容从对齐线开始，右边能露出下一张
        panel.SetValue(MarginProperty, new Thickness(Ui.Gutter - Ui.CardInset, 0, Ui.Gutter - Ui.CardInset, 0));
        _items.ItemsPanel = new ItemsPanelTemplate(panel);
        var containerStyle = new Style(typeof(ContentPresenter));
        containerStyle.Setters.Add(new Setter(WidthProperty, new System.Windows.Data.Binding(nameof(CardWidth)) { Source = this }));
        _items.ItemContainerStyle = containerStyle;
        _scroller.Content = _items;

        var body = new Grid();
        body.Children.Add(_scroller);
        body.Children.Add(_left);
        body.Children.Add(_right);
        _left.Click += (_, _) => Page(-1);
        _right.Click += (_, _) => Page(1);

        var root = new StackPanel { Background = Brushes.Transparent };
        root.Children.Add(header);
        root.Children.Add(_status);
        root.Children.Add(body);
        Content = root;

        root.MouseEnter += (_, _) => UpdateChrome(true);
        root.MouseLeave += (_, _) => UpdateChrome(false);
        _scroller.ScrollChanged += (_, _) => UpdateChrome(root.IsMouseOver);

        SizeChanged += (_, _) => UpdateWidth();
        _items.PreviewMouseLeftButtonUp += (_, e) => { if (ItemFrom(e.OriginalSource) is { } i) Shell.OpenCard(i); };
        _items.PreviewMouseRightButtonUp += (_, e) =>
        {
            if (ItemFrom(e.OriginalSource) is { } i && Shell.MenuFor(i) is { } m) { m.IsOpen = true; e.Handled = true; }
        };
        BobCard.Attach(_items);
        UpdateChrome(false);
    }

    private static MainWindow Shell => (MainWindow)Application.Current.MainWindow;

    private static Button Arrow(string glyph, HorizontalAlignment side) => new()
    {
        Content = glyph, FontFamily = (FontFamily)Application.Current.Resources["Icons"], FontSize = 26, Foreground = Brushes.White,
        Style = (Style)Application.Current.Resources["GhostBtn"], Width = Ui.Gutter - 8, HorizontalAlignment = side,
        Background = new SolidColorBrush(Color.FromArgb(0x80, 0x14, 0x14, 0x14)), Visibility = Visibility.Collapsed,
        Template = ArrowTemplate(),
    };

    private static ControlTemplate ArrowTemplate()
    {
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(BackgroundProperty));
        var cp = new FrameworkElementFactory(typeof(ContentPresenter));
        cp.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center);
        cp.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
        border.AppendChild(cp);
        var t = new ControlTemplate(typeof(Button)) { VisualTree = border };
        var hover = new Trigger { Property = IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(FontSizeProperty, 34.0));
        hover.Setters.Add(new Setter(BackgroundProperty, new SolidColorBrush(Color.FromArgb(0xB3, 0x14, 0x14, 0x14))));
        t.Triggers.Add(hover);
        return t;
    }

    private double PageWidth => Math.Max(1, _visible * CardWidth);

    private void Page(int dir) => SmoothScroll.ScrollHorizontallyBy(_scroller, dir * PageWidth);

    private void UpdateChrome(bool hover)
    {
        _left.Visibility = hover && _scroller.HorizontalOffset > 2 ? Visibility.Visible : Visibility.Collapsed;
        _right.Visibility = hover && _scroller.HorizontalOffset < _scroller.ScrollableWidth - 2 ? Visibility.Visible : Visibility.Collapsed;
        if (_seeAll is not null) _seeAll.Opacity = hover ? 1 : 0;

        // 页码短横：当前页白，其它灰
        var count = (_items.ItemsSource as System.Collections.ICollection)?.Count ?? 0;
        var pages = _visible > 0 ? (int)Math.Ceiling(count / (double)_visible) : 0;
        _dots.Visibility = hover && pages > 1 ? Visibility.Visible : Visibility.Hidden;
        if (_dots.Children.Count != pages)
        {
            _dots.Children.Clear();
            for (var i = 0; i < pages; i++) _dots.Children.Add(new Border { Width = 12, Height = 2, Margin = new Thickness(1, 0, 0, 0) });
        }
        var current = (int)Math.Round(_scroller.HorizontalOffset / PageWidth);
        if (_scroller.HorizontalOffset >= _scroller.ScrollableWidth - 2) current = pages - 1;
        for (var i = 0; i < _dots.Children.Count; i++)
            ((Border)_dots.Children[i]).Background = new SolidColorBrush(i == current ? Color.FromRgb(0xAA, 0xAA, 0xAA) : Color.FromRgb(0x4D, 0x4D, 0x4D));
    }

    private void UpdateWidth()
    {
        var w = ActualWidth - 2 * (Ui.Gutter - Ui.CardInset);
        if (w <= 0) return;
        _visible = ActualWidth switch { > 1500 => 6, > 1150 => 5, > 850 => 4, > 560 => 3, _ => 2 };
        if (_template == "PosterCardTemplate") _visible += 2;
        CardWidth = Math.Floor(w / _visible);
    }

    public async Task LoadAsync(CancellationToken ct)
    {
        var ratio = _template == "PosterCardTemplate" ? 1.42 : 0.5625;
        _items.ItemsSource = Enumerable.Range(0, 7).Select(_ => CardItem.Skeleton(ratio)).ToList();
        var template = _items.ItemTemplate;
        _items.ItemTemplate = (DataTemplate)Application.Current.Resources["SkeletonCardTemplate"];
        try
        {
            var list = await _loader(ct);
            _items.ItemTemplate = template;
            if (list.Count == 0)
            {
                Visibility = Visibility.Collapsed;
                return;
            }
            Motion.AllowAppear();
            _items.ItemsSource = list; // 卡片自己依次浮现（Motion.Appear）
            UpdateChrome(IsMouseOver);
        }
        catch (OperationCanceledException) { }
        catch (Exception e) when (e is SourceException or System.Net.Http.HttpRequestException)
        {
            _items.ItemsSource = null;
            _status.Text = "读取失败：" + e.Message;
            _status.Visibility = Visibility.Visible;
        }
    }

    private static CardItem? ItemFrom(object source)
    {
        for (var d = source as DependencyObject; d is not null; d = Tree.Parent(d))
        {
            if (d is Button) return null;
            if (d is FrameworkElement { DataContext: CardItem item }) return item.Title.Length > 0 ? item : null; // 骨架卡片没有标题
        }
        return null;
    }
}
