namespace JableLens.Core.Sites;

public sealed record PageResult<T>(IReadOnlyList<T> Items, bool HasMore);

/// <summary>
/// 用真实浏览器内核读有 Cloudflare 防护的站点（Windows 是 WebView2，安卓是系统 WebView）。
/// 遇到人机验证时由各平台把浏览器显示出来，让用户自己通过。
/// </summary>
public interface IPageReader
{
    /// <summary>取某个地址的整页 HTML。浏览器已停在该站点时直接在页面里 fetch，否则打开页面读。</summary>
    Task<string> FetchHtmlAsync(string url, string sourceName, CancellationToken ct = default);

    /// <summary>打开 <paramref name="url"/>，轮询 <paramref name="readyScript"/>（JS 表达式，未就绪返回 null），返回其结果的 JSON 文本。</summary>
    Task<string> ReadAsync(string url, string readyScript, string sourceName, TimeSpan? timeout = null, CancellationToken ct = default);
}

/// <summary>各平台共用的页面脚本。</summary>
public static class PageScripts
{
    public const string IsChallengeJs = """
        (() => {
          const t = (document.title || '').toLowerCase();
          return t.includes('just a moment') || t.includes('请稍候') || t.includes('請稍候') || t.includes('attention required')
            || !!document.querySelector('#challenge-form, #challenge-running, .cf-turnstile, #cf-challenge-running, iframe[src*="challenges.cloudflare.com"]');
        })()
        """;

    /// <summary>服务端渲染的页面：出现 footer 且不是验证页 = 内容已完整，返回整页 HTML。</summary>
    public const string FullHtmlWhenReadyJs =
        "(document.querySelector('footer, img[x-ref^=\"page-\"]') && !" + IsChallengeJs + ") ? document.documentElement.outerHTML : null";

    /// <summary>页面正文是 JSON（接口地址）时返回它。要等整页加载完：大的 JSON 是边下边显示的，中途读会拿到半截。</summary>
    public const string JsonWhenReadyJs =
        "(() => { if (document.readyState !== 'complete') return null; const t = document.body && document.body.innerText.trim(); return t && t.startsWith('{') ? t : null; })()";

    /// <summary>fetch 拿到的是不是 Cloudflare 验证页。</summary>
    public static bool LooksLikeChallenge(string body) =>
        body.Length < 60_000 && (body.Contains("challenge-platform", StringComparison.Ordinal)
                                 || body.Contains("<title>Just a moment", StringComparison.OrdinalIgnoreCase)
                                 || body.Contains("cf-chl", StringComparison.Ordinal));

    /// <summary>站点、视频 CDN、Cloudflare 验证：浏览器只放行这些主机（顺带挡掉广告和统计脚本）。</summary>
    public static readonly string[] AllowedHosts =
    [
        "jable.tv", "mushroomtrack.com",
        "cloudflare.com",
    ];

    public static bool IsAllowedHost(string host) =>
        AllowedHosts.Any(h => host.Equals(h, StringComparison.OrdinalIgnoreCase) || host.EndsWith("." + h, StringComparison.OrdinalIgnoreCase));
}
