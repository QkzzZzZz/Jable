using System.Collections.Concurrent;
using JableLens.Core.Http;
using JableLens.Core.Models;
using JableLens.Core.Sources.Jable;
using JableLens.Core.Text;

namespace JableLens.Core.Sites;

/// <summary>
/// 站点读取：浏览器取 HTML（各站一个浏览器，可同时进行），后台线程解析。
/// HTML 在内存里缓存几分钟：返回上一页、打开刚预读过的作品都是瞬间完成。
/// </summary>
public sealed class SiteClient(IPageReader jable)
{
    private static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(5);
    private const int CacheMax = 120;

    /// <summary>耗时日志（排查卡顿用），平台层按需挂上。</summary>
    public static Action<string>? Log { get; set; }

    private readonly ConcurrentDictionary<string, (DateTime At, Task<string> Html)> _cache = new();

    private IPageReader ReaderFor(string url) => jable;

    private static string SourceFor(string url) => JableParser.Source;

    /// <summary>取 HTML（带缓存）。同一地址同时被多处请求时只读一次。</summary>
    public Task<string> HtmlAsync(string url, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        if (_cache.TryGetValue(url, out var hit) && now - hit.At < CacheFor && !hit.Html.IsFaulted && !hit.Html.IsCanceled)
            return hit.Html.WaitAsync(ct);

        // 底层读取不跟随调用方取消：别的页面可能也在等同一份结果
        var task = ReaderFor(url).FetchHtmlAsync(url, SourceFor(url));
        if (Log is { } log)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            _ = task.ContinueWith(t => log($"{sw.ElapsedMilliseconds,6} ms  {(t.IsCompletedSuccessfully ? t.Result.Length + " chars" : "FAILED")}  {url}"));
        }
        _cache[url] = (now, task);
        _ = task.ContinueWith(t => _cache.TryRemove(new KeyValuePair<string, (DateTime, Task<string>)>(url, (now, task))),
            TaskContinuationOptions.NotOnRanToCompletion);
        if (_cache.Count > CacheMax)
            foreach (var old in _cache.OrderBy(kv => kv.Value.At).Take(_cache.Count - CacheMax).ToList())
                _cache.TryRemove(old.Key, out _);
        return task.WaitAsync(ct);
    }

    /// <summary>预读（比如鼠标停在卡片上、或下一页），失败不报错。</summary>
    public void Prefetch(string url)
    {
        if (_cache.ContainsKey(url)) return;
        _ = HtmlAsync(url).ContinueWith(_ => { }, TaskContinuationOptions.OnlyOnFaulted);
    }

    /// <summary>启动时预热：两个浏览器先各自停到站点上（顺便缓存首页要用的列表），之后的读取都走 fetch。</summary>
    public void WarmUp()
    {
        Prefetch(JableUrls.Latest());
    }


    public async Task<PageResult<VideoCard>> JableListAsync(string pagedUrl, int page, CancellationToken ct = default)
    {
        var html = await HtmlAsync(pagedUrl, ct);
        return await Task.Run(() => new PageResult<VideoCard>(JableParser.ParseList(html), JableParser.HasNextPage(html, page)), ct);
    }

    /// <summary>列表解析（公开版只有 Jable）。</summary>
    public Task<PageResult<VideoCard>> ListAsync(string pagedUrl, int page, CancellationToken ct = default) => JableListAsync(pagedUrl, page, ct);

    public async Task<VideoDetail> DetailAsync(VideoSource source, string pageUrl, CancellationToken ct = default)
    {
        var html = await HtmlAsync(pageUrl, ct);
        return await Task.Run(() => JableParser.ParseVideo(html, pageUrl), ct);
    }

    /// <summary>播放地址：Jable 在页面变量 hlsUrl 里。取不到返回 null。</summary>
    public async Task<string?> StreamUrlAsync(VideoCard card, CancellationToken ct = default)
    {
        try
        {
            var html = await HtmlAsync(card.PageUrl, ct);
            return await Task.Run(() => JableParser.ParseVideo(html, card.PageUrl).StreamUrl, ct);
        }
        catch (SourceException)
        {
            return null;
        }
    }

    /// <summary>
    /// 按番号找可播放的页面：先试 Jable 的 /videos/番号/，不行就在 Jable 搜（完整版最后再试另一个来源）。
    /// 都没有返回 null。
    /// </summary>
    public async Task<VideoDetail?> FindByCodeAsync(string code, CancellationToken ct = default)
    {
        try
        {
            return await DetailAsync(VideoSource.Jable, JableUrls.Video(code), ct);
        }
        catch (SourceException) { }

        try
        {
            var hits = await JableListAsync(JableUrls.Search(code), 1, ct);
            var hit = hits.Items.FirstOrDefault(c => VideoCode.Compact(c.Code) == VideoCode.Compact(code));
            if (hit is not null) return await DetailAsync(VideoSource.Jable, hit.PageUrl, ct);
        }
        catch (SourceException) { }
        return null;
    }

    public async Task<IReadOnlyList<CategoryLink>> JableCategoriesAsync(CancellationToken ct = default)
    {
        var html = await HtmlAsync(JableUrls.Categories, ct);
        return await Task.Run(() => JableParser.ParseCategories(html), ct);
    }

    /// <summary>Jable 的标签分组（衣着、身材……），组名和标签名转成简体显示。</summary>
    public async Task<IReadOnlyList<TagGroup>> JableTagGroupsAsync(CancellationToken ct = default)
    {
        var html = await HtmlAsync(JableUrls.Categories, ct);
        return await Task.Run(() => JableParser.ParseTagGroups(html)
            .Select(g => new TagGroup(ChineseConverter.ToSimplified(g.Name),
                g.Tags.Select(t => t with { Name = ChineseConverter.ToSimplified(t.Name) }).ToArray()))
            .ToArray(), ct);
    }
}
