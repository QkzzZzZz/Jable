using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using JableLens.App.Services;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace JableLens.App.Pages;

/// <summary>
/// 嵌入的外部网站（如黄果短剧）：用真实网页（封面解密、播放、登录都由网站自己完成，软件不碰），
/// 放在单独的浏览器环境里（自己的 cookie，不和 Jable 共用），登录状态会记住。
/// 只在本站内跳转；点到别的网站（第三方登录、支付、客服群）交给系统浏览器。统计 / 追踪请求一律拦掉。
/// 有 <see cref="Cleanup"/> 的站点再做一层「按 Jable 的样子整理」：注入样式藏掉网站自己的导航、广告、推广、页脚，
/// 栏目切换和搜索放到软件自己的信息栏里。
/// </summary>
public sealed class ExternalSitePage : PageBase
{
    /// <summary>页面里常见的统计 / 追踪服务：不让它们收到任何请求。</summary>
    private static readonly string[] Trackers =
    [
        "googletagmanager.com", "google-analytics.com", "analytics.google.com", "doubleclick.net",
        "mc.yandex.ru", "yandex.ru", "analytics.ahrefs.com", "hm.baidu.com", "cnzz.com", "51.la",
    ];

    /// <summary>
    /// 整理规则：<paramref name="Sections"/> 是信息栏里的栏目（名字 + 站内路径），<paramref name="Search"/> 把关键词变成站内搜索路径，
    /// <paramref name="Css"/> 注入每个页面，<paramref name="BlockedPaths"/> 是不让加载的站内脚本（广告 SDK 等，按路径片段认）。
    /// </summary>
    /// <param name="Script">额外注入的脚本（只在顶层页面运行），比如按标题过滤卡片。</param>
    /// <param name="BlockedPages">不许打开的站内页面（路径前缀），点到了就停在原处。</param>
    public sealed record Cleanup(
        IReadOnlyList<(string Name, string Path)> Sections, Func<string, string>? Search, string Css,
        IReadOnlyList<string> BlockedPaths, string Script = "", IReadOnlyList<string>? BlockedPages = null);

    private readonly string _title;
    private readonly string _home;
    private readonly Func<string, bool> _isSite;
    private readonly bool _followRedirects;
    private readonly Cleanup? _clean;
    /// <summary>网站自己重定向过去的域名（换域名的站点每次可能不一样），记下来当作本站。</summary>
    private readonly HashSet<string> _redirectHosts = new(StringComparer.OrdinalIgnoreCase);

