using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;

namespace JableLens.App.Controls;

/// <summary>
/// 毛玻璃底：把主窗口页面（<see cref="Source"/>）在自己背后的那一块画进来、高斯模糊，再盖一层深色半透明和一圈上亮下暗的细边。
/// 顶栏、下拉菜单、悬停卡、右键菜单、详情弹窗都用它，所以整个软件的「玻璃」是同一种。
/// 弹出层是单独的窗口，也一样能用：按屏幕坐标换算出它在页面上对应的位置。
/// 用法：放在容器的第一个子元素，内容叠在上面。
/// </summary>
public sealed class GlassBackdrop : Grid
{
    private const double Pad = 40; // 四周多取一圈，模糊后边缘不发黑

    /// <summary>被模糊的页面（主窗口的页面宿主）。</summary>
    public static Visual? Source { get; set; }

    public static readonly DependencyProperty CornerRadiusProperty = DependencyProperty.Register(
        nameof(CornerRadius), typeof(double), typeof(GlassBackdrop), new PropertyMetadata(16.0, (d, _) => ((GlassBackdrop)d).UpdateClip()));

    public static readonly DependencyProperty TintProperty = DependencyProperty.Register(
        nameof(Tint), typeof(Brush), typeof(GlassBackdrop), new PropertyMetadata(new SolidColorBrush(Color.FromArgb(0xB8, 0x1C, 0x1C, 0x1E)), (d, _) => ((GlassBackdrop)d).ApplyStyle()));

    private static double _transparency = 0.5;
    private static double _blurRadius = 40;
    private static event Action? StyleChanged;

    /// <summary>全局透明度 0..1：0.5 是默认的深色玻璃，越大越透（只剩模糊），越小越接近不透明。设置页的滑块改它。</summary>
    public static double Transparency
    {
        get => _transparency;
        set { _transparency = Math.Clamp(value, 0, 1); StyleChanged?.Invoke(); }
    }

    /// <summary>全局模糊半径（0 = 不模糊）。</summary>
    public static double BlurRadius
    {
        get => _blurRadius;
        set { _blurRadius = Math.Clamp(value, 0, 100); StyleChanged?.Invoke(); }
    }

    public double CornerRadius { get => (double)GetValue(CornerRadiusProperty); set => SetValue(CornerRadiusProperty, value); }

    public Brush Tint { get => (Brush)GetValue(TintProperty); set => SetValue(TintProperty, value); }

    private readonly VisualBrush _brush = new() { ViewboxUnits = BrushMappingMode.Absolute, Stretch = Stretch.Fill, AutoLayoutContent = false };
    private readonly Rectangle _blur = new() { Margin = new Thickness(-Pad), Effect = new BlurEffect { Radius = 40, KernelType = KernelType.Gaussian, RenderingBias = RenderingBias.Performance } };
    private readonly Border _tint = new();
    private readonly Border _edge = new() { BorderThickness = new Thickness(1) };
    private Rect _lastBox;

    public GlassBackdrop()
    {
        IsHitTestVisible = false;
        ApplyStyle();
        _edge.BorderBrush = new LinearGradientBrush(Color.FromArgb(0x38, 255, 255, 255), Color.FromArgb(0x0F, 255, 255, 255), 90);
        _blur.Fill = _brush;
        Children.Add(new Border { Background = new SolidColorBrush(Color.FromRgb(0x1C, 0x1C, 0x1E)) }); // 页面没盖到的地方（比如页面上方）也是深色
        Children.Add(_blur);
        Children.Add(_tint);
        // 顶部一点点高光，像玻璃的反光
        Children.Add(new Border { Background = new LinearGradientBrush(Color.FromArgb(0x10, 255, 255, 255), Color.FromArgb(0, 255, 255, 255), 90) });
        Children.Add(_edge);
        SizeChanged += (_, _) => { UpdateClip(); Update(); };
        LayoutUpdated += (_, _) => Update();
        Loaded += (_, _) => { _brush.Visual = Source; StyleChanged += ApplyStyle; ApplyStyle(); Update(); FollowAnimation(); };
        Unloaded += (_, _) => StyleChanged -= ApplyStyle;
    }

    /// <summary>按全局透明度调深色层的不透明度（各处的基础深浅不同：顶栏浅些、详情弹窗深些），按全局值设模糊半径。</summary>
    private void ApplyStyle()
    {
        ((BlurEffect)_blur.Effect).Radius = _blurRadius;
        if (Tint is SolidColorBrush sc)
        {
            var factor = (1 - _transparency) * 2;
            var a = (byte)Math.Clamp(sc.Color.A * factor, 0, 250);
            _tint.Background = new SolidColorBrush(Color.FromArgb(a, sc.Color.R, sc.Color.G, sc.Color.B));
        }
        else _tint.Background = Tint;
    }

    /// <summary>弹出时的放大 / 淡入动画不触发布局，刚出现的半秒里每帧对一次位置。</summary>
    public void FollowAnimation()
    {
        var until = DateTime.UtcNow.AddMilliseconds(600);
        EventHandler? tick = null;
        tick = (_, _) =>
        {
            _lastBox = Rect.Empty;
            Update();
            if (DateTime.UtcNow > until || !IsLoaded) CompositionTarget.Rendering -= tick;
        };
        CompositionTarget.Rendering += tick;
    }

    private void UpdateClip()
    {
        Clip = new RectangleGeometry(new Rect(RenderSize), CornerRadius, CornerRadius);
        _edge.CornerRadius = new System.Windows.CornerRadius(CornerRadius);
    }

    /// <summary>算出自己在页面上的位置，让模糊的那一块正好是自己背后的内容。</summary>
    private void Update()
    {
        if (Source is not Visual src || ActualWidth <= 0 || PresentationSource.FromVisual(this) is null || PresentationSource.FromVisual(src) is null) return;
        _brush.Visual ??= src;
        Point p;
        try
        {
            p = ((UIElement)src).PointFromScreen(PointToScreen(new Point(0, 0)));
        }
        catch (InvalidOperationException) { return; }
        var box = new Rect(p.X - Pad, p.Y - Pad, ActualWidth + 2 * Pad, ActualHeight + 2 * Pad);
        if (box == _lastBox) return;
        _lastBox = box;
        _brush.Viewbox = box;
    }
}
