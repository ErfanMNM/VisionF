using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Threading;
using ClassLibraryMT.Logging;
using ClassLibraryMT.Po;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace TanTienQR.ViewModels;

/// <summary>
/// ViewModel cho tab "Truy xuất Sản xuất (Local)".
///
/// <para>
/// Quản lý các PO đã tải về từ server:
/// tải PO, xem danh sách, bắt đầu / hoàn tất sản xuất và gửi mã kích hoạt
/// thủ công.
/// </para>
/// </summary>
internal sealed partial class ProductionOrdersViewModel : ViewModelBase, IDisposable
{
    private readonly ProductionOrderService _po;
    private readonly IUserActionLogger _actionLog;

    /// <summary>Cờ chống dispose 2 lần.</summary>
    private bool _disposed;

    public ProductionOrdersViewModel(
        ProductionOrderService po,
        IUserActionLogger actionLog)
    {
        _po = po;
        _actionLog = actionLog;
        _po.Changed += OnPoChanged;
    }

    // ---------- Danh sách PO ----------

    /// <summary>Các PO đã tải về (mới nhất trước).</summary>
    public ObservableCollection<ProductionOrderRowViewModel> Orders { get; } = [];

    /// <summary>PO được chọn trong dropdown.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanStart))]
    [NotifyPropertyChangedFor(nameof(CanComplete))]
    public partial ProductionOrderRowViewModel? SelectedOrder { get; set; }

    /// <summary>True khi đang tải dữ liệu (disable nút).</summary>
    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    /// <summary>Thông báo trạng thái.</summary>
    [ObservableProperty]
    public partial string StatusMessage { get; set; } = "Chưa tải PO nào.";

    /// <summary>True khi có PO đang chạy.</summary>
    [ObservableProperty]
    public partial bool HasRunningOrder { get; set; }

    /// <summary>Mã PO đang chạy.</summary>
    [ObservableProperty]
    public partial string RunningOrderNoText { get; set; } = "—";

    /// <summary>Số mã đã quét / tổng của PO đang chạy.</summary>
    [ObservableProperty]
    public partial string RunningProgressText { get; set; } = "0 / 0";

    /// <summary>True khi có thể bắt đầu sản xuất (có PO được chọn và chưa có PO chạy).</summary>
    public bool CanStart => !IsBusy && !HasRunningOrder && SelectedOrder is not null;

    /// <summary>True khi có thể hoàn tất sản xuất (đang có PO chạy).</summary>
    public bool CanComplete => !IsBusy && HasRunningOrder;

    /// <summary>True khi có thể gửi kích hoạt thủ công (đang có PO chạy).</summary>
    public bool CanActivate => !IsBusy && HasRunningOrder;

    // ---------- Lệnh ----------

    /// <summary>
    /// Tải danh sách PO từ server (đồng thời tải tập mã + tạo file DB riêng).
    /// </summary>
    [RelayCommand]
    private async Task DownloadOrdersAsync()
    {
        if (IsBusy) return;

        IsBusy = true;
        StatusMessage = "Đang tải PO từ server...";

        try
        {
            var summaries = await _po.DownloadOrdersAsync().ConfigureAwait(true);

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                Orders.Clear();
                foreach (var summary in summaries)
                {
                    Orders.Add(new ProductionOrderRowViewModel(summary));
                }

                if (SelectedOrder is null && Orders.Count > 0)
                {
                    SelectedOrder = Orders[0];
                }
            });

            StatusMessage = $"Đã tải {summaries.Count} PO từ server.";

