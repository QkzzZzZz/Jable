using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace JableLens.App.Controls;

/// <summary>
/// 卡片展开成详情（Material 的「容器变换」/ App Store 那种）：详情弹窗本身从被点的卡片的位置和大小长出来——
/// 开始时缩到卡片大小、只露出顶部大图那一块（正好盖住卡片），然后放大到原位，同时往下展开露出其余内容；关闭时反过来缩回卡片。
/// 只有弹窗一个东西在动，没有替身，所以不会有「换图」那一下闪。
/// </summary>
public static class CardFly
{
    private static readonly Duration OpenTime = new(TimeSpan.FromMilliseconds(460));
    private static readonly Duration CloseTime = new(TimeSpan.FromMilliseconds(340));
    private static readonly IEasingFunction OpenEase = Frozen(new QuinticEase { EasingMode = EasingMode.EaseOut });
    private static readonly IEasingFunction CloseEase = Frozen(new CubicEase { EasingMode = EasingMode.EaseInOut });
    private const double CardRadius = 12, PanelRadius = 24;

    private static IEasingFunction Frozen(EasingFunctionBase e) { e.Freeze(); return e; }

    public static TimeSpan CloseDuration => CloseTime.TimeSpan;

    /// <summary>这个元素还在窗口里、看得见，能当起点 / 终点。</summary>
    public static bool Usable(FrameworkElement? fe, Visual window) =>
        fe is { IsVisible: true, ActualWidth: > 0, ActualHeight: > 0 } && PresentationSource.FromVisual(fe) is { } ps && ps == PresentationSource.FromVisual(window);

    /// <summary>卡片和大图形状差不多（都是 16:9）才做形变；竖版海报卡片差太多，会被压扁，交给普通弹出。</summary>
    public static bool SameShape(FrameworkElement card, FrameworkElement hero)
    {
        if (card.ActualHeight <= 0 || hero.ActualHeight <= 0) return false;
        var a = card.ActualWidth / card.ActualHeight;
        var b = hero.ActualWidth / hero.ActualHeight;
        return Math.Abs(a - b) < b * 0.2;
    }

    /// <summary>卡片封面里的图（模板里第一张有图的 Image）。</summary>
    public static ImageSource? ImageOf(DependencyObject root)
    {
        var queue = new Queue<DependencyObject>([root]);
        while (queue.Count > 0)
        {
            var n = queue.Dequeue();
            if (n is System.Windows.Controls.Image { Source: { } s }) return s;
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(n); i++) queue.Enqueue(VisualTreeHelper.GetChild(n, i));
        }
        return null;
    }

    /// <summary>
    /// 弹窗 <paramref name="panel"/> 在卡片 <paramref name="card"/> 和自己原位之间形变：<paramref name="open"/> 为真是展开，否则收回。
    /// <paramref name="hero"/> 是弹窗里的顶部大图，形变时它和卡片封面对齐。坐标都换算到 <paramref name="root"/> 上。调用前弹窗要已经排好版。
    /// </summary>
    public static void Morph(FrameworkElement panel, FrameworkElement hero, FrameworkElement card, Visual root, bool open, Action? done = null)
    {
        panel.RenderTransform = null;
        panel.Clip = null;
        var p = panel.TransformToVisual(root).TransformBounds(new Rect(panel.RenderSize));
        var h = hero.TransformToVisual(panel).TransformBounds(new Rect(hero.RenderSize));
        var c = card.TransformToVisual(root).TransformBounds(new Rect(card.RenderSize));
        if (h.Width <= 0 || h.Height <= 0) { done?.Invoke(); return; }

        // 缩小到卡片时：大图区域 h 正好落在卡片 c 上
        var sx = c.Width / h.Width;
        var sy = c.Height / h.Height;
        var tx = c.X - p.X - h.X * sx;
        var ty = c.Y - p.Y - h.Y * sy;

        var scale = new ScaleTransform();
        var move = new TranslateTransform();
        panel.RenderTransformOrigin = new Point(0, 0);
        panel.RenderTransform = new TransformGroup { Children = { scale, move } };
        var clip = new RectangleGeometry();
        panel.Clip = clip;

        var time = open ? OpenTime : CloseTime;
        var ease = open ? OpenEase : CloseEase;
        DoubleAnimation D(double small, double full) => open ? new(small, full, time) { EasingFunction = ease } : new(full, small, time) { EasingFunction = ease };

        // 圆角：缩小时换算回屏幕上是卡片的 8，展开后是弹窗的 20
        var r = D(CardRadius / sx, PanelRadius);
        var rect = open ? new RectAnimation(h, new Rect(panel.RenderSize), time) { EasingFunction = ease }
                        : new RectAnimation(new Rect(panel.RenderSize), h, time) { EasingFunction = ease };
        rect.Completed += (_, _) =>
        {
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            move.BeginAnimation(TranslateTransform.XProperty, null);
            move.BeginAnimation(TranslateTransform.YProperty, null);
            if (open) // 展开完：拿掉变换和裁剪（裁剪会把弹窗的阴影切掉）
            {
                panel.RenderTransform = null;
                panel.Clip = null;
            }
            done?.Invoke();
        };

        scale.BeginAnimation(ScaleTransform.ScaleXProperty, D(sx, 1));
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, D(sy, 1));
        move.BeginAnimation(TranslateTransform.XProperty, D(tx, 0));
        move.BeginAnimation(TranslateTransform.YProperty, D(ty, 0));
        clip.BeginAnimation(RectangleGeometry.RadiusXProperty, r);
        clip.BeginAnimation(RectangleGeometry.RadiusYProperty, r);
        clip.BeginAnimation(RectangleGeometry.RectProperty, rect);
        Motion.FollowGlass(panel); // 弹窗里的毛玻璃逐帧对齐
    }
}
