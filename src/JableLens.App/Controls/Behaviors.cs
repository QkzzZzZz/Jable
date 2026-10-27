using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using JableLens.App.Services;

namespace JableLens.App.Controls;

/// <summary>
/// 往上找父元素。鼠标事件的来源可能是文字里的 Run（不是 Visual），直接 VisualTreeHelper.GetParent 会抛
/// 「System.Windows.Documents.Run 不是 Visual 或 Visual3D」——这种走逻辑树。
/// </summary>
public static class Tree
{
    public static DependencyObject? Parent(DependencyObject d) =>
        d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) ?? LogicalTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
}

/// <summary>按元素实际大小设置圆角裁剪（Border 的 CornerRadius 裁不了里面的图片）。</summary>
public static class RoundClip
{
    public static readonly DependencyProperty RadiusProperty = DependencyProperty.RegisterAttached(
        "Radius", typeof(double), typeof(RoundClip), new PropertyMetadata(0.0, OnChanged));

    public static double GetRadius(DependencyObject o) => (double)o.GetValue(RadiusProperty);
    public static void SetRadius(DependencyObject o, double v) => o.SetValue(RadiusProperty, v);

    private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement fe) return;
        fe.SizeChanged -= Update;
        fe.SizeChanged += Update;
        Apply(fe);
    }

    private static void Update(object sender, SizeChangedEventArgs e) => Apply((FrameworkElement)sender);

    private static void Apply(FrameworkElement fe)
    {
        var r = GetRadius(fe);
        var g = new RectangleGeometry(new Rect(0, 0, fe.ActualWidth, fe.ActualHeight), r, r);
        g.Freeze();
        fe.Clip = g;
    }
}

/// <summary>
/// 封面的细边（Apple TV 那种）：在 Grid 最上层叠一圈很淡的白色描边，暗色封面贴着深色背景时也看得出边缘。
/// 用法：c:Hairline.Radius="12"（和封面的圆角一致）。
/// </summary>
public static class Hairline
{
    public static readonly DependencyProperty RadiusProperty = DependencyProperty.RegisterAttached(
        "Radius", typeof(double), typeof(Hairline), new PropertyMetadata(0.0, OnChanged));

    public static double GetRadius(DependencyObject o) => (double)o.GetValue(RadiusProperty);
    public static void SetRadius(DependencyObject o, double v) => o.SetValue(RadiusProperty, v);

    private static readonly Brush Edge = Freeze(new SolidColorBrush(Color.FromArgb(0x1F, 255, 255, 255)));

    private static Brush Freeze(Brush b) { b.Freeze(); return b; }

    private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not Grid g || e.NewValue is not double r || r <= 0) return;
        var line = new Border { BorderBrush = Edge, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(r), IsHitTestVisible = false };
        Grid.SetRowSpan(line, 99);
        Grid.SetColumnSpan(line, 99);
        Panel.SetZIndex(line, 50);
        g.Children.Add(line);
    }
}

/// <summary>
/// 卡片悬停效果：放大 + 阴影 + 抬到最上层（Netflix / Apple TV 那种）。
/// Scale=1.06 表示放大 6%。只改 RenderTransform，不触发重新排版，很省。
/// </summary>
public static class HoverZoom
{
    public static readonly DependencyProperty ScaleProperty = DependencyProperty.RegisterAttached(
        "Scale", typeof(double), typeof(HoverZoom), new PropertyMetadata(1.0, OnChanged));

    public static double GetScale(DependencyObject o) => (double)o.GetValue(ScaleProperty);
    public static void SetScale(DependencyObject o, double v) => o.SetValue(ScaleProperty, v);

    private static readonly Duration In = new(TimeSpan.FromMilliseconds(170));
    private static readonly IEasingFunction Ease = new CubicEase { EasingMode = EasingMode.EaseOut };

