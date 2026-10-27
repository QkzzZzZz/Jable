using System.Windows;
using System.Windows.Controls;
using JableLens.App.Controls;
using JableLens.App.Services;
using JableLens.Core.Models;
using JableLens.Core.Text;

namespace JableLens.App.Pages;

/// <summary>关注的演员 + 检查到的新作。</summary>
public sealed class FollowsPage : PageBase
{
    private readonly CardGrid _grid = new() { MinItemWidth = 180, Padding = Ui.GridPadding };
    private readonly StackPanel _header = new() { Margin = new Thickness(0, 20, 0, 0) };
    private readonly TextBlock _status = Ui.Text("", "Secondary");
    private readonly WrapPanel _people = new() { Margin = new Thickness(-Ui.CardInset, 4, 0, 0) };

    public FollowsPage()
    {
        _grid.ItemTemplate = (DataTemplate)Application.Current.Resources["PosterCardTemplate"];
        _grid.EmptyText = "暂无新作";
        _grid.ItemClicked += item =>
        {
            if (item.Payload is not CodedWork w) return;
            MarkSeen(w.Code);
            Shell.OpenCode(w.Code);
        };

        _header.Children.Add(Ui.Text("关注", "H1"));
        var row = new WrapPanel { Margin = new Thickness(0, 16, 0, 0) };
        row.Children.Add(Ui.Button("检查新作", async (s, _) =>
        {
            var b = (Button)s;
            b.IsEnabled = false;
            _status.Text = "检查中…";
            var found = Shell.Notifier is { } n ? await n.CheckNowAsync(notify: false) : [];
            _status.Text = found.Count == 0 ? $"{DateTime.Now:HH:mm} 检查完毕，没有新作" : $"{DateTime.Now:HH:mm} 发现 {found.Count} 部新作";
            b.IsEnabled = true;
            Render();
        }, "PrimaryBtn"));
        row.Children.Add(Ui.Button("全部标为已读", (_, _) =>
        {
            AppServices.Library.Mutate(d => d.Follows.ForEach(f => f.NewWorks.Clear()));
            Render();
        }));
        _status.VerticalAlignment = VerticalAlignment.Center;
        _status.Margin = new Thickness(6, 0, 0, 8);
        row.Children.Add(_status);
        _header.Children.Add(row);
        _header.Children.Add(Ui.Text("关注的演员", "H2"));
        _header.Children.Add(_people);
        _header.Children.Add(Ui.Text("新作", "H2"));
        _grid.Header = _header;
        Content = _grid;
        Loaded += (_, _) => Render();
    }

    public override string Title => "关注";

    public override void OnRevisited() => Render();

    private static void MarkSeen(string code) => AppServices.Library.Mutate(d =>
    {
        foreach (var f in d.Follows) f.NewWorks.RemoveAll(w => VideoCode.Compact(w.Code) == VideoCode.Compact(code));
    });

    private void Render()
    {
        var follows = AppServices.Library.Read(d => d.Follows.Select(f => (f.Name, f.IconUrl, New: f.NewWorks.ToList(), f.LastChecked)).ToList());
        _people.Children.Clear();
        if (follows.Count == 0)
            _people.Children.Add(new TextBlock { Text = "还没有关注演员", Foreground = Ui.Res("Fg2"), Margin = new Thickness(Ui.CardInset, 0, 0, 0) });
        var template = (DataTemplate)Application.Current.Resources["PerformerCardTemplate"];
        foreach (var f in follows.OrderByDescending(f => f.New.Count).ThenBy(f => f.Name))
        {
            var item = new CardItem
            {
                Title = f.Name,
                IconUrl = f.IconUrl,
                Subtitle = f.New.Count > 0 ? $"{f.New.Count} 部新作" : f.LastChecked is { } t ? $"{t:MM-dd HH:mm} 已查" : "还没检查",
                Payload = f.Name,
            };
            var cp = new ContentPresenter { Content = item, ContentTemplate = template, Width = 150, Cursor = System.Windows.Input.Cursors.Hand };
            var name = f.Name;
            cp.MouseLeftButtonUp += (_, e) => { e.Handled = true; Shell.OpenPerformer(name, f.IconUrl); };
            _people.Children.Add(cp);
        }
        var works = follows.SelectMany(f => f.New.Select(w => CardItem.From(w, f.Name))).ToList();
        _grid.SetItems(works);
    }
}
