using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using JableLens.App.Controls;
using JableLens.App.Pages;
using JableLens.App.Services;
using JableLens.Core.Models;

namespace JableLens.App;

public partial class MainWindow : Window
{
    private const double NavHeight = 80;
    private readonly Stack<PageBase> _stack = new();
    private Notifier? _notifier;
    private bool _exiting;
    private bool _trayHintShown;

    public MainWindow()
    {
        InitializeComponent();
        Motion.AnimateContextMenus();
        if (Core.Storage.AppPaths.Override is not null) Title += " (dev)";
        SearchBox.TextChanged += (_, _) => SearchHint.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        MouseDown += (_, e) => { if (e.ChangedButton == MouseButton.XButton1) GoBack(); };
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape && _immersive) { SetImmersive(false); e.Handled = true; }
            else if (e.Key == Key.Escape && ModalOpen) { CloseModal(); e.Handled = true; }
            else if (e.Key == Key.Left && Keyboard.Modifiers == ModifierKeys.Alt) { GoBack(); e.Handled = true; }
            else if (e.Key == Key.Back && Keyboard.FocusedElement is not System.Windows.Controls.Primitives.TextBoxBase) { GoBack(); e.Handled = true; } // 退格键返回（输入框里照常删字）
            else if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control) { OpenSearch(); e.Handled = true; }
        };
        // 全幅页面往下滚时，导航条从透明渐变变成实心
        Host.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler(OnPageScroll));
        SizeChanged += (_, _) => UpdateNarrow();
        Loaded += (_, _) => Dispatcher.BeginInvoke(UpdateNarrow, System.Windows.Threading.DispatcherPriority.Loaded); // 排完版才量得出栏目宽度
        SmoothScroll.SetIsEnabled(ModalScroll, true);
        WindowFrame.UseDark(this);
        StateChanged += (_, _) => OnStateChanged();
        GlassBackdrop.Source = Host; // 顶栏、菜单、悬停卡、详情弹窗的毛玻璃都模糊页面
    }

    // ---------------- 窗口外框 ----------------

    private void OnStateChanged()
    {
        var max = WindowState == WindowState.Maximized;
        Root.Margin = max ? WindowFrame.MaximizedInset(this) : new Thickness(0);
        MaxButton.Content = max ? "\uE923" : "\uE922";
        MaxButton.ToolTip = max ? "还原" : "最大化";
    }

    private bool _immersive;
    private WindowState _stateBeforeImmersive;

    /// <summary>沉浸模式（全屏看页面）：藏起顶栏，页面铺满并最大化；Esc 退出。</summary>
    public void SetImmersive(bool on)
    {
        if (on == _immersive) return;
        _immersive = on;
        NavBar.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
        if (Host.Content is PageBase page) Host.Margin = new Thickness(0, on || page.FullBleed ? 0 : NavHeight, 0, 0);
        if (on)
        {
            _stateBeforeImmersive = WindowState;
            WindowState = WindowState.Maximized;
        }
        else if (_stateBeforeImmersive != WindowState.Maximized) WindowState = _stateBeforeImmersive;
    }

    private void OnMinimize(object sender, RoutedEventArgs e) => SystemCommands.MinimizeWindow(this);

    private void OnMaximize(object sender, RoutedEventArgs e)
    {
        if (WindowState == WindowState.Maximized) SystemCommands.RestoreWindow(this);
        else SystemCommands.MaximizeWindow(this);
    }

    private void OnCloseWindow(object sender, RoutedEventArgs e) => Close();

    public void Start()
    {
        // 哪个站要人机验证，就把哪个浏览器显示出来
        void OnChallenge(WebPageReader reader, bool on)
        {
            foreach (var r in Readers) r.View.Visibility = on && r == reader ? Visibility.Visible : Visibility.Collapsed;
            ChallengeTitle.Text = $"{reader.Name} 需要人机验证，请在下方完成";
            ChallengePanel.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
            if (on) { Show(); Activate(); }
        }
        foreach (var r in Readers) r.ChallengeStateChanged += OnChallenge;
        AppServices.Sites.WarmUp();

        _notifier = new Notifier();
        _notifier.OpenCodeRequested += code => { ShowFromTray(); OpenCode(code); };
        _notifier.ShowWindowRequested += ShowFromTray;
        _notifier.ExitRequested += () => _ = ExitAsync();
        _notifier.NewWorksFound += _ => Dispatcher.Invoke(UpdateNewBadge);
        AppServices.Library.Changed += () => Dispatcher.BeginInvoke(UpdateNewBadge);
        UpdateNewBadge();

        var glass = AppServices.Library.Read(d => (d.Settings.GlassTransparency, d.Settings.GlassBlur));
        GlassBackdrop.Transparency = glass.GlassTransparency / 100.0;
        GlassBackdrop.BlurRadius = glass.GlassBlur;

        AppServices.Downloads.Changed += () => Dispatcher.BeginInvoke(UpdateDownloadBadge);
        AppServices.Downloads.Start();
        UpdateDownloadBadge();

        AttachMenus();
        if (Perf.Enabled) LogFonts();
        NavHome.IsChecked = true; // 触发 OnNav → 首页
    }

    /// <summary>记下界面字体各字重实际用的是哪个文件（确认 MiSans 有没有加载上、字重有没有对上）。</summary>
    private static void LogFonts()
    {
        var family = (FontFamily)Application.Current.Resources["UiFont"];
        foreach (var w in new[] { FontWeights.Normal, Weights.SemiBold, Weights.Bold, Weights.Black })
        {
            var face = new Typeface(family, FontStyles.Normal, w, FontStretches.Normal);
            Perf.Log($"font {w.ToOpenTypeWeight()} → {(face.TryGetGlyphTypeface(out var g) ? g.FontUri.ToString() : "未找到")}");
        }
    }

    public Notifier? Notifier => _notifier;

    private static WebPageReader[] Readers => [AppServices.JableReader];

    // ---------------- 导航 ----------------

    public void Navigate(PageBase page, bool root = false)
    {
        CloseModal();
        BobCard.Hide();
        NavMenu.Close();
        if (root)
        {
            while (_stack.Count > 0) _stack.Pop().OnClosed();
        }
        _stack.Push(page);
        Show(page);
    }

    public void GoBack()
    {
        if (ModalOpen) { CloseModal(); return; }
        if (_stack.Count <= 1) return;
        _stack.Pop().OnClosed();
        var page = _stack.Peek();
        Show(page);
        page.OnRevisited();
    }

    private void Show(PageBase page)
    {
        Host.Content = page;
        Host.Margin = new Thickness(0, page.FullBleed ? 0 : NavHeight, 0, 0);
        SetNavSolid(!page.FullBleed);
        BackButton.Visibility = _stack.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        Motion.EnterPage(page);
        Motion.AllowAppear();
    }

    private bool _navSolid;

    private void SetNavSolid(bool solid)
    {
        if (solid == _navSolid) return;
        _navSolid = solid;
        NavSolid.BeginAnimation(OpacityProperty, new DoubleAnimation(solid ? 1 : 0, TimeSpan.FromMilliseconds(250)));
    }

    private void OnPageScroll(object sender, ScrollChangedEventArgs e)
    {
        if (e.VerticalChange != 0) BobCard.Hide(); // 卡片已经滚走了
        if (Host.Content is not PageBase { FullBleed: true } || e.OriginalSource is not ScrollViewer sv) return;
        if (sv.ScrollableHeight <= 0 || e.VerticalChange == 0) return; // 横向的行滚动不算
        SetNavSolid(sv.VerticalOffset > 8);
    }

    /// <summary>各栏目文字的宽度（全部显示时量一次；收起后的元素量不出宽度）。</summary>
    private readonly Dictionary<RadioButton, double> _navWidths = [];

    /// <summary>
    /// 顶栏放得下几个栏目就显示几个，放不下的从后往前收进「更多」下拉（不再窄了就整排收起）。
    /// 可用宽度 = 顶栏宽 − 右边图标和窗口按钮 − 栏目左边的返回键和标志。
    /// </summary>
    private void UpdateNarrow()
    {
        if (!IsLoaded || NavDock.ActualWidth <= 0) return;
        var links = NavLinks.Children.OfType<RadioButton>().ToList();
        foreach (var rb in links.Where(r => !_navWidths.ContainsKey(r) && r.IsVisible))
            _navWidths[rb] = rb.ActualWidth + rb.Margin.Left + rb.Margin.Right + 8; // 选中时加粗会宽一点，留 8 的余量
        if (_navWidths.Count < links.Count) return; // 还没量全（刚启动），等下一次

        var right = NavDock.Children.OfType<FrameworkElement>().Where(c => DockPanel.GetDock(c) == Dock.Right).Sum(c => c.ActualWidth + c.Margin.Left + c.Margin.Right);
        var avail = NavDock.ActualWidth - right - NavLinks.TranslatePoint(new Point(0, 0), NavDock).X - 24;
        var total = links.Sum(r => _navWidths[r]);
        var budget = total <= avail ? avail : avail - 90; // 要收起时给「更多」留位置
        double used = 0;
        var overflow = false;
        foreach (var rb in links)
        {
            overflow |= used + _navWidths[rb] > budget;
            rb.Visibility = overflow ? Visibility.Collapsed : Visibility.Visible;
            if (!overflow) used += _navWidths[rb];
        }
        BrowseButton.Visibility = overflow ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnBack(object sender, RoutedEventArgs e) => GoBack();

    private void OnLogo(object sender, RoutedEventArgs e)
    {
        if (NavHome.IsChecked == true) NavigateTo("home");
        else NavHome.IsChecked = true;
    }

    private bool _suppressNav;

    private void OnNav(object sender, RoutedEventArgs e)
    {
        if (!_suppressNav) NavigateTo((string)((FrameworkElement)sender).Tag);
    }

    /// <summary>从悬浮菜单直接进某个栏目里的某一页：高亮对应栏目，但不先打开栏目首页。</summary>
    private void NavigateSection(RadioButton link, PageBase page)
    {
        _suppressNav = true;
        link.IsChecked = true;
        _suppressNav = false;
        Navigate(page, root: true);
    }

    /// <summary>栏目的悬浮菜单（二级：分组，三级：栏目 / 标签）。</summary>
    private void AttachMenus()
    {
        NavMenu.Attach(NavTags,
            async () => (await AppServices.Sites.JableTagGroupsAsync())
                .Select(g => new MenuGroup(g.Name, "\uE8EC", g.Tags.Select(t => new MenuEntry(t.Name, t)).ToList())).ToList(),
            (_, e) => NavigateSection(NavTags, new TagsPage((TagLink)e.Payload)));
    }

    /// <summary>点一下已经选中的导航项：回到该栏目的顶层。</summary>
    private void OnNavReclick(object sender, MouseButtonEventArgs e)
    {
        if (sender is RadioButton { IsChecked: true } rb && (_stack.Count > 1 || ModalOpen)) NavigateTo((string)rb.Tag);
    }

    public void NavigateTo(string tag)
    {
        // 不在顶部栏目里的页面（关注、观看记录、设置）：取消栏目高亮
        var link = NavLinks.Children.OfType<RadioButton>().FirstOrDefault(r => (string)r.Tag == tag);
        if (link is null) ClearNav();
        else if (link.IsChecked != true) { link.IsChecked = true; return; } // 会再进 OnNav
        PageBase page = tag switch
        {
            "categories" => new CategoriesPage(),
            "tags" => new TagsPage(),
            "performers" => new PerformerDirectoryPage(),
            "wall" => new CoverWallPage(),
            "follows" => new FollowsPage(),
            "history" => new HistoryPage(),
            "favorites" => new FavoritesPage(),
            "settings" => new SettingsPage(),
            "downloads" => new DownloadsPage(),
            "portrait" => new PortraitPage(),
            "drama" => ExternalSitePage.ShortDrama(),
            _ => new HomePage(),
        };
        Navigate(page, root: true);
    }

    private void OnBrowse(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = (UIElement)sender, Placement = PlacementMode.Bottom };
        foreach (var rb in NavLinks.Children.OfType<RadioButton>().Where(r => r.Visibility != Visibility.Visible)) // 只列收起来的
        {
            var item = new MenuItem { Header = rb.Content, FontWeight = rb.IsChecked == true ? Weights.Bold : FontWeights.Normal };
            var tag = (string)rb.Tag;
            item.Click += (_, _) => NavigateTo(tag);
            menu.Items.Add(item);
        }
        menu.IsOpen = true;
    }

    private void OnBell(object sender, RoutedEventArgs e) => NavigateTo("follows");

    private void OnDownloads(object sender, RoutedEventArgs e) => NavigateTo("downloads");

    private void UpdateDownloadBadge()
    {
        var n = AppServices.Downloads.ActiveCount;
        DownloadBadge.Visibility = n == 0 ? Visibility.Collapsed : Visibility.Visible;
        DownloadBadgeText.Text = n.ToString();
    }

    private void OnProfile(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = (UIElement)sender, Placement = PlacementMode.Bottom, HorizontalOffset = -120 };
        void Add(string text, Action click)
        {
            var item = new MenuItem { Header = text };
            item.Click += (_, _) => click();
            menu.Items.Add(item);
        }
        Add("我的画像", () => NavigateTo("portrait"));
        menu.Items.Add(new Separator());
        Add("关注的演员", () => NavigateTo("follows"));
        Add("观看记录", () => NavigateTo("history"));
        Add("下载", () => NavigateTo("downloads"));
        Add("设置", () => NavigateTo("settings"));

        menu.Items.Add(new Separator());
        Add("退出 Jable", () => _ = ExitAsync());
        menu.IsOpen = true;
    }

    // ---------------- 搜索（点放大镜展开输入框） ----------------

    private void OnSearchOpen(object sender, RoutedEventArgs e) => OpenSearch();

    private void OpenSearch()
    {
        SearchIcon.Visibility = Visibility.Hidden;
        SearchFrame.Opacity = 1;
        SearchFrame.BeginAnimation(WidthProperty, new DoubleAnimation(280, TimeSpan.FromMilliseconds(220)) { EasingFunction = new CubicEase() });
        SearchBox.Focus();
        SearchBox.SelectAll();
    }

    private void OnSearchLostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (SearchBox.Text.Length > 0) return;
        SearchFrame.BeginAnimation(WidthProperty, null);
        SearchFrame.Width = 0;
        SearchFrame.Opacity = 0;
        SearchIcon.Visibility = Visibility.Visible;
    }

    private void OnSearchKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) DoSearch();
        if (e.Key == Key.Escape) { SearchBox.Text = ""; Keyboard.ClearFocus(); Focus(); }
    }

    private void DoSearch()
    {
        var q = SearchBox.Text.Trim();
        if (q.Length == 0) return;
        ClearNav();
        Navigate(new SearchPage(q));
    }

    private void ClearNav()
    {
        foreach (var rb in NavLinks.Children.OfType<RadioButton>()) rb.IsChecked = false;
    }

    // ---------------- 详情弹窗 ----------------

    private bool ModalOpen => ModalLayer.Visibility == Visibility.Visible && !_modalClosing;

    /// <summary>详情是从哪张卡片展开的（关闭时缩回去）。</summary>
    private FrameworkElement? _flyFrom;

    private void ShowDetail(VideoDetailPage page, FrameworkElement? from)
    {
        BobCard.Hide();
        _modalClosing = false;
        ModalLayer.IsHitTestVisible = true;
        Motion.Reset(ModalLayer);
        (ModalHost.Content as PageBase)?.OnClosed();
        page.Background = Brushes.Transparent; // 弹窗本身是玻璃
        ModalHost.Content = page;
        ModalLayer.Visibility = Visibility.Visible;
        ModalScroll.ScrollToTop();
        // 背景遮罩淡入
        ModalLayer.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200)) { EasingFunction = Motion.Ease });

        // 上一次收回动画留下的变换 / 裁剪清掉，先按原位排好版，才知道大图在哪；卡片也先回正，起点才准
        Tilt.Reset(from);
        ModalPanel.BeginAnimation(OpacityProperty, null);
        ModalPanel.Opacity = 1;
        ModalPanel.RenderTransform = null;
        ModalPanel.Clip = null;
        ModalLayer.UpdateLayout();

        _flyFrom = CardFly.Usable(from, this) && CardFly.SameShape(from!, page.Hero) ? from : null;
        if (_flyFrom is null)
        {
            Motion.PopIn(ModalPanel, 0.9, 0.5, 0.3); // 不知道从哪张卡片来（或卡片形状差太多）：面板从略小弹到原大小
            return;
        }
        // 卡片展开：弹窗从卡片的位置长出来。高清大图到之前先用卡片上那张封面顶着，开头就和卡片一模一样
        if (CardFly.ImageOf(_flyFrom) is { } cover) page.ShowPlaceholder(cover);
        CardFly.Morph(ModalPanel, page.Hero, _flyFrom, Root, open: true);
    }

    private bool _modalClosing;

    public void CloseModal()
    {
        if (!ModalOpen) return;
        _modalClosing = true;
        ModalLayer.IsHitTestVisible = false; // 收起动画期间点击穿到下面的页面
        (ModalHost.Content as PageBase)?.OnClosed();
        (Host.Content as PageBase)?.OnRevisited();
        void Finish()
        {
            if (!_modalClosing) return; // 收起途中又打开了别的作品
            _modalClosing = false;
            ModalHost.Content = null;
            ModalLayer.Visibility = Visibility.Collapsed;
            Motion.Reset(ModalLayer);
        }

        // 从卡片展开的、而且没往下滚（大图还在顶上）：缩回那张卡片，最后一小段淡出
        var card = _flyFrom;
        _flyFrom = null;
        if (card is not null && CardFly.Usable(card, this) && ModalHost.Content is VideoDetailPage page && ModalScroll.VerticalOffset < 4)
        {
            CardFly.Morph(ModalPanel, page.Hero, card, Root, open: false, Finish);
            var fade = CardFly.CloseDuration * 0.35;
            ModalLayer.BeginAnimation(OpacityProperty, new DoubleAnimation(0, fade) { BeginTime = CardFly.CloseDuration - fade });
            return;
        }
        Motion.PopOut(ModalLayer, Finish, 0.97);
    }

    private void OnModalClose(object sender, RoutedEventArgs e) => CloseModal();

    private void OnModalBackdrop(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource == ModalBackdrop) CloseModal();
    }

    // ---------------- 打开东西 ----------------

    /// <param name="from">点的是哪张卡片的封面；不传就取鼠标下面那张（详情从它展开）。</param>
    public void OpenCard(CardItem item, FrameworkElement? from = null)
    {
        from ??= BobCard.CoverUnder(Mouse.DirectlyOver as DependencyObject);
        switch (item.Payload)
        {
            case VideoCard v: ShowDetail(new VideoDetailPage(v), from); break;
            case CodedWork w: ShowDetail(VideoDetailPage.ForCode(w.Code, w), from); break;
            case PerformerSummary p: Navigate(new PerformerPage(p.Name, p.IconUrl)); break;
            case CategoryLink c: Navigate(TabbedGridPage.ForJableList(c.Name, c.Url)); break;
        }
    }

    public void OpenCode(string code) => ShowDetail(VideoDetailPage.ForCode(code, null), null);

    public void OpenPerformer(string name, string? iconUrl = null) => Navigate(new PerformerPage(name, iconUrl));


    public static void Play(VideoCard card)
    {
        BobCard.Hide();
        var w = new PlayerWindow(card);
        w.Show();
    }

    /// <summary>作品卡片的右键菜单：播放 / 片单。</summary>
    public ContextMenu? MenuFor(CardItem item)
    {
        if (item.Payload is not VideoCard v) return null;
        var menu = new ContextMenu();
        var play = new MenuItem { Header = "播放" };
        play.Click += (_, _) => Play(v);
        var fav = new MenuItem { Header = AppServices.Library.IsFavorite(v.PageUrl) ? "从我的片单移除" : "加入我的片单" };
        fav.Click += (_, _) => AppServices.Library.ToggleFavorite(v);
        var open = new MenuItem { Header = "详细信息" };
        open.Click += (_, _) => OpenCard(item);
        var dl = new MenuItem { Header = AppServices.Downloads.Find(v) is null ? "下载" : "已在下载列表" , IsEnabled = AppServices.Downloads.Find(v) is null };
        dl.Click += (_, _) => AppServices.Downloads.Enqueue(v);
        menu.Items.Add(play);
        menu.Items.Add(open);
        menu.Items.Add(fav);
        menu.Items.Add(dl);
        return menu;
    }

    /// <summary>网格的通用接线：点击打开、右键菜单、悬停卡。</summary>
    public void Wire(CardGrid grid)
    {
        grid.ItemClicked += item => OpenCard(item);
        grid.ContextMenuFor = MenuFor;
        BobCard.Attach(grid);
    }

    private void UpdateNewBadge()
    {
        var n = AppServices.Library.Read(d => d.Follows.Sum(f => f.NewWorks.Count));
        FollowBadge.Visibility = n == 0 ? Visibility.Collapsed : Visibility.Visible;
        FollowBadgeText.Text = n > 99 ? "99+" : n.ToString();
    }

    // ---------------- 托盘 / 退出 ----------------

    public void ShowFromTray()
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    protected override void OnDeactivated(EventArgs e)
    {
        base.OnDeactivated(e);
        BobCard.Hide();
        NavMenu.Close();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        BobCard.Hide();
        if (!_exiting && AppServices.Library.Read(d => d.Settings.CloseToTray))
        {
            e.Cancel = true;
            Hide();
            if (!_trayHintShown)
            {
                _trayHintShown = true;
                _notifier?.ShowBalloon("Jable 还在后台运行", "会继续检查关注演员的新作。右键托盘图标可以退出。");
            }
            return;
        }
        base.OnClosing(e);
        if (!_exiting) _ = ExitAsync();
    }

    public async Task ExitAsync()
    {
        _exiting = true;
        BobCard.Hide();
        foreach (var w in Application.Current.Windows.OfType<PlayerWindow>().ToList()) w.Close();
        await AppServices.Library.FlushAsync();
        _notifier?.Dispose();
        Application.Current.Shutdown();
    }
}