            _actionLog.Log(
                "Po.Download",
                "ProductionOrder",
                $"Tai {summaries.Count} PO tu server",
                UserActionResults.Success);
        }
        catch (Exception ex)
        {
            StatusMessage = "Không tải được PO: " + ex.Message;

            _actionLog.Log(
                "Po.Download",
                "ProductionOrder",
                $"Loi khi tai PO: {ex.Message}",
                UserActionResults.Failed);
        }
        finally
        {
            IsBusy = false;
            RefreshRunningState();
        }
    }

    /// <summary>
    /// Bắt đầu sản xuất cho PO được chọn.
    /// </summary>
    [RelayCommand]
    private async Task StartProductionAsync()
    {
        var order = SelectedOrder;
        if (order is null || IsBusy) return;

        IsBusy = true;
        StatusMessage = $"Đang bắt đầu sản xuất PO {order.OrderNo}...";

        try
        {
            var ok = await _po.StartProductionAsync(order.OrderNo).ConfigureAwait(true);
            StatusMessage = ok
                ? $"Đã bắt đầu sản xuất PO {order.OrderNo}."
                : $"Không thể bắt đầu PO {order.OrderNo} (đã có PO đang chạy hoặc PO không có mã).";

            _actionLog.Log(
                "Po.Start",
                "ProductionOrder",
                $"Bat dau PO {order.OrderNo}: {(ok ? "OK" : "That bai")}",
                ok ? UserActionResults.Success : UserActionResults.Failed);
        }
        catch (Exception ex)
        {
            StatusMessage = "Không bắt đầu được sản xuất: " + ex.Message;

            _actionLog.Log(
                "Po.Start",
                "ProductionOrder",
                $"Loi khi bat dau PO {order.OrderNo}: {ex.Message}",
                UserActionResults.Failed);
        }
        finally
        {
            IsBusy = false;
            RefreshRunningState();
        }
    }

    /// <summary>
    /// Hoàn tất sản xuất PO đang chạy (mở khoá chọn PO).
    /// </summary>
    [RelayCommand]
    private async Task CompleteProductionAsync()
    {
        if (IsBusy) return;

        IsBusy = true;
        StatusMessage = "Đang hoàn tất sản xuất...";

        try
        {
            var ok = await _po.CompleteProductionAsync().ConfigureAwait(true);
            StatusMessage = ok
                ? "Đã hoàn tất sản xuất."
                : "Không có PO nào đang chạy.";

            _actionLog.Log(
                "Po.Complete",
                "ProductionOrder",
                ok ? "Hoan tat san xuat" : "Khong co PO dang chay",
                ok ? UserActionResults.Success : UserActionResults.Failed);
        }
        catch (Exception ex)
        {
            StatusMessage = "Không hoàn tất được sản xuất: " + ex.Message;

            _actionLog.Log(
                "Po.Complete",
                "ProductionOrder",
                $"Loi khi hoan tat san xuat: {ex.Message}",
                UserActionResults.Failed);
        }
        finally
        {
            IsBusy = false;
            RefreshRunningState();
        }
    }

    /// <summary>
    /// Gửi thủ công các mã chưa kích hoạt lên server.
    /// </summary>
    [RelayCommand]
    private async Task ActivatePendingAsync()
    {
        if (IsBusy) return;

        IsBusy = true;
        StatusMessage = "Đang gửi mã kích hoạt...";

        try
        {
            var count = await _po.ActivatePendingAsync().ConfigureAwait(true);
            StatusMessage = count > 0
                ? $"Đã gửi kích hoạt {count} mã."
                : "Không có mã nào chờ kích hoạt.";

            _actionLog.Log(
                "Po.Activate",
                "ProductionOrder",
                $"Gui kich hoat {count} ma",
                UserActionResults.Success);
        }
        catch (Exception ex)
        {
            StatusMessage = "Không gửi được mã kích hoạt: " + ex.Message;

            _actionLog.Log(
                "Po.Activate",
                "ProductionOrder",
                $"Loi khi gui kich hoat: {ex.Message}",
                UserActionResults.Failed);
        }
        finally
        {
            IsBusy = false;
            RefreshRunningState();
        }
    }

    /// <summary>
    /// Nạp lại danh sách (khi mở tab).
    /// </summary>
    public void Load()
    {
        if (Orders.Count == 0)
        {
            DownloadOrdersCommand.Execute(null);
        }
        else
        {
            RefreshRunningState();
        }
    }

    /// <summary>Cập nhật trạng thái PO đang chạy lên UI.</summary>
    private void RefreshRunningState()
    {
        var running = _po.CurrentOrder;
        HasRunningOrder = _po.IsProductionRunning;
        RunningOrderNoText = running?.OrderNo ?? "—";
        RunningProgressText = $"{_po.ScannedCount} / {_po.TotalCount}";

        OnPropertyChanged(nameof(CanStart));
        OnPropertyChanged(nameof(CanComplete));
        OnPropertyChanged(nameof(CanActivate));
    }

    /// <summary>PO thay đổi → cập nhật UI (dispatch về luồng UI).</summary>
    private void OnPoChanged(object? sender, EventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            RefreshRunningState();
            foreach (var row in Orders)
            {
                row.Refresh();
            }
        });
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _po.Changed -= OnPoChanged;
    }
}

/// <summary>
/// Một dòng trong bảng danh sách PO.
/// </summary>
internal sealed class ProductionOrderRowViewModel : ViewModelBase
{
    private readonly ProductionOrderSummary _summary;

    public ProductionOrderRowViewModel(ProductionOrderSummary summary)
    {
        _summary = summary;
    }

    /// <summary>Mã PO.</summary>
    public string OrderNo => _summary.OrderNo;

    /// <summary>Tổng số mã của PO.</summary>
    public int OrderQty => _summary.OrderQty;

    /// <summary>Số mã đã quét.</summary>
    public int ScannedCount => _summary.ScannedCount;

    /// <summary>Chuỗi "đã quét / tổng".</summary>
    public string ProgressText => $"{ScannedCount} / {OrderQty}";

    /// <summary>Thời điểm tạo PO (hiển thị local).</summary>
    public string CreatedText =>
        _summary.CreatedUtc.ToLocalTime().ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture);

    /// <summary>Trạng thái PO (tiếng Việt).</summary>
    public string StatusText => _summary.Status switch
    {
        ProductionOrderStatus.Running => "Đang chạy",
        ProductionOrderStatus.Completed => "Hoàn tất",
        ProductionOrderStatus.Downloaded => "Đã tải về",
        ProductionOrderStatus.Closed => "Đã đóng",
        _ => "Chưa bắt đầu",
    };

    /// <summary>Class style cho badge trạng thái.</summary>
    public string StatusBadgeClass => _summary.Status switch
    {
        ProductionOrderStatus.Running => "running",
        ProductionOrderStatus.Completed => "warning",
        _ => "",
    };

    /// <summary>True khi PO đang chạy (badge xanh).</summary>
    public bool IsRunning => _summary.Status == ProductionOrderStatus.Running;

    /// <summary>True khi PO đã hoàn tất (badge vàng).</summary>
    public bool IsCompleted => _summary.Status == ProductionOrderStatus.Completed;

    /// <summary>Nạp lại thông tin từ service (khi PO đổi trạng thái).</summary>
    public void Refresh()
    {
        OnPropertyChanged(nameof(ScannedCount));
        OnPropertyChanged(nameof(ProgressText));
    }
}
