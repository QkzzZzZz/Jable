using System.Text.Json;

namespace JableLens.Core.Storage;

/// <summary>
/// 简单的 JSON 文件持久化，写入时先写临时文件再替换，避免写一半崩溃导致文件损坏。
/// 默认目录：%LOCALAPPDATA%\JableLens。
/// </summary>
public sealed class JsonFileStore<T>(string path) where T : new()
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly SemaphoreSlim _lock = new(1, 1);

    public static string DefaultDirectory => AppPaths.Data;

    public string FilePath { get; } = path;

    public async Task<T> LoadAsync(CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            if (!File.Exists(FilePath)) return new T();
            await using var fs = File.OpenRead(FilePath);
            return await JsonSerializer.DeserializeAsync<T>(fs, Options, ct) ?? new T();
        }
        catch (JsonException)
        {
            return new T(); // 文件损坏就当没有缓存
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task SaveAsync(T value, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            var tmp = FilePath + ".tmp";
            await using (var fs = File.Create(tmp))
                await JsonSerializer.SerializeAsync(fs, value, Options, ct);
            File.Move(tmp, FilePath, overwrite: true);
        }
        finally
        {
            _lock.Release();
        }
    }
}
