using System.Text.Json.Nodes;
using JableLens.Core.Http;
using JableLens.Core.Text;

namespace JableLens.Core.Sources.R18;

public sealed record R18Person(string? NameKanji, string? NameKana, string? NameRomaji);

public sealed record R18Detail(
    string ContentId,
    string? TitleJa,
    string? TitleEn,
    DateOnly? ReleaseDate,
    int? RuntimeMinutes,
    string? Maker,
    string? Label,
    string? JacketFullUrl,
    string? JacketThumbUrl,
    IReadOnlyList<string> Gallery,
    IReadOnlyList<R18Person> Actresses,
    IReadOnlyList<R18Person> Directors,
    IReadOnlyList<string> Categories);

/// <summary>
/// R18.dev 公开 JSON。只用 combined=&lt;content_id&gt;，不用 dvd_id 查询（会返回别的作品）。
/// </summary>
public sealed class R18Client(HttpClient http)
{
    public const string Source = "R18.dev";

    public async Task<R18Detail?> GetByContentIdAsync(string contentId, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get,
            $"https://r18.dev/videos/vod/movies/detail/-/combined={Uri.EscapeDataString(contentId)}/json");
        try
        {
            return Parse(await HttpClients.GetStringAsync(http, req, Source, ct));
        }
        catch (SourceException e) when (e.Message.Contains("404"))
        {
            return null;
        }
    }

    /// <summary>
    /// 按番号查资料：依次试 content_id 候选，只有发行日与 <paramref name="knownReleaseDate"/> 完全一致才采用。
    /// 推不出 content_id 或日期不符都返回 null（当作没有资料）。
    /// </summary>
    public async Task<R18Detail?> LookupAsync(string code, DateOnly knownReleaseDate, CancellationToken ct = default)
    {
        foreach (var id in VideoCode.DmmContentIdCandidates(code))
        {
            var d = await GetByContentIdAsync(id, ct);
            if (d?.ReleaseDate == knownReleaseDate) return d;
        }
        return null;
    }

    public static R18Detail? Parse(string json)
    {
        JsonNode? r;
        try { r = JsonNode.Parse(json); }
        catch (System.Text.Json.JsonException e) { throw new SourceException(Source, "返回的不是 JSON", e); }
        var id = r.Str("content_id");
        if (id is null) return null;
        return new R18Detail(
            id, r.Str("title_ja"), r.Str("title_en"), r.Date("release_date"), r.Int("runtime_mins"),
            r.Str("maker_name_ja"), r.Str("label_name_ja"), r.Str("jacket_full_url"), r.Str("jacket_thumb_url"),
            (r?["gallery"] as JsonArray ?? []).Select(g => g is JsonValue v && v.TryGetValue<string>(out var s) ? s : g.Str("image_full")).OfType<string>().ToArray(),
            People(r?["actresses"]), People(r?["directors"]),
            (r?["categories"] as JsonArray ?? []).Select(c => c.Str("name_ja")).OfType<string>().ToArray());
    }

    private static IReadOnlyList<R18Person> People(JsonNode? arr) =>
        (arr as JsonArray ?? []).Select(p => new R18Person(p.Str("name_kanji"), p.Str("name_kana"), p.Str("name_romaji"))).ToArray();
}
