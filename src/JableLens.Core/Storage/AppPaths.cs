namespace JableLens.Core.Storage;

/// <summary>
/// 本地数据目录：默认 %LOCALAPPDATA%\JableLens。
/// 设了环境变量 JABLELENS_DATA 时改用那个目录（开发测试用，不碰正式数据）。
/// </summary>
public static class AppPaths
{
    public static string? Override => Environment.GetEnvironmentVariable("JABLELENS_DATA") is { Length: > 0 } d ? d : null;

    public static string Data => Override ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "JableLens");

    public static string Combine(params string[] parts) => Path.Combine([Data, .. parts]);
}
