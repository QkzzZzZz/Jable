using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using JableLens.App.Controls;
using JableLens.App.Services;
using JableLens.Core.Library;

namespace JableLens.App.Pages;

/// <summary>下载：每个任务一行（封面、标题、进度条、状态），右边暂停 / 继续 / 打开 / 删除。上面是保存位置。</summary>
public sealed class DownloadsPage : PageBase
{
    private readonly StackPanel _list = new();
    private readonly TextBlock _folder = new() { Foreground = Ui.Res("Fg3"), FontSize = 13, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly Dictionary<string, (TextBlock Status, Border Fill, Grid Bar, StackPanel Buttons, DownloadState State)> _rows = [];

    public DownloadsPage()
    {
        var header = new StackPanel { Margin = new Thickness(Ui.Gutter, 20, Ui.Gutter, 18) };
        header.Children.Add(Ui.Text("下载", "H1"));
        var folderRow = new DockPanel { Margin = new Thickness(0, 14, 0, 0) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        var open = Ui.Button("打开文件夹", (_, _) => { Directory.CreateDirectory(Downloader.Folder); Open("explorer.exe", $"\"{Downloader.Folder}\""); });
        var change = Ui.Button("更改位置", (_, _) => ChangeFolder());
        buttons.Children.Add(open);
        buttons.Children.Add(change);
        DockPanel.SetDock(buttons, Dock.Right);
        folderRow.Children.Add(buttons);
        folderRow.Children.Add(_folder);
        header.Children.Add(folderRow);
        header.Children.Add(new TextBlock
        {
            Text = "下载的是 .ts（少数是 .mp4）文件，可以用系统自带的「媒体播放器」、PotPlayer、VLC 打开。",
            Foreground = Ui.Res("Fg3"), FontSize = 12.5, Margin = new Thickness(0, 4, 0, 0),
        });

        var scroll = new ScrollViewer { Content = _list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(Ui.Gutter, 0, Ui.Gutter, 40) };
        SmoothScroll.SetIsEnabled(scroll, true);
        var dock = new DockPanel();
        DockPanel.SetDock(header, Dock.Top);
        dock.Children.Add(header);
        dock.Children.Add(scroll);
        Content = dock;

        AppServices.Downloads.Changed += Refresh;
        Refresh();
    }

    public override string Title => "下载";

    public override void OnRevisited() => Refresh();

    public override void OnClosed()
    {
        base.OnClosed();
        AppServices.Downloads.Changed -= Refresh;
    }

    private static void Open(string file, string args = "") =>
        Process.Start(new ProcessStartInfo(file, args) { UseShellExecute = true });

    private void ChangeFolder()
    {
        using var dlg = new System.Windows.Forms.FolderBrowserDialog { Description = "选择下载保存位置", SelectedPath = Downloader.Folder, UseDescriptionForTitle = true };
        if (dlg.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
        AppServices.Library.Mutate(d => d.Settings.DownloadFolder = dlg.SelectedPath);
        Refresh();
    }

    /// <summary>任务增删、状态变了就重建那一行；只是进度变了就只改进度条和文字。</summary>
    private void Refresh()
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(Refresh); return; }
        _folder.Text = "保存到  " + Downloader.Folder;
        var all = AppServices.Downloads.All;
        var ids = all.Select(r => r.Id).ToList();
        if (_list.Children.Count == 0 || !ids.SequenceEqual(_rows.Keys.ToList()) || all.Any(r => _rows[r.Id].State != r.State))
        {
            _list.Children.Clear();
            _rows.Clear();
            if (all.Count == 0)
                _list.Children.Add(new TextBlock { Text = "还没有下载。在作品详情里点 ⤓，或在播放器下方点下载按钮。", Foreground = Ui.Res("Fg3"), FontSize = 14, Margin = new Thickness(0, 20, 0, 0) });
            foreach (var r in all) _list.Children.Add(Row(r));
        }
        foreach (var r in all) UpdateRow(r);
    }

    private UIElement Row(DownloadRecord r)
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 12) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(176) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var cover = new Grid { Width = 160, Height = 90, HorizontalAlignment = HorizontalAlignment.Left };
        RoundClip.SetRadius(cover, 12);
        cover.Children.Add(new Border { Background = Ui.Res("Card") });
        var img = new Image { Stretch = Stretch.UniformToFill };
        ImageLoader.SetDecodeWidth(img, 320);
        ImageLoader.SetSource(img, r.Card.CoverUrl);
        cover.Children.Add(img);
        grid.Children.Add(cover);

