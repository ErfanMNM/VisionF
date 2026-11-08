using System.Globalization;
using ClassLibraryMT.Po;
using CommunityToolkit.Mvvm.ComponentModel;

namespace TanTienQR.ViewModels;

/// <summary>
/// ViewModel cho bảng "Thông tin sản xuất" (tab Thông tin sản xuất).
///
/// <para>
/// Nguồn dữ liệu là <see cref="ProductionOrderService"/> — PO đang chạy,
/// số mã đã quét / tổng số mã, trạng thái và các mốc thời gian.
/// </para>
/// </summary>
internal sealed partial class ProductionInfoViewModel : ViewModelBase, IDisposable
{
    private static readonly CultureInfo ViCulture = CreateViCulture();

    private readonly ProductionOrderService _po;

    /// <summary>Cờ chống dispose 2 lần.</summary>
    private bool _disposed;

    public ProductionInfoViewModel(ProductionOrderService po)
    {
        _po = po;
        _po.Changed += OnPoChanged;

        Refresh();
    }

    // ---------- Trạng thái PO ----------

    /// <summary>Mã lệnh sản xuất đang chạy (hiển thị "—" nếu chưa có).</summary>
    [ObservableProperty]
    public partial string OrderNoText { get; set; } = "—";

    /// <summary>Tổng số mã của PO.</summary>
    [ObservableProperty]
    public partial string OrderQtyText { get; set; } = "0";

    /// <summary>Số mã đã quét trong PO.</summary>
    [ObservableProperty]
    public partial string ScannedText { get; set; } = "0";

    /// <summary>Chuỗi "đã quét / tổng" ví dụ "12 / 100".</summary>
    public string ProgressText => $"{ScannedText} / {OrderQtyText}";

    /// <summary>Phần trăm đã quét (0-100).</summary>
    [ObservableProperty]
    public partial double ProgressPercent { get; set; }

    /// <summary>Phần trăm đã quét hiển thị dạng văn bản (ví dụ "42.5%").</summary>
    public string ProgressPercentText =>
        $"{ProgressPercent.ToString("0.#", CultureInfo.InvariantCulture)}%";

    /// <summary>Trạng thái PO (Chưa bắt đầu / Đang chạy / Hoàn tất).</summary>
    [ObservableProperty]
    public partial string StatusText { get; set; } = "Chưa bắt đầu";

    /// <summary>Class style cho badge trạng thái.</summary>
    public string StatusBadgeClass => StatusText switch
    {
        "Đang chạy" => "running",
        "Hoàn tất" => "warning",
        _ => "",
    };

    /// <summary>Thời điểm bắt đầu sản xuất.</summary>
    [ObservableProperty]
    public partial string StartedText { get; set; } = "—";

    /// <summary>Thời điểm hoàn tất sản xuất.</summary>
    [ObservableProperty]
    public partial string CompletedText { get; set; } = "—";

    /// <summary>Thời điểm tạo PO.</summary>
    [ObservableProperty]
    public partial string CreatedText { get; set; } = "—";

    /// <summary>True khi đang có PO chạy.</summary>
    [ObservableProperty]
    public partial bool IsRunning { get; set; }

    /// <summary>True khi chưa có PO nào được chọn.</summary>
    public bool IsEmpty => !IsRunning;

    /// <summary>
    /// Nạp lại thông tin từ <see cref="ProductionOrderService"/>.
    /// </summary>
    public void Refresh()
    {
        var order = _po.CurrentOrder;

        if (order is null)
        {
            OrderNoText = "—";
            OrderQtyText = "0";
            ScannedText = "0";
            ProgressPercent = 0;
            StatusText = "Chưa bắt đầu";
            StartedText = "—";
            CompletedText = "—";
            CreatedText = "—";
            IsRunning = false;
            OnPropertyChanged(nameof(ProgressText));
            OnPropertyChanged(nameof(ProgressPercentText));
            OnPropertyChanged(nameof(StatusBadgeClass));
            OnPropertyChanged(nameof(IsEmpty));
            return;
        }

        OrderNoText = order.OrderNo;
        OrderQtyText = _po.TotalCount > 0
            ? _po.TotalCount.ToString(CultureInfo.InvariantCulture)
            : order.OrderQty.ToString(CultureInfo.InvariantCulture);
        ScannedText = _po.ScannedCount.ToString(CultureInfo.InvariantCulture);
        ProgressPercent = _po.TotalCount > 0
            ? Math.Round(_po.ScannedCount * 100.0 / _po.TotalCount, 1)
            : 0;

        StatusText = _po.CurrentStatus switch
        {
            ProductionOrderStatus.Running => "Đang chạy",
            ProductionOrderStatus.Completed => "Hoàn tất",
            ProductionOrderStatus.Downloaded => "Đã tải về",
            _ => "Chưa bắt đầu",
        };

        IsRunning = _po.IsProductionRunning;
        CreatedText = order.CreatedUtc.ToLocalTime().ToString("dd/MM/yyyy HH:mm", ViCulture);
        StartedText = IsRunning
            ? DateTime.UtcNow.ToLocalTime().ToString("dd/MM/yyyy HH:mm", ViCulture)
            : "—";
        CompletedText = _po.CurrentStatus == ProductionOrderStatus.Completed
            ? DateTime.UtcNow.ToLocalTime().ToString("dd/MM/yyyy HH:mm", ViCulture)
            : "—";

        OnPropertyChanged(nameof(ProgressText));
        OnPropertyChanged(nameof(ProgressPercentText));
        OnPropertyChanged(nameof(StatusBadgeClass));
        OnPropertyChanged(nameof(IsEmpty));
    }

    /// <summary>
    /// PO thay đổi trạng thái → cập nhật UI.
    /// </summary>
    private void OnPoChanged(object? sender, EventArgs e)
    {
        // Event có thể phát từ luồng worker -> đưa về luồng UI.
        Avalonia.Threading.Dispatcher.UIThread.Post(Refresh);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _po.Changed -= OnPoChanged;
    }

    private static CultureInfo CreateViCulture()
    {
        try
        {
            return new CultureInfo("vi-VN");
        }
        catch (CultureNotFoundException)
        {
            return CultureInfo.InvariantCulture;
        }
    }
}
