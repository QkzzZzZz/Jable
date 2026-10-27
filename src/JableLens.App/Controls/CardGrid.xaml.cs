using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using JableLens.App.Services;
using JableLens.Core.Http;

namespace JableLens.App.Controls;

/// <summary>网格的一行：要么是表头、要么是一排卡片、要么是底部状态。</summary>
public sealed class CardRow
{
    public object? HeaderContent { get; init; }
    public IReadOnlyList<CardItem> Items { get; init; } = [];
    public int Columns { get; init; } = 1;
    public DataTemplate? Template { get; init; }
    public string? FooterText { get; init; }
    public string? RetryText { get; init; }
    public Thickness Margin { get; init; }
}

/// <summary>
/// 卡片网格：按行虚拟化，滚动到底自动加载下一页。
/// 设置 <see cref="Loader"/> 后调用 <see cref="Reload"/>；本地列表直接用 <see cref="SetItems"/>。
/// </summary>
public partial class CardGrid : UserControl
{
    private readonly ObservableCollection<CardRow> _rows = [];
    private readonly List<CardItem> _items = [];
    private CancellationTokenSource? _cts;
    private int _page;
    private bool _hasMore;
    private bool _loading;
    private string? _error;
    private int _columns = 4;
    private ScrollViewer? _scroller;
    private int _generation;

    public CardGrid()
    {
        InitializeComponent();
        List.ItemsSource = _rows;
        SizeChanged += (_, _) => Relayout(force: false);
        Loaded += (_, _) =>
        {
            if (_scroller is not null) return;
            _scroller = FindScroller();
            if (_scroller is null) return;
            _scroller.ScrollChanged += (_, _) => MaybeLoadMore();
            SmoothScroll.SetIsEnabled(_scroller, true);
        };
        PreviewMouseLeftButtonUp += OnClick;
        PreviewMouseRightButtonUp += OnRightClick;
        PreviewMouseMove += OnHover;
        _hoverTimer.Tick += (_, _) =>
        {
            _hoverTimer.Stop();
            if (_hovered?.Payload is Core.Models.VideoCard v) AppServices.Sites.Prefetch(v.PageUrl);
        };
    }

    // 鼠标在一张卡片上停 0.3 秒，就预读它的详情页：点进去时基本是瞬开
    private readonly System.Windows.Threading.DispatcherTimer _hoverTimer = new() { Interval = TimeSpan.FromMilliseconds(300) };
    private CardItem? _hovered;

    private void OnHover(object sender, MouseEventArgs e)
    {
        var item = ItemFrom(e.OriginalSource);
        if (item == _hovered) return;
        _hovered = item;
        _hoverTimer.Stop();
        if (item is not null) _hoverTimer.Start();
    }

    private Task<PageResult<CardItem>>? _prefetch;
    private int _prefetchPage;

    public DataTemplate ItemTemplate { get; set; } = (DataTemplate)Application.Current.Resources["VideoCardTemplate"];
    public double MinItemWidth { get; set; } = 230;

    /// <summary>卡片行之间的竖向间距（Netflix 网格的行距比列距大得多）。</summary>
    public Thickness RowGap { get; set; } = new(0, 0, 0, 26);
    public string EmptyText { get; set; } = "没有内容";

    /// <summary>表头（随列表一起滚动）。</summary>
    public object? Header
    {
        get => _header;
        set { _header = value; Relayout(force: true); }
    }
    private object? _header;

    public Func<int, CancellationToken, Task<PageResult<CardItem>>>? Loader { get; set; }

    public event Action<CardItem>? ItemClicked;

    public Func<CardItem, ContextMenu?>? ContextMenuFor { get; set; }

    public IReadOnlyList<CardItem> Items => _items;

    public void Reload()
    {
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        _generation++;
        _prefetch = null;
        _items.Clear();
        _page = 0;
        _hasMore = Loader is not null;
        _error = null;
        _loading = false;
        Relayout(force: true);
        _scroller?.ScrollToTop();
        _ = LoadMoreAsync();
    }

    public void SetItems(IEnumerable<CardItem> items)
    {
        _cts?.Cancel();
        Loader = null;
        _generation++;
        _items.Clear();
        _items.AddRange(items);
        Motion.AllowAppear();
        _hasMore = false;
        _loading = false;
        _error = null;
        Relayout(force: true);
    }

    public void Cancel() => _cts?.Cancel();

