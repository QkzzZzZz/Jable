using System.Collections.Concurrent;
using System.IO;
using System.Text;
using System.Text.Json;
using JableLens.Core.Http;
using JableLens.Core.Library;
using JableLens.Core.Models;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace JableLens.App.Services;

/// <summary>
/// HLS 下载（任务队列、分片、解密、合并在共用的 <see cref="DownloadEngine"/> 里）。
///
/// 这里只负责「在浏览器里取数据」：视频 CDN 有 Cloudflare，普通 HTTP 请求会被 403，所以和播放一样走浏览器内核：
/// 一个隐藏的 WebView2 停在作品页同一个网址（本地应答一个空白页，同源），页面里 fetch 分片，
/// 再把字节 POST 到同源的 /__jablelens/put/…，这里拦下请求直接拿到二进制（不经过 base64）。
/// </summary>
public sealed class Downloader(WebView2 view) : IStreamFetcher
{
    private const string Prefix = "/__jablelens/";

    private const string PageJs = """
        window.JLD = {
          get(url, id) {
            fetch(url)
              .then(r => { if (!r.ok) throw new Error('HTTP ' + r.status); return r.arrayBuffer(); })
              .then(b => fetch('/__jablelens/put/' + id, { method: 'POST', body: b }))
              .catch(e => window.chrome.webview.postMessage({ jld: id, error: String(e && e.message || e) }));
          }
        };
        """;

    private readonly ConcurrentDictionary<string, TaskCompletionSource<byte[]>> _pending = new();
    private readonly HashSet<string> _extraHosts = new(StringComparer.OrdinalIgnoreCase);
    private CoreWebView2Environment? _env;
    private string? _pageUrl;
    private TaskCompletionSource? _pageReady;
    private DownloadEngine? _engine;

    private DownloadEngine Engine => _engine ??= new DownloadEngine(AppServices.Library, AppServices.Sites, this, () => Folder);

    /// <summary>任务状态或进度变了（UI 线程上触发，进度大约每秒一次）。</summary>
    public event Action? Changed
    {
        add => Engine.Changed += value;
        remove => Engine.Changed -= value;
    }

    public static string Folder
    {
        get
        {
            var custom = AppServices.Library.Read(d => d.Settings.DownloadFolder);
            return string.IsNullOrWhiteSpace(custom)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "Jable")
                : custom;
        }
    }

    public IReadOnlyList<DownloadRecord> All => Engine.All;

    public int ActiveCount => Engine.ActiveCount;

    public DownloadRecord? Find(VideoCard card) => Engine.Find(card);

    /// <summary>启动时：上次没下完的接着下。</summary>
    public void Start() => Engine.Start();

    public void Enqueue(VideoCard card) => Engine.Enqueue(card);

    public void Pause(string id) => Engine.Pause(id);

    public void Resume(string id) => Engine.Resume(id);

    /// <summary>删除任务（和没下完的分片）；deleteFile 时连下好的文件一起删。</summary>
    public void Remove(string id, bool deleteFile) => Engine.Remove(id, deleteFile);

    // ---------------- 浏览器里取数据 ----------------

    public void AllowHost(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var u)) _extraHosts.Add(u.Host);
    }

    /// <summary>让下载用的浏览器停在作品页的网址上（本地应答一个只有下载脚本的空白页），之后的 fetch 和播放器一样是同源发出的。</summary>
    public async Task PrepareAsync(string pageUrl, string streamUrl, CancellationToken ct)
    {
        AllowHost(streamUrl);
        if (view.CoreWebView2 is null)
        {
            _env = await WebHost.EnvironmentAsync();
            await view.EnsureCoreWebView2Async(_env);
            var core = view.CoreWebView2!;
            WebHost.Configure(core, _env, lightweight: true, _extraHosts);
            core.AddWebResourceRequestedFilter("*" + Prefix + "*", CoreWebView2WebResourceContext.All);
            core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.Document);
            core.WebResourceRequested += OnRequest;
            core.WebMessageReceived += OnMessage;
            core.NavigationCompleted += (_, _) => _pageReady?.TrySetResult();
        }
        var origin = new Uri(pageUrl).GetLeftPart(UriPartial.Authority);
        if (_pageUrl is not null && new Uri(_pageUrl).GetLeftPart(UriPartial.Authority) == origin) return;
        _pageUrl = pageUrl;
        _pageReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        view.CoreWebView2!.Navigate(pageUrl);
        await _pageReady.Task.WaitAsync(TimeSpan.FromSeconds(20), ct);
    }

    private void OnRequest(object? sender, CoreWebView2WebResourceRequestedEventArgs e)
    {
        if (_env is null || _pageUrl is null || !Uri.TryCreate(e.Request.Uri, UriKind.Absolute, out var u)) return;
        var page = new Uri(_pageUrl);
        if (!u.Host.Equals(page.Host, StringComparison.OrdinalIgnoreCase)) return;
        if (e.ResourceContext == CoreWebView2WebResourceContext.Document && u.AbsolutePath == page.AbsolutePath)
        {
            var html = "<!doctype html><html><head><meta charset=\"utf-8\"></head><body><script>" + PageJs + "</script></body></html>";
            e.Response = _env.CreateWebResourceResponse(new MemoryStream(Encoding.UTF8.GetBytes(html)), 200, "OK", "Content-Type: text/html; charset=utf-8\nCache-Control: no-store");
            return;
        }
        if (!u.AbsolutePath.StartsWith(Prefix + "put/", StringComparison.Ordinal)) return;
        var id = u.AbsolutePath[(Prefix.Length + 4)..];
        byte[] body;
        using (var ms = new MemoryStream())
        {
            e.Request.Content?.CopyTo(ms);
            body = ms.ToArray();
        }
        if (_pending.TryRemove(id, out var tcs)) tcs.TrySetResult(body);
        e.Response = _env.CreateWebResourceResponse(null, 204, "No Content", "");
    }

    private void OnMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            using var doc = JsonDocument.Parse(e.WebMessageAsJson);
            if (doc.RootElement.TryGetProperty("jld", out var id) && id.GetString() is { } key && _pending.TryRemove(key, out var tcs))
                tcs.TrySetException(new SourceException("下载", doc.RootElement.TryGetProperty("error", out var err) ? err.GetString() ?? "失败" : "失败"));
        }
        catch (JsonException) { }
    }

    public async Task<byte[]> FetchAsync(string url, CancellationToken ct)
    {
        var id = Guid.NewGuid().ToString("N");
        var tcs = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;
        try
        {
            await view.Dispatcher.InvokeAsync(() => view.CoreWebView2.ExecuteScriptAsync($"window.JLD && window.JLD.get({JsonSerializer.Serialize(url)}, '{id}')")).Task.Unwrap();
            return await tcs.Task.WaitAsync(TimeSpan.FromSeconds(90), ct);
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }
}