    private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement fe) return;
        fe.RenderTransformOrigin = new Point(0.5, 0.5);
        fe.RenderTransform = new ScaleTransform(1, 1);
        fe.MouseEnter -= OnEnter;
        fe.MouseLeave -= OnLeave;
        fe.MouseEnter += OnEnter;
        fe.MouseLeave += OnLeave;
    }

    private static void OnEnter(object sender, MouseEventArgs e)
    {
        var fe = (FrameworkElement)sender;
        Animate(fe, GetScale(fe));
        SetZ(fe, 10);
        fe.Effect = Shadow;
    }

    private static readonly DropShadowEffect Shadow = Frozen(new DropShadowEffect { BlurRadius = 28, ShadowDepth = 6, Opacity = 0.65, Color = Colors.Black, RenderingBias = RenderingBias.Performance });

    private static DropShadowEffect Frozen(DropShadowEffect e) { e.Freeze(); return e; }

    private static void OnLeave(object sender, MouseEventArgs e)
    {
        var fe = (FrameworkElement)sender;
        Animate(fe, 1);
        SetZ(fe, 0);
        fe.Effect = null;
    }

    private static void Animate(FrameworkElement fe, double to)
    {
        if (fe.RenderTransform is not ScaleTransform st || st.IsFrozen) fe.RenderTransform = st = new ScaleTransform(1, 1);
        // 放大用弹簧，缩回用普通缓出（缩回时回弹会显得抖）
        var a = to > 1 ? new DoubleAnimation(to, Motion.SpringTime) { EasingFunction = Motion.Spring } : new DoubleAnimation(to, In) { EasingFunction = Ease };
        st.BeginAnimation(ScaleTransform.ScaleXProperty, a);
        st.BeginAnimation(ScaleTransform.ScaleYProperty, a);
    }

    /// <summary>放大的卡片要盖住邻居：把它所在的面板子元素抬到上面。</summary>
    private static void SetZ(DependencyObject d, int z)
    {
        for (var cur = d; cur is not null; cur = Tree.Parent(cur))
        {
            if (Tree.Parent(cur) is Panel && cur is UIElement ui)
            {
                Panel.SetZIndex(ui, z);
                return;
            }
        }
    }
}

/// <summary>
/// 悬停预览（YouTube 那种）：鼠标停留 0.7 秒后在封面上静音循环播放预览短片。
/// 预览文件先下载到本地缓存（约 200KB），同一时间只播一个。
/// </summary>
public static class HoverPreview
{
    public static readonly DependencyProperty UrlProperty = DependencyProperty.RegisterAttached(
        "Url", typeof(string), typeof(HoverPreview), new PropertyMetadata(null, OnChanged));

    public static string? GetUrl(DependencyObject o) => (string?)o.GetValue(UrlProperty);
    public static void SetUrl(DependencyObject o, string? v) => o.SetValue(UrlProperty, v);

    private static readonly DispatcherTimer Delay = new() { Interval = TimeSpan.FromMilliseconds(700) };
    private static Grid? _pendingHost;
    private static (Grid Host, MediaElement Player)? _active;
    private static readonly SemaphoreSlim Downloads = new(2, 2);

    public static string Folder => JableLens.Core.Storage.AppPaths.Combine("preview");

    static HoverPreview()
    {
        Delay.Tick += async (_, _) =>
        {
            Delay.Stop();
            if (_pendingHost is { } host && host.IsMouseOver && GetUrl(host) is { } url) await StartAsync(host, url);
        };
    }

    private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not Grid g) return;
        g.MouseEnter -= OnEnter;
        g.MouseLeave -= OnLeave;
        g.MouseEnter += OnEnter;
        g.MouseLeave += OnLeave;
    }

    private static void OnEnter(object sender, MouseEventArgs e)
    {
        if (string.IsNullOrEmpty(GetUrl((Grid)sender))) return;
        _pendingHost = (Grid)sender;
        Delay.Stop();
        Delay.Start();
    }

    private static void OnLeave(object sender, MouseEventArgs e)
    {
        if (_pendingHost == sender) { _pendingHost = null; Delay.Stop(); }
        if (_active is { } a && a.Host == sender) Stop();
    }

    private static async Task StartAsync(Grid host, string url)
    {
        Stop();
        var file = await DownloadAsync(url);
        if (file is null || !host.IsMouseOver) return;
        var player = new MediaElement
        {
            LoadedBehavior = MediaState.Manual, UnloadedBehavior = MediaState.Close, IsMuted = true,
            Stretch = Stretch.UniformToFill, Opacity = 0, IsHitTestVisible = false, Source = new Uri(file),
        };
        player.MediaOpened += (_, _) => player.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(250)));
        player.MediaEnded += (_, _) => { player.Position = TimeSpan.Zero; player.Play(); };
        player.MediaFailed += (_, _) => Stop();
        // 放在封面图片之上、角标之下
        host.Children.Insert(Math.Min(1, host.Children.Count), player);
        _active = (host, player);
        player.Play();
    }

    private static void Stop()
    {
        if (_active is not { } a) return;
        _active = null;
        a.Player.Stop();
        a.Player.Close();
        a.Host.Children.Remove(a.Player);
    }

    /// <summary>预览短片下载到本地缓存，返回文件路径（失败返回 null）。</summary>
    public static async Task<string?> DownloadAsync(string url)
    {
        var file = Path.Combine(Folder, Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(url))) + ".mp4");
        if (File.Exists(file)) return file;
        if (ImageLoader.Http is null) return null;
        await Downloads.WaitAsync();
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            using var resp = await ImageLoader.Http.SendAsync(req);
            if (!resp.IsSuccessStatusCode) return null;
            Directory.CreateDirectory(Folder);
            var bytes = await resp.Content.ReadAsByteArrayAsync();
            await File.WriteAllBytesAsync(file, bytes);
            return file;
        }
        catch (Exception e) when (e is HttpRequestException or IOException or TaskCanceledException or InvalidOperationException)
        {
            return null;
        }
        finally
        {
            Downloads.Release();
        }
    }
}

