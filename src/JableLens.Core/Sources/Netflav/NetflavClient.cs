using System.Runtime.CompilerServices;
using JableLens.Core.Http;
using JableLens.Core.Models;

namespace JableLens.Core.Sources.Netflav;

public sealed class NetflavClient(HttpClient http)
{
    private const string Base = "https://netflav.com";

    /// <summary>演员搜索。只返回名字和作品数，不含身高等资料。</summary>
    public async Task<Page<PerformerSummary>> SearchActressAsync(string keyword, int page = 1, CancellationToken ct = default)
    {
        var json = await GetApiAsync($"/api98/actress/searchActress?page={page}&keyword={Uri.EscapeDataString(keyword)}", ct);
        return ToSummaryPage(json, page);
    }

    /// <summary>演员目录（可筛选）。有关键词时走演员搜索。</summary>
    public async Task<Page<PerformerSummary>> GetActressDirectoryAsync(PerformerQuery q, int page = 1, CancellationToken ct = default)
    {
        if (!string.IsNullOrWhiteSpace(q.Keyword)) return await SearchActressAsync(q.Keyword, page, ct);
        var type = q.Type == CensorType.Uncensored ? "uncensored" : "censored";
        var url = $"/api98/actress/getActress?page={page}&type={type}&age={q.Age}&height={q.Height}&breast={q.Breast}&waist=&hip=&cup={q.Cup}";
        return ToSummaryPage(await GetApiAsync(url, ct), page);
    }

    /// <summary>番号搜作品（精确到番号的才保留）。</summary>
    public async Task<IReadOnlyList<CodedWork>> SearchByCodeAsync(string code, CancellationToken ct = default)
    {
        var json = await GetApiAsync($"/api98/video/advanceSearchVideo?type=title&page=1&keyword={Uri.EscapeDataString(code)}", ct);
        var (docs, _, _) = NetflavParser.ParseApiResult(json);
        var wanted = Text.VideoCode.Compact(code);
        return docs.Select(NetflavParser.ParseWork).OfType<CodedWork>()
            .Where(w => Text.VideoCode.Compact(w.Code) == wanted).ToArray();
    }

    /// <summary>
    /// 演员作品列表 + 演员资料（同一个网页）。名字必须是 Netflav 的写法（通常是日文原名），
    /// 否则会得到空列表。
    /// </summary>
    public async Task<NetflavActressPage> GetActressPageAsync(string name, int page = 1, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, $"{Base}/all?actress={Uri.EscapeDataString(name)}&page={page}");
        var html = await HttpClients.GetStringAsync(http, req, NetflavParser.Source, ct);
        return NetflavParser.ParseActressPage(html, page);
    }

    /// <summary>逐页读取全部作品，直到某页为空（不信 total/pages）。</summary>
    public async IAsyncEnumerable<CodedWork> GetAllWorksAsync(string name, int maxPages = 50, [EnumeratorCancellation] CancellationToken ct = default)
    {
        for (var p = 1; p <= maxPages; p++)
        {
            var page = await GetActressPageAsync(name, p, ct);
            if (!page.Works.HasMore) yield break;
            foreach (var w in page.Works.Items) yield return w;
        }
    }

    private async Task<string> GetApiAsync(string pathAndQuery, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, Base + pathAndQuery);
        req.Headers.Add("client", "client");
        return await HttpClients.GetStringAsync(http, req, NetflavParser.Source, ct);
    }

    private static Page<PerformerSummary> ToSummaryPage(string json, int page)
    {
        var (docs, pages, total) = NetflavParser.ParseApiResult(json);
        return new Page<PerformerSummary>(docs.Select(NetflavParser.ParseSummary).OfType<PerformerSummary>().ToArray(), page, pages, total);
    }
}
