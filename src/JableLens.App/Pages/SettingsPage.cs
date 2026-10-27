using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using JableLens.App.Services;

namespace JableLens.App.Pages;

/// <summary>设置：每行左边是名称（固定宽度对齐），右边是选项。</summary>
public sealed class SettingsPage : PageBase
{
    private const double LabelWidth = 112;
    private static readonly int[] Intervals = [30, 60, 180, 360];
    private static readonly (string Code, string Name)[] Languages = [("zh-CN", "简体中文"), ("zh-TW", "繁体中文"), ("en", "English"), ("ja", "日本語")];

    public SettingsPage()
    {
        var s = AppServices.Library.Read(d => (d.Settings.CloseToTray, d.Settings.CheckIntervalMinutes, d.Subtitles.PreferredLanguage, d.Subtitles.Enabled));
        var root = new StackPanel { Margin = new Thickness(Ui.Gutter, 20, Ui.Gutter, 40), MaxWidth = 900, HorizontalAlignment = HorizontalAlignment.Left };
        root.Children.Add(Ui.Text("设置", "H1"));

        root.Children.Add(Ui.Text("通用", "H2"));
        root.Children.Add(Ui.Chips(["留在托盘", "退出程序"], s.CloseToTray ? 0 : 1,
            i => AppServices.Library.Mutate(d => d.Settings.CloseToTray = i == 0), "关闭窗口时", captionWidth: LabelWidth));
        root.Children.Add(Ui.Chips(Intervals.Select(m => m < 60 ? $"{m} 分钟" : $"{m / 60} 小时").ToArray(),
            Math.Max(0, Array.IndexOf(Intervals, s.CheckIntervalMinutes)),
            i => AppServices.Library.Mutate(d => d.Settings.CheckIntervalMinutes = Intervals[i]), "检查新作间隔", captionWidth: LabelWidth));

        // 玻璃效果：顶栏、菜单、悬停卡、详情弹窗一起变，拖动时立刻看到效果
        root.Children.Add(Ui.Text("外观", "H2"));
        var glass = AppServices.Library.Read(d => (d.Settings.GlassTransparency, d.Settings.GlassBlur));
        root.Children.Add(SliderRow("玻璃透明度", glass.GlassTransparency, 0, 100, v => $"{v:0}%", v =>
        {
            Controls.GlassBackdrop.Transparency = v / 100;
            AppServices.Library.Mutate(d => d.Settings.GlassTransparency = (int)Math.Round(v));
        }));
        root.Children.Add(SliderRow("模糊程度", glass.GlassBlur, 0, 100, v => v < 1 ? "不模糊" : $"{v:0}", v =>
        {
            Controls.GlassBackdrop.BlurRadius = v;
            AppServices.Library.Mutate(d => d.Settings.GlassBlur = (int)Math.Round(v));
        }));
        var reset = Ui.Button("恢复默认", (_, _) => { });
        root.Children.Add(reset);
        reset.Margin = new Thickness(LabelWidth, 4, 0, 8);
        reset.HorizontalAlignment = HorizontalAlignment.Left;
        reset.Click += (_, _) =>
        {
            Controls.GlassBackdrop.Transparency = 0.5;
            Controls.GlassBackdrop.BlurRadius = 40;
            AppServices.Library.Mutate(d => { d.Settings.GlassTransparency = 50; d.Settings.GlassBlur = 40; });
            Shell.NavigateTo("settings");
        };

        root.Children.Add(Ui.Text("字幕", "H2"));
        root.Children.Add(Ui.Chips(["显示", "不显示"], s.Enabled ? 0 : 1,
            i => AppServices.Library.Mutate(d => d.Subtitles.Enabled = i == 0), "默认", captionWidth: LabelWidth));
        root.Children.Add(Ui.Chips(Languages.Select(l => l.Name).ToArray(),
            Math.Max(0, Array.FindIndex(Languages, l => l.Code == s.PreferredLanguage)),
            i => AppServices.Library.Mutate(d => d.Subtitles.PreferredLanguage = Languages[i].Code), "优先语言", captionWidth: LabelWidth));

        root.Children.Add(Ui.Text("数据", "H2"));
        var data = JableLens.Core.Storage.AppPaths.Data;
        var row = new WrapPanel();
        row.Children.Add(Ui.Caption("本地数据", LabelWidth));
        row.Children.Add(Ui.Button("打开文件夹", (_, _) => { Directory.CreateDirectory(data); Process.Start("explorer.exe", data); }, tip: data));
        row.Children.Add(Ui.Button("清空图片缓存", (b, _) =>
        {
            try { Directory.Delete(ImageLoader.CacheFolder, recursive: true); ((Button)b).Content = "已清空"; }
            catch (IOException) { ((Button)b).Content = "部分文件占用中，重启后再试"; }
            catch (UnauthorizedAccessException) { }
        }));
        row.Children.Add(Ui.Button("退出 Jable", async (_, _) => await Shell.ExitAsync()));
        root.Children.Add(row);

        // 免责声明（和 README 的一致，另加嵌入网站一条）
        root.Children.Add(Ui.Text("免责声明", "H2"));
        string[] terms =
        [
            "仅限成年人：本软件涉及成人内容，仅供已满 18 周岁（或所在地法定成年年龄）的用户使用。所在地区禁止此类内容的，请勿使用。",
            "不提供任何内容：本软件不存储、不上传、不分发任何影片、图片或其他媒体内容，所有内容均来自第三方网站，其版权与合法性由对应网站及权利人负责。",
            "嵌入的第三方网站（如黄果短剧）由其运营方提供，本软件只做显示上的整理（隐藏广告、调整样式），不修改其内容。",
            "不绕过保护措施：本软件不破解人机验证，不伪造浏览器指纹，不绕过付费墙或 DRM。",
            "风险自负：使用本软件产生的一切后果（包括违反当地法律法规、第三方网站服务条款、版权纠纷等）由使用者自行承担，作者不承担任何责任。",
            "与相关网站无关：本软件与其中出现的任何网站均无关联，也未获其授权；相关名称和商标归各自所有者所有。",
            "侵权处理：权利人如认为本软件侵犯其合法权益，请通过 GitHub Issues 联系，核实后会及时下架相关内容。",
        ];
        for (var i = 0; i < terms.Length; i++)
            root.Children.Add(new TextBlock
            {
                Text = $"{i + 1}. {terms[i]}", TextWrapping = TextWrapping.Wrap, FontSize = 13, LineHeight = 21,
                Foreground = Ui.Res("Fg2"), Margin = new Thickness(0, 0, 0, 8),
            });
        var issues = Ui.Button("GitHub Issues", (_, _) => Process.Start(new ProcessStartInfo("https://github.com/QkzzZzZz/Jable/issues") { UseShellExecute = true }));
        issues.HorizontalAlignment = HorizontalAlignment.Left;
        issues.Margin = new Thickness(0, 4, 0, 0);
        root.Children.Add(issues);
        root.Children.Add(new TextBlock { Text = "本软件按「现状」提供，不附带任何担保。使用本软件即表示同意以上条款。", Style = (Style)Application.Current.Resources["Secondary"], TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 14, 0, 0) });

