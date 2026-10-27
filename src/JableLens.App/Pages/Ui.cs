using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Media;

namespace JableLens.App.Pages;

/// <summary>页面基类：Title 显示在顶部；离开页面时取消未完成的加载。</summary>
public abstract class PageBase : UserControl
{
    protected PageBase() => Background = (Brush)Application.Current.Resources["Bg"];

    public abstract string Title { get; }

    /// <summary>全幅页面从窗口顶端开始画（大图在导航条下面透出来）；否则页面从导航条下方开始。</summary>
    public virtual bool FullBleed => false;

    protected CancellationTokenSource Life { get; } = new();

    /// <summary>页面被移出导航栈时调用。</summary>
    public virtual void OnClosed() => Life.Cancel();

    /// <summary>从别的页面返回到本页时调用（比如刷新收藏状态）。</summary>
    public virtual void OnRevisited() { }

    protected static MainWindow Shell => (MainWindow)Application.Current.MainWindow;
}

public static class Ui
{
    /// <summary>所有页面内容的左右边距（Netflix 约为窗口宽的 4%）：标题、筛选、卡片封面的左边缘都对齐到这里。</summary>
    public const double Gutter = 56;

    /// <summary>卡片模板左右各留 6（卡片之间 12 的缝），所以网格只留 Gutter - 6。</summary>
    public const double CardInset = 6;

    public static Thickness GridPadding => new(Gutter - CardInset, 0, Gutter - CardInset, 0);

    /// <summary>筛选行左边小标题的宽度（「排序」「年份」…），各页统一。</summary>
    public const double CaptionWidth = 56;

    public static TextBlock Text(string text, string? style = null, double? size = null, Brush? fg = null)
    {
        var t = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap };
        if (style is not null) t.Style = (Style)Application.Current.Resources[style];
        if (size is { } s) t.FontSize = s;
        if (fg is not null) t.Foreground = fg;
        return t;
    }

    public static Brush Res(string key) => (Brush)Application.Current.Resources[key];

    /// <summary>只有图标的按钮也给读屏 / 自动化一个名字。</summary>
    public static T WithName<T>(this T e, string name) where T : UIElement
    {
        AutomationProperties.SetName(e, name);
        return e;
    }

    public static Button Button(string text, RoutedEventHandler click, string style = "Btn", string? tip = null)
    {
        var b = new Button { Content = text, Style = (Style)Application.Current.Resources[style], Margin = new Thickness(0, 0, 8, 8), ToolTip = tip };
        b.Click += click;
        return b;
    }

    /// <summary>一组单选 chip。返回容器；选中项变化时回调 index。有小标题时，换行的 chip 仍对齐在小标题右边。</summary>
    public static Panel Chips(IReadOnlyList<string> labels, int selected, Action<int> onSelect, string? caption = null,
        string style = "ChipToggle", double captionWidth = CaptionWidth)
    {
        var panel = new WrapPanel();
        var buttons = new List<ToggleButton>();
        for (var i = 0; i < labels.Count; i++)
        {
            var idx = i;
            var b = new ToggleButton { Content = labels[i], IsChecked = i == selected, Style = (Style)Application.Current.Resources[style] };
            b.Click += (_, _) =>
            {
                foreach (var o in buttons) o.IsChecked = o == b;
                onSelect(idx);
            };
            buttons.Add(b);
            panel.Children.Add(b);
        }
        if (caption is null)
        {
            panel.Margin = new Thickness(0, 0, 0, 2);
            return panel;
        }
        var row = new DockPanel { Margin = new Thickness(0, 0, 0, 2), LastChildFill = true };
        var cap = Caption(caption, captionWidth);
        DockPanel.SetDock(cap, Dock.Left);
        row.Children.Add(cap);
        row.Children.Add(panel);
        return row;
    }

    /// <summary>筛选 / 设置行左边的小标题，和右边 chip 的文字垂直居中对齐（chip 高 30）。</summary>
    public static TextBlock Caption(string text, double width = CaptionWidth) => new()
    {
        Text = text, Width = width, Height = 30, Foreground = Res("Fg2"), FontSize = 13.5,
        Padding = new Thickness(0, 6, 0, 0), VerticalAlignment = VerticalAlignment.Top,
    };

    /// <summary>
    /// Netflix 下拉：黑底白框的按钮（可带灰色前缀「排序」），点开黑色面板，项多时自动分成多列。
    /// </summary>
    public static Button Dropdown(IReadOnlyList<string> labels, int selected, Action<int> onSelect, string? prefix = null, string style = "DropdownBtn")
    {
        var current = Math.Clamp(selected, 0, Math.Max(0, labels.Count - 1));
        var value = new TextBlock { VerticalAlignment = VerticalAlignment.Center };
        var content = new StackPanel { Orientation = Orientation.Horizontal };
        if (prefix is not null) content.Children.Add(new TextBlock { Text = prefix, Foreground = Res("Fg2"), Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center });
        content.Children.Add(value);
        content.Children.Add(new TextBlock { Text = "\uE70D", FontFamily = (FontFamily)Application.Current.Resources["Icons"], FontSize = 10, Margin = new Thickness(10, 2, 0, 0), VerticalAlignment = VerticalAlignment.Center });
        var btn = new Button { Style = (Style)Application.Current.Resources[style], Content = content };
        if (prefix is not null) AutomationProperties.SetName(btn, prefix);
        value.Text = labels.Count > 0 ? labels[current] : "";
        btn.Click += (_, _) =>
        {
            var popup = MenuPopup(btn);
            var cols = new WrapPanel { Orientation = Orientation.Vertical, MaxHeight = 400 };
            for (var i = 0; i < labels.Count; i++)
            {
                var idx = i;
                var link = new Button { Content = labels[i], Style = (Style)Application.Current.Resources["MenuLink"], MinWidth = 150, Margin = new Thickness(0, 0, 4, 0), FontWeight = i == current ? Weights.Bold : FontWeights.Normal };
                link.Click += (_, _) =>
                {
                    popup.IsOpen = false;
                    current = idx;
                    value.Text = labels[idx];
                    onSelect(idx);
                };
                cols.Children.Add(link);
            }
            popup.Child = MenuFrame(cols);
            popup.IsOpen = true;
        };
        return btn;
    }

    /// <summary>挂在某个按钮下方的弹出面板（点外面自动关）。</summary>
    public static Popup MenuPopup(FrameworkElement target)
    {
        var p = new Popup
        {
            PlacementTarget = target, Placement = PlacementMode.Bottom, VerticalOffset = 2,
            StaysOpen = false, AllowsTransparency = true, PopupAnimation = PopupAnimation.None,
        };
        // 从按钮下方弹出来
        p.Opened += (_, _) => { if (p.Child is FrameworkElement c) Controls.Motion.PopIn(c, 0.94, 0.2, 0); };
        return p;
    }

    /// <summary>下拉面板的外框：和顶栏同一种毛玻璃，圆角 16。</summary>
    public static Border MenuFrame(UIElement content)
    {
        var glass = new Grid();
        glass.Children.Add(new Controls.GlassBackdrop { CornerRadius = 16 });
        glass.Children.Add(new Border { Padding = new Thickness(8), Child = content });
        var b = new Border { Child = glass };
        // 弹出层不在窗口的可视树里，字体颜色不会继承，自己设
        b.SetValue(TextElement.FontFamilyProperty, Application.Current.Resources["UiFont"]);
        b.SetValue(TextElement.ForegroundProperty, Res("Fg"));
        return b;
    }

    public static Border Divider() => new() { Height = 1, Background = Res("Line"), Margin = new Thickness(0, 12, 0, 12) };
}