    private async Task LoadMoreAsync()
    {
        if (_loading || !_hasMore || Loader is null || _error is not null) return;
        _loading = true;
        var gen = _generation;
        var ct = _cts!.Token;
        Relayout(force: true);
        try
        {
            var next = _page + 1;
            var task = _prefetch is not null && _prefetchPage == next && !_prefetch.IsFaulted ? _prefetch : Loader(next, ct);
            _prefetch = null;
            var result = await task;
            if (gen != _generation) return;
            _page++;
            if (_page == 1) Motion.AllowAppear();
            var known = _items.Select(i => i.Payload).ToHashSet();
            _items.AddRange(result.Items.Where(i => !known.Contains(i.Payload)));
            _hasMore = result.HasMore && result.Items.Count > 0;
            // 趁用户还在看这一页，先把下一页读好
            if (_hasMore)
            {
                _prefetchPage = _page + 1;
                _prefetch = Loader(_prefetchPage, ct);
                _ = _prefetch.ContinueWith(_ => { }, TaskContinuationOptions.OnlyOnFaulted);
            }
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (SourceException e)
        {
            if (gen == _generation) _error = e.Message;
        }
        catch (Exception e)
        {
            if (gen == _generation) _error = "出错了：" + e.Message;
        }
        finally
        {
            if (gen == _generation) _loading = false;
        }
        Relayout(force: true);
        // 内容不够一屏时继续加载
        await Dispatcher.InvokeAsync(MaybeLoadMore, System.Windows.Threading.DispatcherPriority.Background);
    }

    private void MaybeLoadMore()
    {
        if (_scroller is null || _loading || !_hasMore || _error is not null) return;
        if (_scroller.ScrollableHeight - _scroller.VerticalOffset < 900) _ = LoadMoreAsync();
    }

    private void OnRetry(object sender, RoutedEventArgs e)
    {
        _error = null;
        if (_page == 0 && _items.Count == 0) Reload();
        else _ = LoadMoreAsync();
    }

    private void Relayout(bool force)
    {
        var width = ActualWidth > 0 ? ActualWidth - 24 : 1000;
        var cols = Math.Max(1, (int)(width / MinItemWidth));
        if (!force && cols == _columns) return;
        _columns = cols;

        var rows = new List<CardRow>();
        if (_header is not null) rows.Add(new CardRow { HeaderContent = _header, Margin = new Thickness(Pages.Ui.CardInset, 0, Pages.Ui.CardInset, 8) });
        for (var i = 0; i < _items.Count; i += cols)
            rows.Add(new CardRow { Items = _items.Skip(i).Take(cols).ToArray(), Columns = cols, Template = ItemTemplate, Margin = RowGap });

        // 第一页还没回来：画两排骨架卡片
        if (_loading && _items.Count == 0)
        {
            var ratio = ItemTemplate == Application.Current.Resources["PosterCardTemplate"] ? 1.42 : 0.5625;
            var skeleton = (DataTemplate)Application.Current.Resources["SkeletonCardTemplate"];
            for (var r = 0; r < 2; r++)
                rows.Add(new CardRow { Items = Enumerable.Range(0, cols).Select(_ => CardItem.Skeleton(ratio)).ToArray(), Columns = cols, Template = skeleton, Margin = RowGap });
        }

        string? footer = null, retry = null;
        if (_loading && _items.Count > 0) footer = "加载中…";
        else if (_error is not null) { footer = _error; retry = "重试"; }
        else if (_items.Count == 0 && (Loader is null || !_hasMore)) footer = EmptyText;
        if (footer is not null) rows.Add(new CardRow { FooterText = footer, RetryText = retry });

        // 只替换变化的部分，避免整表闪烁
        var common = 0;
        while (common < rows.Count && common < _rows.Count && SameRow(rows[common], _rows[common])) common++;
        while (_rows.Count > common) _rows.RemoveAt(_rows.Count - 1);
        for (var i = common; i < rows.Count; i++) _rows.Add(rows[i]);
    }

    private static bool SameRow(CardRow a, CardRow b) =>
        a.HeaderContent == b.HeaderContent && a.FooterText == b.FooterText && a.Columns == b.Columns
        && a.Template == b.Template && a.Items.SequenceEqual(b.Items);

    private ScrollViewer? FindScroller()
    {
        DependencyObject o = List;
        var queue = new Queue<DependencyObject>([o]);
        while (queue.Count > 0)
        {
            var n = queue.Dequeue();
            if (n is ScrollViewer sv) return sv;
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(n); i++) queue.Enqueue(VisualTreeHelper.GetChild(n, i));
        }
        return null;
    }

    private static CardItem? ItemFrom(object source)
    {
        for (var d = source as DependencyObject; d is not null; d = Tree.Parent(d))
        {
            if (d is Button) return null; // 表头里的按钮自己处理
            if (d is FrameworkElement { DataContext: CardItem item }) return item;
            if (d is CardGrid) return null;
        }
        return null;
    }

    private void OnClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject) return;
        if (ItemFrom(e.OriginalSource) is { } item) ItemClicked?.Invoke(item);
    }

    private void OnRightClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject || ContextMenuFor is null) return;
        if (ItemFrom(e.OriginalSource) is { } item && ContextMenuFor(item) is { } menu)
        {
            menu.IsOpen = true;
            e.Handled = true;
        }
    }
}
