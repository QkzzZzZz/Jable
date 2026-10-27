namespace JableLens.Core.Models;

/// <summary>按番号的作品（Netflav 演员作品列表等）。</summary>
public sealed record CodedWork(
    string Code,
    string Title,
    string? CoverUrl,
    DateOnly? Date,
    IReadOnlyList<string> Actors,
    IReadOnlyList<string> Tags);

public sealed record PerformerProfile(
    string Name,
    string? NameJa,
    string? NameZh,
    DateOnly? Birthday,
    int? Height,
    string? Cup,
    int? Bust,
    int? Waist,
    int? Hip,
    int? VideoCount)
{
    public int? AgeOn(DateOnly today)
    {
        if (Birthday is not { } b) return null;
        var age = today.Year - b.Year;
        if (today < b.AddYears(age)) age--;
        return age;
    }
}

/// <summary>演员目录 / 搜索结果里的一条（不含身高等资料）。</summary>
public sealed record PerformerSummary(string Id, string Name, string? NameEn, string? IconUrl, string? Type, int VideoCount);

public enum CensorType { Censored, Uncensored }

/// <summary>演员目录筛选。数值档位含义见 docs：age 2..6，height 1..6，breast 1..11。</summary>
public sealed record PerformerQuery(
    CensorType Type = CensorType.Censored,
    int? Age = null,
    int? Height = null,
    int? Breast = null,
    string? Cup = null,
    string? Keyword = null);

/// <summary>一页数据。HasMore 以「本页非空」为准，不信 total/pages。</summary>
public sealed record Page<T>(IReadOnlyList<T> Items, int PageNumber, int? ReportedPages, int? ReportedTotal)
{
    public bool HasMore => Items.Count > 0;
}

public sealed record SubtitleTrack(string LanguageCode, string DisplayName, string SourceLabel, Uri Url);

public sealed record SubtitleCue(TimeSpan Start, TimeSpan End, string Text);
