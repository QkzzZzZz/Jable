using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using JableLens.App.Services;
using JableLens.Core.Http;
using JableLens.Core.Models;
using JableLens.Core.Subtitles;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace JableLens.App;

/// <summary>
/// 播放窗口：在站点自己的播放页里注入 JableLens 播放器（同源请求，能通过 Cloudflare），
/// 字幕由应用下载后交给页面叠加显示，所以小窗里也有字幕。
/// </summary>
public sealed class PlayerWindow : Window
{
    private static readonly Lazy<string> HlsJs = new(() => ReadResource("hls.min.js"));
    private static readonly Lazy<string> PlayerJs = new(() => ReadResource("player.js"));

    private static readonly Dictionary<string, string> LanguageNames = new()
    {
        ["zh-CN"] = "简体中文", ["zh-TW"] = "繁体中文", ["en"] = "英语", ["ja"] = "日语", ["ko"] = "韩语",
        ["th"] = "泰语", ["vi"] = "越南语", ["id"] = "印尼语", ["ms"] = "马来语", ["es"] = "西班牙语",
        ["ar"] = "阿拉伯语", ["hi"] = "印地语", ["tl"] = "菲律宾语", ["ro"] = "罗马尼亚语", ["fr"] = "法语", ["de"] = "德语", ["ru"] = "俄语",
    };

    private readonly VideoCard _card;
    private readonly WebView2 _view = new() { Visibility = Visibility.Hidden };
    private readonly TextBlock _status = new() { Foreground = Brushes.White, FontSize = 15, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(30) };
    private readonly Border _miniBar;
    private readonly HashSet<string> _extraHosts = new(StringComparer.OrdinalIgnoreCase);
    private readonly CancellationTokenSource _life = new();
    private readonly List<SubtitleTrack> _tracks = [];
    private bool _started;
    private readonly System.Diagnostics.Stopwatch _opened = System.Diagnostics.Stopwatch.StartNew();
    private bool _mini;
    private bool _fullscreen;
    private Rect _normalBounds;
    private WindowState _normalState;
    private double _lastPosition;
    private double _lastDuration;

    public PlayerWindow(VideoCard card)
    {
        _card = card;
        Controls.WindowFrame.UseDark(this);
        Title = $"{card.Code}  {card.Title}";
        Width = 1280;
        Height = 760;
        MinWidth = 320;
        MinHeight = 200;
        Background = Brushes.Black;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Icon = new System.Windows.Media.Imaging.BitmapImage(new Uri("pack://application:,,,/Assets/app.ico"));

        _miniBar = BuildMiniBar();
        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition());
        grid.Children.Add(_miniBar);
        Grid.SetRow(_status, 1);
        Grid.SetRow(_view, 1);
        grid.Children.Add(_status);
        grid.Children.Add(_view);
        Content = grid;

