using System.Windows;

namespace JableLens.App;

/// <summary>
/// 界面字体 MiSans 的字重。MiSans 文件里标的数值不标准（Regular 330、Semibold 520、Bold 630、Heavy 700），
/// 直接用 FontWeights.SemiBold(600) / Bold(700) 会被 WPF 就近匹配到更粗的一档，所以按它的实际数值取。
/// XAML 里直接写数字：FontWeight="520" / "630" / "700"。
/// </summary>
public static class Weights
{
    public static readonly FontWeight SemiBold = FontWeight.FromOpenTypeWeight(520);
    public static readonly FontWeight Bold = FontWeight.FromOpenTypeWeight(630);
    public static readonly FontWeight Black = FontWeight.FromOpenTypeWeight(700);
}
