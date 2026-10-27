using System.Net;

namespace JableLens.Core.Http;

/// <summary>某个数据源读取失败时抛出，Message 是可以直接给用户看的中文说明。</summary>
public sealed class SourceException(string source, string message, Exception? inner = null)
    : Exception($"{source}：{message}", inner)
{
    public string SourceName { get; } = source;
}

/// <summary>只允许访问白名单主机，防止任何数据被发到第三方。</summary>
public sealed class HostAllowListHandler(HttpMessageHandler inner) : DelegatingHandler(inner)
{
    public static readonly string[] AllowedHosts =
    [
        "netflav.com", "av01.media", "subtitlecat.com", "r18.dev",
        "dmm.co.jp", "jable.tv",
        "imgur.com", "r18.com", // Netflav 演员头像
    ];

    public static bool IsAllowed(Uri uri) =>
        AllowedHosts.Any(h => uri.Host.Equals(h, StringComparison.OrdinalIgnoreCase)
                              || uri.Host.EndsWith("." + h, StringComparison.OrdinalIgnoreCase));

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        if (request.RequestUri is null || !IsAllowed(request.RequestUri))
            throw new InvalidOperationException($"主机不在白名单内：{request.RequestUri?.Host}");
        return base.SendAsync(request, ct);
    }
}

public static class HttpClients
{
    public const string UserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0 Safari/537.36";

    public static HttpClient Create(TimeSpan? timeout = null)
    {
        var sockets = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        };
        var client = new HttpClient(new HostAllowListHandler(sockets))
        {
            Timeout = timeout ?? TimeSpan.FromSeconds(30),
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        return client;
    }

    internal static async Task<string> GetStringAsync(HttpClient http, HttpRequestMessage req, string source, CancellationToken ct)
    {
        HttpResponseMessage resp;
        try
        {
            resp = await http.SendAsync(req, ct);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            throw new SourceException(source, "连接失败或超时，请检查网络/代理", e);
        }
        using (resp)
        {
            if (resp.StatusCode == HttpStatusCode.NotFound)
                throw new SourceException(source, "找不到（404）");
            if (!resp.IsSuccessStatusCode)
                throw new SourceException(source, $"服务器返回 {(int)resp.StatusCode}");
            return await resp.Content.ReadAsStringAsync(ct);
        }
    }
}