/// <summary>
/// 平滑滚动：滚轮不再一格一格跳，而是带缓动地滑到目标位置。
/// 鼠标在内层还能竖向滚动的区域里时，交给内层处理。
/// </summary>
public static class SmoothScroll
{
    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled", typeof(bool), typeof(SmoothScroll), new PropertyMetadata(false, OnChanged));

    public static bool GetIsEnabled(DependencyObject o) => (bool)o.GetValue(IsEnabledProperty);
    public static void SetIsEnabled(DependencyObject o, bool v) => o.SetValue(IsEnabledProperty, v);

    private sealed class State
    {
        public double TargetY;
        public double TargetX;
        public bool Running;
        public TimeSpan Last;
        public EventHandler? Tick;
    }

    private static readonly DependencyProperty StateProperty = DependencyProperty.RegisterAttached(
        "State", typeof(State), typeof(SmoothScroll), new PropertyMetadata(null));

    private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ScrollViewer sv) return;
        sv.PreviewMouseWheel -= OnWheel;
        if ((bool)e.NewValue) sv.PreviewMouseWheel += OnWheel;
    }

    private static void OnWheel(object sender, MouseWheelEventArgs e)
    {
        var sv = (ScrollViewer)sender;
        // 内层有自己能竖向滚动的 ScrollViewer：让它处理
        for (var cur = e.OriginalSource as DependencyObject; cur is not null && cur != sv; cur = Tree.Parent(cur))
            if (cur is ScrollViewer inner && inner.ScrollableHeight > 0) return;
        if (sv.ScrollableHeight <= 0) return;
        e.Handled = true;
        var st = StateOf(sv);
        if (!st.Running) { st.TargetY = sv.VerticalOffset; st.TargetX = sv.HorizontalOffset; }
        st.TargetY = Math.Clamp(st.TargetY - e.Delta * 1.4, 0, sv.ScrollableHeight);
        Run(sv, st);
    }

    /// <summary>横向平滑滚动一段距离（行的左右箭头用）。</summary>
    public static void ScrollHorizontallyBy(ScrollViewer sv, double dx)
    {
        var st = StateOf(sv);
        if (!st.Running) { st.TargetX = sv.HorizontalOffset; st.TargetY = sv.VerticalOffset; }
        st.TargetX = Math.Clamp(st.TargetX + dx, 0, sv.ScrollableWidth);
        Run(sv, st);
    }

    private static State StateOf(ScrollViewer sv)
    {
        if (sv.GetValue(StateProperty) is State s) return s;
        s = new State { TargetY = sv.VerticalOffset, TargetX = sv.HorizontalOffset };
        sv.SetValue(StateProperty, s);
        return s;
    }

    private static void Run(ScrollViewer sv, State st)
    {
        if (st.Running) return;
        st.Running = true;
        st.Last = TimeSpan.Zero;
        st.Tick = (_, e) =>
        {
            // 按真实经过的时间推进（60Hz 每帧走剩余距离的 20%），刷新率不同、偶尔掉帧时速度都一样
            var now = ((RenderingEventArgs)e).RenderingTime;
            var dt = st.Last == TimeSpan.Zero ? 1000.0 / 60 : (now - st.Last).TotalMilliseconds;
            st.Last = now;
            if (dt <= 0) return; // 同一帧触发了两次
            var k = 1 - Math.Pow(0.8, Math.Min(dt, 100) / (1000.0 / 60));
            var dy = st.TargetY - sv.VerticalOffset;
            var dx = st.TargetX - sv.HorizontalOffset;
            if (Math.Abs(dy) < 0.6 && Math.Abs(dx) < 0.6)
            {
                sv.ScrollToVerticalOffset(st.TargetY);
                sv.ScrollToHorizontalOffset(st.TargetX);
                CompositionTarget.Rendering -= st.Tick;
                st.Running = false;
                return;
            }
            if (Math.Abs(dy) >= 0.6) sv.ScrollToVerticalOffset(sv.VerticalOffset + dy * k);
            if (Math.Abs(dx) >= 0.6) sv.ScrollToHorizontalOffset(sv.HorizontalOffset + dx * k);
        };
        CompositionTarget.Rendering += st.Tick;
    }
}