    private readonly WebView2 _view = new() { DefaultBackgroundColor = System.Drawing.Color.FromArgb(0x14, 0x14, 0x14) };
    private readonly TextBlock _status = new() { FontSize = 13, Foreground = Ui.Res("Fg2"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(14, 0, 0, 0) };
    private readonly Button _back;
    private readonly Border _sectionHost = new();
    private int _section = -1;

    /// <param name="isSite">哪些域名算本站（在这里打开）。</param>
    /// <param name="followRedirects">网站会换域名：服务器重定向到的新域名也算本站。</param>
    private ExternalSitePage(string title, string home, Func<string, bool> isSite, bool followRedirects, Cleanup? clean = null)
    {
        _title = title;
        _home = home;
        _isSite = isSite;
        _followRedirects = followRedirects;
        _clean = clean;

        var bar = new DockPanel { Margin = new Thickness(Ui.Gutter, 6, Ui.Gutter, 14) };
        var right = new StackPanel { Orientation = Orientation.Horizontal };
        if (clean?.Search is { } search)
        {
            var box = new TextBox { Width = 220, Height = 32, Padding = new Thickness(12, 0, 12, 0), FontSize = 13.5, VerticalContentAlignment = VerticalAlignment.Center, ToolTip = "搜索（回车）" };
            box.KeyDown += (_, e) =>
            {
                if (e.Key != Key.Enter || box.Text.Trim() is not { Length: > 0 } q) return;
                Go(search(q));
                e.Handled = true;
            };
            right.Children.Add(box);
        }
        _back = BarButton("  后退", () => { if (_view.CoreWebView2?.CanGoBack == true) _view.CoreWebView2.GoBack(); });
        _back.IsEnabled = false;
        right.Children.Add(_back);
        right.Children.Add(BarButton("  刷新", () => _view.CoreWebView2?.Reload()));
        right.Children.Add(BarButton("  在浏览器中打开", () => OpenExternal(_view.Source?.AbsoluteUri ?? _home)));
        DockPanel.SetDock(right, Dock.Right);
        bar.Children.Add(right);

        var left = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        left.Children.Add(new TextBlock { Text = title, FontSize = 18, FontWeight = Weights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        if (clean is { Sections.Count: > 0 })
        {
            // 栏目切换（一排胶囊按钮），代替网站自己的顶部导航；页面跳转后按地址换成对应栏目
            _sectionHost.Margin = new Thickness(20, 0, 0, 0);
            _sectionHost.VerticalAlignment = VerticalAlignment.Center;
            RenderSections(0);
            left.Children.Add(_sectionHost);
        }
        left.Children.Add(_status);
        bar.Children.Add(left);

        var dock = new DockPanel { Background = Ui.Res("Bg") };
        DockPanel.SetDock(bar, Dock.Top);
        dock.Children.Add(bar);
        dock.Children.Add(_view);
        Content = dock;
        Loaded += OnFirstLoaded;
    }

    /// <summary>栏目平铺成一排胶囊按钮（和设置页同一种），选中第 <paramref name="index"/> 个；页面跳转后按地址重画。</summary>
    private void RenderSections(int index)
    {
        if (_clean is not { } clean || index == _section) return;
        _section = index;
        var chips = Ui.Chips(clean.Sections.Select(s => s.Name).ToArray(), index, i => { _section = i; Go(clean.Sections[i].Path); });
        chips.VerticalAlignment = VerticalAlignment.Center;
        _sectionHost.Child = chips;
    }

    /// <summary>当前地址属于哪个栏目：最长的路径前缀（首页只认「/」本身）；作品页等不属于任何栏目的，保持原样。</summary>
    private void SyncSection(Uri u)
    {
        if (_clean is not { } clean) return;
        var best = -1;
        for (var i = 0; i < clean.Sections.Count; i++)
        {
            var p = clean.Sections[i].Path;
            var hit = p == "/" ? u.AbsolutePath == "/" : u.AbsolutePath.StartsWith(p, StringComparison.OrdinalIgnoreCase);
            if (hit && (best < 0 || p.Length > clean.Sections[best].Path.Length)) best = i;
        }
        if (best >= 0) RenderSections(best);
    }


    /// <summary>
    /// 黄果短剧：入口地址会跳到随机的镜像域名，跟着服务器重定向走；huangguoai.* 是它的官方域名。
    /// 页面结构（2026-10-27 抓取）：卡片 .hg-drama-card，广告位 .hg-ssp-*（播放页还有暂停广告、播放器边浮动广告），
    /// 顶栏 .hg-header、侧栏 .app-aside、公告条 .hg-announce、页脚 .hg-footer、悬浮下载 .hg-fab、换地址公告 .vd-address、意见反馈 .hg-fb__*。
    /// 封面图是网站加密的，由它自己的脚本解开，软件不解密。
    /// </summary>
    public static ExternalSitePage ShortDrama() =>
        new("黄果短剧", "https://qmrv93.40ygo615.cc/",
            h => IsUnder(h, "40ygo615.cc") || h.StartsWith("huangguoai.", StringComparison.OrdinalIgnoreCase), followRedirects: true,
            new Cleanup(
                [("首页", "/"), ("AI成人短剧", "/ai-duanju/"), ("AI成人漫剧", "/ai-manju/"), ("专题", "/topics/"), ("排行榜", "/ranks/hot/")],
                q => $"/search/video/{Uri.EscapeDataString(q)}/",
                DramaCss,
                ["/ssp-", "/ssp_", "address-poster", "/sdk/ssp"],
                DramaScript,
                ["/ai-huanlian", "/ai-mogai"]));

    /// <summary>
    /// 短剧页面脚本（每 0.3 秒查一次：轮播会自己换页，列表也会后加载，必须一直查）：
    /// 1. 涉及未成年人的作品（标题 / 标签 / 简介里有高中生、高三、初中生、萝莉、未成年等）一律藏掉；
    ///    「AI换脸」「AI魔改」是用真实明星的脸合成的色情视频（未经本人同意），也一律藏掉。
    ///    打开了这两类作品的播放页，整页换成提示，不播放。
    /// 2. 首页大图轮播跳过广告和上面两类作品。
    /// </summary>
    private const string DramaScript = """
        // 只认明确的学生身份 / 年龄词：「中学老师」「高中同学聚会」这类成年人剧情不算
        const minor = /(初中生|高中生|中学生|小学生|高一|高二|高三|初一|初二|初三|高考|萝莉|未成年|幼女|女童|\bJK\b)/i;
        const deepfake = /^\s*AI\s*(换脸|魔改)/;
        const titleOf = el => (el.querySelector('[class*="title"]') || el).textContent || '';
        let blockedPage = false;
        const sweep = () => {
          if (blockedPage) return;
          const playTitle = document.querySelector('.hg-web-play__title')?.textContent || '';
          // 只要这一部自己的标签：播放页下方「相关推荐」也是作品卡片，它们的标签文字里带着别的作品名
          const playTags = [...document.querySelectorAll('.hg-web-play a[href*="/tag/"]')].filter(a => !a.closest('.hg-drama-card')).map(a => a.textContent).join(' ');
          if (location.pathname.startsWith('/video/') && playTitle && (minor.test(playTitle + ' ' + playTags) || deepfake.test(playTitle))) {
            blockedPage = true;
            document.querySelectorAll('video').forEach(v => { v.pause(); v.removeAttribute('src'); v.load(); });
            document.body.innerHTML = '<div style="padding:120px 0;text-align:center;color:#808080;font-size:15px">这部作品不在 Jable 里显示</div>';
            return;
          }
          document.querySelectorAll('.hg-drama-card, .hg-category-item, .hg-topic-card, .hg-rank-item, .hg-search-suggest__hot-item, .hg-hero__thumb').forEach(el => {
            if (!el.dataset.jlHidden && (minor.test(el.textContent || '') || deepfake.test(titleOf(el)))) { el.dataset.jlHidden = '1'; el.style.setProperty('display', 'none', 'important'); }
          });
          skipHeroAds();
        };
        // 首页大图轮播：数据里 isAd 为真的是广告（站外推广）。缩略图按名字藏掉；轮到广告页（或上面两类作品）就切到下一张
        let adTitles = null;
        const skipHeroAds = () => {
          if (location.pathname !== '/') return; // 只有首页有这个轮播；排行榜等页面也用了 data-hero-* 标记，点了会跳进作品页
          const thumbs = [...document.querySelectorAll('[data-hero-thumb]')];
          if (!thumbs.length) return;
          if (!adTitles) { // 缩略图出来时轮播数据（页面里的内联脚本）已经在了，只读一次
            adTitles = new Set();
            document.querySelectorAll('script:not([src])').forEach(s => {
              for (const m of (s.textContent || '').matchAll(/"title":"([^"]*)"[^{}]*?"isAd":true/g)) adTitles.add(m[1]);
            });
          }
          const isAd = b => { const t = b.querySelector('img')?.alt || ''; return adTitles.has(t) || minor.test(t) || deepfake.test(t); };
          thumbs.forEach(b => { if (isAd(b)) b.style.setProperty('display', 'none', 'important'); });
          const cover = document.querySelector('[data-hero-cover]');
          const heroText = document.querySelector('.hg-hero__content')?.textContent || '';
          const heroTitle = document.querySelector('.hg-hero__title')?.textContent || '';
          if (cover && ((cover.dataset.heroAdId && cover.dataset.heroAdId !== '0') || minor.test(heroText) || deepfake.test(heroTitle))) {
            const i = thumbs.findIndex(b => b.classList.contains('is-active'));
            const next = thumbs.slice(i + 1).concat(thumbs.slice(0, i)).find(b => !isAd(b));
            next?.click();
          }
        };
        setInterval(() => { if (document.body) sweep(); }, 300);
        """;

    /// <summary>黄果短剧的整理样式：藏掉网站外壳和所有推广，背景和卡片换成 Jable 的深色样式。</summary>
    private const string DramaCss = """
        /* 网站外壳：顶栏、侧栏、公告滚动条、页脚、悬浮按钮（下载 App / 回顶部）、换地址公告、意见反馈、年龄确认（软件启动时已确认） */
        .hg-header, header.header, .app-aside, .hg-announce, .hg-footer, .hg-fab, .vd-address, .vd-address-custom,
        .hg-fb__mask, .hg-fb__panel, .hg-age-modal,
        /* 广告：所有 SSP 广告位、播放器暂停广告和边上的浮动广告、海报式推广 */
        [class*="hg-ssp"], [class*="address-poster"], [class*="ssp-slot"],
        /* 无关栏目和推广链接：吃瓜、换地址、官网介绍、App 下载 */
        a[href^="/chigua"], a[href^="/go-home"], a[href^="/official"], a[href^="/about"], a[href^="/faq"], a[href*="/chan/"],
        /* 真人换脸（AI换脸 / AI魔改）：入口链接和「分类推荐」里的整列 */
        a[href*="/ai-huanlian"], a[href*="/ai-mogai"],
        .hg-category-col:has(a[href*="/ai-huanlian"]), .hg-category-col:has(a[href*="/ai-mogai"]),
        /* 网站自己标了 sponsored 的推广链接，以及包着它的卡片 */
        a[rel~="sponsored"], .hg-drama-card:has(a[rel~="sponsored"]), .hg-topic-card:has(a[rel~="sponsored"]), .hg-category-item:has(a[rel~="sponsored"]),
        /* 第三方 iframe（广告），播放器里的除外 */
        iframe:not(.hg-web-play iframe) { display: none !important; }

        /* 网站的主题变量：顶栏藏了就不再给它留 78px；主色从黄色换成 Jable 的红色，底色换成 Jable 的深灰 */
        :root {
          --dx-header-height: 0px !important;
          --color-primary: #E50914 !important; --color-primary-end: #B20710 !important; --color-light-primary: rgba(229, 9, 20, .14) !important;
          --color-page: #141414 !important; --color-dark: #141414 !important; --color-base1: #181818 !important;
          --color-link: #54B9C5 !important;
        }
        html, body { background: #141414 !important; color: #E5E5E5 !important; }
        /* 「立即播放」和 Jable 首页大图的「播放」一样：白色胶囊；登录 / 提交按钮用红色 */
        .hg-hero__play { background: #fff !important; color: #000 !important; }
        .hg-auth-submit, button.hg-header__login { background: #E50914 !important; background-image: none !important; color: #fff !important; }
        .hg-hero__ad-badge { display: none !important; }
        body { padding-top: 0 !important; overflow-x: hidden !important; }
        a { color: inherit; }
        ::-webkit-scrollbar { width: 10px; height: 10px; }
        ::-webkit-scrollbar-thumb { background: #3A3A3A; border-radius: 5px; }
        ::-webkit-scrollbar-track { background: transparent; }

        /* 卡片：小圆角封面，标题白、其余灰，和 Jable 的作品卡片一致 */
        .hg-drama-card, .hg-topic-card, .hg-category-item { background: transparent !important; border: 0 !important; box-shadow: none !important; }
        .hg-drama-card__cover, .hg-drama-card__cover img, .hg-topic-card img, .hg-category-item__thumb, .hg-category-item__thumb img { border-radius: 12px !important; }
        .hg-drama-card__title, .hg-topic-card__title, .hg-category-item__title, .hg-section-title { color: #E5E5E5 !important; }
        .hg-drama-card__desc, .hg-drama-card__tags, .hg-drama-card__episode, .hg-topic-card__meta, .hg-category-item__desc { color: #808080 !important; }
        .hg-drama-card__badge, .hg-drama-card__badge--new { background: #E50914 !important; color: #fff !important; }
        .hg-tag { background: rgba(255,255,255,.12) !important; color: #B3B3B3 !important; border: 0 !important; border-radius: 999px !important; }
        .hg-section-accent { background: #E50914 !important; }
        .hg-section-title { font-weight: 700 !important; }

        /* 播放页：只留播放器、标题、选集、简介 */
        .hg-web-play, .hg-web-play__side, .hg-web-play__introcard { background: transparent !important; }
        .hg-web-play__ep.is-active { background: #E50914 !important; color: #fff !important; }
        /* 资料：黄色竖线小图换成红色细线；关注按钮红色；评分区去掉黄底，分数白色，亮星变白 */
        .hg-web-play__fact > img { display: none !important; }
        .hg-web-play__fact { border-left: 2px solid #E50914 !important; padding-left: 12px !important; }
        .hg-web-play__follow { background: #E50914 !important; background-image: none !important; color: #fff !important; }
        .hg-web-play__scorebox { background: rgba(255,255,255,.04) !important; border-color: rgba(255,255,255,.08) !important; }
        .hg-web-play__scorenum, .hg-web-play__scorenum * { color: #fff !important; }
        img.is-on[src*="star-on"] { filter: grayscale(1) brightness(2.2) !important; }
        .hg-web-play__intro-split { opacity: .15 !important; filter: grayscale(1) !important; }

        /* 网站其余写死的黄色：按钮、排行榜标签、悬停描边 */
        .hg-btn--primary { background: #E50914 !important; background-image: none !important; color: #fff !important; }
        .hg-btn--outline, .hg-btn--soft { background: transparent !important; border-color: rgba(255,255,255,.3) !important; color: #fff !important; }
        .hg-rank-tag, .hg-rank-metric-badge, .hg-rank-hero__icon { background: rgba(255,255,255,.08) !important; border-color: transparent !important; }
        .hg-search-hot__rank.is-top, .hg-search-hot__item:hover .hg-search-hot__name { color: #E50914 !important; }
        .hg-drama-card:hover, .hg-rank-item:hover, .hg-history-card:hover, .hg-post-card:hover, .hg-see-more:hover { border-color: rgba(255,255,255,.2) !important; }

        /* ===== Jable 皮肤：字体、边距、标题、卡片网格、胶囊按钮，让页面看起来像 Jable 自己的页面 ===== */

        /* 字体：软件自带的 MiSans（/__jable/font/ 由软件应答），四个字重按 Jable 的用法分档 */
        @font-face { font-family: 'JableUI'; src: url('/__jable/font/MiSans-Regular.ttf') format('truetype'); font-weight: 100 449; font-display: swap; }
        @font-face { font-family: 'JableUI'; src: url('/__jable/font/MiSans-Semibold.ttf') format('truetype'); font-weight: 450 599; font-display: swap; }
        @font-face { font-family: 'JableUI'; src: url('/__jable/font/MiSans-Bold.ttf') format('truetype'); font-weight: 600 749; font-display: swap; }
        @font-face { font-family: 'JableUI'; src: url('/__jable/font/MiSans-Heavy.ttf') format('truetype'); font-weight: 750 1000; font-display: swap; }
        html, body, button, input, textarea, select, a, span, div, p, h1, h2, h3, h4, b, strong, small, li {
          font-family: 'JableUI', 'Microsoft YaHei UI', 'Segoe UI', sans-serif !important;
        }

        /* 边距和 Jable 一致（左右 56），面包屑不要 */
        :root { --hg-page-gutter: 56px !important; }
        .hg-breadcrumb { display: none !important; }

        /* 标题：页面大标题 30 粗体；区块标题像 Jable 的行标题（20 粗体、浅灰、不带红色竖条） */
        .hg-list-page h1, .hg-channel-page h1, main h1 { font-size: 30px !important; font-weight: 700 !important; color: #fff !important; letter-spacing: 0 !important; }
        .hg-section-accent { display: none !important; }
        .hg-section-title { font-size: 20px !important; font-weight: 700 !important; color: #E5E5E5 !important; padding-left: 0 !important; }
        .hg-section-title::before, .hg-section-header::before { display: none !important; }
        .hg-section-header { margin-bottom: 12px !important; }
        .hg-see-more { background: transparent !important; border: 0 !important; color: #54B9C5 !important; font-size: 13px !important; font-weight: 600 !important; padding: 0 !important; }

        /* 卡片网格：一排多张小卡片（最小 176 宽），行距 26、列距 12，和 Jable 的作品网格一样 */
        .hg-card-grid { display: grid !important; grid-template-columns: repeat(auto-fill, minmax(176px, 1fr)) !important; gap: 26px 12px !important; }

        /* 卡片：圆角封面，悬停时封面放大 4% 并浮起阴影（Jable 的悬停效果）；只留标题和一行标签，不要简介 */
        .hg-drama-card { padding: 0 !important; transform: none !important; }
        .hg-drama-card__cover { border-radius: 12px !important; overflow: hidden !important; box-shadow: inset 0 0 0 1px rgba(255,255,255,.12) !important; transition: transform .25s cubic-bezier(.2,.8,.2,1), box-shadow .25s ease !important; }
        .hg-drama-card:hover .hg-drama-card__cover { transform: scale(1.04) !important; box-shadow: 0 14px 30px rgba(0,0,0,.65) !important; }
        .hg-drama-card__body { padding: 10px 1px 0 !important; }
        .hg-drama-card__title { font-size: 14px !important; font-weight: 400 !important; line-height: 20px !important; color: #E5E5E5 !important; }
        .hg-drama-card:hover .hg-drama-card__title { color: #fff !important; }
        .hg-drama-card__desc { display: none !important; }
        .hg-drama-card__tags { margin-top: 6px !important; max-height: 22px !important; overflow: hidden !important; }
        .hg-drama-card .hg-tag { background: transparent !important; padding: 0 8px 0 0 !important; font-size: 12px !important; color: #808080 !important; }
        /* 封面上的角标：「新上架 / 原创」红色小标，评分半透明黑底，集数压在底部渐变上 */
        .hg-drama-card__badge { border-radius: 4px !important; font-size: 11px !important; font-weight: 600 !important; padding: 1px 6px !important; }
        .hg-drama-card__score { background: rgba(0,0,0,.6) !important; color: #fff !important; border-radius: 4px !important; font-size: 11.5px !important; font-weight: 600 !important; }
        .hg-drama-card__episode { background: linear-gradient(transparent, rgba(0,0,0,.75)) !important; color: #fff !important; font-size: 12px !important; border: 0 !important; }

        /* 栏目标签（最新更新 / 当前热播…）和分页：Jable 的胶囊，选中的白底黑字 */
        .hg-channel-tabs { border: 0 !important; gap: 8px !important; margin-bottom: 18px !important; }
        .hg-channel-tabs__item { background: rgba(255,255,255,.12) !important; color: #fff !important; border: 0 !important; border-radius: 999px !important; padding: 7px 18px !important; font-size: 13.5px !important; }
        .hg-channel-tabs__item:hover { background: rgba(255,255,255,.2) !important; }
        .hg-channel-tabs__item.is-active { background: #fff !important; color: #000 !important; font-weight: 600 !important; }
        .hg-channel-tabs__item::after, .hg-channel-tabs__item::before { display: none !important; }
        .hg-pager__page, .hg-pager__arrow, .hg-pager__jump-btn, .hg-pager__jump-input { background: rgba(255,255,255,.12) !important; color: #fff !important; border: 0 !important; border-radius: 999px !important; }
        .hg-pager__page.is-active { background: #fff !important; color: #000 !important; font-weight: 600 !important; }

        /* 播放页和排行榜的面板：Jable 弹窗那种 #181818 圆角 16，细边 */
        .hg-web-play__titlebar, .hg-web-play__side, .hg-web-play__facts, .hg-web-play__introcard, .hg-rank-item, .hg-web-play__action {
          background: #181818 !important; border: 1px solid rgba(255,255,255,.06) !important; border-radius: 16px !important;
        }
        .hg-web-play__action { border-radius: 999px !important; }
        .hg-web-play__ep { border-radius: 8px !important; background: rgba(255,255,255,.08) !important; border: 0 !important; }
        .hg-web-play__viewport, .hg-web-play__video { border-radius: 16px !important; overflow: hidden !important; }
        .hg-web-play__more, .hg-web-play__action--more { display: none !important; }
        """;

    /// <summary>每个页面一开始就插入整理样式；年龄确认按钮替用户点掉（Jable 启动时已经确认过已满 18 岁）。</summary>
    private static string InjectScript(Cleanup c) => $$"""
        (() => {
          if (window.top !== window) return;
          const st = document.createElement('style');
          st.textContent = {{System.Text.Json.JsonSerializer.Serialize(c.Css)}};
          const add = () => (document.head || document.documentElement).appendChild(st);
          if (document.documentElement) add(); else document.addEventListener('DOMContentLoaded', add);
          let n = 0;
          const t = setInterval(() => {
            const b = document.querySelector('.hg-age-modal__btn--enter');
            if (b) { b.click(); clearInterval(t); }
            if (++n > 40) clearInterval(t);
          }, 250);
          {{c.Script}}
        })();
        """;

    public override string Title => _title;

    /// <summary>开发用：第一次打开时直接进这个站内路径（启动参数 --page drama /video/7628/）。</summary>
    public string? StartPath { get; set; }

    private static bool IsUnder(string host, string domain) =>
        host.Equals(domain, StringComparison.OrdinalIgnoreCase) || host.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase);

    private bool IsSite(Uri u) => _isSite(u.Host) || _redirectHosts.Contains(u.Host);

    private static bool IsTracker(Uri u) => Trackers.Any(t => IsUnder(u.Host, t));

    /// <summary>页面里引用字体的站内路径前缀（被软件拦下来，用自带的 MiSans 应答）。</summary>
    private const string FontPath = "/__jable/font/";
    private static readonly Dictionary<string, byte[]?> Fonts = [];

    /// <summary>软件自带的 MiSans 文件（Assets/Fonts，打包在程序里）；没打包字体时返回 null，页面回落到微软雅黑。</summary>
    private static byte[]? FontBytes(string name)
    {
        if (name is not ("MiSans-Regular.ttf" or "MiSans-Semibold.ttf" or "MiSans-Bold.ttf" or "MiSans-Heavy.ttf")) return null;
        if (Fonts.TryGetValue(name, out var cached)) return cached;
        byte[]? bytes = null;
        try
        {
            using var s = Application.GetResourceStream(new Uri($"pack://application:,,,/Assets/Fonts/{name}"))?.Stream;
            if (s is not null) { using var ms = new MemoryStream(); s.CopyTo(ms); bytes = ms.ToArray(); }
        }
        catch (IOException) { }
        return Fonts[name] = bytes;
    }

    /// <summary>站内路径：在当前所在的域名下打开（换过域名就用新的），还没打开过就从入口地址开始。</summary>
    private void Go(string path)
    {
        var core = _view.CoreWebView2;
        if (core is null) return;
        var origin = Uri.TryCreate(core.Source, UriKind.Absolute, out var cur) && IsSite(cur) ? cur : new Uri(_home);
        core.Navigate(new Uri(origin, path).AbsoluteUri);
    }

    private static Button BarButton(string text, Action click)
    {
        var b = new Button { Content = text, Style = (Style)Application.Current.Resources["Btn"], FontFamily = (FontFamily)Application.Current.Resources["IconText"], Margin = new Thickness(8, 0, 0, 0) };
        b.Click += (_, _) => click();
        return b;
    }

    private static void OpenExternal(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme is "https" or "http")
            Process.Start(new ProcessStartInfo(u.AbsoluteUri) { UseShellExecute = true });
    }

    private async void OnFirstLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnFirstLoaded;
        _status.Text = "正在打开…";
        try
        {
            await _view.EnsureCoreWebView2Async(await WebHost.ExternalEnvironmentAsync());
        }
        catch (Exception ex)
        {
            _status.Text = "打不开：" + ex.Message;
            return;
        }
        if (Life.IsCancellationRequested) return;
        var core = _view.CoreWebView2;
        core.Settings.AreDevToolsEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.IsGeneralAutofillEnabled = false;
        core.Settings.IsPasswordAutosaveEnabled = false;

        // 统计 / 追踪请求、站内的广告脚本直接拦掉
        core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
        core.WebResourceRequested += (_, a) =>
        {
            if (!Uri.TryCreate(a.Request.Uri, UriKind.Absolute, out var u)) return;
            // 整理样式里的 MiSans：用站内地址提供软件自带的字体（同源，不用跨域），不访问任何外部服务
            if (_clean is not null && u.AbsolutePath.StartsWith(FontPath, StringComparison.OrdinalIgnoreCase))
            {
                a.Response = FontBytes(u.AbsolutePath[FontPath.Length..]) is { } font
                    ? core.Environment.CreateWebResourceResponse(new MemoryStream(font, writable: false), 200, "OK", "Content-Type: font/ttf\r\nCache-Control: max-age=31536000")
                    : core.Environment.CreateWebResourceResponse(null, 404, "Not Found", "");
                return;
            }
            if (IsTracker(u) || (_clean is { } c && c.BlockedPaths.Any(p => u.AbsolutePath.Contains(p, StringComparison.OrdinalIgnoreCase))))
                a.Response = core.Environment.CreateWebResourceResponse(null, 403, "Blocked", "");
        };
        if (_clean is { } clean) await core.AddScriptToExecuteOnDocumentCreatedAsync(InjectScript(clean));
        // 新窗口：站内的就在这里打开，站外的交给系统浏览器
        core.NewWindowRequested += (_, a) =>
        {
            a.Handled = true;
            if (Uri.TryCreate(a.Uri, UriKind.Absolute, out var u) && IsSite(u)) core.Navigate(u.AbsoluteUri);
            else OpenExternal(a.Uri);
        };
        core.NavigationStarting += (_, a) =>
        {
            if (!Uri.TryCreate(a.Uri, UriKind.Absolute, out var u) || !u.Scheme.StartsWith("http")) return;
            if (_followRedirects && a.IsRedirected) _redirectHosts.Add(u.Host); // 网站自己换到的新域名
            if (!IsSite(u))
            {
                a.Cancel = true;
                OpenExternal(a.Uri);
                return;
            }
            if (_clean?.BlockedPages?.Any(p => u.AbsolutePath.StartsWith(p, StringComparison.OrdinalIgnoreCase)) == true)
            {
                a.Cancel = true; // 不收的栏目：停在原处
                _status.Text = "这个栏目不在 Jable 里显示";
                return;
            }
            _status.Text = "正在打开…";
        };
        core.NavigationCompleted += (_, a) =>
        {
            _status.Text = a.IsSuccess ? "" : "没打开，可以点「刷新」再试";
            _back.IsEnabled = core.CanGoBack;
            if (Uri.TryCreate(core.Source, UriKind.Absolute, out var u)) SyncSection(u);
        };
        core.Navigate(StartPath is { } start ? new Uri(new Uri(_home), start).AbsoluteUri : _home);
    }

    public override void OnClosed()
    {
        base.OnClosed();
        _view.Dispose();
    }
}