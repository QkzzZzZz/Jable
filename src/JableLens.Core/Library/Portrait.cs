using JableLens.Core.Models;
using JableLens.Core.Text;

namespace JableLens.Core.Library;

/// <summary>画像里的一项：名字、链接、加权分、出现在几部作品里。</summary>
public sealed record WeightedName(string Name, string? Url, double Weight, int Count);

/// <summary>某一周（周一开始）的观看时长。</summary>
public sealed record WeekWatch(DateOnly WeekStart, double Seconds, int Works);

/// <summary>
/// 用户画像：全部从本机数据算出来（观看记录、片单、关注、下载等）。
/// 口味按 <see cref="Recommender.Seeds"/> 的权重统计（看得越完整、越近、在片单里的越重）。
/// </summary>
public sealed record Portrait(
    IReadOnlyList<WeightedName> Types,
    IReadOnlyList<WeightedName> Tags,
    IReadOnlyList<WeightedName> Performers,
    IReadOnlyList<WeightedName> FavoriteTypes,
    int ProfiledWorks,
    double WatchedSeconds,
    int Works,
    int Finished,
    IReadOnlyList<WeekWatch> Weeks,
    int Favorites,
    int Downloads,
    long DownloadBytes,
    int Comics)
{
    public double FinishRate => Works == 0 ? 0 : (double)Finished / Works;

    public static Portrait Build(LibraryData d, DateTimeOffset now, int weeks = 12)
    {
        var seeds = Recommender.Seeds(d, now, int.MaxValue);
        var types = new Tally();
        var tags = new Tally();
        var people = new Tally();
        foreach (var s in seeds)
        {
            if (!d.Profiles.TryGetValue(s.PageUrl, out var p)) continue;
            foreach (var t in p.Tags.DistinctBy(t => t.PageUrl ?? t.Name))
                (IsType(t) ? types : tags).Add(ChineseConverter.ToSimplified(t.Name), t.PageUrl, s.Weight);
            foreach (var a in p.Performers.DistinctBy(a => a.Name))
                people.Add(a.Name, a.PageUrl, s.Weight);
        }

        // 片单里的作品：类型和标签一起统计（「收藏类型」）
        var fav = new Tally();
        foreach (var f in d.Favorites)
            if (d.Profiles.TryGetValue(f.PageUrl, out var p))
                foreach (var t in p.Tags.DistinctBy(t => t.PageUrl ?? t.Name))
                    fav.Add(ChineseConverter.ToSimplified(t.Name), t.PageUrl, 1);

        // 观看时长：每部只记了看到的位置，按位置算（重看不重复计）
        var history = d.History.Values.ToList();
        var thisWeek = WeekStart(DateOnly.FromDateTime(now.LocalDateTime));
        var weekList = Enumerable.Range(0, weeks).Select(i => thisWeek.AddDays(-7 * (weeks - 1 - i))).ToList();
        var byWeek = history.GroupBy(h => WeekStart(DateOnly.FromDateTime(h.UpdatedAt.LocalDateTime)))
            .ToDictionary(g => g.Key, g => (Seconds: g.Sum(h => h.Position), Works: g.Count()));

        return new Portrait(
            types.Top(), tags.Top(40), people.Top(), fav.Top(),
            seeds.Count(s => d.Profiles.ContainsKey(s.PageUrl)),
            history.Sum(h => h.Position),
            history.Count,
            history.Count(h => h.Duration > 0 && h.Position >= h.Duration * 0.9),
            weekList.Select(w => byWeek.TryGetValue(w, out var x) ? new WeekWatch(w, x.Seconds, x.Works) : new WeekWatch(w, 0, 0)).ToList(),
            d.Favorites.Count,
            d.Downloads.Count(r => r.State == DownloadState.Done),
            d.Downloads.Where(r => r.State == DownloadState.Done).Sum(r => r.Bytes),
            d.Comics.Count);
    }

    /// <summary>类型（Jable 的 /categories/）和标签（Jable 的 /tags/）分开。</summary>
    private static bool IsType(TagLink t) =>
        t.PageUrl is null || t.PageUrl.Contains("/categories/", StringComparison.OrdinalIgnoreCase);

    public static DateOnly WeekStart(DateOnly day) => day.AddDays(-(((int)day.DayOfWeek + 6) % 7));

    private sealed class Tally
    {
        private readonly Dictionary<string, (string Name, string? Url, double W, int N)> _items = [];

        public void Add(string name, string? url, double w)
        {
            var key = name.Trim();
            if (key.Length == 0) return;
            var (_, oldUrl, ow, on) = _items.GetValueOrDefault(key, (key, url, 0, 0));
            _items[key] = (key, oldUrl ?? url, ow + w, on + 1);
        }

        public IReadOnlyList<WeightedName> Top(int n = 12) =>
            _items.Values.OrderByDescending(x => x.W).ThenByDescending(x => x.N).Take(n).Select(x => new WeightedName(x.Name, x.Url, x.W, x.N)).ToList();
    }
}