/// <summary>
/// 弹簧曲线（苹果那种）：阻尼振动 1 - e^(-ζωt)(cos ω'd t + …)，ζ=0.72 时冲过头约 4% 再回来，
/// 看起来「有重量」但不晃。EaseIn 模式下 WPF 直接用 EaseInCore 的结果。
/// </summary>
public sealed class SpringEase : EasingFunctionBase
{
    private const double Zeta = 0.72, Omega = 10;
    private static readonly double Wd = Omega * Math.Sqrt(1 - Zeta * Zeta);

    public SpringEase() => EasingMode = EasingMode.EaseIn;

    protected override double EaseInCore(double t) =>
        t >= 1 ? 1 : 1 - Math.Exp(-Zeta * Omega * t) * (Math.Cos(Wd * t) + Zeta * Omega / Wd * Math.Sin(Wd * t));

    protected override Freezable CreateInstanceCore() => new SpringEase();
}

/// <summary>全局动画：弹簧缩放 / 位移 + 淡入，统一时长和曲线。</summary>
public static class Motion
{
    public static readonly IEasingFunction Ease = Frozen(new CubicEase { EasingMode = EasingMode.EaseOut });
    public static readonly IEasingFunction EaseIn = Frozen(new CubicEase { EasingMode = EasingMode.EaseIn });
    public static readonly IEasingFunction Spring = Frozen(new SpringEase());

    /// <summary>弹簧动画的时长（弹簧本身在约 70% 处就基本停稳）。</summary>
    public static readonly Duration SpringTime = new(TimeSpan.FromMilliseconds(460));
    private static readonly Duration FadeTime = new(TimeSpan.FromMilliseconds(200));

    private static IEasingFunction Frozen(EasingFunctionBase e) { e.Freeze(); return e; }

