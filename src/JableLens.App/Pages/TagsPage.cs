using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using JableLens.App.Controls;
using JableLens.App.Services;
using JableLens.Core.Http;
using JableLens.Core.Models;
using JableLens.Core.Sources.Jable;

namespace JableLens.App.Pages;

/// <summary>
/// 标签（Netflix 类别页）：大标题「标签」，旁边一个下拉按钮显示当前标签，点开是按组分列的全部标签
/// （衣着、身材……）；下面是该标签的作品。标签可以「固定到首页」，首页会多一行。
/// </summary>
public sealed class TagsPage : PageBase
{
    private readonly CardGrid _grid = new() { Padding = Ui.GridPadding };
    private readonly StackPanel _header = new() { Margin = new Thickness(0, 20, 0, 14) };
    private readonly Button _picker = new() { Style = (Style)Application.Current.Resources["TitleDropdownBtn"], Margin = new Thickness(16, 8, 0, 0), VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed };
    private readonly TextBlock _pickerText = new();
    private readonly Button _pin = new() { Style = (Style)Application.Current.Resources["Btn"], Margin = new Thickness(8, 8, 0, 0), VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed };

    private readonly TagLink? _initial;
    private IReadOnlyList<TagGroup> _groups = [];
    private TagLink? _current;

    public TagsPage(TagLink? initial = null)
    {
        _initial = initial;
        _grid.ItemTemplate = (DataTemplate)Application.Current.Resources["VideoCardTemplate"];
        _grid.EmptyText = "";
        Shell.Wire(_grid);

        var title = new WrapPanel();
        title.Children.Add(Ui.Text("标签", "H1"));
        var pickerContent = new StackPanel { Orientation = Orientation.Horizontal };
        pickerContent.Children.Add(_pickerText);
        pickerContent.Children.Add(new TextBlock { Text = "", FontFamily = (FontFamily)Application.Current.Resources["Icons"], FontSize = 10, Margin = new Thickness(10, 2, 0, 0), VerticalAlignment = VerticalAlignment.Center });
        _picker.Content = pickerContent;
        System.Windows.Automation.AutomationProperties.SetName(_picker, "选择标签");
        _picker.Click += (_, _) => OpenPicker();
        title.Children.Add(_picker);
        title.Children.Add(_pin);
        _header.Children.Add(title);
        _pin.Click += (_, _) =>
        {
            if (_current is null) return;
            AppServices.Library.ToggleTagPin(_current);
            RenderPin();
        };

        _grid.Header = _header;
        Content = _grid;
        Loaded += OnFirstLoaded;
    }

    public override string Title => _current is null ? "标签" : _current.Name;

    public override void OnRevisited() => RenderPin();

    public override void OnClosed()
    {
        base.OnClosed();
        _grid.Cancel();
    }

    private async void OnFirstLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnFirstLoaded;
        _grid.SetItems([]);
        try
        {
            _groups = await AppServices.Sites.JableTagGroupsAsync(Life.Token);
        }
        catch (OperationCanceledException) { return; }
        catch (SourceException ex)
        {
            _grid.EmptyText = "读取失败：" + ex.Message;
            _grid.SetItems([]);
            return;
        }
        if (_groups.Count == 0)
        {
            _grid.EmptyText = "没有读到标签";
            _grid.SetItems([]);
            return;
        }
        var start = _initial is null ? null : _groups.SelectMany(g => g.Tags).FirstOrDefault(t => SameTag(t, _initial));
        // 详情页点进来的标签可能不在分组里（比如很冷门的），照样能看
        Select(start ?? _initial ?? _groups[0].Tags[0]);
    }

    private static bool SameTag(TagLink a, TagLink b) =>
        string.Equals(a.PageUrl?.TrimEnd('/'), b.PageUrl?.TrimEnd('/'), StringComparison.OrdinalIgnoreCase);

    /// <summary>下拉面板：每组一列（组名灰色，标签白色），长的组自动折成两列。</summary>
    private void OpenPicker()
    {
        var popup = Ui.MenuPopup(_picker);
        var groups = new WrapPanel { MaxWidth = Math.Max(400, ActualWidth - 2 * Ui.Gutter) };
        foreach (var g in _groups)
        {
            var col = new StackPanel { Margin = new Thickness(0, 0, 18, 10) };
            col.Children.Add(new TextBlock { Text = g.Name, FontSize = 12.5, FontWeight = Weights.Bold, Foreground = Ui.Res("Fg3"), Margin = new Thickness(12, 4, 0, 4) });
            var items = new WrapPanel { Orientation = Orientation.Vertical, MaxHeight = 280 };
            foreach (var t in g.Tags)
            {
                var tag = t;
                var link = new Button
                {
                    Content = t.Name, Style = (Style)Application.Current.Resources["MenuLink"], MinWidth = 96, Margin = new Thickness(0),
                    FontWeight = _current is not null && SameTag(t, _current) ? Weights.Bold : FontWeights.Normal,
                };
                link.Click += (_, _) => { popup.IsOpen = false; Select(tag); };
                items.Children.Add(link);
            }
            col.Children.Add(items);
            groups.Children.Add(col);
        }
        popup.Child = Ui.MenuFrame(groups);
        popup.IsOpen = true;
    }

    private void Select(TagLink tag)
    {
        _current = tag;
        _pickerText.Text = tag.Name;
        _picker.Visibility = Visibility.Visible;
        _pin.Visibility = Visibility.Visible;
        RenderPin();

        var url = tag.PageUrl!;
        _grid.EmptyText = "这个标签下没有作品";
        _grid.Loader = async (p, ct) =>
        {
            var r = await AppServices.Sites.JableListAsync(JableUrls.WithPage(url, p), p, ct);
            return new PageResult<CardItem>(r.Items.Select(TabbedGridPage.VideoItem).ToList(), r.HasMore);
        };
        _grid.Reload();
    }

    private void RenderPin()
    {
        if (_current?.PageUrl is not { } url) return;
        var pinned = AppServices.Library.IsTagPinned(url);
        _pin.Content = pinned ? "   已固定到首页" : "   固定到首页";
        _pin.ToolTip = pinned ? "点一下取消固定" : "首页会多一行这个标签的作品";
    }
}
