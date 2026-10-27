using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using JableLens.App.Controls;
using JableLens.App.Services;
using JableLens.Core.Models;

namespace JableLens.App.Pages;

/// <summary>演员目录（Netflav）：大标题 + 一排下拉（类型、年龄、身高、胸围、罩杯）+ 名字搜索。</summary>
public sealed class PerformerDirectoryPage : PageBase
{
    // 档位文字来自 Netflav 网页（2026-10-24 核对）
    private static readonly string[] Ages = ["不限", "超年轻", "年轻", "轻熟女", "熟女", "四十路"];          // 2..6
    private static readonly string[] Heights = ["不限", "160 以下", "161-165", "166-170", "171-175", "176-180", "180 以上"]; // 1..6
    private static readonly string[] Breasts = ["不限", "<28", "28", "30", "32", "34", "36", "38", "40", "42", "44", "44+"]; // 1..11
    private static readonly string[] Cups = ["不限", "A", "B", "C", "D", "E", "F", "G", "H", "I", "J", "K", "L", "M", "O"];

    private readonly CardGrid _grid = new() { MinItemWidth = 150, Padding = Ui.GridPadding, RowGap = new Thickness(0) };
    private readonly TextBox _keyword = new() { Width = 220, Height = 32, Padding = new Thickness(12, 0, 12, 0), FontSize = 13.5, VerticalContentAlignment = VerticalAlignment.Center };
    private PerformerQuery _query = new();

    public PerformerDirectoryPage()
    {
        _grid.ItemTemplate = (DataTemplate)Application.Current.Resources["PerformerCardTemplate"];
        _grid.EmptyText = "没有符合条件的演员";
        _grid.Loader = async (page, ct) =>
        {
            var r = await AppServices.Netflav.GetActressDirectoryAsync(_query, page, ct);
            return new PageResult<CardItem>(r.Items.Select(CardItem.From).ToList(), r.HasMore);
        };
        Shell.Wire(_grid);

        var header = new StackPanel { Margin = new Thickness(0, 20, 0, 10) };
        var title = new DockPanel();
        var search = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        System.Windows.Automation.AutomationProperties.SetName(_keyword, "演员名字");
        _keyword.KeyDown += (_, e) => { if (e.Key == Key.Enter) Apply(_query with { Keyword = _keyword.Text.Trim() }); };
        search.Children.Add(_keyword);
        var go = Ui.Button("搜索", (_, _) => Apply(_query with { Keyword = _keyword.Text.Trim() }));
        go.Margin = new Thickness(8, 0, 0, 0);
        go.Height = 32;
        go.Padding = new Thickness(16, 0, 16, 0);
        search.Children.Add(go);
        DockPanel.SetDock(search, Dock.Right);
        title.Children.Add(search);
        title.Children.Add(Ui.Text("演员", "H1"));
        header.Children.Add(title);

        var filters = new WrapPanel { Margin = new Thickness(0, 18, 0, 0) };
        filters.Children.Add(Ui.Dropdown(["有码", "无码"], 0, i => Apply(_query with { Type = i == 0 ? CensorType.Censored : CensorType.Uncensored }), "类型"));
        filters.Children.Add(Ui.Dropdown(Ages, 0, i => Apply(_query with { Age = i == 0 ? null : i + 1 }), "年龄"));
        filters.Children.Add(Ui.Dropdown(Heights, 0, i => Apply(_query with { Height = i == 0 ? null : i }), "身高"));
        filters.Children.Add(Ui.Dropdown(Breasts, 0, i => Apply(_query with { Breast = i == 0 ? null : i }), "胸围"));
        filters.Children.Add(Ui.Dropdown(Cups, 0, i => Apply(_query with { Cup = i == 0 ? null : Cups[i] }), "罩杯"));
        header.Children.Add(filters);

        _grid.Header = header;
        Content = _grid;
        Loaded += (_, _) => { if (_grid.Items.Count == 0) _grid.Reload(); };
    }

    public override string Title => "演员";

    private void Apply(PerformerQuery q)
    {
        if (q.Keyword is { Length: 0 }) q = q with { Keyword = null };
        _query = q;
        _grid.Reload();
    }

    public override void OnClosed()
    {
        base.OnClosed();
        _grid.Cancel();
    }
}
