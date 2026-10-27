using JableLens.Core.Models;
using JableLens.Core.Storage;

namespace JableLens.Core.Library;

public sealed class HistoryEntry
{
    public required VideoCard Card { get; set; }
    public double Position { get; set; }
    public double Duration { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>看了一点但没看完（剩余超过 3% 且至少看了 10 秒）。</summary>
    public bool IsUnfinished => Duration > 0 && Position > 10 && Position < Duration * 0.97;

    public double ProgressRatio => Duration > 0 ? Math.Clamp(Position / Duration, 0, 1) : 0;
}

public sealed class FollowedPerformer
{
    /// <summary>Netflav 上的写法（用来查作品）。</summary>
    public required string Name { get; set; }
    public string? IconUrl { get; set; }
    public HashSet<string> KnownCodes { get; set; } = [];
    /// <summary>检查出来、用户还没看过的新作。</summary>
    public List<CodedWork> NewWorks { get; set; } = [];
    public DateTimeOffset? LastChecked { get; set; }
    public DateTimeOffset FollowedAt { get; set; } = DateTimeOffset.Now;
}

public sealed class SubtitlePrefs
{
    public bool Enabled { get; set; } = true;
    public string PreferredLanguage { get; set; } = "zh-CN";
    /// <summary>按番号保存的字幕偏移（秒）。</summary>
    public Dictionary<string, double> Offsets { get; set; } = [];
}

public sealed class AppSettings
{
    public bool AgeConfirmed { get; set; }
    public double PlaybackRate { get; set; } = 1;
    /// <summary>播放器音量 0..1 和静音，下次打开沿用。</summary>
    public double Volume { get; set; } = 1;
    public bool Muted { get; set; }
    /// <summary>玻璃效果：透明度 0..100（50 = 默认），模糊半径 0..100（默认 40）。</summary>
    public int GlassTransparency { get; set; } = 50;
    public int GlassBlur { get; set; } = 40;
    /// <summary>下载保存位置；null = 「视频\Jable」。</summary>
    public string? DownloadFolder { get; set; }
    public bool CloseToTray { get; set; } = true;
    public int CheckIntervalMinutes { get; set; } = 60;
}

public enum DownloadState { Queued, Running, Paused, Done, Failed }

/// <summary>一个下载任务。分片先存在临时目录，全部到齐后合并成 FilePath，中断后可以接着下。</summary>
public sealed class DownloadRecord
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public VideoCard Card { get; set; } = null!;
    public DownloadState State { get; set; }
    public int Done { get; set; }
    public int Total { get; set; }
    public long Bytes { get; set; }
    public string? FilePath { get; set; }
    public string? Error { get; set; }
    public DateTimeOffset AddedAt { get; set; } = DateTimeOffset.Now;
}

/// <summary>一本漫画读到哪一话（只存在本机）。</summary>
public sealed record ComicProgress(int ComicId, string Title, string? CoverUrl, int ChapterId, string ChapterTitle, DateTimeOffset ReadAt);

public sealed class LibraryData
{
    public Dictionary<string, HistoryEntry> History { get; set; } = [];
    public List<VideoCard> Favorites { get; set; } = [];
    public List<FollowedPerformer> Follows { get; set; } = [];
    /// <summary>固定到首页的标签（每个在首页占一行）。</summary>
    public List<TagLink> PinnedTags { get; set; } = [];
    /// <summary>漫画阅读进度，最近读的在前。</summary>
    public List<ComicProgress> Comics { get; set; } = [];
    /// <summary>下载任务，最新的在前。</summary>
    public List<DownloadRecord> Downloads { get; set; } = [];
    /// <summary>看过 / 收藏的作品的标签和相关作品（「猜我喜欢」用），按播放页地址。</summary>
    public Dictionary<string, WorkProfile> Profiles { get; set; } = [];
    public SubtitlePrefs Subtitles { get; set; } = new();
    public AppSettings Settings { get; set; } = new();
}

/// <summary>
/// 本地数据（历史、收藏、关注、字幕偏好、设置），存在 %LOCALAPPDATA%\JableLens\library.json。
/// 所有修改在内存里完成，然后合并成一次延迟写盘，不阻塞界面。
/// </summary>
public sealed class UserLibrary
{
    private readonly JsonFileStore<LibraryData> _store;
    private readonly object _lock = new();
    private CancellationTokenSource? _pendingSave;

    private UserLibrary(JsonFileStore<LibraryData> store, LibraryData data)
    {
        _store = store;
        Data = data;
    }

    public LibraryData Data { get; }

    public event Action? Changed;

    public static async Task<UserLibrary> LoadAsync(string? path = null)
    {
        var store = new JsonFileStore<LibraryData>(path ?? Path.Combine(JsonFileStore<LibraryData>.DefaultDirectory, "library.json"));
        return new UserLibrary(store, await store.LoadAsync());
    }

    public void Mutate(Action<LibraryData> change)
    {
        lock (_lock) change(Data);
        Changed?.Invoke();
        ScheduleSave();
    }

    public T Read<T>(Func<LibraryData, T> read)
    {
        lock (_lock) return read(Data);
    }

