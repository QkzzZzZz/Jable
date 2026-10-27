using JableLens.Core.Http;
using JableLens.Core.Sources.Av01;
using JableLens.Core.Sources.Netflav;
using JableLens.Core.Storage;
using JableLens.Core.Text;

namespace JableLens.Core.Names;

/// <summary>对应结果：Netflav 上的写法，以及是怎么对上的（方便排查）。</summary>
public sealed record NameMatch(string NetflavName, string Method);

/// <summary>
/// 把 Jable 的演员名（中文/汉字，如「和香奈月」）对应到 Netflav 的写法（日文原名，如「和香なつき」）。
/// 顺序：缓存 → Netflav 演员搜索 → AV01 番号配对 → Netflav 番号反查（相似度 ≥ 0.5）。
/// </summary>
public sealed class PerformerNameResolver(NetflavClient netflav, Av01Client av01, JsonFileStore<Dictionary<string, NameMatch>>? store = null)
{
    public const double MinSimilarity = 0.5;

    private Dictionary<string, NameMatch>? _cache;

    public static JsonFileStore<Dictionary<string, NameMatch>> DefaultStore() =>
        new(Path.Combine(JsonFileStore<Dictionary<string, NameMatch>>.DefaultDirectory, "name-map.json"));

    /// <param name="name">Jable 上的演员名。</param>
    /// <param name="code">该演员某部作品的番号（有的话命中率高很多）。</param>
    /// <param name="actorsInWork">这部作品在 Jable 上列了几位演员；只有 1 位时才允许「单人作品直接采用」。</param>
    public async Task<NameMatch?> ResolveAsync(string name, string? code = null, int actorsInWork = 1, CancellationToken ct = default)
    {
        var cache = await LoadCacheAsync(ct);
        var key = NameText.Normalize(name);
        if (cache.TryGetValue(key, out var cached)) return cached with { Method = "缓存：" + cached.Method };

        var match = await ResolveUncachedAsync(name, code, actorsInWork, ct);
        if (match is not null)
        {
            cache[key] = match;
            if (store is not null) await store.SaveAsync(cache, ct);
        }
        return match;
    }

    private async Task<NameMatch?> ResolveUncachedAsync(string name, string? code, int actorsInWork, CancellationToken ct)
    {
        var candidates = NameText.SplitAliases(name).SelectMany(ChineseConverter.Variants).Distinct().ToArray();

        foreach (var c in candidates)
        {
            var found = await TryAsync(() => netflav.SearchActressAsync(c, 1, ct));
            if (found is null) continue;
            var exact = found.Items.FirstOrDefault(p => NameText.SameName(p.Name, c) || (p.NameEn is { } en && NameText.SameName(en, c)));
            if (exact is not null) return new NameMatch(exact.Name, "Netflav 演员搜索（精确）");
            if (found.Items.Count == 1) return new NameMatch(found.Items[0].Name, "Netflav 演员搜索（唯一结果）");
        }

        if (code is null) return null;

        var av = await TryAsync(() => av01.FindByCodeAsync(code, ct));
        if (av is not null)
        {
            var hit = av.Actresses.FirstOrDefault(a => a.AllNames.Any(n => candidates.Any(c => NameText.SameName(n, c))));
            if (hit is not null) return new NameMatch(hit.Name, $"AV01 番号配对（{code}）");
            if (av.Actresses.Count == 1 && actorsInWork == 1)
                return new NameMatch(av.Actresses[0].Name, $"AV01 单人作品（{code}）");
        }

        var works = await TryAsync(() => netflav.SearchByCodeAsync(code, ct));
        var actors = works?.SelectMany(w => w.Actors).Distinct().ToArray() ?? [];
        if (actors.Length == 1 && actorsInWork == 1)
            return new NameMatch(actors[0], $"Netflav 番号反查·单人作品（{code}）");
        var best = actors
            .Select(a => (Name: a, Score: candidates.Max(c => NameText.Similarity(a, c))))
            .OrderByDescending(x => x.Score)
            .FirstOrDefault();
        if (best.Name is not null && best.Score >= MinSimilarity)
            return new NameMatch(best.Name, $"Netflav 番号反查·相似度 {best.Score:0.00}（{code}）");

        return null;
    }

    private async Task<Dictionary<string, NameMatch>> LoadCacheAsync(CancellationToken ct) =>
        _cache ??= store is null ? [] : await store.LoadAsync(ct);

    /// <summary>某个来源失败不影响后面的来源。</summary>
    private static async Task<T?> TryAsync<T>(Func<Task<T>> f) where T : class?
    {
        try { return await f(); }
        catch (SourceException) { return null; }
    }
}
