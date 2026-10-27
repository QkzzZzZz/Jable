using JableLens.Core.Models;
using JableLens.Core.Text;

namespace JableLens.Core.Sites;

/// <summary>作品详情里的「类型」和「标签」。</summary>
public static class WorkInfo
{
    /// <summary>
    /// Jable 的标签区同时有类型（/categories/，如「中文字幕」「角色剧情」）和标签（/tags/），按网址分开，名字转简体。
    /// </summary>
    public static (IReadOnlyList<TagLink> Types, IReadOnlyList<TagLink> Tags) Split(VideoDetail d)
    {
        var all = d.Tags.Select(t => t with { Name = ChineseConverter.ToSimplified(t.Name) }).ToList();
        return (all.Where(IsCategory).ToList(), all.Where(t => !IsCategory(t)).ToList());
    }

    private static bool IsCategory(TagLink t) => t.PageUrl?.Contains("/categories/", StringComparison.OrdinalIgnoreCase) == true;
}
