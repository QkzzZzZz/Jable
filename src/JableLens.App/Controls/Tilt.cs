using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace JableLens.App.Controls;

/// <summary>
/// Apple TV 那种悬停视差：鼠标在封面上移动时，卡片略微抬起并朝鼠标方向倾斜，封面图反向平移形成视差，
/// 表面一道柔和的高光跟着鼠标走；离开时慢慢回正。
/// WPF 没有透视变换，倾斜用小角度斜切模拟。只改渲染变换、不触发排版；只有正在动的卡片每帧计算。
/// 用法：加在封面那个带圆角裁剪的 Grid 上：c:Tilt.IsEnabled="True"。
/// </summary>
public static class Tilt
{
    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled", typeof(bool), typeof(Tilt), new PropertyMetadata(false, OnChanged));

    public static bool GetIsEnabled(DependencyObject o) => (bool)o.GetValue(IsEnabledProperty);
    public static void SetIsEnabled(DependencyObject o, bool v) => o.SetValue(IsEnabledProperty, v);

    private const double MaxSkew = 1.4;     // 最大斜切角（度）
    private const double Lift = 1.04;       // 悬停时放大
    private const double ImageZoom = 1.06;  // 封面图额外放大，平移时边缘不露底
    private const double Shift = 6;         // 封面视差平移（像素）
    private const double Glare = 0.16;      // 高光最亮处的不透明度

    private sealed class State
    {
        public required Grid Host;
        public readonly ScaleTransform Scale = new(1, 1);
        public readonly SkewTransform Skew = new();
        public readonly ScaleTransform ImageScale = new(1, 1);
        public readonly TranslateTransform ImageShift = new();
        public readonly RadialGradientBrush GlareBrush = new(Color.FromArgb(0, 255, 255, 255), Color.FromArgb(0, 255, 255, 255)) { RadiusX = 0.9, RadiusY = 0.9 };
        public readonly Border GlareLayer = new() { IsHitTestVisible = false, Opacity = 0 };
        public Image? Image;
        public double X, Y, Up;       // 当前值：鼠标位置 -1..1、抬起程度 0..1
        public double TX, TY, TUp;    // 目标值
    }

    private static readonly DependencyProperty StateProperty = DependencyProperty.RegisterAttached(
        "State", typeof(State), typeof(Tilt), new PropertyMetadata(null));

    private static readonly HashSet<State> Moving = [];
    private static TimeSpan _last;

