using System.IO;

namespace JableLens.App.Services;

/// <summary>耗时日志（排查卡顿用）。设置环境变量 JABLELENS_PERF=1 才会写 %LOCALAPPDATA%\JableLens\perf.log。</summary>
public static class Perf
{
    public static readonly bool Enabled = Environment.GetEnvironmentVariable("JABLELENS_PERF") == "1";

    private static readonly object Gate = new();

    private static string File_ => JableLens.Core.Storage.AppPaths.Combine("perf.log");

    public static void Log(string line)
    {
        if (!Enabled) return;
        lock (Gate)
        {
            try { File.AppendAllText(File_, $"{DateTime.Now:HH:mm:ss.fff}  {line}\n"); }
            catch (IOException) { }
        }
    }
}
