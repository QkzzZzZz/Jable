using System.Globalization;
using System.Text.RegularExpressions;
using JableLens.Core.Models;

namespace JableLens.Core.Subtitles;

public static partial class SrtParser
{
    /// <summary>含这些字样的行是广告，整行丢掉。</summary>
    public static readonly string[] AdMarkers = ["transub.cc", "subtitlecat.com"];

    [GeneratedRegex(@"(\d{1,2}):(\d{2}):(\d{2})[,.](\d{1,3})\s*-->\s*(\d{1,2}):(\d{2}):(\d{2})[,.](\d{1,3})")]
    private static partial Regex TimeLine();

    [GeneratedRegex(@"<[^>]*>|\{\\[^}]*\}")]
    private static partial Regex Tags();

    public static IReadOnlyList<SubtitleCue> Parse(string text)
    {
        text = text.TrimStart('﻿').Replace("\r\n", "\n").Replace('\r', '\n');
        var cues = new List<SubtitleCue>();
        foreach (var block in Regex.Split(text, @"\n\s*\n"))
        {
            var lines = block.Split('\n');
            var timeIdx = Array.FindIndex(lines, l => TimeLine().IsMatch(l));
            if (timeIdx < 0) continue;
            var m = TimeLine().Match(lines[timeIdx]);
            var body = lines.Skip(timeIdx + 1)
                .Select(l => Tags().Replace(l, "").Trim())
                .Where(l => l.Length > 0 && !AdMarkers.Any(a => l.Contains(a, StringComparison.OrdinalIgnoreCase)));
            var joined = string.Join("\n", body);
            if (joined.Length == 0) continue;
            var start = Time(m, 1);
            var end = Time(m, 5);
            if (end < start) end = start;
            cues.Add(new SubtitleCue(start, end, joined));
        }
        cues.Sort((a, b) => a.Start.CompareTo(b.Start));
        return cues;
    }

    private static TimeSpan Time(Match m, int g)
    {
        int P(int i) => int.Parse(m.Groups[g + i].Value, CultureInfo.InvariantCulture);
        var ms = m.Groups[g + 3].Value.PadRight(3, '0');
        return new TimeSpan(0, P(0), P(1), P(2), int.Parse(ms, CultureInfo.InvariantCulture));
    }
}
