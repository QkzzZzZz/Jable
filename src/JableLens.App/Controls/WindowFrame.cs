using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace JableLens.App.Controls;

/// <summary>
/// 窗口外框：深色标题栏 / 深色细边框（Windows 11 默认是白边），最大化时补上被系统裁掉的边距。
/// </summary>
public static class WindowFrame
{
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmwaBorderColor = 34;
    private const int DwmwaCaptionColor = 35;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetricsForDpi(int index, uint dpi);

    /// <summary>标题栏、边框都改成深色。Windows 10 上只有深色标题栏生效，其它属性会被忽略。</summary>
    public static void UseDark(Window w)
    {
        void Apply()
        {
            var hwnd = new WindowInteropHelper(w).Handle;
            Set(hwnd, DwmwaUseImmersiveDarkMode, 1);
            Set(hwnd, DwmwaWindowCornerPreference, 2); // 圆角
            Set(hwnd, DwmwaBorderColor, 0x262626);     // COLORREF 是 0x00BBGGRR
            Set(hwnd, DwmwaCaptionColor, 0x141414);
        }
        if (new WindowInteropHelper(w).Handle != IntPtr.Zero) Apply();
        else w.SourceInitialized += (_, _) => Apply();
    }

    private static void Set(IntPtr hwnd, int attr, int value) =>
        DwmSetWindowAttribute(hwnd, attr, ref value, sizeof(int));

    /// <summary>自绘标题栏的窗口最大化时，系统会把窗口往屏幕外扩出一圈边框，内容要往里缩同样的距离。</summary>
    public static Thickness MaximizedInset(Window w)
    {
        var dpi = (uint)Math.Round(96 * PresentationSource.FromVisual(w)?.CompositionTarget.TransformToDevice.M11 ?? 96);
        var px = GetSystemMetricsForDpi(32 /* SM_CXFRAME */, dpi) + GetSystemMetricsForDpi(92 /* SM_CXPADDEDBORDER */, dpi);
        var d = px * 96.0 / dpi;
        return new Thickness(d);
    }
}
