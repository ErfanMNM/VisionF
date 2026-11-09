using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Media;

namespace TanTienQR.Controls;

/// <summary>
/// Vòng tròn tiến trình (donut) hiển thị phần trăm hoàn thành.
///
/// <para>
/// Dùng cho panel "Thông tin sản xuất": vòng ngoài là nền mảnh, vòng trong
/// là phần đã hoàn thành (màu accent), ở giữa hiện số phần trăm.
/// </para>
///
/// <para>
/// Vẽ bằng <see cref="Arc"/> nên không cần thư viện vẽ ngoài; tương thích
/// Native AOT.
/// </para>
/// </summary>
public sealed class DonutProgress : Panel
{
    /// <summary>Khoảng cách an toàn giữa vòng và mép control (cho số phần trăm).</summary>
    private const double Inset = 3;

    // ---------- Styled properties ----------

    /// <summary>Giá trị hiện tại (0 - 100).</summary>
    public static readonly StyledProperty<double> ValueProperty =
        AvaloniaProperty.Register<DonutProgress, double>(nameof(Value));

    /// <summary>Độ dày vòng (pixel).</summary>
    public static readonly StyledProperty<double> ThicknessProperty =
        AvaloniaProperty.Register<DonutProgress, double>(nameof(Thickness), defaultValue: 7);

    /// <summary>Màu vòng nền.</summary>
    public static readonly StyledProperty<IBrush?> TrackBrushProperty =
        AvaloniaProperty.Register<DonutProgress, IBrush?>(nameof(TrackBrush));

    /// <summary>Màu vòng tiến trình.</summary>
    public static readonly StyledProperty<IBrush?> ProgressBrushProperty =
        AvaloniaProperty.Register<DonutProgress, IBrush?>(nameof(ProgressBrush));

    /// <summary>Màu chữ phần trăm.</summary>
    public static readonly StyledProperty<IBrush?> TextBrushProperty =
        AvaloniaProperty.Register<DonutProgress, IBrush?>(nameof(TextBrush));

    /// <summary>Cỡ chữ phần trăm.</summary>
    public static readonly StyledProperty<double> FontSizeProperty =
        AvaloniaProperty.Register<DonutProgress, double>(nameof(FontSize), defaultValue: 16);

    /// <summary>Giá trị hiện tại (0 - 100).</summary>
    public double Value
    {
        get => GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    /// <summary>Độ dày vòng (pixel).</summary>
    public double Thickness
    {
        get => GetValue(ThicknessProperty);
        set => SetValue(ThicknessProperty, value);
    }

    /// <summary>Màu vòng nền.</summary>
    public IBrush? TrackBrush
    {
        get => GetValue(TrackBrushProperty);
        set => SetValue(TrackBrushProperty, value);
    }

    /// <summary>Màu vòng tiến trình.</summary>
    public IBrush? ProgressBrush
    {
        get => GetValue(ProgressBrushProperty);
        set => SetValue(ProgressBrushProperty, value);
    }

    /// <summary>Màu chữ phần trăm.</summary>
    public IBrush? TextBrush
    {
        get => GetValue(TextBrushProperty);
        set => SetValue(TextBrushProperty, value);
    }

    /// <summary>Cỡ chữ phần trăm.</summary>
    public double FontSize
    {
        get => GetValue(FontSizeProperty);
        set => SetValue(FontSizeProperty, value);
    }

    // ---------- Elements ----------

    private readonly Arc _track = new();
    private readonly Arc _progress = new();
    private readonly TextBlock _label = new();

    /// <summary>Khởi tạo control.</summary>
    public DonutProgress()
    {
        _track.StrokeLineCap = PenLineCap.Round;
        _progress.StrokeLineCap = PenLineCap.Round;

        _label.TextAlignment = TextAlignment.Center;
        _label.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center;
        _label.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center;
        _label.FontWeight = FontWeight.Bold;

        _track.IsHitTestVisible = false;
        _progress.IsHitTestVisible = false;
        _label.IsHitTestVisible = false;

        Children.Add(_track);
        Children.Add(_progress);
        Children.Add(_label);

        TrackBrush = new SolidColorBrush(Color.Parse("#D8E6EC"));
        ProgressBrush = new SolidColorBrush(Color.Parse("#1A56DB"));
        TextBrush = new SolidColorBrush(Color.Parse("#0F2A38"));

        AffectsRender<DonutProgress>(ValueProperty);
        AffectsRender<DonutProgress>(ThicknessProperty);
        AffectsRender<DonutProgress>(TrackBrushProperty);
        AffectsRender<DonutProgress>(ProgressBrushProperty);
        AffectsRender<DonutProgress>(TextBrushProperty);
        AffectsRender<DonutProgress>(FontSizeProperty);
    }

    /// <inheritdoc/>
    protected override Size ArrangeOverride(Size finalSize)
    {
        LayoutChildren(finalSize);
        return finalSize;
    }

    /// <inheritdoc/>
    protected override Size MeasureOverride(Size availableSize)
    {
        var side = Math.Min(availableSize.Width, availableSize.Height);
        if (double.IsInfinity(side) || side <= 0)
            side = 80;

        _track.Measure(new Size(side, side));
        _progress.Measure(new Size(side, side));
        _label.Measure(new Size(side, side));

        return new Size(side, side);
    }

    /// <summary>Bố trí các phần tử theo hình tròn.</summary>
    private void LayoutChildren(Size size)
    {
        var side = Math.Min(size.Width, size.Height);
        if (side <= 0)
            return;

        var offsetX = (size.Width - side) / 2;
        var offsetY = (size.Height - side) / 2;

        var thickness = Math.Max(1, Thickness);
        var radius = (side / 2) - (thickness / 2) - Inset;
        if (radius <= 0)
        {
            _track.IsVisible = false;
            _progress.IsVisible = false;
            return;
        }

        var ring = new Rect(offsetX, offsetY, side, side);

        // Vòng nền: tròn kín.
        _track.Stroke = TrackBrush;
        _track.StrokeThickness = thickness;
        _track.StartAngle = 0;
        _track.SweepAngle = 360;
        _track.IsVisible = true;
        _track.Arrange(ring);

        // Vòng tiến trình: bắt đầu từ 12 giờ, chiều kim đồng hồ.
        var percent = Math.Clamp(Value, 0, 100);
        _progress.Stroke = ProgressBrush;
        _progress.StrokeThickness = thickness;
        _progress.StartAngle = -90;
        _progress.SweepAngle = percent * 3.6;
        _progress.IsVisible = percent > 0;
        _progress.Arrange(ring);

        // Số phần trăm ở giữa.
        _label.Text = $"{percent:0}%";
        _label.FontSize = FontSize;
        _label.Foreground = TextBrush;
        _label.Arrange(new Rect(
            offsetX, offsetY + (side / 2) - (FontSize * 0.75), side, FontSize * 1.5));
    }
}
