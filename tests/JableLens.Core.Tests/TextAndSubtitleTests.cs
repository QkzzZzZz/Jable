using JableLens.Core.Subtitles;
using JableLens.Core.Text;

namespace JableLens.Core.Tests;

public class TextAndSubtitleTests
{
    [Theory]
    [InlineData("MFYD-202", "MFYD-202.zh-cn(by transub)", true)]
    [InlineData("mfyd202", "[中字]MFYD-202 标题", true)]
    [InlineData("MFYD-202", "MFYD-2021", false)]
    [InlineData("MFYD-202", "XMFYD-202", true)] // 前缀不判断边界：站点标题常有方括号前缀
    public void VideoCode_AppearsIn(string code, string text, bool expected) =>
        Assert.Equal(expected, VideoCode.AppearsIn(code, text));

    [Fact]
    public void VideoCode_DmmCandidates()
    {
        Assert.Equal(["mfyd00202", "mfyd202"], VideoCode.DmmContentIdCandidates("MFYD-202"));
        Assert.Equal(["pred00749", "pred749"], VideoCode.DmmContentIdCandidates("PRED-749"));
        Assert.Equal(["abc12345"], VideoCode.DmmContentIdCandidates("ABC-12345"));
        Assert.Empty(VideoCode.DmmContentIdCandidates("FC2-PPV-1234567"));
    }

    [Fact]
    public void Chinese_SimplifiedTraditional()
    {
        Assert.Equal("和香夏樹", ChineseConverter.ToTraditional("和香夏树"));
        Assert.Equal("和香夏树", ChineseConverter.ToSimplified("和香夏樹"));
        Assert.Equal(["和香夏树", "和香夏樹"], ChineseConverter.Variants("和香夏树"));
    }

    [Fact]
    public void NameText_AliasesAndSameName()
    {
        Assert.Equal(["森沢かな", "飯岡かなこ"], NameText.SplitAliases("森沢かな（飯岡かなこ）"));
        Assert.True(NameText.SameName("飯岡かなこ", "森沢かな（飯岡かなこ）"));
        Assert.True(NameText.SameName("和香夏樹", "和香夏树"));
        Assert.True(NameText.SameName("Natsuki Waka", "natsukiwaka"));
        Assert.False(NameText.SameName("和香奈月", "和香なつき"));
    }

    [Fact]
    public void NameText_Similarity()
    {
        Assert.Equal(1.0, NameText.Similarity("和香夏樹", "和香夏树"));
        Assert.Equal(0.75, NameText.Similarity("和香夏树", "和香夏月"));
        Assert.True(NameText.Similarity("和香奈月", "三好佑香") < 0.5);
    }

    private const string Srt =
        "﻿1\r\n00:00:15,715 --> 00:00:17,054\r\n<i>欢迎光临</i>\r\n\r\n" +
        "2\r\n00:00:22,136 --> 00:00:24,476\r\n{\\an8}我是今天的按摩师\r\n\r\n" +
        "3\r\n00:00:30,000 --> 00:00:32,000\r\n字幕来自 transub.cc\r\n\r\n" +
        "4\r\n00:00:24,000 --> 00:00:26,5\r\n第一行\r\n第二行\r\n";

    [Fact]
    public void Srt_ParsesCleansAndSorts()
    {
        var cues = SrtParser.Parse(Srt);
        Assert.Equal(3, cues.Count); // 广告句被丢掉
        Assert.Equal("欢迎光临", cues[0].Text);
        Assert.Equal(TimeSpan.FromMilliseconds(15_715), cues[0].Start);
        Assert.Equal("我是今天的按摩师", cues[1].Text);
        Assert.Equal("第一行\n第二行", cues[2].Text);
        Assert.Equal(TimeSpan.FromMilliseconds(26_500), cues[2].End);
    }

    [Fact]
    public void Timing_AlignFirstCueAndLookup()
    {
        var cues = SrtParser.Parse(Srt);
        var offset = SubtitleTiming.OffsetToAlignFirstCue(cues, TimeSpan.FromSeconds(60));
        Assert.Equal(TimeSpan.FromMilliseconds(60_000 - 15_715), offset);

        Assert.Equal("欢迎光临", SubtitleTiming.CueAt(cues, TimeSpan.FromSeconds(60.5), offset)?.Text);
        Assert.Null(SubtitleTiming.CueAt(cues, TimeSpan.FromSeconds(59), offset));
        Assert.Null(SubtitleTiming.CueAt(cues, TimeSpan.FromSeconds(16), offset));
        Assert.Equal("欢迎光临", SubtitleTiming.CueAt(cues, TimeSpan.FromSeconds(16), TimeSpan.Zero)?.Text);
        // 重叠时显示较晚开始的那句；前一句结束前、后一句开始前仍显示前一句
        Assert.Equal("第一行\n第二行", SubtitleTiming.CueAt(cues, TimeSpan.FromSeconds(24.2), TimeSpan.Zero)?.Text);
        Assert.Equal("我是今天的按摩师", SubtitleTiming.CueAt(cues, TimeSpan.FromSeconds(23.9), TimeSpan.Zero)?.Text);
    }
}