        var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 20, 0) };
        info.Children.Add(new TextBlock { Text = r.Card.Code, FontSize = 15, FontWeight = Weights.SemiBold });
        info.Children.Add(new TextBlock { Text = r.Card.Title, FontSize = 13, Foreground = Ui.Res("Fg2"), TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 4, 0, 10) });
        var bar = new Grid { Height = 4, Background = new SolidColorBrush(Color.FromArgb(0x33, 255, 255, 255)) };
        var fill = new Border { Background = Ui.Res("Accent"), HorizontalAlignment = HorizontalAlignment.Left, CornerRadius = new CornerRadius(2) };
        bar.Children.Add(fill);
        bar.SizeChanged += (_, _) => UpdateRow(r);
        info.Children.Add(bar);
        var status = new TextBlock { FontSize = 12.5, Foreground = Ui.Res("Fg3"), Margin = new Thickness(0, 8, 0, 0) };
        info.Children.Add(status);
        Grid.SetColumn(info, 1);
        grid.Children.Add(info);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        Button B(string text, Action click, string style = "Btn")
        {
            var b = new Button { Content = text, Style = (Style)Application.Current.Resources[style], Margin = new Thickness(8, 0, 0, 0) };
            b.Click += (_, _) => click();
            buttons.Children.Add(b);
            return b;
        }
        var id = r.Id;
        switch (r.State)
        {
            case DownloadState.Running or DownloadState.Queued:
                B("暂停", () => AppServices.Downloads.Pause(id));
                break;
            case DownloadState.Paused or DownloadState.Failed:
                B(r.State == DownloadState.Failed ? "重试" : "继续", () => AppServices.Downloads.Resume(id));
                break;
            case DownloadState.Done when r.FilePath is { } path:
                B("播放", () => { if (File.Exists(path)) Open(path); }, "PrimaryBtn");
                B("在文件夹中显示", () => Open("explorer.exe", $"/select,\"{path}\""));
                break;
        }
        B("删除", () =>
        {
            var deleteFile = false;
            if (r.State == DownloadState.Done)
            {
                var answer = MessageBox.Show("连下载好的文件一起删除吗？\n\n选「否」只从列表里移除，文件保留。", "删除下载", MessageBoxButton.YesNoCancel);
                if (answer == MessageBoxResult.Cancel) return;
                deleteFile = answer == MessageBoxResult.Yes;
            }
            AppServices.Downloads.Remove(id, deleteFile);
        });
        Grid.SetColumn(buttons, 2);
        grid.Children.Add(buttons);

        _rows[r.Id] = (status, fill, bar, buttons, r.State);
        return new Border { Background = new SolidColorBrush(Color.FromArgb(0x0D, 255, 255, 255)), CornerRadius = new CornerRadius(14), Padding = new Thickness(12), Child = grid, Margin = new Thickness(0, 0, 0, 10) };
    }

    private void UpdateRow(DownloadRecord r)
    {
        if (!_rows.TryGetValue(r.Id, out var row)) return;
        var ratio = r.Total > 0 ? Math.Clamp((double)r.Done / r.Total, 0, 1) : 0;
        row.Fill.Width = row.Bar.ActualWidth * (r.State == DownloadState.Done ? 1 : ratio);
        var size = r.Bytes >= 1L << 30 ? $"{r.Bytes / (double)(1L << 30):0.00} GB" : $"{r.Bytes / (double)(1 << 20):0} MB";
        row.Status.Text = r.State switch
        {
            DownloadState.Queued => "等待下载",
            DownloadState.Running when r.Total == 0 => "正在读取播放列表…",
            DownloadState.Running => $"下载中  {ratio:P0}  ·  {r.Done} / {r.Total} 段  ·  {size}",
            DownloadState.Paused => $"已暂停  {ratio:P0}",
            DownloadState.Done => $"已完成  ·  {size}",
            DownloadState.Failed => "失败：" + r.Error,
            _ => "",
        };
        row.Status.Foreground = r.State == DownloadState.Failed ? Ui.Res("Accent") : Ui.Res("Fg3");
    }
}
