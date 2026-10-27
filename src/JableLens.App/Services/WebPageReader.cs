using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using JableLens.Core.Http;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace JableLens.App.Services;

/// <summary>
/// 用真实浏览器内核（WebView2）读取有 Cloudflare 防护的站点。两种方式：
/// <list type="bullet">
/// <item>导航读取：打开页面，每 200ms 执行「就绪脚本」，拿到结果就停止加载（readEarly）。遇到人机验证时触发
/// <see cref="ChallengeStateChanged"/>，界面把浏览器显示出来让用户自己通过。</item>
/// <item>fetch 读取：浏览器已经停在该站点的页面上时，直接在页面里 fetch 目标地址的 HTML。
/// 请求由浏览器发出、带着同一份 cookie，和用户点链接一样，但不用渲染整页、不跑广告脚本，快很多。
/// 如果 fetch 拿到的是验证页，就退回导航读取，让用户在浏览器里处理。</item>
/// </list>
/// fetch 可以同时进行多个；导航一次只能一个。
/// </summary>
public sealed class WebPageReader(WebView2 view, string name, Func<Task<CoreWebView2Environment>>? environment = null) : IPageReader
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(35);
    public static readonly TimeSpan ChallengeTimeout = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan FetchTimeout = TimeSpan.FromSeconds(25);

    public const string IsChallengeJs = PageScripts.IsChallengeJs;

    public const string FullHtmlWhenReadyJs = PageScripts.FullHtmlWhenReadyJs;

    private const string FetchPrefix = "{\"jlFetch\":\"";

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, TaskCompletionSource<string>> _pending = [];
    private Task? _init;
    private Task<string>? _landing;

    /// <summary>true = 正在等用户通过验证（界面应显示这个浏览器）；false = 验证结束。</summary>
    public event Action<WebPageReader, bool>? ChallengeStateChanged;

    public WebView2 View => view;

    public string Name => name;

    private Task EnsureInitAsync() => _init ??= InitAsync();

    private async Task InitAsync()
    {
        var env = await (environment ?? WebHost.EnvironmentAsync)();
        await view.EnsureCoreWebView2Async(env);
        var core = view.CoreWebView2;
        WebHost.Configure(core, env, lightweight: true);
        core.WebMessageReceived += OnMessage;
        // 页面一跳转，正在进行的 fetch 就会中断：立即让它们失败并重试，而不是等超时
        core.NavigationStarting += (_, _) => FailPending();
    }

    // ---------------- fetch 读取 ----------------

    private sealed class RetryFetchException : Exception;

    /// <summary>取某个地址的 HTML。能 fetch 就 fetch，不行就导航读取。</summary>
    public async Task<string> FetchHtmlAsync(string url, string sourceName, CancellationToken ct = default)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            // 有导航正在进行就等它结束（导航会打断 fetch）
            await _gate.WaitAsync(ct);
            _gate.Release();

            var onOrigin = await OnUi(async () =>
            {
                await EnsureInitAsync();
                return SameOrigin(view.CoreWebView2.Source, url);
            });
            if (!onOrigin)
            {
                // 第一次访问这个站：只让一个请求完整打开页面（顺便过验证），同时到的其它请求等它完成后再 fetch
                Task<string> landing;
                bool mine;
                lock (_gate)
                {
                    mine = _landing is null || _landing.IsCompleted;
                    if (mine) _landing = ReadHtmlAsync(url, sourceName);
                    landing = _landing!;
                }
                if (mine) return await landing.WaitAsync(ct);
                try { await landing.WaitAsync(ct); }
                catch (SourceException) { }
                continue;
            }

            string json;
            try
            {
                json = await OnUi(() => StartFetch(url)).WaitAsync(FetchTimeout, ct);
            }
            catch (RetryFetchException)
            {
                continue;
            }
            catch (TimeoutException)
            {
                return await ReadHtmlAsync(url, sourceName, ct);
            }

            var (status, body) = await Task.Run(() => ParseFetchResult(json), ct);
            if (status == 404) throw new SourceException(sourceName, "找不到（404）");
            if (status is 0 or 403 or 429 or 503 || LooksLikeChallenge(body))
                return await ReadHtmlAsync(url, sourceName, ct); // 被拦了：交给浏览器正常打开，必要时让用户验证
            if (status >= 400) throw new SourceException(sourceName, $"服务器返回 {status}");
            return body;
        }
        return await ReadHtmlAsync(url, sourceName, ct);
    }

    private Task<string> StartFetch(string url)
    {
        var id = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;
        var script = $$"""
            (async () => {
              const id = '{{id}}';
              try {
                const r = await fetch({{JsonSerializer.Serialize(url)}}, { credentials: 'include' });
                const body = await r.text();
                window.chrome.webview.postMessage({ jlFetch: id, status: r.status, body });
              } catch (e) {
                window.chrome.webview.postMessage({ jlFetch: id, status: 0, body: String(e) });
              }
            })(); 0
            """;
        _ = view.CoreWebView2.ExecuteScriptAsync(script);
        return tcs.Task;
    }

    private void OnMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        var json = e.WebMessageAsJson;
        // id 是随机的 32 位十六进制，页面里的脚本猜不到，不会被冒充
        if (!json.StartsWith(FetchPrefix, StringComparison.Ordinal) || json.Length < FetchPrefix.Length + 32) return;
        var id = json.Substring(FetchPrefix.Length, 32);
        if (_pending.Remove(id, out var tcs)) tcs.TrySetResult(json);
    }

    private void FailPending()
    {
        foreach (var tcs in _pending.Values) tcs.TrySetException(new RetryFetchException());
        _pending.Clear();
    }

    private static (int Status, string Body) ParseFetchResult(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        return (root.GetProperty("status").GetInt32(), root.GetProperty("body").GetString() ?? "");
    }

    private static bool LooksLikeChallenge(string body) => PageScripts.LooksLikeChallenge(body);

    private static bool SameOrigin(string? current, string target) =>
        Uri.TryCreate(current, UriKind.Absolute, out var a) && Uri.TryCreate(target, UriKind.Absolute, out var b)
        && a.Scheme == b.Scheme && a.Host.Equals(b.Host, StringComparison.OrdinalIgnoreCase);

    // ---------------- 导航读取 ----------------

    /// <summary>导航读取整页 HTML。</summary>
    public async Task<string> ReadHtmlAsync(string url, string sourceName, CancellationToken ct = default)
    {
        Perf.Log($"  navigate  {url}");
        var raw = await ReadAsync(url, FullHtmlWhenReadyJs, sourceName, null, ct);
        return await Task.Run(() => JsonSerializer.Deserialize<string>(raw)!, ct);
    }

    /// <summary>
    /// 打开 <paramref name="url"/>，轮询 <paramref name="readyScript"/>（JS 表达式，未就绪返回 null），
    /// 返回其结果的 JSON 文本。
    /// </summary>
    public async Task<string> ReadAsync(string url, string readyScript, string sourceName, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            return await OnUi(async () =>
            {
                await EnsureInitAsync();
                var core = view.CoreWebView2;
                core.Navigate(url);

                var normal = timeout ?? DefaultTimeout;
                var deadline = DateTime.UtcNow + normal;
                var inChallenge = false;
                try
                {
                    while (true)
                    {
                        if (ct.IsCancellationRequested)
                        {
                            core.Stop();
                            ct.ThrowIfCancellationRequested();
                        }
                        await Task.Delay(PollInterval, CancellationToken.None);

                        string raw;
                        try { raw = await core.ExecuteScriptAsync($"(() => {{ try {{ return ({readyScript}); }} catch (e) {{ return null; }} }})()"); }
                        catch (Exception e) when (e is InvalidOperationException or System.Runtime.InteropServices.COMException) { continue; }

                        if (raw != "null" && raw != "undefined")
                        {
                            core.Stop();
                            return raw;
                        }

                        bool challenge;
                        try { challenge = await core.ExecuteScriptAsync(IsChallengeJs) == "true"; }
                        catch (Exception e) when (e is InvalidOperationException or System.Runtime.InteropServices.COMException) { continue; }

                        if (challenge != inChallenge)
                        {
                            inChallenge = challenge;
                            deadline = DateTime.UtcNow + (challenge ? ChallengeTimeout : normal);
                            ChallengeStateChanged?.Invoke(this, challenge);
                        }

                        if (DateTime.UtcNow > deadline)
                        {
                            core.Stop();
                            throw new SourceException(sourceName, inChallenge
                                ? "人机验证没有通过（超时），请重试"
                                : "页面加载超时，或页面结构变了读不到内容");
                        }
                    }
                }
                finally
                {
                    if (inChallenge) ChallengeStateChanged?.Invoke(this, false);
                }
            });
        }
        finally
        {
            _gate.Release();
        }
    }

    internal static Task<T> OnUi<T>(Func<Task<T>> f)
    {
        var d = Application.Current.Dispatcher;
        return d.CheckAccess() ? f() : d.InvokeAsync(f).Task.Unwrap();
    }
}
