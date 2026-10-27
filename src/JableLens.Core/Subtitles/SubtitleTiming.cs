using JableLens.Core.Models;

namespace JableLens.Core.Subtitles;

/// <summary>
/// 字幕对时（整体平移）。约定：显示时间 = 字幕时间 + offset，
/// 即 offset 为正表示字幕整体往后推。
/// </summary>
public static class SubtitleTiming
{
    /// <summary>「把当前位置对准第一句台词」：offset = 当前播放时间 − 第一句开始时间。</summary>
    public static TimeSpan OffsetToAlignFirstCue(IReadOnlyList<SubtitleCue> cues, TimeSpan playbackPosition) =>
        cues.Count == 0 ? TimeSpan.Zero : playbackPosition - cues[0].Start;

    /// <summary>当前播放时间应显示的那句；没有返回 null。cues 需按开始时间排序。</summary>
    public static SubtitleCue? CueAt(IReadOnlyList<SubtitleCue> cues, TimeSpan playbackPosition, TimeSpan offset)
    {
        var t = playbackPosition - offset;
        int lo = 0, hi = cues.Count - 1, found = -1;
        while (lo <= hi)
        {
            var mid = (lo + hi) / 2;
            if (cues[mid].Start <= t) { found = mid; lo = mid + 1; }
            else hi = mid - 1;
        }
        // 往回看几句，处理重叠的字幕
        for (var i = found; i >= 0 && i > found - 5; i--)
            if (cues[i].Start <= t && t < cues[i].End) return cues[i];
        return null;
    }
}