    private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not Grid g) return;
        g.MouseEnter -= OnEnter;
        g.MouseMove -= OnMove;
        g.MouseLeave -= OnLeave;
        if (!(bool)e.NewValue) return;
        g.MouseEnter += OnEnter;
        g.MouseMove += OnMove;
        g.MouseLeave += OnLeave;
    }

    private static State StateOf(Grid g)
    {
        if (g.GetValue(StateProperty) is State s) return s;
        s = new State { Host = g };
        s.GlareBrush.GradientStops[0].Color = Color.FromArgb((byte)(255 * Glare), 255, 255, 255);
        s.GlareLayer.Background = s.GlareBrush;
        g.RenderTransformOrigin = new Point(0.5, 0.5);
        g.RenderTransform = new TransformGroup { Children = { s.Skew, s.Scale } };
        g.SetValue(StateProperty, s);
        return s;
    }

    /// <summary>封面图和高光层：模板里的元素被复用时可能换过，进入时再确认一次。</summary>
    private static void Prepare(State s)
    {
        var img = FirstImage(s.Host);
        if (img != s.Image)
        {
            s.Image = img;
            if (img is not null)
            {
                img.RenderTransformOrigin = new Point(0.5, 0.5);
                img.RenderTransform = new TransformGroup { Children = { s.ImageScale, s.ImageShift } };
            }
        }
        if (s.GlareLayer.Parent is null)
        {
            Grid.SetRowSpan(s.GlareLayer, 99);
            Grid.SetColumnSpan(s.GlareLayer, 99);
            s.Host.Children.Add(s.GlareLayer);
        }
    }

    private static Image? FirstImage(DependencyObject root)
    {
        var queue = new Queue<DependencyObject>([root]);
        while (queue.Count > 0)
        {
            var n = queue.Dequeue();
            if (n is Image i) return i;
            for (var k = 0; k < VisualTreeHelper.GetChildrenCount(n); k++) queue.Enqueue(VisualTreeHelper.GetChild(n, k));
        }
        return null;
    }

    private static void OnEnter(object sender, MouseEventArgs e)
    {
        var s = StateOf((Grid)sender);
        Prepare(s);
        s.TUp = 1;
        Aim(s, e);
        Start(s);
    }

    private static void OnMove(object sender, MouseEventArgs e)
    {
        // 被 Reset 过（悬停卡盖在上面）就不再跟随；悬停卡收起、鼠标回到卡片上时会重新触发 MouseEnter
        if (((Grid)sender).GetValue(StateProperty) is State s && s.TUp > 0) Aim(s, e);
    }

    private static void OnLeave(object sender, MouseEventArgs e)
    {
        if (((Grid)sender).GetValue(StateProperty) is not State s) return;
        s.TX = s.TY = s.TUp = 0;
        Start(s);
    }

    /// <summary>
    /// 立刻回正、停止跟随（不做回弹）。悬停卡弹出、详情展开前调用：弹出层按卡片当时的变换定位，
    /// 卡片还在倾斜 / 放大的话，弹出层会先出现在偏掉的位置，等卡片回正后又跳回来。
    /// </summary>
    public static void Reset(DependencyObject? d)
    {
        for (; d is not null; d = Tree.Parent(d))
        {
            if (d is not Grid g || g.GetValue(StateProperty) is not State s) continue;
            s.X = s.Y = s.Up = s.TX = s.TY = s.TUp = 0;
            Apply(s);
            Moving.Remove(s);
            if (Moving.Count == 0) CompositionTarget.Rendering -= OnFrame;
            return;
        }
    }

    private static void Aim(State s, MouseEventArgs e)
    {
        var w = s.Host.ActualWidth;
        var h = s.Host.ActualHeight;
        if (w <= 0 || h <= 0) return;
        // 位置按未变换的大小算（抬起后的放大会让坐标略超 ±1，夹住即可）
        var p = e.GetPosition(s.Host);
        s.TX = Math.Clamp(p.X / w * 2 - 1, -1, 1);
        s.TY = Math.Clamp(p.Y / h * 2 - 1, -1, 1);
    }

    private static void Start(State s)
    {
        Moving.Add(s);
        if (Moving.Count == 1)
        {
            _last = TimeSpan.Zero;
            CompositionTarget.Rendering += OnFrame;
        }
    }

    private static void OnFrame(object? sender, EventArgs e)
    {
        var now = ((RenderingEventArgs)e).RenderingTime;
        var dt = _last == TimeSpan.Zero ? 1000.0 / 60 : (now - _last).TotalMilliseconds;
        _last = now;
        if (dt <= 0) return;
        // 跟随：60Hz 每帧走剩余的 18%，按真实时间换算；抬起 / 落下稍慢一点
        var k = 1 - Math.Pow(0.82, Math.Min(dt, 100) / (1000.0 / 60));
        var kUp = 1 - Math.Pow(0.86, Math.Min(dt, 100) / (1000.0 / 60));

        foreach (var s in Moving.ToArray())
        {
            s.X += (s.TX - s.X) * k;
            s.Y += (s.TY - s.Y) * k;
            s.Up += (s.TUp - s.Up) * kUp;
            Apply(s);
            if (s.TUp == 0 && Math.Abs(s.X) < 0.002 && Math.Abs(s.Y) < 0.002 && s.Up < 0.002)
            {
                s.X = s.Y = s.Up = 0;
                Apply(s);
                Moving.Remove(s);
            }
        }
        if (Moving.Count == 0) CompositionTarget.Rendering -= OnFrame;
    }

    private static void Apply(State s)
    {
        var lift = 1 + (Lift - 1) * s.Up;
        s.Scale.ScaleX = s.Scale.ScaleY = lift;
        // 鼠标在右边：右侧往下压；在下边：下沿往左偏——小角度下看起来就是朝鼠标方向倾斜
        s.Skew.AngleY = s.X * MaxSkew * s.Up;
        s.Skew.AngleX = -s.Y * MaxSkew * s.Up;
        var zoom = 1 + (ImageZoom - 1) * s.Up;
        s.ImageScale.ScaleX = s.ImageScale.ScaleY = zoom;
        s.ImageShift.X = -s.X * Shift * s.Up;
        s.ImageShift.Y = -s.Y * Shift * s.Up;
        // 高光中心跟着鼠标
        var c = new Point((s.X + 1) / 2, (s.Y + 1) / 2);
        s.GlareBrush.Center = c;
        s.GlareBrush.GradientOrigin = c;
        s.GlareLayer.Opacity = s.Up;
    }
}