        _status.Text = "正在打开播放页…";
        Loaded += async (_, _) => await StartAsync();
        Closing += OnClosingWindow;
    }

    private Border BuildMiniBar()
    {
        var restore = new Button { Content = "还原", Style = (Style)Application.Current.Resources["GhostBtn"], Padding = new Thickness(8, 1, 8, 1), FontSize = 12 };
        restore.Click += (_, _) => SetMini(false);
        var close = new Button { Content = "✕", Style = (Style)Application.Current.Resources["GhostBtn"], Padding = new Thickness(8, 1, 8, 1), FontSize = 12 };
        close.Click += (_, _) => Close();
        var dock = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(close, Dock.Right);
        DockPanel.SetDock(restore, Dock.Right);
        dock.Children.Add(close);
        dock.Children.Add(restore);
        dock.Children.Add(new TextBlock { Text = "⠿  " + _card.Code, Foreground = Brushes.Gainsboro, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) });
        var bar = new Border { Background = new SolidColorBrush(Color.FromRgb(24, 24, 24)), Height = 26, Child = dock, Visibility = Visibility.Collapsed, Cursor = Cursors.SizeAll };
        bar.MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed) DragMove(); };
        return bar;
    }

    private async Task StartAsync()
    {
        var ct = _life.Token;
        try
        {
            var env = await WebHost.EnvironmentAsync();
            var envTask = _view.EnsureCoreWebView2Async(env);
            // 播放地址通常在详情页已经读过（有缓存），和浏览器初始化同时进行
            var quickUrl = await AppServices.Sites.StreamUrlAsync(_card, ct);
            await envTask;
            var core = _view.CoreWebView2;
            WebHost.Configure(core, env, lightweight: false, _extraHosts);
            core.WebMessageReceived += OnMessage;

            if (quickUrl is not null)
            {
                // 快速路径：拦下播放页的请求，直接返回自己的播放器页面（同一个网址 = 同源，cookie/跨域都对）
                if (Uri.TryCreate(quickUrl, UriKind.Absolute, out var qu)) _extraHosts.Add(qu.Host);
                ServeSyntheticPlayer(core, env, BuildOptions(quickUrl, synthetic: true));
                core.Navigate(_card.PageUrl);
                return; // 页面里的 JL.start 完成后会发 started 消息
            }

            // 慢速路径：打开站点真实的播放页，等页面脚本生成播放地址
            core.Navigate(_card.PageUrl);
            var url = await WaitForStreamUrlAsync(core, ct);
            if (Uri.TryCreate(url, UriKind.Absolute, out var u)) _extraHosts.Add(u.Host);

            await core.ExecuteScriptAsync(HlsBootstrap());
            await core.ExecuteScriptAsync(PlayerJs.Value);
            await core.ExecuteScriptAsync($"window.JL.start({BuildOptions(url, synthetic: false).ToJsonString()});");
        }
        catch (OperationCanceledException) { }
        catch (SourceException e)
        {
            _view.Visibility = Visibility.Hidden;
            _status.Text = "无法播放：" + e.Message;
        }
        catch (Exception e) when (e is WebView2RuntimeNotFoundException or System.Runtime.InteropServices.COMException)
        {
            _status.Text = "WebView2 出错：" + e.Message;
        }
    }

    private const string SyntheticPrefix = "/__jablelens/";

    private static string HlsBootstrap() =>
        "(function(){var module=undefined,exports=undefined,define=undefined;" + HlsJs.Value + "\n;window.JLHls=window.Hls;}).call(window);";

    private JsonObject BuildOptions(string url, bool synthetic)
    {
        var history = AppServices.Library.GetHistory(_card.PageUrl);
        var startAt = history?.IsUnfinished == true ? history.Position : 0;
        var prefs = AppServices.Library.Read(d => (d.Settings.PlaybackRate, d.Subtitles.Enabled, d.Settings.Volume, d.Settings.Muted));
        return new JsonObject
        {
            ["url"] = url,
            ["startAt"] = startAt,
            ["rate"] = prefs.PlaybackRate,
            ["subtitlesEnabled"] = prefs.Enabled,
            ["volume"] = prefs.Volume,
            ["muted"] = prefs.Muted,
            ["offset"] = AppServices.Library.SubtitleOffset(_card.Code),
            ["title"] = $"{_card.Code}  {_card.Title}",
            ["synthetic"] = synthetic,
        };
    }

    /// <summary>把播放页地址和两个脚本地址的请求都在本地应答，不发到网站。</summary>
    private void ServeSyntheticPlayer(CoreWebView2 core, CoreWebView2Environment env, JsonObject opts)
    {
        var page = new Uri(_card.PageUrl);
        var origin = page.GetLeftPart(UriPartial.Authority);
        var html = $$"""
            <!doctype html><html><head><meta charset="utf-8"><title>Jable</title>
            <style>html,body{margin:0;background:#000;height:100%;overflow:hidden}</style></head>
            <body><script src="{{origin}}{{SyntheticPrefix}}hls.js"></script><script src="{{origin}}{{SyntheticPrefix}}player.js"></script>
            <script>window.JL.start({{opts.ToJsonString()}});</script></body></html>
            """;
        core.WebResourceRequested += (_, e) =>
        {
            if (!Uri.TryCreate(e.Request.Uri, UriKind.Absolute, out var u) || !u.Host.Equals(page.Host, StringComparison.OrdinalIgnoreCase)) return;
            string? body = null, type = null;
            if (u.AbsolutePath == page.AbsolutePath && e.ResourceContext == CoreWebView2WebResourceContext.Document) { body = html; type = "text/html"; }
            else if (u.AbsolutePath == SyntheticPrefix + "hls.js") { body = HlsBootstrap(); type = "text/javascript"; }
            else if (u.AbsolutePath == SyntheticPrefix + "player.js") { body = PlayerJs.Value; type = "text/javascript"; }
            if (body is null) return;
            e.Response = env.CreateWebResourceResponse(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(body)), 200, "OK",
                $"Content-Type: {type}; charset=utf-8\nCache-Control: no-store");
        };
    }

    private void OnStarted()
    {
        if (_started) return;
        _started = true;
        Perf.Log($"player started after {_opened.ElapsedMilliseconds} ms  {_card.PageUrl}");
        _view.Visibility = Visibility.Visible;
        _view.Focus();
        _ = LoadSubtitlesAsync(_life.Token);
    }

    /// <summary>等页面里出现播放地址。Jable：hlsUrl。</summary>
    private async Task<string> WaitForStreamUrlAsync(CoreWebView2 core, CancellationToken ct)
    {
        const string script = "typeof hlsUrl === 'string' && hlsUrl ? hlsUrl : null";
        const string site = "Jable";
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(40);
        var challenge = false;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            await Task.Delay(200, ct);
            string raw;
            try
            {
                raw = await core.ExecuteScriptAsync($"(() => {{ try {{ return ({script}); }} catch (e) {{ return null; }} }})()");
                var isChallenge = await core.ExecuteScriptAsync(WebPageReader.IsChallengeJs) == "true";
                if (isChallenge != challenge)
                {
                    challenge = isChallenge;
                    // 验证页要让用户看见并点击
                    _view.Visibility = challenge ? Visibility.Visible : Visibility.Hidden;
                    _status.Text = challenge ? "" : "正在打开播放页…";
                    if (challenge) deadline = DateTime.UtcNow + WebPageReader.ChallengeTimeout;
                }
            }
            catch (Exception e) when (e is InvalidOperationException or System.Runtime.InteropServices.COMException)
            {
                continue;
            }
            if (raw != "null")
            {
                var url = JsonSerializer.Deserialize<string>(raw);
                if (!string.IsNullOrEmpty(url)) return url;
            }
            if (DateTime.UtcNow > deadline)
                throw new SourceException(site,
                    challenge ? "人机验证没有通过（超时）" : "播放页里找不到视频地址（作品可能已下架，或页面结构变了）");
        }
    }

    // ---------------- 字幕 ----------------

    private async Task LoadSubtitlesAsync(CancellationToken ct)
    {
        var core = _view.CoreWebView2;
        try
        {
            var hits = await AppServices.Subtitles.SearchAsync(_card.Code, ct);
            foreach (var h in hits.Take(4))
            {
                try { _tracks.AddRange(await AppServices.Subtitles.GetTracksAsync(h, ct)); }
                catch (SourceException) { }
            }
        }
        catch (SourceException) { }
        catch (OperationCanceledException) { return; }
        if (ct.IsCancellationRequested) return;

        var multiSource = _tracks.Select(t => t.SourceLabel).Distinct().Count() > 1;
        var list = new JsonArray(_tracks.Select((t, i) => (JsonNode)new JsonObject
        {
            ["id"] = i,
            ["name"] = LanguageNames.GetValueOrDefault(t.LanguageCode, t.DisplayName) + (multiSource ? $" · {t.SourceLabel}" : ""),
        }).ToArray());

        var preferred = AppServices.Library.Read(d => d.Subtitles.PreferredLanguage);
        var pick = PickTrack(preferred);
        await core.ExecuteScriptAsync($"window.JL.setTracks({list.ToJsonString()}, {(pick is null ? "null" : pick.Value.ToString())});");
        if (pick is { } p) await LoadTrackAsync(p);
    }

    private int? PickTrack(string preferred)
    {
        foreach (var lang in new[] { preferred, "zh-CN", "zh-TW", "en" })
        {
            var i = _tracks.FindIndex(t => t.LanguageCode.Equals(lang, StringComparison.OrdinalIgnoreCase));
            if (i >= 0) return i;
        }
        return _tracks.Count > 0 ? 0 : null;
    }

    private async Task LoadTrackAsync(int index)
    {
        if (index < 0 || index >= _tracks.Count) return;
        var core = _view.CoreWebView2;
        try
        {
            var cues = await AppServices.Subtitles.DownloadAsync(_tracks[index], _life.Token);
            var arr = new JsonArray(cues.Select(c => (JsonNode)new JsonObject
            {
                ["s"] = Math.Round(c.Start.TotalSeconds, 3),
                ["e"] = Math.Round(c.End.TotalSeconds, 3),
                ["t"] = c.Text,
            }).ToArray());
            await core.ExecuteScriptAsync($"window.JL.setCues({arr.ToJsonString()}, {AppServices.Library.SubtitleOffset(_card.Code).ToString(System.Globalization.CultureInfo.InvariantCulture)});");
        }
        catch (Exception e) when (e is SourceException or System.Net.Http.HttpRequestException or TaskCanceledException)
        {
            await core.ExecuteScriptAsync($"window.JL.toast({JsonSerializer.Serialize("字幕下载失败")});");
        }
    }

    // ---------------- 页面消息 ----------------

    private void OnMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        JsonNode? msg;
        try { msg = JsonNode.Parse(e.WebMessageAsJson); }
        catch (JsonException) { return; }
        switch ((string?)msg?["type"])
        {
            case "started":
                OnStarted();
                break;
            case "progress":
                _lastPosition = (double?)msg["position"] ?? 0;
                _lastDuration = (double?)msg["duration"] ?? 0;
                AppServices.Library.RecordProgress(_card, _lastPosition, _lastDuration);
                break;
            case "offset":
                AppServices.Library.SetSubtitleOffset(_card.Code, (double?)msg["value"] ?? 0);
                break;
            case "selectTrack":
                if ((int?)msg["id"] is { } id) _ = LoadTrackAsync(id);
                break;
            case "subsEnabled":
                AppServices.Library.Mutate(d => d.Subtitles.Enabled = (bool?)msg["value"] ?? true);
                break;
            case "rate":
                AppServices.Library.Mutate(d => d.Settings.PlaybackRate = (double?)msg["value"] ?? 1);
                break;
            case "volume":
                AppServices.Library.Mutate(d =>
                {
                    d.Settings.Volume = Math.Clamp((double?)msg["value"] ?? 1, 0, 1);
                    d.Settings.Muted = (bool?)msg["muted"] ?? false;
                });
                break;
            case "download":
                var already = AppServices.Downloads.Find(_card) is not null;
                if (!already) AppServices.Downloads.Enqueue(_card);
                _ = _view.CoreWebView2?.ExecuteScriptAsync($"window.JL.toast({JsonSerializer.Serialize(already ? "已经在下载列表里了" : "已加入下载，在主窗口右上角 ⤓ 查看进度")})");
                break;
            case "mini":
                SetMini(!_mini);
                break;
            case "fullscreen":
                SetFullscreen(!_fullscreen);
                break;
            case "escape":
                if (_fullscreen) SetFullscreen(false);
                else if (_mini) SetMini(false);
                break;
            case "error":
                _status.Text = (string?)msg["message"] ?? "播放失败";
                break;
        }
    }

    // ---------------- 小窗 / 全屏 ----------------

    private void SaveBounds()
    {
        _normalState = WindowState;
        _normalBounds = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
    }

    private void RestoreNormal()
    {
        WindowStyle = WindowStyle.SingleBorderWindow;
        ResizeMode = ResizeMode.CanResize;
        Topmost = false;
        WindowState = WindowState.Normal;
        Left = _normalBounds.Left;
        Top = _normalBounds.Top;
        Width = _normalBounds.Width;
        Height = _normalBounds.Height;
        WindowState = _normalState == WindowState.Maximized ? WindowState.Maximized : WindowState.Normal;
    }

    private void SetMini(bool on)
    {
        if (on == _mini) return;
        if (_fullscreen) SetFullscreen(false);
        _mini = on;
        if (on)
        {
            SaveBounds();
            WindowState = WindowState.Normal;
            WindowStyle = WindowStyle.ToolWindow;
            ResizeMode = ResizeMode.CanResizeWithGrip;
            Topmost = true;
            var area = SystemParameters.WorkArea;
            Width = 520;
            Height = 520 * 9 / 16 + 26 + 8;
            Left = area.Right - Width - 16;
            Top = area.Bottom - Height - 16;
            WindowStyle = WindowStyle.None;
            // 去掉无边框窗口顶部残留的白边，保留拖边缩放
            System.Windows.Shell.WindowChrome.SetWindowChrome(this, new System.Windows.Shell.WindowChrome
            {
                CaptionHeight = 0, ResizeBorderThickness = new Thickness(6), GlassFrameThickness = new Thickness(0), CornerRadius = new CornerRadius(0),
            });
            _miniBar.Visibility = Visibility.Visible;
        }
        else
        {
            System.Windows.Shell.WindowChrome.SetWindowChrome(this, null);
            _miniBar.Visibility = Visibility.Collapsed;
            RestoreNormal();
        }
        if (_started) _ = _view.CoreWebView2.ExecuteScriptAsync($"window.JL.setMini({(on ? "true" : "false")});");
    }

    private void SetFullscreen(bool on)
    {
        if (on == _fullscreen) return;
        if (_mini) SetMini(false);
        _fullscreen = on;
        if (on)
        {
            SaveBounds();
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            WindowState = WindowState.Normal; // 先还原再最大化，才能盖住任务栏
            WindowState = WindowState.Maximized;
        }
        else
        {
            RestoreNormal();
        }
    }

    private void OnClosingWindow(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        _life.Cancel();
        if (_started && _lastDuration > 0) AppServices.Library.RecordProgress(_card, _lastPosition, _lastDuration);
        try { _view.Dispose(); } catch (InvalidOperationException) { }
    }

    private static string ReadResource(string name)
    {
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream(name)
                      ?? throw new FileNotFoundException("缺少内置资源 " + name);
        using var r = new StreamReader(s);
        return r.ReadToEnd();
    }
}