        // MiSans 许可要求在软件中注明
        root.Children.Add(new TextBlock { Text = "界面字体：MiSans（小米）", Style = (Style)Application.Current.Resources["Secondary"], Margin = new Thickness(0, 24, 0, 0) });

        var sv =new ScrollViewer { Content = root, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Controls.SmoothScroll.SetIsEnabled(sv, true);
        Content = sv;
    }

    public override string Title => "设置";

    /// <summary>「名称  ━━━●━━━  数值」一行滑块。</summary>
    private static UIElement SliderRow(string caption, double value, double min, double max, Func<double, string> format, Action<double> changed)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
        row.Children.Add(Ui.Caption(caption, LabelWidth));
        var slider = new Slider { Minimum = min, Maximum = max, Value = value, Width = 320, VerticalAlignment = VerticalAlignment.Center, IsMoveToPointEnabled = true };
        var text = new TextBlock { Text = format(value), Foreground = Ui.Res("Fg2"), FontSize = 13.5, Margin = new Thickness(14, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Width = 60 };
        // 只认用户拖动：加载 / 套模板时滑块自己调整数值不写回设置
        slider.ValueChanged += (_, e) => { if (!slider.IsLoaded) return; text.Text = format(e.NewValue); changed(e.NewValue); };
        row.Children.Add(slider);
        row.Children.Add(text);
        return row;
    }
}
