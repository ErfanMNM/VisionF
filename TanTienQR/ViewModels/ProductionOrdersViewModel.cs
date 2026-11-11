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
/// cào PO mới từ server, xem danh sách, bắt đầu / huỷ sản xuất và gửi mã
/// kích hoạt thủ công.
/// </para>
///
/// <para>
/// <b>Mô hình local-first:</b> danh sách PO luôn đọc từ DB local
/// (<c>logs/po/{yyyy-MM}/</c>). Nút "Tải PO" chỉ <i>cào</i> dữ liệu mới từ
/// server xuống local — có thể bấm nhiều lần, PO mới và mã mới được thêm vào,
/// còn mã cũ và lịch sử đã quét giữ nguyên.
/// </para>
/// </summary>
internal sealed partial class ProductionOrdersViewModel : ViewModelBase, IDisposable
{
    private readonly ProductionOrderService _po;
    private readonly IUserActionLogger _actionLog;

    /// <summary>Cờ chống dispose 2 lần.</summary>
    private bool _disposed;

    /// <summary>
    /// Event phát khi danh sách PO được tải lại (dùng bởi panel
    /// "Thông tin sản xuất" để cập nhật trạng thái nút).
    /// </summary>
    public event EventHandler? ListChanged;

    public ProductionOrdersViewModel(
        ProductionOrderService po,
        IUserActionLogger actionLog)
    {
        _po = po;
        _actionLog = actionLog;
        _po.Changed += OnPoChanged;
        _po.HistoryConflict += OnHistoryConflict;
    }

    // ---------- Danh sách PO ----------

    /// <summary>Tất cả PO đã có trong DB local (mới nhất trước).</summary>
    public ObservableCollection<ProductionOrderRowViewModel> Orders { get; } = [];

    /// <summary>
    /// Các PO có thể chọn để chạy (loại bỏ PO đã bị huỷ).
    ///
    /// <para>
    /// PO đã huỷ vẫn hiện trong bảng quản lý <see cref="Orders"/> để người
    /// dùng tra cứu, nhưng không được chọn để chạy lại.
    /// </para>
    /// </summary>
    public ObservableCollection<ProductionOrderRowViewModel> SelectableOrders { get; } = [];

    /// <summary>
    /// True khi bảng danh sách PO đang trống (dùng cho trạng thái rỗng UI-016).
    /// </summary>
    public bool IsEmpty => Orders.Count == 0;

    /// <summary>PO được chọn trong dropdown.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanStart))]
    [NotifyPropertyChangedFor(nameof(CanCancel))]
    public partial ProductionOrderRowViewModel? SelectedOrder { get; set; }

    /// <summary>True khi đang tải dữ liệu (disable nút).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanStart))]
    [NotifyPropertyChangedFor(nameof(CanCancel))]
    public partial bool IsBusy { get; set; }

    /// <summary>Thông báo trạng thái.</summary>
    [ObservableProperty]
    public partial string StatusMessage { get; set; } = "Chưa tải PO nào.";

    /// <summary>True khi có PO đang chạy.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanCancel))]
    public partial bool HasRunningOrder { get; set; }

    /// <summary>Mã PO đang chạy.</summary>
    [ObservableProperty]
    public partial string RunningOrderNoText { get; set; } = "—";

    /// <summary>Số mã đã quét / tổng của PO đang chạy.</summary>
    [ObservableProperty]
    public partial string RunningProgressText { get; set; } = "0 / 0";

    /// <summary>
    /// True khi máy đã bị dừng do phát hiện mã đã từng xuất hiện trong lịch sử.
    /// </summary>
    [ObservableProperty]
    public partial bool IsHalted { get; set; }

    /// <summary>True khi có thể bắt đầu sản xuất (có PO được chọn và chưa có PO chạy).</summary>
    public bool CanStart => !IsBusy && !HasRunningOrder && !IsHalted && SelectedOrder is not null;

