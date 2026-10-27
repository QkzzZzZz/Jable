namespace JableLens.Core.Models;

public enum VideoSource { Jable }

/// <summary>列表里的一张卡片。PageUrl 是播放页地址，也是唯一 id。</summary>
public sealed record VideoCard(
    VideoSource Source,
    string PageUrl,
    string Code,
    string Title,
    string? CoverUrl,
    string? PreviewUrl,
    string? Duration);

public sealed record PerformerLink(string Name, string? PageUrl);

public sealed record TagLink(string Name, string? PageUrl);

/// <summary>一组标签（如 Jable 的「衣著」「身材」）。</summary>
public sealed record TagGroup(string Name, IReadOnlyList<TagLink> Tags);

/// <summary>播放页解析出的作品详情。</summary>
public sealed record VideoDetail(
    VideoSource Source,
    string PageUrl,
    string Code,
    string Title,
    string? CoverUrl,
    DateOnly? ReleaseDate,
    IReadOnlyList<PerformerLink> Performers,
    IReadOnlyList<TagLink> Tags,
    string? StreamUrl,
    IReadOnlyList<VideoCard> Related);

/// <summary>分类 / 主题入口。</summary>
public sealed record CategoryLink(string Name, string Url, string? ImageUrl, int? VideoCount);

/// <summary>Jable 女优目录里的一项。</summary>
public sealed record SitePerformer(string Name, string PageUrl, int? VideoCount);
