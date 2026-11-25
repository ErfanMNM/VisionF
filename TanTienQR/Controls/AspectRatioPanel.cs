using Avalonia;
using Avalonia.Controls;

namespace TanTienQR.Controls;

/// <summary>
/// Xếp các con vào cùng một vùng 16:9, vừa cả chiều rộng lẫn chiều cao.
/// Dùng kích thước thật của control, phù hợp surface native không thể scale bằng Viewbox.
/// </summary>
public sealed class AspectRatioPanel : Panel
{
    public const double ScreenRatio = 16d / 9d;

    /// <summary>Tính vùng nội dung hữu hạn; khi chưa bị giới hạn cả hai chiều, dùng 960×540 DIP.</summary>
    public static Size Fit(Size available)
    {
        var width = double.IsPositiveInfinity(available.Width) ? double.PositiveInfinity : Math.Max(0, available.Width);
        var height = double.IsPositiveInfinity(available.Height) ? double.PositiveInfinity : Math.Max(0, available.Height);

        if (double.IsPositiveInfinity(width) && double.IsPositiveInfinity(height))
            return new Size(960, 540);

        var fittedWidth = Math.Min(width, height * ScreenRatio);
        return new Size(fittedWidth, fittedWidth / ScreenRatio);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var fitted = Fit(availableSize);
        foreach (var child in Children)
            child.Measure(fitted);
        return fitted;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var fitted = Fit(finalSize);
        var bounds = new Rect((finalSize.Width - fitted.Width) / 2,
            (finalSize.Height - fitted.Height) / 2, fitted.Width, fitted.Height);
        foreach (var child in Children)
            child.Arrange(bounds);
        return finalSize;
    }
}
