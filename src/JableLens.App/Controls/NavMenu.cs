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

namespace JableLens.App.Controls;

public sealed record MenuEntry(string Name, object Payload);

/// <summary>悬浮菜单的一组：左栏一行（二级），右栏是这组的条目（三级）。</summary>
public sealed record MenuGroup(string Name, string Icon, IReadOnlyList<MenuEntry> Items);

/// <summary>
/// 顶部导航的悬浮菜单：鼠标停在栏目文字上 0.15 秒，下方弹出一块深色面板。
/// 左栏是分组（悬停即切换），右栏是该组的条目（两列）；点条目回调。鼠标离开文字和面板就收起。
/// </summary>
public static class NavMenu
{
    private const double Shadow = 24;
    private static readonly Popup Popup = new() { AllowsTransparency = true, Placement = PlacementMode.Bottom, StaysOpen = true, Focusable = false };
    private static readonly DispatcherTimer OpenTimer = new() { Interval = TimeSpan.FromMilliseconds(150) };
    private static readonly DispatcherTimer CloseTimer = new() { Interval = TimeSpan.FromMilliseconds(120) };
    private static readonly Brush Panel = Freeze(new SolidColorBrush(Color.FromArgb(0xF7, 0x16, 0x16, 0x16)));
    private static readonly Brush Hover = Freeze(new SolidColorBrush(Color.FromArgb(0x1F, 0xFF, 0xFF, 0xFF)));
    private static readonly Brush Muted = Freeze(new SolidColorBrush(Color.FromRgb(0x8C, 0x8C, 0x8C)));

    private static FrameworkElement? _pendingLink;
    private static FrameworkElement? _openLink;
    private static Border? _panel;
    private static int _outside;

    private sealed record Source(Func<Task<IReadOnlyList<MenuGroup>>> Groups, Action<MenuGroup, MenuEntry> Pick);

    static NavMenu()
    {
        OpenTimer.Tick += async (_, _) =>
        {
            OpenTimer.Stop();
            if (_pendingLink is { IsMouseOver: true } link && link.Tag is not null) await OpenAsync(link);
        };
        CloseTimer.Tick += (_, _) =>
        {
            if (Contains(_panel) || Contains(_openLink)) { _outside = 0; return; }
            if (++_outside >= 2) Close();
        };
    }

    private static Brush Freeze(Brush b) { b.Freeze(); return b; }

    private static readonly DependencyProperty SourceProperty = DependencyProperty.RegisterAttached("Source", typeof(Source), typeof(NavMenu));

    /// <summary>给导航上的一个栏目挂悬浮菜单。groups 每次打开时取（可以是异步读来的）。</summary>
    public static void Attach(FrameworkElement link, Func<Task<IReadOnlyList<MenuGroup>>> groups, Action<MenuGroup, MenuEntry> pick)
    {
        link.SetValue(SourceProperty, new Source(groups, pick));
        link.MouseEnter += (_, _) =>
        {
            _pendingLink = link;
            OpenTimer.Stop();
            if (_openLink != link) OpenTimer.Start();
        };
        link.MouseLeave += (_, _) => { if (_pendingLink == link) OpenTimer.Stop(); };
        link.PreviewMouseLeftButtonDown += (_, _) => Close();
    }

    public static void Close()
    {
        OpenTimer.Stop();
        CloseTimer.Stop();
        Popup.IsOpen = false;
        Popup.Child = null;
        _panel = null;
        _openLink = null;
    }

    private static bool Contains(FrameworkElement? fe)
    {
        if (fe is null || !fe.IsVisible || PresentationSource.FromVisual(fe) is null) return false;
        var cursor = System.Windows.Forms.Cursor.Position;
        var p = fe.PointFromScreen(new Point(cursor.X, cursor.Y));
        return p.X >= -4 && p.Y >= -4 && p.X <= fe.ActualWidth + 4 && p.Y <= fe.ActualHeight + 18; // 文字和面板之间的空隙也算
    }