    /// <summary>
    /// True khi có thể huỷ PO (đang có PO chạy, chưa chạy xong 100%).
    /// </summary>
    public bool CanCancel => !IsBusy && HasRunningOrder && !IsHalted;

    /// <summary>True khi có thể gửi kích hoạt thủ công (đang có PO chạy).</summary>
    public bool CanActivate => !IsBusy && HasRunningOrder && !IsHalted;

    /// <summary>
    /// True khi PO đang chạy đã quét đủ 100% (sẽ tự hoàn tất).
    /// </summary>
    public bool IsComplete => _po.ProgressPercent >= 100.0;

    // ---------- Lệnh ----------

    /// <summary>
    /// Cào PO + tập mã từ server xuống DB local (có thể bấm nhiều lần).
    /// </summary>
    [RelayCommand]
    private async Task DownloadOrdersAsync()
    {
        if (IsBusy) return;

        IsBusy = true;
        StatusMessage = "Đang cào PO từ server...";

        try
        {
            var sync = await _po.SyncFromServerAsync().ConfigureAwait(true);
            await ReloadLocalAsync().ConfigureAwait(true);

            StatusMessage =
                $"Đã cào {sync.PoCount} PO từ server " +
                $"({sync.NewPoCount} PO mới, {sync.NewCodeCount} mã mới).";

            _actionLog.Log(
                "Po.Download",
                "ProductionOrder",
                $"Cao {sync.PoCount} PO tu server ({sync.NewPoCount} moi, {sync.NewCodeCount} ma moi)",
                UserActionResults.Success);

            ListChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            StatusMessage = "Không cào được PO: " + ex.Message;

            _actionLog.Log(
                "Po.Download",
                "ProductionOrder",
                $"Loi khi cao PO: {ex.Message}",
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
                : $"Không thể bắt đầu PO {order.OrderNo} " +
                  "(đã có PO đang chạy, PO đã bị huỷ, hoặc PO chưa được cào về máy).";

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
    /// Huỷ PO đang chạy — PO bị huỷ sẽ không được chạy lại.
    /// </summary>
    [RelayCommand]
    private async Task CancelProductionAsync()
    {
        if (IsBusy) return;

        var orderNo = _po.CurrentOrder?.OrderNo;
        if (string.IsNullOrWhiteSpace(orderNo)) return;

        IsBusy = true;
        StatusMessage = $"Đang huỷ PO {orderNo}...";

        try
        {
            var ok = await _po.CancelProductionAsync(orderNo).ConfigureAwait(true);
            StatusMessage = ok
                ? $"Đã huỷ PO {orderNo}. PO này không thể chạy lại."
                : $"Không huỷ được PO {orderNo}.";

            _actionLog.Log(
                "Po.Cancel",
                "ProductionOrder",
                ok ? $"Huy PO {orderNo}" : $"Khong huy duoc PO {orderNo}",
                ok ? UserActionResults.Success : UserActionResults.Failed);

            await ReloadLocalAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            StatusMessage = "Không huỷ được PO: " + ex.Message;

            _actionLog.Log(
                "Po.Cancel",
                "ProductionOrder",
                $"Loi khi huy PO {orderNo}: {ex.Message}",
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
    /// Nạp lại danh sách PO từ DB local (không gọi mạng).
    /// </summary>
    public async Task LoadLocalAsync()
    {
        if (IsBusy) return;

        try
        {
            await ReloadLocalAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            StatusMessage = "Không đọc được danh sách PO local: " + ex.Message;
        }
        finally
        {
            RefreshRunningState();
        }
    }

    /// <summary>
    /// Nạp lại danh sách (khi mở tab) — chỉ đọc DB local, không gọi mạng.
    /// </summary>
    public void Load()
    {
        if (Orders.Count == 0)
        {
            // Đọc local trước; nếu chưa có gì thì báo người dùng bấm "Tải PO".
            // Fire-and-forget: Load() là callback đồng bộ của UI.
            _ = LoadLocalAsync();
        }
        else
        {
            RefreshRunningState();
        }
    }

    /// <summary>
    /// Đọc danh sách PO từ DB local và cập nhật 2 collection.
    /// </summary>
    private async Task ReloadLocalAsync()
    {
        var summaries = await _po.GetLocalOrdersAsync().ConfigureAwait(true);

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var previous = SelectedOrder?.OrderNo;

            Orders.Clear();
            SelectableOrders.Clear();

            foreach (var summary in summaries)
            {
                var row = new ProductionOrderRowViewModel(summary);
                Orders.Add(row);

                if (!row.IsCancelled)
                {
                    SelectableOrders.Add(row);
                }
            }

            // Giữ lại PO đang chọn nếu vẫn còn hợp lệ.
            SelectedOrder = SelectableOrders.FirstOrDefault(
                p => string.Equals(p.OrderNo, previous, StringComparison.OrdinalIgnoreCase))
                ?? SelectableOrders.FirstOrDefault();

            OnPropertyChanged(nameof(IsEmpty));
        });
    }

    /// <summary>Cập nhật trạng thái PO đang chạy lên UI.</summary>
    private void RefreshRunningState()
    {
        var running = _po.CurrentOrder;
        HasRunningOrder = _po.IsProductionRunning;
        RunningOrderNoText = running?.OrderNo ?? "—";
        RunningProgressText = $"{_po.ScannedCount} / {_po.TotalCount}";
        IsHalted = _po.IsHalted;

        OnPropertyChanged(nameof(CanStart));
        OnPropertyChanged(nameof(CanCancel));
        OnPropertyChanged(nameof(CanActivate));
        OnPropertyChanged(nameof(IsComplete));
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

    /// <summary>
    /// Phát hiện mã đã từng xuất hiện trong lịch sử → máy đã dừng.
    /// </summary>
    private void OnHistoryConflict(object? sender, IReadOnlyList<string> conflicts)
    {
        Dispatcher.UIThread.Post(() =>
        {
            IsHalted = true;
            StatusMessage =
                $"Phát hiện {conflicts.Count} mã đã từng quét: {string.Join(", ", conflicts)}. " +
                "Đã dừng máy — vui lòng kiểm tra và xử lý.";

            _actionLog.Log(
                "Po.Halt",
                "ProductionOrder",
                $"Phat hien {conflicts.Count} ma da tung quet: {string.Join(", ", conflicts)}. Da dung may.",
                UserActionResults.Failed);
        });
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _po.Changed -= OnPoChanged;
        _po.HistoryConflict -= OnHistoryConflict;
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
        ProductionOrderStatus.Cancelled => "Đã huỷ",
        ProductionOrderStatus.Closed => "Đã đóng",
        _ => "Chưa bắt đầu",
    };

    /// <summary>Class style cho badge trạng thái.</summary>
    public string StatusBadgeClass => _summary.Status switch
    {
        ProductionOrderStatus.Running => "running",
        ProductionOrderStatus.Completed => "warning",
        ProductionOrderStatus.Cancelled => "fault",
        _ => "",
    };

    /// <summary>True khi PO đang chạy (badge xanh).</summary>
    public bool IsRunning => _summary.Status == ProductionOrderStatus.Running;

    /// <summary>True khi PO đã hoàn tất (badge vàng).</summary>
    public bool IsCompleted => _summary.Status == ProductionOrderStatus.Completed;

    /// <summary>True khi PO đã bị huỷ (badge đỏ — không được chạy lại).</summary>
    public bool IsCancelled => _summary.Status == ProductionOrderStatus.Cancelled;

    /// <summary>Nạp lại thông tin từ service (khi PO đổi trạng thái).</summary>
    public void Refresh()
    {
        OnPropertyChanged(nameof(ScannedCount));
        OnPropertyChanged(nameof(ProgressText));
    }
}