    /// <summary>页面进入（影院转场）：淡入，同时从 98.5% 缓缓放大到原大小，不再往上浮。</summary>
    public static void EnterPage(FrameworkElement fe)
    {
        var s = new ScaleTransform(0.985, 0.985);
        fe.RenderTransformOrigin = new Point(0.5, 0.35);
        fe.RenderTransform = s;
        fe.Opacity = 0;
        var time = new Duration(TimeSpan.FromMilliseconds(380));
        fe.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, new Duration(TimeSpan.FromMilliseconds(320))) { EasingFunction = Ease });
        s.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.985, 1, time) { EasingFunction = Ease });
        s.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.985, 1, time) { EasingFunction = Ease });
    }

    public static void FadeIn(UIElement e, int ms = 300) =>
        e.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(ms)) { EasingFunction = Ease });

    /// <summary>弹出：从 <paramref name="from"/> 倍弹到原大小 + 淡入（弹窗、菜单、悬停卡）。</summary>
    public static void PopIn(FrameworkElement fe, double from = 0.92, double originX = 0.5, double originY = 0.5)
    {
        var st = new ScaleTransform(from, from);
        fe.RenderTransformOrigin = new Point(originX, originY);
        fe.RenderTransform = st;
        st.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(from, 1, SpringTime) { EasingFunction = Spring });
        st.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(from, 1, SpringTime) { EasingFunction = Spring });
        fe.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, FadeTime) { EasingFunction = Ease });
        FollowGlass(fe);
    }

    /// <summary>已经显示着的玻璃底（比如详情弹窗）不会再触发 Loaded，缩放时要叫它们逐帧对齐。</summary>
    public static void FollowGlass(DependencyObject d)
    {
        if (d is GlassBackdrop { IsLoaded: true } g) { g.FollowAnimation(); return; }
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(d); i++) FollowGlass(VisualTreeHelper.GetChild(d, i));
    }

    /// <summary>收起：缩小一点 + 淡出，结束后调用 <paramref name="done"/>。</summary>
    public static void PopOut(FrameworkElement fe, Action done, double to = 0.95)
    {
        var d = new Duration(TimeSpan.FromMilliseconds(170));
        if (fe.RenderTransform is not ScaleTransform st || st.IsFrozen) fe.RenderTransform = st = new ScaleTransform(1, 1);
        fe.RenderTransformOrigin = new Point(0.5, 0.5);
        st.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(to, d) { EasingFunction = EaseIn });
        st.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(to, d) { EasingFunction = EaseIn });
        var fade = new DoubleAnimation(0, d) { EasingFunction = EaseIn };
        fade.Completed += (_, _) => done();
        fe.BeginAnimation(UIElement.OpacityProperty, fade);
        FollowGlass(fe);
    }

    /// <summary>停掉 PopOut 留下的动画，恢复原样（收起途中又被打开时用）。</summary>
    public static void Reset(FrameworkElement fe)
    {
        fe.BeginAnimation(UIElement.OpacityProperty, null);
        fe.Opacity = 1;
        if (fe.RenderTransform is ScaleTransform { IsFrozen: false } st)
        {
            st.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            st.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        }
    }

    private static bool _menusHooked;

    /// <summary>所有右键 / 下拉菜单（ContextMenu）打开时从上沿弹出来。程序启动时调用一次。</summary>
    public static void AnimateContextMenus()
    {
        if (_menusHooked) return;
        _menusHooked = true;
        EventManager.RegisterClassHandler(typeof(ContextMenu), ContextMenu.OpenedEvent, new RoutedEventHandler((s, _) =>
        {
            if (s is ContextMenu m && VisualTreeHelper.GetChildrenCount(m) > 0 && VisualTreeHelper.GetChild(m, 0) is FrameworkElement root)
                PopIn(root, 0.94, 0.5, 0);
        }));
    }

    // ---- 卡片依次浮现：加在卡片模板的根元素上 ----

    public static readonly DependencyProperty AppearProperty = DependencyProperty.RegisterAttached(
        "Appear", typeof(bool), typeof(Motion), new PropertyMetadata(false, OnAppearChanged));

    public static bool GetAppear(DependencyObject o) => (bool)o.GetValue(AppearProperty);
    public static void SetAppear(DependencyObject o, bool v) => o.SetValue(AppearProperty, v);

    private static readonly DependencyProperty AppearedProperty = DependencyProperty.RegisterAttached(
        "Appeared", typeof(bool), typeof(Motion), new PropertyMetadata(false));

    private static void OnAppearChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement fe) return;
        fe.Loaded -= OnAppearLoaded;
        if ((bool)e.NewValue) fe.Loaded += OnAppearLoaded;
    }

    private static DateTime _appearUntil;

    /// <summary>内容刚到时调用：接下来一小段时间里出现的卡片依次浮现；之后滚动带出来的卡片直接显示，不再边滚边闪。</summary>
    public static void AllowAppear() => _appearUntil = DateTime.UtcNow.AddMilliseconds(1200);

    /// <summary>
    /// 卡片还在浮现（往上滑入）：最后一张最晚在窗口末尾开始，再错开 0.32 秒、弹 0.46 秒。
    /// 这时弹出的悬停卡会按卡片还没到位的位置定位，等内容加载完重新定位时跳一下，所以悬停卡要等它结束。
    /// </summary>
    public static bool Settling => DateTime.UtcNow < _appearUntil + TimeSpan.FromMilliseconds(800);

    /// <summary>每张卡片只浮现一次；同一行里按位置错开 40ms，像一排依次亮起来。</summary>
    private static void OnAppearLoaded(object sender, RoutedEventArgs e)
    {
        var fe = (FrameworkElement)sender;
        if ((bool)fe.GetValue(AppearedProperty)) return;
        fe.SetValue(AppearedProperty, true);
        if (DateTime.UtcNow > _appearUntil) return;

        var index = 0;
        if (VisualTreeHelper.GetParent(fe) is DependencyObject container && ItemsControl.ItemsControlFromItemContainer(container) is { } ic)
            index = Math.Max(0, ic.ItemContainerGenerator.IndexFromContainer(container));
        var delay = TimeSpan.FromMilliseconds(Math.Min(index, 8) * 40);

        var t = new TranslateTransform(0, 16);
        fe.RenderTransform = t;
        fe.Opacity = 0;
        fe.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, new Duration(TimeSpan.FromMilliseconds(300))) { BeginTime = delay, EasingFunction = Ease });
        t.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(16, 0, SpringTime) { BeginTime = delay, EasingFunction = Spring });
    }
}
