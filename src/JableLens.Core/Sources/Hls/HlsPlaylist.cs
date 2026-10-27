using System.Globalization;
using System.Security.Cryptography;

namespace JableLens.Core.Sources.Hls;

/// <summary>主列表里的一档清晰度。</summary>
public sealed record HlsVariant(string Url, long Bandwidth, string? Resolution);

/// <summary>EXT-X-KEY。Iv 为 null 时用分片序号当 IV（HLS 规范）。</summary>
public sealed record HlsKey(string Method, string? Url, byte[]? Iv);

public sealed record HlsSegment(int Index, string Url, long Sequence, double Duration, HlsKey? Key);

/// <summary>媒体列表：分片 + 可能有的 fMP4 初始化段（EXT-X-MAP）。</summary>
public sealed record HlsMedia(IReadOnlyList<HlsSegment> Segments, string? InitUrl)
{
    public double Duration => Segments.Sum(s => s.Duration);

    /// <summary>fMP4 分片（有 EXT-X-MAP）合并出来是 mp4，否则是 MPEG-TS。</summary>
    public string Extension => InitUrl is null ? ".ts" : ".mp4";
}

/// <summary>HLS（m3u8）播放列表解析和 AES-128 解密。只处理标准 HLS；不涉及任何 DRM。</summary>
public static class HlsPlaylist
{
    public static bool IsMaster(string text) => text.Contains("#EXT-X-STREAM-INF", StringComparison.Ordinal);

    public static IReadOnlyList<HlsVariant> ParseMaster(string text, string baseUrl)
    {
        var list = new List<HlsVariant>();
        Dictionary<string, string>? pending = null;
        foreach (var raw in Lines(text))
        {
            if (raw.StartsWith("#EXT-X-STREAM-INF:", StringComparison.Ordinal)) pending = Attributes(raw["#EXT-X-STREAM-INF:".Length..]);
            else if (pending is not null && !raw.StartsWith('#'))
            {
                list.Add(new HlsVariant(Resolve(baseUrl, raw),
                    long.TryParse(pending.GetValueOrDefault("BANDWIDTH"), out var bw) ? bw : 0,
                    pending.GetValueOrDefault("RESOLUTION")));
                pending = null;
            }
        }
        return list;
    }

    /// <summary>最高清晰度：先比分辨率（像素数），再比码率。</summary>
    public static HlsVariant Best(IReadOnlyList<HlsVariant> variants) =>
        variants.OrderByDescending(v => Pixels(v.Resolution)).ThenByDescending(v => v.Bandwidth).First();

    private static long Pixels(string? res)
    {
        var p = res?.Split('x');
        return p is [var w, var h] && long.TryParse(w, out var a) && long.TryParse(h, out var b) ? a * b : 0;
    }

    public static HlsMedia ParseMedia(string text, string baseUrl)
    {
        var segments = new List<HlsSegment>();
        long seq = 0;
        HlsKey? key = null;
        string? init = null;
        double duration = 0;
        foreach (var raw in Lines(text))
        {
            if (raw.StartsWith("#EXT-X-MEDIA-SEQUENCE:", StringComparison.Ordinal))
                long.TryParse(raw["#EXT-X-MEDIA-SEQUENCE:".Length..], out seq);
            else if (raw.StartsWith("#EXT-X-KEY:", StringComparison.Ordinal))
            {
                var a = Attributes(raw["#EXT-X-KEY:".Length..]);
                var method = a.GetValueOrDefault("METHOD", "NONE");
                key = method == "NONE" ? null
                    : new HlsKey(method, a.TryGetValue("URI", out var u) ? Resolve(baseUrl, u) : null, a.TryGetValue("IV", out var iv) ? ParseHex(iv) : null);
            }
            else if (raw.StartsWith("#EXT-X-MAP:", StringComparison.Ordinal))
            {
                if (Attributes(raw["#EXT-X-MAP:".Length..]).TryGetValue("URI", out var u)) init = Resolve(baseUrl, u);
            }
            else if (raw.StartsWith("#EXTINF:", StringComparison.Ordinal))
                double.TryParse(raw["#EXTINF:".Length..].Split(',')[0], NumberStyles.Float, CultureInfo.InvariantCulture, out duration);
            else if (!raw.StartsWith('#'))
            {
                segments.Add(new HlsSegment(segments.Count, Resolve(baseUrl, raw), seq, duration, key));
                seq++;
                duration = 0;
            }
        }
        return new HlsMedia(segments, init);
    }

    /// <summary>AES-128-CBC 解密一个分片。IV 没写时是分片序号（16 字节大端）。</summary>
    public static byte[] Decrypt(byte[] data, byte[] key, HlsSegment segment)
    {
        using var aes = Aes.Create();
        aes.Key = key;
        return aes.DecryptCbc(data, segment.Key?.Iv ?? SequenceIv(segment.Sequence), PaddingMode.PKCS7);
    }

    public static byte[] SequenceIv(long sequence)
    {
        var iv = new byte[16];
        for (var i = 0; i < 8; i++) iv[15 - i] = (byte)(sequence >> (8 * i));
        return iv;
    }

    private static IEnumerable<string> Lines(string text) =>
        text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0);

    private static string Resolve(string baseUrl, string href) =>
        Uri.TryCreate(new Uri(baseUrl), href.Trim(), out var u) ? u.ToString() : href;

    private static byte[]? ParseHex(string s)
    {
        s = s.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? s[2..] : s;
        try { return Convert.FromHexString(s.PadLeft(32, '0')); }
        catch (FormatException) { return null; }
    }

    /// <summary>KEY=VALUE,KEY="带,逗号的值"。</summary>
    private static Dictionary<string, string> Attributes(string s)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var i = 0;
        while (i < s.Length)
        {
            var eq = s.IndexOf('=', i);
            if (eq < 0) break;
            var name = s[i..eq].Trim();
            string value;
            if (eq + 1 < s.Length && s[eq + 1] == '"')
            {
                var end = s.IndexOf('"', eq + 2);
                if (end < 0) end = s.Length;
                value = s[(eq + 2)..end];
                i = end + 1;
            }
            else
            {
                var end = s.IndexOf(',', eq + 1);
                if (end < 0) end = s.Length;
                value = s[(eq + 1)..end];
                i = end;
            }
            d[name] = value;
            if (i < s.Length && s[i] == ',') i++;
        }
        return d;
    }
}
