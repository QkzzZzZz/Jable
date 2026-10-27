using System.IO;
using Microsoft.Web.WebView2.Core;

namespace JableLens.App.Services;

/// <summary>
/// 所有 WebView2 共用一个环境（同一个用户数据目录 → 同一份 cookie，人机验证通过一次大家都能用）。
/// 网络请求按主机白名单过滤：不在名单里的一律拦掉（顺带挡掉了广告和统计脚本）。
/// </summary>
public static class WebHost
{
    private static Task<CoreWebView2Environment>? _env;

    /// <summary>站点、视频 CDN、Cloudflare 验证。</summary>
    public static readonly string[] AllowedHosts =
    [
        "jable.tv", "mushroomtrack.com",
        "cloudflare.com", // challenges.cloudflare.com 人机验证
    ];

    public static string UserDataFolder =>
        JableLens.Core.Storage.AppPaths.Combine("WebView2");

    public static Task<CoreWebView2Environment> EnvironmentAsync() =>
        _env ??= CoreWebView2Environment.CreateAsync(null, UserDataFolder,
            new CoreWebView2EnvironmentOptions("--autoplay-policy=no-user-gesture-required"));

    private static Task<CoreWebView2Environment>? _externalEnv;

    /// <summary>嵌入的外部网站用的浏览器：单独的数据目录，cookie 和看片的站点互不相通。</summary>
    public static Task<CoreWebView2Environment> ExternalEnvironmentAsync() =>
        _externalEnv ??= CoreWebView2Environment.CreateAsync(null, JableLens.Core.Storage.AppPaths.Combine("WebView2-External"),
            new CoreWebView2EnvironmentOptions("--autoplay-policy=no-user-gesture-required"));

    public static bool IsAllowedHost(string host) =>
        AllowedHosts.Any(h => host.Equals(h, StringComparison.OrdinalIgnoreCase) || host.EndsWith("." + h, StringComparison.OrdinalIgnoreCase));

    /// <param name="lightweight">只读 HTML 时不加载图片、视频、字体，快很多。</param>
    /// <param name="extraHosts">额外放行的主机（比如这次播放的视频 CDN）。</param>
    public static void Configure(CoreWebView2 core, CoreWebView2Environment env, bool lightweight, ISet<string>? extraHosts = null)
    {
        core.Settings.AreDevToolsEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.IsZoomControlEnabled = false;
        core.Settings.AreBrowserAcceleratorKeysEnabled = false;
        core.Settings.IsGeneralAutofillEnabled = false;
        core.Settings.IsPasswordAutosaveEnabled = false;
        core.NewWindowRequested += (_, e) => e.Handled = true; // 弹窗广告

        core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
        core.WebResourceRequested += (_, e) =>
        {
            if (!Uri.TryCreate(e.Request.Uri, UriKind.Absolute, out var u) || (u.Scheme != "http" && u.Scheme != "https")) return;
            var host = u.Host;
            var ok = IsAllowedHost(host) || (extraHosts?.Contains(host) ?? false);
            if (ok && lightweight && !host.EndsWith("cloudflare.com", StringComparison.OrdinalIgnoreCase)
                && e.ResourceContext is CoreWebView2WebResourceContext.Image or CoreWebView2WebResourceContext.Media or CoreWebView2WebResourceContext.Font)
                ok = false;
            if (!ok) e.Response = env.CreateWebResourceResponse(null, 403, "Blocked", "");
        };
        core.NavigationStarting += (_, e) =>
        {
            if (Uri.TryCreate(e.Uri, UriKind.Absolute, out var u) && u.Scheme.StartsWith("http") && !IsAllowedHost(u.Host))
                e.Cancel = true;
        };
    }
}
