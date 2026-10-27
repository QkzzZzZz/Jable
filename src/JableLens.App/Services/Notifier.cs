using System.Windows;
using System.Windows.Threading;
using JableLens.Core.Library;
using Microsoft.Toolkit.Uwp.Notifications;
using Forms = System.Windows.Forms;

namespace JableLens.App.Services;

/// <summary>
/// 托盘图标 + 定时检查关注演员新作 + Windows 通知。
/// 点通知只把番号交给主界面（<see cref="OpenCodeRequested"/>），回调里不做重活。
/// </summary>
public sealed class Notifier : IDisposable
{
    private readonly Forms.NotifyIcon _tray;
    private readonly DispatcherTimer _timer;
    private bool _checking;

    public event Action<string>? OpenCodeRequested;
    public event Action? ShowWindowRequested;
    public event Action? ExitRequested;
    public event Action<IReadOnlyList<NewWorkFound>>? NewWorksFound;

    public Notifier()
    {
        var icon = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/app.ico"))?.Stream;
        _tray = new Forms.NotifyIcon
        {
            Icon = icon is null ? System.Drawing.SystemIcons.Application : new System.Drawing.Icon(icon),
            Text = "Jable",
            Visible = true,
            ContextMenuStrip = new Forms.ContextMenuStrip(),
        };
        _tray.ContextMenuStrip.Items.Add("打开 Jable", null, (_, _) => ShowWindowRequested?.Invoke());
        _tray.ContextMenuStrip.Items.Add("立即检查新作", null, async (_, _) => await CheckNowAsync(notify: true));
        _tray.ContextMenuStrip.Items.Add("退出", null, (_, _) => ExitRequested?.Invoke());
        _tray.DoubleClick += (_, _) => ShowWindowRequested?.Invoke();

        ToastNotificationManagerCompat.OnActivated += e =>
        {
            var args = ToastArguments.Parse(e.Argument);
            Application.Current.Dispatcher.BeginInvoke(() =>
            {
                if (args.TryGetValue("code", out var code)) OpenCodeRequested?.Invoke(code);
                else ShowWindowRequested?.Invoke();
            });
        };

        var minutes = Math.Max(15, AppServices.Library.Read(d => d.Settings.CheckIntervalMinutes));
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(minutes) };
        _timer.Tick += async (_, _) => await CheckNowAsync(notify: true);
        _timer.Start();
        // 启动 1 分钟后先查一次
        var first = new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
        first.Tick += async (_, _) => { first.Stop(); await CheckNowAsync(notify: true); };
        first.Start();
    }

    public void ShowBalloon(string title, string text) => _tray.ShowBalloonTip(3000, title, text, Forms.ToolTipIcon.Info);

    public async Task<IReadOnlyList<NewWorkFound>> CheckNowAsync(bool notify)
    {
        if (_checking) return [];
        _checking = true;
        try
        {
            var found = await AppServices.Checker.CheckAllAsync();
            if (found.Count > 0)
            {
                NewWorksFound?.Invoke(found);
                if (notify) Toast(found);
            }
            return found;
        }
        catch (OperationCanceledException)
        {
            return [];
        }
        finally
        {
            _checking = false;
        }
    }

    private static void Toast(IReadOnlyList<NewWorkFound> found)
    {
        foreach (var f in found.Take(3))
        {
            new ToastContentBuilder()
                .AddArgument("code", f.Work.Code)
                .AddText($"{f.PerformerName} 有新作")
                .AddText($"{f.Work.Code} {f.Work.Title}")
                .Show();
        }
        if (found.Count > 3)
        {
            new ToastContentBuilder()
                .AddText($"还有 {found.Count - 3} 部新作")
                .AddText("打开「关注」查看")
                .Show();
        }
    }

    public void Dispose()
    {
        _timer.Stop();
        _tray.Visible = false;
        _tray.Dispose();
    }
}
