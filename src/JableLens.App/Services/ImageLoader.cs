using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace JableLens.App.Services;

/// <summary>
/// 图片异步加载：内存 LRU + 磁盘缓存，后台线程按显示宽度解码（省内存、不卡界面）。
/// 用法：&lt;Image svc:ImageLoader.Source="{Binding CoverUrl}" svc:ImageLoader.DecodeWidth="360"/&gt;
/// CropRight=0.475 表示只保留右侧 47.5%（DMM 宽幅封面的正面）。
/// </summary>
public static class ImageLoader
{
    public static readonly DependencyProperty SourceProperty = DependencyProperty.RegisterAttached(
        "Source", typeof(string), typeof(ImageLoader), new PropertyMetadata(null, OnSourceChanged));

    public static readonly DependencyProperty DecodeWidthProperty = DependencyProperty.RegisterAttached(
        "DecodeWidth", typeof(int), typeof(ImageLoader), new PropertyMetadata(360, OnOptionChanged));

    public static readonly DependencyProperty CropRightProperty = DependencyProperty.RegisterAttached(
        "CropRight", typeof(double), typeof(ImageLoader), new PropertyMetadata(0.0, OnOptionChanged));

    public static string? GetSource(DependencyObject o) => (string?)o.GetValue(SourceProperty);
    public static void SetSource(DependencyObject o, string? v) => o.SetValue(SourceProperty, v);
    public static int GetDecodeWidth(DependencyObject o) => (int)o.GetValue(DecodeWidthProperty);
    public static void SetDecodeWidth(DependencyObject o, int v) => o.SetValue(DecodeWidthProperty, v);
    public static double GetCropRight(DependencyObject o) => (double)o.GetValue(CropRightProperty);
    public static void SetCropRight(DependencyObject o, double v) => o.SetValue(CropRightProperty, v);

    private const long MemoryBudget = 320L * 1024 * 1024; // 按解码后的像素算，超了就丢最久没用的
    private static long _memoryUsed;
    private static readonly LinkedList<(string Key, ImageSource Image)> Lru = new();
    private static readonly Dictionary<string, LinkedListNode<(string Key, ImageSource Image)>> Index = [];
    private static readonly SemaphoreSlim Downloads = new(6, 6);
    private static readonly Dictionary<string, Task<byte[]?>> InFlight = [];

    /// <summary>这个 Image 当前应显示哪一次加载（防止旧请求后完成覆盖新结果）。</summary>
    private static readonly DependencyProperty KeyProperty = DependencyProperty.RegisterAttached(
        "Key", typeof(string), typeof(ImageLoader), new PropertyMetadata(null));

    public static HttpClient? Http { get; set; }

    public static string CacheFolder =>
        JableLens.Core.Storage.AppPaths.Combine("img");

