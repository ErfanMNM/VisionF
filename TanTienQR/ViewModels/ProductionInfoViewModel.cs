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
///
/// <para>
/// Panel này còn cho phép <b>chọn PO và bắt đầu chạy ngay tại chỗ</b>
/// (tải PO, chọn PO, bắt đầu / hoàn tất sản xuất) mà không cần chuyển sang
/// tab "Truy xuất Sản xuất (Local)". Phần chọn PO dùng chung
/// <see cref="ProductionOrdersViewModel"/> để hai nơi luôn thấy cùng danh sách
/// và cùng PO đang chạy — không có hai nguồn sự thật.
/// </para>
/// </summary>
internal sealed partial class ProductionInfoViewModel : ViewModelBase, IDisposable
{
    private static readonly CultureInfo ViCulture = CreateViCulture();

    private readonly ProductionOrderService _po;

    /// <summary>Cờ chống dispose 2 lần.</summary>
    private bool _disposed;

    public ProductionInfoViewModel(
        ProductionOrderService po,
        ProductionOrdersViewModel orders)
    {
        _po = po;
        _po.Changed += OnPoChanged;

        Orders = orders;
        Orders.ListChanged += OnOrdersListChanged;
        Orders.PropertyChanged += OnOrdersPropertyChanged;

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

    // ---------- Chọn & chạy PO ngay tại panel ----------

    /// <summary>
    /// ViewModel quản lý PO dùng chung — cung cấp danh sách PO, PO đang chọn
    /// và các lệnh tải / bắt đầu / hoàn tất sản xuất.
    /// </summary>
    public ProductionOrdersViewModel Orders { get; }

    /// <summary>True khi chưa tải được PO nào từ server.</summary>
    public bool HasNoOrders => Orders.Orders.Count == 0;

    /// <summary>Chuỗi mô tả trạng thái thao tác (đồng bộ với tab PO).</summary>
    public string StatusMessage => Orders.StatusMessage;

    /// <summary>True khi có thể bắt đầu sản xuất PO đang chọn.</summary>
    public bool CanStart => Orders.CanStart;

    /// <summary>True khi có thể hoàn tất sản xuất PO đang chạy.</summary>
    public bool CanComplete => Orders.CanComplete;

    /// <summary>True khi đang xử lý (disable nút).</summary>
    public bool IsBusy => Orders.IsBusy;

    /// <summary>
    /// Mã PO đang chọn trong dropdown (hiển thị "—" nếu chưa chọn).
    /// </summary>
    public string SelectedOrderNoText => Orders.SelectedOrder?.OrderNo ?? "—";

    /// <summary>True khi sản xuất đã từng bắt đầu (dùng cho timeline).</summary>
    public bool HasStarted => StartedText != "—";

    /// <summary>True khi sản xuất đã hoàn tất (dùng cho timeline).</summary>
    public bool HasCompleted => CompletedText != "—";

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
            RaiseSelectionStateChanged();
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
        RaiseSelectionStateChanged();
    }

    /// <summary>
    /// Báo UI cập nhật các thuộc tính phụ thuộc vào ViewModel PO dùng chung
    /// (danh sách PO, PO đang chọn, trạng thái nút bấm).
    /// </summary>
    private void RaiseSelectionStateChanged()
    {
        OnPropertyChanged(nameof(HasNoOrders));
        OnPropertyChanged(nameof(StatusMessage));
        OnPropertyChanged(nameof(CanStart));
        OnPropertyChanged(nameof(CanComplete));
        OnPropertyChanged(nameof(IsBusy));
        OnPropertyChanged(nameof(SelectedOrderNoText));
        OnPropertyChanged(nameof(HasStarted));
        OnPropertyChanged(nameof(HasCompleted));
    }

    /// <summary>
    /// PO thay đổi trạng thái → cập nhật UI.
    /// </summary>
    private void OnPoChanged(object? sender, EventArgs e)
    {
        // Event có thể phát từ luồng worker -> đưa về luồng UI.
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            Refresh();
            RaiseSelectionStateChanged();
        });
    }

    /// <summary>Danh sách PO vừa được tải lại → cập nhật trạng thái panel.</summary>
    private void OnOrdersListChanged(object? sender, EventArgs e)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(RaiseSelectionStateChanged);
    }

    /// <summary>
    /// Thuộc tính của ViewModel PO dùng chung thay đổi (PO được chọn, trạng
    /// thái nút, thông báo) → cập nhật các binding tương ứng của panel.
    /// </summary>
    private void OnOrdersPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(RaiseSelectionStateChanged);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _po.Changed -= OnPoChanged;
        Orders.ListChanged -= OnOrdersListChanged;
        Orders.PropertyChanged -= OnOrdersPropertyChanged;
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
