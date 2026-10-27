using System.Security.Cryptography;
using JableLens.Core.Sources.Hls;
using Xunit;

namespace JableLens.Core.Tests;

public class HlsTests
{
    [Fact]
    public void Master_PicksHighestResolution()
    {
        const string master = """
            #EXTM3U
            #EXT-X-STREAM-INF:BANDWIDTH=800000,RESOLUTION=842x480,CODECS="avc1.4d401f,mp4a.40.2"
            480p/video.m3u8
            #EXT-X-STREAM-INF:BANDWIDTH=3000000,RESOLUTION=1920x1080
            1080p/video.m3u8
            #EXT-X-STREAM-INF:BANDWIDTH=1400000,RESOLUTION=1280x720
            https://cdn.example.com/abc/720p/video.m3u8
            """;
        Assert.True(HlsPlaylist.IsMaster(master));
        var variants = HlsPlaylist.ParseMaster(master, "https://cdn.example.com/abc/playlist.m3u8");
        Assert.Equal(3, variants.Count);
        Assert.Equal("https://cdn.example.com/abc/480p/video.m3u8", variants[0].Url);
        Assert.Equal("https://cdn.example.com/abc/1080p/video.m3u8", HlsPlaylist.Best(variants).Url);
    }

    [Fact]
    public void Media_ParsesSegmentsKeyAndSequence()
    {
        const string media = """
            #EXTM3U
            #EXT-X-TARGETDURATION:4
            #EXT-X-MEDIA-SEQUENCE:7
            #EXT-X-KEY:METHOD=AES-128,URI="key.bin",IV=0x000102030405060708090a0b0c0d0e0f
            #EXTINF:4.000,
            seg0.ts
            #EXTINF:3.5,
            /hls/seg1.ts?t=1
            #EXT-X-KEY:METHOD=NONE
            #EXTINF:2,
            seg2.ts
            #EXT-X-ENDLIST
            """;
        Assert.False(HlsPlaylist.IsMaster(media));
        var m = HlsPlaylist.ParseMedia(media, "https://cdn.example.com/v/1/index.m3u8");
        Assert.Equal(3, m.Segments.Count);
        Assert.Equal(".ts", m.Extension);
        Assert.Equal(9.5, m.Duration, 3);
        Assert.Equal("https://cdn.example.com/v/1/seg0.ts", m.Segments[0].Url);
        Assert.Equal("https://cdn.example.com/hls/seg1.ts?t=1", m.Segments[1].Url);
        Assert.Equal(7, m.Segments[0].Sequence);
        Assert.Equal("https://cdn.example.com/v/1/key.bin", m.Segments[0].Key!.Url);
        Assert.Equal(15, m.Segments[0].Key!.Iv![15]);
        Assert.Null(m.Segments[2].Key);
    }

    [Fact]
    public void Media_Fmp4UsesInitSegment()
    {
        var m = HlsPlaylist.ParseMedia("#EXTM3U\n#EXT-X-MAP:URI=\"init.mp4\"\n#EXTINF:6,\na.m4s\n", "https://x.com/p/v.m3u8");
        Assert.Equal("https://x.com/p/init.mp4", m.InitUrl);
        Assert.Equal(".mp4", m.Extension);
    }

    [Fact]
    public void Decrypt_UsesSequenceIvWhenNoneGiven()
    {
        var key = RandomNumberGenerator.GetBytes(16);
        var plain = RandomNumberGenerator.GetBytes(1000);
        var seg = new HlsSegment(0, "https://x/s.ts", 42, 4, new HlsKey("AES-128", "https://x/k", null));
        using var aes = Aes.Create();
        aes.Key = key;
        var cipher = aes.EncryptCbc(plain, HlsPlaylist.SequenceIv(42), PaddingMode.PKCS7);
        Assert.Equal(plain, HlsPlaylist.Decrypt(cipher, key, seg));
    }
}
