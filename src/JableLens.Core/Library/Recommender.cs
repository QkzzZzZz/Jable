using JableLens.Core.Models;
using JableLens.Core.Text;

namespace JableLens.Core.Library;

/// <summary>看过 / 收藏的作品的标签、演员和站点给的「相关作品」，只存在本机，用来算「猜我喜欢」。</summary>
public sealed record WorkProfile(
    string PageUrl,
    IReadOnlyList<TagLink> Tags,
    IReadOnlyList<PerformerLink> Performers,
    IReadOnlyList<VideoCard> Related,
    DateTimeOffset At);

/// <summary>
/// 「猜我喜欢」全部在本机计算：
/// 观看记录（看得越多、越近权重越高）和片单是种子；种子的相关作品、常看的标签 / 演员的作品是候选；
/// 候选按命中的种子权重累加打分，去掉看过的和片单里已有的。
/// </summary>
public static class Recommender
{
    public sealed record Seed(string PageUrl, VideoSource Source, double Weight);

    public sealed record Taste(IReadOnlyList<(TagLink Tag, double Weight)> Tags, IReadOnlyList<(PerformerLink Performer, double Weight)> Performers);

    private const double HalfLifeDays = 30;

    /// <summary>种子：观看记录 + 片单，按权重从高到低。</summary>
    public static IReadOnlyList<Seed> Seeds(LibraryData d, DateTimeOffset now, int max = 40)
    {
        var seeds = new Dictionary<string, Seed>();
        foreach (var h in d.History.Values)
        {
            var days = Math.Max(0, (now - h.UpdatedAt).TotalDays);
            // 只点开没怎么看的算一点，看完的算满分；越久以前的越不算数
            var watched = h.Duration > 0 ? 0.5 + 1.5 * h.ProgressRatio : 1;
            seeds[h.Card.PageUrl] = new Seed(h.Card.PageUrl, h.Card.Source, watched * Math.Pow(0.5, days / HalfLifeDays));
        }
        foreach (var f in d.Favorites)
            seeds[f.PageUrl] = new Seed(f.PageUrl, f.Source, (seeds.GetValueOrDefault(f.PageUrl)?.Weight ?? 0) + 2);
        return seeds.Values.OrderByDescending(s => s.Weight).Take(max).ToList();
    }

    /// <summary>从种子的资料里统计常看的标签和演员。只出现在一部作品里的不算（偶然）。</summary>
    public static Taste BuildTaste(LibraryData d, IReadOnlyList<Seed> seeds, int maxTags = 3, int maxPerformers = 2)
    {
        var tags = new Dictionary<string, (TagLink Tag, double W, int N)>();
        var people = new Dictionary<string, (PerformerLink P, double W, int N)>();
        foreach (var s in seeds)
        {
            if (!d.Profiles.TryGetValue(s.PageUrl, out var p)) continue;
            foreach (var t in p.Tags.Where(t => t.PageUrl is not null).DistinctBy(t => t.PageUrl))
            {
                var (_, w, n) = tags.GetValueOrDefault(t.PageUrl!, (t, 0, 0));
                tags[t.PageUrl!] = (t, w + s.Weight, n + 1);
            }
            foreach (var a in p.Performers.Where(a => a.PageUrl is not null).DistinctBy(a => a.PageUrl))
            {
                var (_, w, n) = people.GetValueOrDefault(a.PageUrl!, (a, 0, 0));
                people[a.PageUrl!] = (a, w + s.Weight, n + 1);
            }
        }
        var enough = seeds.Count(s => d.Profiles.ContainsKey(s.PageUrl)) >= 2 ? 2 : 1;
        return new Taste(
            tags.Values.Where(x => x.N >= enough).OrderByDescending(x => x.W).Take(maxTags).Select(x => (x.Tag, x.W)).ToList(),
            people.Values.Where(x => x.N >= enough).OrderByDescending(x => x.W).Take(maxPerformers).Select(x => (x.P, x.W)).ToList());
    }

    /// <summary>
    /// 打分排序。<paramref name="extra"/> 是按标签 / 演员读来的列表（每个列表带它的权重）。
    /// 同一部作品被多处推荐时分数累加；看过的、片单里的、番号重复的去掉。
    /// </summary>
    public static IReadOnlyList<VideoCard> Rank(LibraryData d, IReadOnlyList<Seed> seeds,
        IEnumerable<(IReadOnlyList<VideoCard> Cards, double Weight)> extra, int take = 60)
    {
        var seen = d.History.Values.Select(h => VideoCode.Compact(h.Card.Code))
            .Concat(d.Favorites.Select(f => VideoCode.Compact(f.Code)))
            .Where(c => c.Length > 0).ToHashSet();
        var seenUrls = d.History.Keys.Concat(d.Favorites.Select(f => f.PageUrl)).ToHashSet();

        var score = new Dictionary<string, (VideoCard Card, double S, int Order)>();
        var order = 0;
        void Add(VideoCard c, double s)
        {
            var key = VideoCode.Compact(c.Code) is { Length: > 0 } k ? k : c.PageUrl;
            if (seenUrls.Contains(c.PageUrl) || seen.Contains(key)) return;
            score[key] = score.TryGetValue(key, out var old) ? (old.Card, old.S + s, old.Order) : (c, s, order++);
        }

        foreach (var s in seeds)
            if (d.Profiles.TryGetValue(s.PageUrl, out var p))
                for (var i = 0; i < p.Related.Count; i++)
                    Add(p.Related[i], s.Weight * (1 - 0.5 * i / Math.Max(1, p.Related.Count))); // 站点排在前面的更相关
        foreach (var (cards, w) in extra)
            for (var i = 0; i < cards.Count; i++)
                Add(cards[i], w * (1 - 0.5 * i / Math.Max(1, cards.Count)));

        return score.Values.OrderByDescending(x => x.S).ThenBy(x => x.Order).Take(take).Select(x => x.Card).ToList();
    }
}
