using JableLens.Core.Http;
using JableLens.Core.Models;
using JableLens.Core.Sources.Netflav;
using JableLens.Core.Text;

namespace JableLens.Core.Library;

public sealed record NewWorkFound(string PerformerName, CodedWork Work);

/// <summary>
/// 检查关注演员的新作：读 Netflav 演员作品列表第 1 页，和已知番号对比。
/// 关注时已把当时的作品记为已知，所以第一次检查不会把老作品当新作。
/// </summary>
public sealed class NewWorkChecker(NetflavClient netflav, UserLibrary library)
{
    public async Task<IReadOnlyList<NewWorkFound>> CheckAllAsync(CancellationToken ct = default)
    {
        var follows = library.Read(d => d.Follows.Select(f => f.Name).ToList());
        var found = new List<NewWorkFound>();
        foreach (var name in follows)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                found.AddRange(await CheckOneAsync(name, ct));
            }
            catch (SourceException)
            {
                // 某个演员读取失败不影响其它演员
            }
            await Task.Delay(TimeSpan.FromSeconds(1), ct); // 别太频繁
        }
        return found;
    }

    public async Task<IReadOnlyList<NewWorkFound>> CheckOneAsync(string name, CancellationToken ct = default)
    {
        var page = await netflav.GetActressPageAsync(name, 1, ct);
        var result = new List<NewWorkFound>();
        library.Mutate(d =>
        {
            var f = d.Follows.FirstOrDefault(x => x.Name == name);
            if (f is null) return;
            foreach (var w in page.Works.Items)
            {
                if (f.KnownCodes.Add(VideoCode.Compact(w.Code)))
                {
                    f.NewWorks.Insert(0, w);
                    result.Add(new NewWorkFound(name, w));
                }
            }
            if (f.NewWorks.Count > 50) f.NewWorks.RemoveRange(50, f.NewWorks.Count - 50);
            f.LastChecked = DateTimeOffset.Now;
        });
        return result;
    }
}
