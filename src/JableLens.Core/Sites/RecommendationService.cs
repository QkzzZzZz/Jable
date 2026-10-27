using JableLens.Core.Http;
using JableLens.Core.Library;
using JableLens.Core.Models;

namespace JableLens.Core.Sites;

/// <summary>「猜我喜欢」：口味在本机算（Recommender），这里只负责补读种子的详情和常看标签 / 演员的列表。</summary>
public sealed class RecommendationService(SiteClient sites, UserLibrary lib)
{
    /// <summary>每次最多补读几部看过但还没记下资料的作品（读详情页，和打开详情一样）。</summary>
    private const int MaxFetchProfiles = 6;

    public bool HasSeeds => lib.Read(d => d.History.Count > 0 || d.Favorites.Count > 0);

    public async Task<IReadOnlyList<VideoCard>> GuessAsync(int take, CancellationToken ct)
    {
        var seeds = lib.Read(d => Recommender.Seeds(d, DateTimeOffset.Now));
        if (seeds.Count == 0) return [];

        await FillProfilesAsync(seeds.Take(12), MaxFetchProfiles, ct);
        var taste = lib.Read(d => Recommender.BuildTaste(d, seeds));
        var lists = taste.Tags.Select(t => (t.Tag.PageUrl!, t.Weight))
            .Concat(taste.Performers.Select(p => (p.Performer.PageUrl!, p.Weight)))
            .Select(async x =>
            {
                try { return ((await sites.ListAsync(x.Item1, 1, ct)).Items, x.Weight * 0.3); }
                catch (SourceException) { return ((IReadOnlyList<VideoCard>)[], 0.0); }
            });
        var extra = await Task.WhenAll(lists);
        return lib.Read(d => Recommender.Rank(d, seeds, extra, take));
    }

    /// <summary>这些种子里还没记下资料的（最多 <paramref name="max"/> 部），读详情页补上。</summary>
    public async Task FillProfilesAsync(IEnumerable<Recommender.Seed> seeds, int max, CancellationToken ct)
    {
        var missing = seeds.Where(s => !lib.HasProfile(s.PageUrl)).Take(max).ToList();
        await Task.WhenAll(missing.Select(async s =>
        {
            try { lib.RememberProfile(await sites.DetailAsync(s.Source, s.PageUrl, ct)); }
            catch (SourceException) { } // 下架了之类，跳过
        }));
        ct.ThrowIfCancellationRequested();
    }
}
