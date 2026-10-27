using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using JableLens.Core.Http;
using JableLens.Core.Models;
using JableLens.Core.Text;

namespace JableLens.Core.Subtitles;

/// <summary>subtitlecat 搜索结果里的一条（一个字幕页，页里有多种语言）。</summary>
public sealed record SubtitleSearchHit(string Title, Uri PageUrl);

public sealed partial class SubtitleCatClient(HttpClient http)
{
    public const string Source = "subtitlecat";
    private static readonly Uri Base = new("https://www.subtitlecat.com/");

    [GeneratedRegex("""<a href="(subs/[^"]+\.html)"[^>]*>([^<]*)</a>""")]
    private static partial Regex SearchRow();

    [GeneratedRegex("""<img src="/assets/flags/[^"]*" alt="([^"]*)"[^>]*>.{0,200}?<span>([^<]*)</span>.{0,300}?id="download_([^"]+)"[^>]*href="([^"]+)""",
        RegexOptions.Singleline)]
    private static partial Regex LanguageRow();

    /// <summary>按番号搜字幕页；只保留标题里含该番号的。</summary>
    public async Task<IReadOnlyList<SubtitleSearchHit>> SearchAsync(string code, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, new Uri(Base, $"index.php?search={Uri.EscapeDataString(code)}"));
        return ParseSearch(await HttpClients.GetStringAsync(http, req, Source, ct), code);
    }

    /// <summary>字幕页里的所有语言轨道。</summary>
    public async Task<IReadOnlyList<SubtitleTrack>> GetTracksAsync(SubtitleSearchHit hit, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, hit.PageUrl);
        return ParseTracks(await HttpClients.GetStringAsync(http, req, Source, ct), hit.Title);
    }

    public async Task<IReadOnlyList<SubtitleCue>> DownloadAsync(SubtitleTrack track, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, track.Url);
        using var resp = await http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode) throw new SourceException(Source, $"下载字幕失败（{(int)resp.StatusCode}）");
        var bytes = await resp.Content.ReadAsByteArrayAsync(ct);
        return SrtParser.Parse(Encoding.UTF8.GetString(bytes));
    }

    public static IReadOnlyList<SubtitleSearchHit> ParseSearch(string html, string code) =>
        SearchRow().Matches(html)
            .Select(m => new SubtitleSearchHit(WebUtility.HtmlDecode(m.Groups[2].Value).Trim(), ToAbsolute(m.Groups[1].Value)))
            .Where(h => VideoCode.AppearsIn(code, h.Title))
            .DistinctBy(h => h.PageUrl)
            .ToArray();

    public static IReadOnlyList<SubtitleTrack> ParseTracks(string html, string sourceLabel) =>
        LanguageRow().Matches(html)
            .Select(m => new SubtitleTrack(
                m.Groups[3].Value,
                WebUtility.HtmlDecode(m.Groups[2].Value).Trim(),
                sourceLabel,
                ToAbsolute(WebUtility.HtmlDecode(m.Groups[4].Value))))
            .DistinctBy(t => t.LanguageCode)
            .ToArray();

    /// <summary>href 里常有空格和括号；已含 % 的视为已编码，不重复编码。</summary>
    internal static Uri ToAbsolute(string href)
    {
        if (!href.Contains('%'))
            href = string.Join('/', href.Split('/').Select(Uri.EscapeDataString))
                .Replace("(", "%28").Replace(")", "%29");
        return new Uri(Base, href);
    }
}
