using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using JableLens.Core.Http;
using JableLens.Core.Library;
using JableLens.Core.Models;
using JableLens.Core.Sources.Hls;

namespace JableLens.Core.Sites;

/// <summary>
/// 在浏览器里取视频数据（视频 CDN 有 Cloudflare，普通 HTTP 请求会被 403）：
/// 浏览器停在作品页同一个网址上（同源），页面里 fetch 播放列表 / 分片，再把字节交回程序。
/// Windows 用 WebView2，安卓用系统 WebView。
/// </summary>
public interface IStreamFetcher
{
    /// <summary>让浏览器停在 <paramref name="pageUrl"/> 所在的站点上，并放行视频地址的主机。</summary>
    Task PrepareAsync(string pageUrl, string streamUrl, CancellationToken ct);

    /// <summary>放行又一个视频主机（主列表里的子列表可能在别的主机）。</summary>
    void AllowHost(string url);

    Task<byte[]> FetchAsync(string url, CancellationToken ct);
}

/// <summary>
/// HLS 下载：读播放列表（主列表取最高清晰度）→ 逐个下载分片（标准 AES-128 的就解密）→ 按顺序合并成一个文件。
/// 分片先存在下载目录的 .parts/任务号 里，中断（退出软件、暂停、断网）后再开始会跳过已下好的。
/// 一次只跑一个任务，任务内 6 个分片并行。
/// </summary>
public sealed class DownloadEngine(UserLibrary lib, SiteClient sites, IStreamFetcher fetcher, Func<string> folder)
{
    private const int Parallel = 6;

    private bool _pumping;
    private CancellationTokenSource? _cts;
    private DownloadRecord? _current;
    private DateTime _lastSave;

    /// <summary>任务状态或进度变了（进度大约每秒一次）。在调用 Start / Enqueue 的线程上下文里触发。</summary>
    public event Action? Changed;

    /// <summary>合并完成后的处理（安卓：放进系统「下载」目录），返回最终的文件位置。</summary>
    public Func<string, CancellationToken, Task<string>>? Publish { get; set; }

    public string Folder => folder();

    public IReadOnlyList<DownloadRecord> All => lib.Read(d => d.Downloads.ToList());

    public int ActiveCount => lib.Read(d => d.Downloads.Count(r => r.State is DownloadState.Queued or DownloadState.Running));

    public DownloadRecord? Find(VideoCard card) =>
        lib.Read(d => d.Downloads.FirstOrDefault(r => r.Card.PageUrl == card.PageUrl && r.State != DownloadState.Failed));

    /// <summary>启动时：上次没下完的接着下。</summary>
    public void Start()
    {
        lib.Mutate(d =>
        {
            foreach (var r in d.Downloads.Where(r => r.State == DownloadState.Running)) r.State = DownloadState.Queued;
        });
        Pump();
    }

    public void Enqueue(VideoCard card)
    {
        lib.Mutate(d =>
        {
            d.Downloads.RemoveAll(r => r.Card.PageUrl == card.PageUrl && r.State == DownloadState.Failed);
            if (d.Downloads.Any(r => r.Card.PageUrl == card.PageUrl)) return;
            d.Downloads.Insert(0, new DownloadRecord { Card = card, State = DownloadState.Queued });
        });
        Changed?.Invoke();
        Pump();
    }

    public void Pause(string id)
    {
        Update(id, r => { if (r.State is DownloadState.Queued or DownloadState.Running) r.State = DownloadState.Paused; });
        if (_current?.Id == id) _cts?.Cancel();
    }

    public void Resume(string id)
    {
        Update(id, r => { if (r.State is DownloadState.Paused or DownloadState.Failed) { r.State = DownloadState.Queued; r.Error = null; } });
        Pump();
    }