    // ---- 历史 / 续播 ----
    public void RecordProgress(VideoCard card, double position, double duration) => Mutate(d =>
    {
        d.History[card.PageUrl] = new HistoryEntry { Card = card, Position = position, Duration = duration, UpdatedAt = DateTimeOffset.Now };
        if (d.History.Count > 500)
            foreach (var old in d.History.Values.OrderBy(h => h.UpdatedAt).Take(d.History.Count - 500).ToList())
                d.History.Remove(old.Card.PageUrl);
    });

    public HistoryEntry? GetHistory(string pageUrl) => Read(d => d.History.GetValueOrDefault(pageUrl));

    public IReadOnlyList<HistoryEntry> RecentHistory(bool unfinishedOnly = false) =>
        Read(d => d.History.Values.Where(h => !unfinishedOnly || h.IsUnfinished).OrderByDescending(h => h.UpdatedAt).ToList());

    // ---- 猜我喜欢 ----
    /// <summary>记下一部作品的标签 / 演员 / 相关作品。只给看过或收藏的作品记（光浏览不算口味）。</summary>
    public void RememberProfile(VideoDetail detail)
    {
        if (!Read(d => d.History.ContainsKey(detail.PageUrl) || d.Favorites.Any(f => f.PageUrl == detail.PageUrl))) return;
        Mutate(d => SetProfile(d, detail));
    }

    private static void SetProfile(LibraryData d, VideoDetail detail)
    {
        d.Profiles[detail.PageUrl] = new WorkProfile(detail.PageUrl, detail.Tags, detail.Performers, detail.Related.Take(24).ToList(), DateTimeOffset.Now);
        if (d.Profiles.Count > 400)
            foreach (var old in d.Profiles.Values.OrderBy(p => p.At).Take(d.Profiles.Count - 400).ToList())
                d.Profiles.Remove(old.PageUrl);
    }

    public bool HasProfile(string pageUrl) => Read(d => d.Profiles.ContainsKey(pageUrl));

    // ---- 收藏 ----
    public bool IsFavorite(string pageUrl) => Read(d => d.Favorites.Any(f => f.PageUrl == pageUrl));

    public bool ToggleFavorite(VideoCard card)
    {
        var now = false;
        Mutate(d =>
        {
            var i = d.Favorites.FindIndex(f => f.PageUrl == card.PageUrl);
            if (i >= 0) d.Favorites.RemoveAt(i);
            else { d.Favorites.Insert(0, card); now = true; }
        });
        return now;
    }

    // ---- 关注 ----
    public bool IsFollowing(string name) => Read(d => d.Follows.Any(f => f.Name == name));

    public bool ToggleFollow(string name, string? iconUrl, IEnumerable<string> currentCodes)
    {
        var now = false;
        Mutate(d =>
        {
            var i = d.Follows.FindIndex(f => f.Name == name);
            if (i >= 0) d.Follows.RemoveAt(i);
            else
            {
                d.Follows.Add(new FollowedPerformer { Name = name, IconUrl = iconUrl, KnownCodes = currentCodes.Select(Text.VideoCode.Compact).ToHashSet() });
                now = true;
            }
        });
        return now;
    }

    // ---- 固定的标签 ----
    public bool IsTagPinned(string url) => Read(d => d.PinnedTags.Any(t => t.PageUrl == url));

    public bool ToggleTagPin(TagLink tag)
    {
        var now = false;
        Mutate(d =>
        {
            var i = d.PinnedTags.FindIndex(t => t.PageUrl == tag.PageUrl);
            if (i >= 0) d.PinnedTags.RemoveAt(i);
            else { d.PinnedTags.Add(tag); now = true; }
        });
        return now;
    }

    // ---- 字幕 ----
    public double SubtitleOffset(string code) => Read(d => d.Subtitles.Offsets.GetValueOrDefault(Text.VideoCode.Compact(code)));

    public void SetSubtitleOffset(string code, double seconds) => Mutate(d =>
    {
        var key = Text.VideoCode.Compact(code);
        if (Math.Abs(seconds) < 0.05) d.Subtitles.Offsets.Remove(key);
        else d.Subtitles.Offsets[key] = seconds;
    });

    /// <summary>立即写盘（退出前调用）。</summary>
    public Task FlushAsync()
    {
        _pendingSave?.Cancel();
        LibraryData snapshot;
        lock (_lock) snapshot = Clone(Data);
        return _store.SaveAsync(snapshot);
    }

    private void ScheduleSave()
    {
        _pendingSave?.Cancel();
        var cts = _pendingSave = new CancellationTokenSource();
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(800, cts.Token);
                LibraryData snapshot;
                lock (_lock) snapshot = Clone(Data);
                await _store.SaveAsync(snapshot, cts.Token);
            }
            catch (OperationCanceledException) { }
            catch (IOException) { } // 下次修改会再写
        });
    }

    private static LibraryData Clone(LibraryData d) =>
        System.Text.Json.JsonSerializer.Deserialize<LibraryData>(System.Text.Json.JsonSerializer.Serialize(d))!;
}
