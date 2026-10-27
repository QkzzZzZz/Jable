using System.Globalization;
using System.Windows;
using System.Windows.Data;
using JableLens.Core.Models;

namespace JableLens.App.Controls;

/// <summary>网格里的一项（作品 / 演员 / 分类通用的显示模型）。Payload 是点击时要打开的东西。</summary>
public sealed class CardItem
{
    public required string Title { get; init; }
    public string? Subtitle { get; init; }
    /// <summary>卡片标题下面那行灰字：番号 · 时长（或上市日）。</summary>
    public string? Meta { get; init; }
    public string? CoverUrl { get; init; }
    public string? IconUrl { get; init; }
    public string? Duration { get; init; }
    public string? Badge { get; init; }
    public string? Code { get; init; }
    /// <summary>观看进度 0..1，0 不显示。</summary>
    public double Progress { get; init; }
    /// <summary>悬停预览短片（mp4）。</summary>
    public string? PreviewUrl { get; init; }
    public required object Payload { get; init; }

    /// <summary>TOP 10 名次（1 起），0 表示不是排行卡。</summary>
    public int Rank { get; init; }

    public string? RankText => Rank > 0 ? $"TOP {Rank}" : null;

    /// <summary>骨架占位卡片的封面高宽比。</summary>
    public double SkeletonRatio { get; init; } = 0.5625;

    public string Initial => Title.Length > 0 ? Title[..1] : "?";

    public static CardItem Skeleton(double ratio = 0.5625) => new() { Title = "", Payload = new object(), SkeletonRatio = ratio };

    public static CardItem From(VideoCard v, string? badge = null, double progress = 0) => new()
    {
        Title = HeroCarousel.TitleOnly(v),
        Meta = v.Duration is { Length: > 0 } d ? $"{v.Code}  ·  {d}" : v.Code,
        CoverUrl = v.CoverUrl,
        PreviewUrl = v.PreviewUrl,
        Duration = v.Duration,
        Code = v.Code,
        Badge = badge,
        Progress = progress,
        Payload = v,
    };

    public static CardItem From(CodedWork w, string? badge = null) => new()
    {
        Title = w.Title.StartsWith(w.Code, StringComparison.OrdinalIgnoreCase) ? w.Title[w.Code.Length..].TrimStart(' ', '-') : w.Title,
        Code = w.Code,
        Subtitle = w.Date?.ToString("yyyy-MM-dd"),
        Meta = w.Date is { } date ? $"{w.Code}  ·  {date:yyyy-MM-dd}" : w.Code,
        CoverUrl = w.CoverUrl,
        Badge = badge,
        Payload = w,
    };

    public static CardItem From(PerformerSummary p) => new()
    {
        Title = p.Name,
        Subtitle = $"{p.VideoCount} 部作品",
        IconUrl = p.IconUrl,
        Payload = p,
    };


    public static CardItem From(CategoryLink c) => new()
    {
        Title = c.Name,
        Subtitle = c.VideoCount is { } n ? $"{n} 部影片" : null,
        CoverUrl = c.ImageUrl,
        Payload = c,
    };
}

public static class Converters
{
    public static readonly IValueConverter NotEmptyToVisible = new Fn(v => v is string s && s.Length > 0 ? Visibility.Visible : Visibility.Collapsed);
    /// <summary>胶囊形：圆角 = 高度的一半（直接写很大的圆角，WPF 会画成椭圆）。</summary>
    public static readonly IValueConverter Pill = new Fn(v => new CornerRadius(v is double h ? h / 2 : 0));
    public static readonly IValueConverter PositiveToVisible = new Fn(v => v is double d && d > 0 ? Visibility.Visible : Visibility.Collapsed);

    private sealed class Fn(Func<object?, object> f) : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => f(value);
        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
    }
}