    /// <summary>删除任务（和没下完的分片）；deleteFile 时连下好的文件一起删。</summary>
    public void Remove(string id, bool deleteFile)
    {
        if (_current?.Id == id) _cts?.Cancel();
        DownloadRecord? rec = null;
        lib.Mutate(d =>
        {
            rec = d.Downloads.FirstOrDefault(r => r.Id == id);
            if (rec is not null) d.Downloads.Remove(rec);
        });
        if (rec is null) return;
        TryDelete(PartsDir(rec), dir: true);
        if (deleteFile && rec.FilePath is not null) TryDelete(rec.FilePath, dir: false);
        Changed?.Invoke();
    }

    private static void TryDelete(string path, bool dir)
    {
        try
        {
            if (dir && Directory.Exists(path)) Directory.Delete(path, true);
            else if (!dir && File.Exists(path)) File.Delete(path);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private void Update(string id, Action<DownloadRecord> change)
    {
        lib.Mutate(d => { if (d.Downloads.FirstOrDefault(r => r.Id == id) is { } r) change(r); });
        Changed?.Invoke();
    }

    private string PartsDir(DownloadRecord r) => Path.Combine(Folder, ".parts", r.Id);

    private async void Pump()
    {
        if (_pumping) return;
        _pumping = true;
        try
        {
            while (lib.Read(d => d.Downloads.LastOrDefault(r => r.State == DownloadState.Queued)) is { } next)
                await RunAsync(next);
        }
        finally
        {
            _pumping = false;
        }
    }

    private async Task RunAsync(DownloadRecord rec)
    {
        _current = rec;
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        Update(rec.Id, r => { r.State = DownloadState.Running; r.Error = null; });
        try
        {
            var url = await sites.StreamUrlAsync(rec.Card, ct)
                      ?? throw new SourceException("下载", "取不到播放地址（先试试能不能播放）");
            await fetcher.PrepareAsync(rec.Card.PageUrl, url, ct);

            var text = Encoding.UTF8.GetString(await fetcher.FetchAsync(url, ct));
            if (HlsPlaylist.IsMaster(text))
            {
                var best = HlsPlaylist.Best(HlsPlaylist.ParseMaster(text, url));
                url = best.Url;
                fetcher.AllowHost(url);
                text = Encoding.UTF8.GetString(await fetcher.FetchAsync(url, ct));
            }
            var media = HlsPlaylist.ParseMedia(text, url);
            if (media.Segments.Count == 0) throw new SourceException("下载", "播放列表里没有分片");

            var dir = PartsDir(rec);
            Directory.CreateDirectory(dir);
            string Part(int i) => Path.Combine(dir, $"{i:D5}.seg");
            var already = media.Segments.Count(s => File.Exists(Part(s.Index)));
            Update(rec.Id, r => { r.Total = media.Segments.Count; r.Done = already; });

            if (media.InitUrl is { } initUrl && !File.Exists(Path.Combine(dir, "init.seg")))
                await File.WriteAllBytesAsync(Path.Combine(dir, "init.seg"), await fetcher.FetchAsync(initUrl, ct), ct);

            var keys = new ConcurrentDictionary<string, Task<byte[]>>();
            var gate = new SemaphoreSlim(Parallel);
            var done = already;
            long bytes = rec.Bytes;
            var tasks = media.Segments.Where(s => !File.Exists(Part(s.Index))).Select(async seg =>
            {
                await gate.WaitAsync(ct);
                try
                {
                    var data = await FetchWithRetryAsync(seg.Url, ct);
                    if (seg.Key is { Method: "AES-128", Url: { } keyUrl })
                    {
                        var key = await keys.GetOrAdd(keyUrl, k => FetchWithRetryAsync(k, ct));
                        data = await Task.Run(() => HlsPlaylist.Decrypt(data, key, seg), ct);
                    }
                    else if (seg.Key is { } other)
                        throw new SourceException("下载", $"不支持的加密方式（{other.Method}）");
                    var tmp = Part(seg.Index) + ".tmp";
                    await File.WriteAllBytesAsync(tmp, data, ct);
                    File.Move(tmp, Part(seg.Index), true);
                    var n = Interlocked.Increment(ref done);
                    var b = Interlocked.Add(ref bytes, data.Length);
                    Progress(rec.Id, n, b);
                }
                finally
                {
                    gate.Release();
                }
            }).ToList();
            await Task.WhenAll(tasks);

            // 合并：init（fMP4 才有）+ 分片按顺序拼起来
            var output = UniquePath(Path.Combine(Folder, SafeName($"{rec.Card.Code} {Shorten(rec.Card.Title, rec.Card.Code)}") + media.Extension));
            await Task.Run(() =>
            {
                using var outStream = new FileStream(output + ".tmp", FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20);
                var init = Path.Combine(dir, "init.seg");
                if (File.Exists(init)) { using var i = File.OpenRead(init); i.CopyTo(outStream); }
                foreach (var s in media.Segments)
                {
                    ct.ThrowIfCancellationRequested();
                    using var p = File.OpenRead(Part(s.Index));
                    p.CopyTo(outStream);
                }
            }, ct);
            File.Move(output + ".tmp", output, true);
            TryDelete(dir, dir: true);
            var size = new FileInfo(output).Length;
            if (Publish is { } publish) output = await publish(output, ct);
            Update(rec.Id, r => { r.State = DownloadState.Done; r.FilePath = output; r.Done = r.Total; r.Bytes = size; });
            _ = lib.FlushAsync().ContinueWith(_ => { });
        }
        catch (OperationCanceledException)
        {
            // 暂停或删除：状态已经在 Pause / Remove 里改好了
        }
        catch (Exception e) when (e is SourceException or IOException or UnauthorizedAccessException or TimeoutException
                                      or System.Security.Cryptography.CryptographicException or JsonException or InvalidOperationException)
        {
            var msg = e is SourceException se ? se.Message.Replace("下载：", "") : e.Message;
            Update(rec.Id, r => { r.State = DownloadState.Failed; r.Error = msg; });
        }
        finally
        {
            _current = null;
            _cts = null;
        }
    }

    private void Progress(string id, int done, long bytes)
    {
        lib.Read(d =>
        {
            if (d.Downloads.FirstOrDefault(r => r.Id == id) is { } r) { r.Done = done; r.Bytes = bytes; }
            return 0;
        });
        // 进度不用每片都写盘，界面一秒刷新一次
        if (DateTime.UtcNow - _lastSave < TimeSpan.FromSeconds(1)) return;
        _lastSave = DateTime.UtcNow;
        Update(id, _ => { });
    }

    private static string Shorten(string title, string code)
    {
        var t = title.StartsWith(code, StringComparison.OrdinalIgnoreCase) ? title[code.Length..].Trim(' ', '-') : title;
        return t.Length > 60 ? t[..60] : t;
    }

    private static string SafeName(string s)
    {
        // 两个平台都不能用的字符一起去掉（安卓的文件系统同样不认 \ : * ? " < > |）
        foreach (var c in Path.GetInvalidFileNameChars().Concat("\\:*?\"<>|")) s = s.Replace(c, ' ');
        return s.Trim().TrimEnd('.');
    }

    private static string UniquePath(string path)
    {
        if (!File.Exists(path)) return path;
        var dir = Path.GetDirectoryName(path)!;
        var name = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        for (var i = 2; ; i++)
        {
            var p = Path.Combine(dir, $"{name} ({i}){ext}");
            if (!File.Exists(p)) return p;
        }
    }

    private async Task<byte[]> FetchWithRetryAsync(string url, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await fetcher.FetchAsync(url, ct);
            }
            catch (Exception e) when (attempt < 4 && e is SourceException or TimeoutException)
            {
                await Task.Delay(1000 * attempt, ct);
            }
        }
    }
}
