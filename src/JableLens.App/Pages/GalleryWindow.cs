using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using JableLens.App.Services;

namespace JableLens.App.Pages;

/// <summary>剧照大图：← → 切换，Esc 关闭，点击空白处关闭。</summary>
public sealed class GalleryWindow : Window
{
    private readonly IReadOnlyList<string> _urls;
    private readonly Image _image = new() { Stretch = Stretch.Uniform };
    private readonly TextBlock _counter = new() { Foreground = Brushes.White, FontSize = 14, Margin = new Thickness(16), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom };
    private int _index;

    public GalleryWindow(IReadOnlyList<string> urls, int index)
    {
        _urls = urls;
        Controls.WindowFrame.UseDark(this);
        _index = index;
        Title = "剧照";
        Width = 1100;
        Height = 780;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.FromRgb(10, 10, 10));
        ImageLoader.SetDecodeWidth(_image, 1600);

        var prev = NavButton("‹", -1, HorizontalAlignment.Left);
        var next = NavButton("›", 1, HorizontalAlignment.Right);
        Content = new Grid { Children = { _image, prev, next, _counter } };
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Left) Move(-1);
            else if (e.Key == Key.Right) Move(1);
            else if (e.Key == Key.Escape) Close();
        };
        MouseWheel += (_, e) => Move(e.Delta > 0 ? -1 : 1);
        Show(_index);
    }

    private Button NavButton(string text, int delta, HorizontalAlignment align)
    {
        var b = new Button
        {
            Content = new TextBlock { Text = text, FontSize = 44, Foreground = Brushes.White },
            Style = (Style)Application.Current.Resources["GhostBtn"],
            HorizontalAlignment = align, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12),
        };
        b.Click += (_, _) => Move(delta);
        return b;
    }

    private void Move(int d) => Show((_index + d + _urls.Count) % _urls.Count);

    private void Show(int i)
    {
        _index = i;
        ImageLoader.SetSource(_image, _urls[i]);
        _counter.Text = $"{i + 1} / {_urls.Count}";
    }
}
