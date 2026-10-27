using System.Windows;
using System.Windows.Controls;

namespace JableLens.App.Controls;

/// <summary>按宽度固定高宽比的容器（封面统一比例，排版整齐）。Ratio = 高 / 宽。</summary>
public sealed class AspectBox : Decorator
{
    public static readonly DependencyProperty RatioProperty = DependencyProperty.Register(
        nameof(Ratio), typeof(double), typeof(AspectBox),
        new FrameworkPropertyMetadata(9.0 / 16.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public double Ratio
    {
        get => (double)GetValue(RatioProperty);
        set => SetValue(RatioProperty, value);
    }

    protected override Size MeasureOverride(Size constraint)
    {
        var w = double.IsInfinity(constraint.Width) ? 320 : constraint.Width;
        var size = new Size(w, w * Ratio);
        Child?.Measure(size);
        return size;
    }

    protected override Size ArrangeOverride(Size arrangeSize)
    {
        var size = new Size(arrangeSize.Width, arrangeSize.Width * Ratio);
        Child?.Arrange(new Rect(size));
        return size;
    }
}