    // 模板里绑定的 Source 可能比 CropRight / DecodeWidth 先到，参数变了就按新参数重新加载
    private static void OnOptionChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is Image img && GetSource(img) is { } url) _ = LoadAsync(img, url);
    }

    private static void OnSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not Image img) return;
        RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality); // 缩小显示时不起锯齿
        _ = LoadAsync(img, e.NewValue as string);
    }

    /// <summary>屏幕缩放倍数（150% 缩放 = 1.5）：解码宽度按实际像素算，高分屏上封面才清楚。</summary>
    private static double Scale
    {
        get
        {
            if (_scale > 0) return _scale;
            var m = Application.Current?.MainWindow is { } w ? PresentationSource.FromVisual(w)?.CompositionTarget?.TransformToDevice.M11 : null;
            if (m is null) return 1; // 主窗口还没显示：先按 1 倍，不记住
            return _scale = Math.Max(1, m.Value);
        }
    }
    private static double _scale;

    private static async Task LoadAsync(Image img, string? url)
    {
        img.SetValue(KeyProperty, null);
        if (string.IsNullOrWhiteSpace(url)) { img.Source = null; return; }

        // 卡片尺寸的图按屏幕缩放放大解码（取整到 40 的倍数，缓存好命中）；大图本来就给得够大，不再放大
        var width = GetDecodeWidth(img);
        if (width <= 800) width = (int)Math.Ceiling(width * Scale / 40) * 40;
        var crop = GetCropRight(img);
        var key = $"{url}|{width}|{crop}";
        img.SetValue(KeyProperty, key);
        // 内存里有：直接换上，不先清空（滚动复用卡片时不闪）
        if (Index.TryGetValue(key, out var node))
        {
            Lru.Remove(node);
            Lru.AddFirst(node);
            img.BeginAnimation(UIElement.OpacityProperty, null);
            img.Source = node.Value.Image;
            return;
        }
        img.Source = null;

        try
        {
            var bytes = await GetBytesAsync(url);
            if (bytes is null || (string?)img.GetValue(KeyProperty) != key) return;
            var bmp = await Task.Run(() => Decode(bytes, width, crop));
            if (bmp is null) return;
            Remember(key, bmp);
            if ((string?)img.GetValue(KeyProperty) != key) return;
            var placeholder = img.Source is not null; // 别处先放了占位图（详情大图先用卡片封面）：直接换，不淡入
            img.Source = bmp;
            if (placeholder) return;
            // 新下载 / 新解码的图淡入，不是「啪」一下冒出来
            var opacity = (double)img.GetAnimationBaseValue(UIElement.OpacityProperty); // 有的图本身是半透明的（比如模糊背景）
            img.BeginAnimation(UIElement.OpacityProperty, new System.Windows.Media.Animation.DoubleAnimation(0, opacity, TimeSpan.FromMilliseconds(180)) { FillBehavior = System.Windows.Media.Animation.FillBehavior.Stop });
        }
        catch
        {
            // 图片失败就留空，不影响页面
        }
    }

    /// <summary>
    /// 下载（走磁盘缓存）并解码，但不进内存缓存：大图又多又大（比如逐页阅读的图片），看过就该释放，由调用方自己管理。
    /// </summary>
    public static async Task<BitmapSource?> LoadUncachedAsync(string url, int width)
    {
        var bytes = await GetBytesAsync(url);
        return bytes is null ? null : await Task.Run(() => Decode(bytes, width, 0));
    }

    private static void Remember(string key, ImageSource bmp)
    {
        if (Index.ContainsKey(key)) return;
        Index[key] = Lru.AddFirst((key, bmp));
        _memoryUsed += SizeOf(bmp);
        while (_memoryUsed > MemoryBudget && Lru.Count > 1)
        {
            _memoryUsed -= SizeOf(Lru.Last!.Value.Image);
            Index.Remove(Lru.Last.Value.Key);
            Lru.RemoveLast();
        }
    }

    private static long SizeOf(ImageSource img) => img is BitmapSource b ? (long)b.PixelWidth * b.PixelHeight * 4 : 0;

    private static Task<byte[]?> GetBytesAsync(string url)
    {
        if (InFlight.TryGetValue(url, out var t)) return t;
        t = FetchAsync(url);
        InFlight[url] = t;
        _ = t.ContinueWith(_ => InFlight.Remove(url), TaskScheduler.FromCurrentSynchronizationContext());
        return t;
    }

    private static async Task<byte[]?> FetchAsync(string url)
    {
        var file = Path.Combine(CacheFolder, Hash(url));
        if (File.Exists(file))
        {
            try { return await File.ReadAllBytesAsync(file); } catch (IOException) { }
        }
        if (Http is null) return null;
        await Downloads.WaitAsync();
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            if (url.Contains("jable.tv")) req.Headers.Referrer = new Uri("https://jable.tv/");
            using var resp = await Http.SendAsync(req);
            if (!resp.IsSuccessStatusCode) return null;
            var bytes = await resp.Content.ReadAsByteArrayAsync();
            try
            {
                Directory.CreateDirectory(CacheFolder);
                await File.WriteAllBytesAsync(file, bytes);
            }
            catch (IOException) { }
            return bytes;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            return null;
        }
        finally
        {
            Downloads.Release();
        }
    }

    private static BitmapSource? Decode(byte[] bytes, int width, double cropRight)
    {
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            bmp.StreamSource = new MemoryStream(bytes);
            if (width > 0) bmp.DecodePixelWidth = cropRight > 0 ? (int)(width / cropRight) : width;
            bmp.EndInit();
            bmp.Freeze();
            if (cropRight is <= 0 or >= 1 || bmp.PixelWidth < bmp.PixelHeight) return bmp; // 竖图不用裁
            var w = (int)(bmp.PixelWidth * cropRight);
            var cropped = new CroppedBitmap(bmp, new Int32Rect(bmp.PixelWidth - w, 0, w, bmp.PixelHeight));
            cropped.Freeze();
            return cropped;
        }
        catch (Exception e) when (e is NotSupportedException or FileFormatException or ArgumentException or InvalidOperationException)
        {
            return null;
        }
    }

    private static string Hash(string s) => Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(s)));

    /// <summary>磁盘缓存超过上限时删掉最旧的文件。</summary>
    public static void TrimDiskCache(long maxBytes = 400L * 1024 * 1024)
    {
        try
        {
            var dir = new DirectoryInfo(CacheFolder);
            if (!dir.Exists) return;
            var files = dir.GetFiles().OrderByDescending(f => f.LastWriteTimeUtc).ToList();
            long total = 0;
            foreach (var f in files)
            {
                total += f.Length;
                if (total > maxBytes) f.Delete();
            }
        }
        catch (IOException) { }
    }
}
