using JableLens.Core.Library;
using JableLens.Core.Models;

namespace JableLens.App.Services;

/// <summary>「猜我喜欢」：转给共用的 <see cref="RecommendationService"/>（Windows / 安卓同一套算法）。</summary>
public static class Recommendations
{
    public static bool HasSeeds => AppServices.Recommend.HasSeeds;

    public static Task<IReadOnlyList<VideoCard>> GuessAsync(int take, CancellationToken ct) => AppServices.Recommend.GuessAsync(take, ct);

    public static Task FillProfilesAsync(IEnumerable<Recommender.Seed> seeds, int max, CancellationToken ct) =>
        AppServices.Recommend.FillProfilesAsync(seeds, max, ct);
}
