using System.Net.Http.Json;
using System.Text.Json.Nodes;
using JableLens.Core.Http;
using JableLens.Core.Text;

namespace JableLens.Core.Sources.Av01;

/// <summary>AV01 上的演员：日文原名 + 各语言译名（cn 简体、tw/hk 繁体、en 罗马字）。</summary>
public sealed record Av01Actress(int Id, string Name, IReadOnlyDictionary<string, string> Translations)
{
    /// <summary>所有写法（原名 + 译名），用于名字对应。</summary>
    public IEnumerable<string> AllNames => Translations.Values.Prepend(Name).Distinct();
}

public sealed record Av01Video(
    int Id,
    string Code,
    string? DmmId,
    string TitleJa,
    string? TitleCn,
    string? DescriptionCn,
    DateOnly? PublishedDate,
    int? DurationSeconds,
    string? Maker,
    IReadOnlyList<Av01Actress> Actresses,
    IReadOnlyList<string> Tags);

/// <summary>
/// AV01 JSON 接口（2026-10-23 从其网页 JS 中找到并实测）：
/// POST https://www.av01.media/api/v1/videos/search?lang=cn  body {"query":..,"pagination":{"page":1,"limit":20}}
/// lang 只接受 jp, tw, hk, cn, kr, en, fr, th, vn, my, id, ph。
/// </summary>
public sealed class Av01Client(HttpClient http)
{
    public const string Source = "AV01";
    private const string Base = "https://www.av01.media/api/v1";

    public async Task<IReadOnlyList<Av01Video>> SearchAsync(string query, int page = 1, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{Base}/videos/search?lang=cn")
        {
            Content = JsonContent.Create(new { query, pagination = new { page, limit = 20 } }),
        };
        var json = await HttpClients.GetStringAsync(http, req, Source, ct);
        return ParseSearch(json);
    }

    /// <summary>按番号查作品，只返回番号完全一致的那条。</summary>
    public async Task<Av01Video?> FindByCodeAsync(string code, CancellationToken ct = default)
    {
        var wanted = VideoCode.Compact(code);
        return (await SearchAsync(code, 1, ct)).FirstOrDefault(v => VideoCode.Compact(v.Code) == wanted);
    }

    public static IReadOnlyList<Av01Video> ParseSearch(string json)
    {
        JsonNode? root;
        try { root = JsonNode.Parse(json); }
        catch (System.Text.Json.JsonException e) { throw new SourceException(Source, "接口返回的不是 JSON", e); }
        return (root?["videos"] as JsonArray ?? []).Select(ParseVideo).OfType<Av01Video>().ToArray();
    }

    private static Av01Video? ParseVideo(JsonNode? v)
    {
        var code = v.Str("dvd_id");
        if (code is null) return null;
        return new Av01Video(
            v.Int("id") ?? 0,
            code,
            v.Str("dmm_id"),
            v.Str("title") ?? code,
            v?["title_translations"].Str("cn"),
            v?["description_translations"].Str("cn") ?? v.Str("description"),
            v.Date("published_time"),
            v.Int("duration"),
            v.Str("maker"),
            (v?["actresses"] as JsonArray ?? []).Select(ParseActress).OfType<Av01Actress>().ToArray(),
            (v?["tags"] as JsonArray ?? []).Select(t => t?["name_translations"].Str("cn") ?? t.Str("name")).OfType<string>().ToArray());
    }

    private static Av01Actress? ParseActress(JsonNode? a)
    {
        var name = a.Str("name");
        if (name is null) return null;
        var tr = new Dictionary<string, string>();
        if (a?["name_translations"] is JsonObject o)
            foreach (var (lang, val) in o)
                if (val is JsonValue jv && jv.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s))
                    tr[lang] = s;
        return new Av01Actress(a.Int("id") ?? 0, name, tr);
    }
}