    private static async Task OpenAsync(FrameworkElement link)
    {
        if (link.GetValue(SourceProperty) is not Source src) return;
        Close();
        BobCard.Hide();
        _openLink = link;

        var right = new StackPanel { Margin = new Thickness(22, 18, 18, 18) };
        var left = new StackPanel { Margin = new Thickness(10, 12, 10, 12) };
        var body = new Grid { MinHeight = 260 };
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(196) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(420) });
        body.Children.Add(left);
        var divider = new Border { Background = new SolidColorBrush(Color.FromArgb(0x1A, 255, 255, 255)), Margin = new Thickness(0, 16, 0, 16) };
        Grid.SetColumn(divider, 1);
        body.Children.Add(divider);
        Grid.SetColumn(right, 2);
        body.Children.Add(right);
        right.Children.Add(new TextBlock { Text = "读取中…", Foreground = Muted, FontSize = 13 });

        // 玻璃面板：阴影单独一层（效果叠在内容上会让整块每帧重画），上面是毛玻璃底和内容
        var glass = new Grid();
        glass.Children.Add(new Border
        {
            Background = Panel, CornerRadius = new CornerRadius(18), IsHitTestVisible = false,
            Effect = new DropShadowEffect { BlurRadius = 36, ShadowDepth = 10, Direction = 270, Opacity = 0.55, Color = Colors.Black, RenderingBias = RenderingBias.Performance },
        });
        glass.Children.Add(new GlassBackdrop { CornerRadius = 18 });
        glass.Children.Add(body);
        _panel = new Border { Child = glass };
        _panel.SetValue(TextElement.FontFamilyProperty, Application.Current.Resources["UiFont"]);
        _panel.SetValue(TextElement.ForegroundProperty, Brushes.White);

        var root = new StackPanel { Margin = new Thickness(Shadow, 10, Shadow, Shadow) };
        root.Children.Add(_panel);

        Popup.PlacementTarget = link;
        Popup.HorizontalOffset = -Shadow - 14;
        Popup.VerticalOffset = 2;
        Popup.Child = root;
        // 从导航项下方弹出（玻璃底在动画期间每帧重新对齐）
        Popup.IsOpen = true;
        Motion.PopIn(_panel, 0.94, 0.08, 0);
        _outside = 0;
        CloseTimer.Start();

        IReadOnlyList<MenuGroup> groups;
        try { groups = await src.Groups(); }
        catch (Exception e) when (e is Core.Http.SourceException or OperationCanceledException)
        {
            right.Children.Clear();
            right.Children.Add(new TextBlock { Text = "读取失败：" + e.Message, Foreground = Muted, FontSize = 13, TextWrapping = TextWrapping.Wrap });
            return;
        }
        if (_openLink != link || groups.Count == 0) return;

        var rows = new List<Border>();
        void ShowGroup(int index)
        {
            for (var i = 0; i < rows.Count; i++) SetRowState(rows[i], i == index);
            var g = groups[index];
            right.Children.Clear();
            var head = new TextBlock { FontSize = 12.5, Foreground = Muted, Margin = new Thickness(2, 0, 0, 10) };
            head.Inlines.Add(new Run(g.Name) { FontWeight = Weights.Bold });
            head.Inlines.Add(new Run($"   {g.Items.Count} 个"));
            right.Children.Add(head);
            var items = new WrapPanel();
            foreach (var entry in g.Items)
            {
                var e = entry;
                items.Children.Add(Item(e.Name, () => { Close(); src.Pick(g, e); }));
            }
            right.Children.Add(items);
            Motion.FadeIn(items, 160);
        }
        left.Children.Clear();
        for (var i = 0; i < groups.Count; i++)
        {
            var idx = i;
            var row = GroupRow(groups[i]);
            row.MouseEnter += (_, _) => ShowGroup(idx);
            rows.Add(row);
            left.Children.Add(row);
        }
        ShowGroup(0);
    }

    /// <summary>左栏一行：图标 + 组名 + 右箭头；当前组高亮，左边一道红线。</summary>
    private static Border GroupRow(MenuGroup g)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(new TextBlock { Text = g.Icon, FontFamily = (FontFamily)Application.Current.Resources["Icons"], FontSize = 15, Foreground = Muted, VerticalAlignment = VerticalAlignment.Center });
        var name = new TextBlock { Text = g.Name, FontSize = 14.5, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(name, 1);
        grid.Children.Add(name);
        var chevron = new TextBlock { Text = "", FontFamily = (FontFamily)Application.Current.Resources["Icons"], FontSize = 10, Foreground = Muted, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(chevron, 2);
        grid.Children.Add(chevron);
        return new Border
        {
            Child = grid, Padding = new Thickness(12, 10, 12, 10), CornerRadius = new CornerRadius(6), Cursor = Cursors.Hand,
            Background = Brushes.Transparent, BorderBrush = (Brush)Application.Current.Resources["Accent"], Margin = new Thickness(0, 1, 0, 1),
        };
    }

    private static void SetRowState(Border row, bool active)
    {
        row.Background = active ? Hover : Brushes.Transparent;
        row.BorderThickness = new Thickness(active ? 3 : 0, 0, 0, 0);
        if (row.Child is Grid g)
            foreach (var tb in g.Children.OfType<TextBlock>())
                if (Grid.GetColumn(tb) == 1) tb.FontWeight = active ? Weights.SemiBold : FontWeights.Normal;
    }

    /// <summary>右栏一项：两列排，悬停时浅底 + 文字变白。</summary>
    private static Border Item(string text, Action click)
    {
        var tb = new TextBlock { Text = text, FontSize = 14, Foreground = new SolidColorBrush(Color.FromRgb(0xD9, 0xD9, 0xD9)), TextTrimming = TextTrimming.CharacterEllipsis };
        var b = new Border { Child = tb, Width = 184, Padding = new Thickness(12, 8, 12, 8), CornerRadius = new CornerRadius(6), Background = Brushes.Transparent, Cursor = Cursors.Hand, Margin = new Thickness(0, 0, 6, 2) };
        b.MouseEnter += (_, _) => { b.Background = Hover; tb.Foreground = Brushes.White; };
        b.MouseLeave += (_, _) => { b.Background = Brushes.Transparent; tb.Foreground = new SolidColorBrush(Color.FromRgb(0xD9, 0xD9, 0xD9)); };
        b.MouseLeftButtonUp += (_, _) => click();
        return b;
    }
}
