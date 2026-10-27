using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using JableLens.App.Services;
using Microsoft.Web.WebView2.Wpf;

namespace JableLens.App;

public partial class App : Application
{
    private Mutex? _single;

    private async void OnStartup(object sender, StartupEventArgs e)
    {
        DispatcherUnhandledException += OnUnhandled;

        // 开发用：Jable.exe --probe <url> <输出文件>，把渲染后的 HTML 存下来看页面结构
        if (e.Args.Length >= 3 && e.Args[0] == "--probe")
        {
            await ProbeAsync(e.Args[1], e.Args[2]);
            return;
        }
        // 开发用：Jable.exe --netprobe <url> <输出前缀> [脚本]，用外部网站的浏览器打开，存 HTML 和页面发出的请求地址
        if (e.Args.Length >= 3 && e.Args[0] == "--netprobe")
        {
            await NetProbeAsync(e.Args[1], e.Args[2], e.Args.Length > 3 ? e.Args[3] : null);
            return;
        }

        // 只允许运行一份：再次启动时通知已运行的那份把窗口调到前面，然后自己退出
        // 用 JABLELENS_DATA 指定了别的数据目录时算另一份（开发测试和正式使用可以同时开）
        var instance = Core.Storage.AppPaths.Override is { } dir ? "." + Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(System.Text.Encoding.UTF8.GetBytes(dir.ToLowerInvariant())))[..10] : "";
        _single = new Mutex(true, "JableLens.SingleInstance" + instance, out var first);
        var showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, "JableLens.Show" + instance);
        if (!first)
        {
            showSignal.Set();
            Shutdown();
            return;
        }

        var main = new MainWindow();
        MainWindow = main;
        ThreadPool.RegisterWaitForSingleObject(showSignal, (_, _) => Dispatcher.BeginInvoke(main.ShowFromTray), null, Timeout.Infinite, executeOnlyOnce: false);
        await AppServices.InitAsync(main.ReaderView, main.DownloadView);

        // 自用软件：不弹年龄确认
        main.Show();
        main.Start();

        // 启动参数：--open <番号> 打开作品；--page <栏目>；--performer <名字>；--play <播放页地址>
        if (e.Args.Length >= 2)
        {
            switch (e.Args[0])
            {
                case "--open": main.OpenCode(e.Args[1]); break;
                case "--page": // 直接进某个栏目（栏目标记见 MainWindow.NavigateTo，如 drama）；嵌入网站可再给一个站内路径
                    main.NavigateTo(e.Args[1]);
                    if (e.Args.Length >= 3 && main.Host.Content is Pages.ExternalSitePage site) site.StartPath = e.Args[2];
                    break;
                case "--performer": main.OpenPerformer(e.Args[1]); break;
                case "--play":
                    var src = Core.Models.VideoSource.Jable;
                    var code = new Uri(e.Args[1]).AbsolutePath.Trim('/').Split('/').Last().ToUpperInvariant();
                    JableLens.App.MainWindow.Play(new Core.Models.VideoCard(src, e.Args[1], code, code, null, null, null));
                    break;
            }
        }
    }

    private void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // 任何一处出错都不让整个程序崩掉
        e.Handled = true;
        try
        {
            var log = JableLens.Core.Storage.AppPaths.Combine("error.log");
            Directory.CreateDirectory(Path.GetDirectoryName(log)!);
            File.AppendAllText(log, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {e.Exception}\n\n");
        }
        catch (IOException) { }
        MessageBox.Show("出错了：" + e.Exception.Message, "Jable", MessageBoxButton.OK, MessageBoxImage.Error);
    }

    private async Task ProbeAsync(string url, string outFile)
    {
        var view = new WebView2();
        var win = new Window { Title = "probe", Width = 1200, Height = 800, Content = view };
        MainWindow = win;
        win.Show();
        var reader = new WebPageReader(view, "probe");
        reader.ChallengeStateChanged += (_, c) => win.Title = c ? "probe：需要人机验证" : "probe";
        try
        {
            var html = await reader.ReadHtmlAsync(url, "probe");
            File.WriteAllText(outFile, html);
        }
        catch (Exception ex)
        {
            File.WriteAllText(outFile, "ERROR: " + ex.Message);
        }
        Shutdown();
    }

    private async Task NetProbeAsync(string url, string outPrefix, string? script)
    {
        var view = new WebView2();
        var win = new Window { Title = "netprobe", Width = 1300, Height = 900, Content = view };
        MainWindow = win;
        win.Show();
        var urls = new List<string>();
        try
        {
            await view.EnsureCoreWebView2Async(await WebHost.ExternalEnvironmentAsync());
            var core = view.CoreWebView2;
            core.AddWebResourceRequestedFilter("*", Microsoft.Web.WebView2.Core.CoreWebView2WebResourceContext.All);
            core.WebResourceRequested += (_, a) => urls.Add(a.ResourceContext + " " + a.Request.Uri);
            core.Navigate(url);
            await Task.Delay(15000);
            if (script is not null)
            {
                if (script.StartsWith('@')) script = File.ReadAllText(script[1..]);
                await core.ExecuteScriptAsync(script);
                await Task.Delay(8000);
            }
            var html = System.Text.Json.JsonSerializer.Deserialize<string>(await core.ExecuteScriptAsync("document.documentElement.outerHTML"));
            File.WriteAllText(outPrefix + ".html", html);
            File.WriteAllLines(outPrefix + ".urls.txt", urls);
        }
        catch (Exception ex)
        {
            File.WriteAllText(outPrefix + ".html", "ERROR: " + ex);
        }
        Shutdown();
    }
}
