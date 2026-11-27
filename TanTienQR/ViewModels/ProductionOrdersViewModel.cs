using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Threading;
using ClassLibraryMT.Logging;
using ClassLibraryMT.Po;
using ClassLibraryMT.PoServer;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace TanTienQR.ViewModels;

/// <summary>
/// ViewModel cho tab "Truy xuất Sản xuất (Local)".
///
/// <para>
/// Quản lý các PO đã tải về từ server:
/// cào PO mới từ server, xem danh sách, bắt đầu / huỷ sản xuất.
/// </para>
///
/// <para>
/// <b>Mô hình local-first:</b> danh sách PO luôn đọc từ DB local
/// (<c>logs/po/{yyyy-MM}/</c>). Nút "Tải PO" chỉ <i>cào</i> dữ liệu mới từ
/// server xuống local — có thể bấm nhiều lần, PO mới và mã mới được thêm vào,
/// còn mã cũ và lịch sử đã quét giữ nguyên.
/// </para>
///
/// <para>
/// Ngoài ra PO có thể được <b>MES đẩy xuống trực tiếp</b> qua HTTP
/// (<c>POST /api/po</c>) — khi đó danh sách tự động làm mới, không cần bấm
/// "Tải PO từ server".
/// </para>
/// </summary>
internal sealed partial class ProductionOrdersViewModel : ViewModelBase, IDisposable
{
    private readonly ProductionOrderService _po;
    private readonly IUserActionLogger _actionLog;
    private readonly PoHttpServer _poServer;

    /// <summary>Cờ chống dispose 2 lần.</summary>
    private bool _disposed;

    /// <summary>
    /// Event phát khi danh sách PO được tải lại (dùng bởi panel
    /// "Thông tin sản xuất" để cập nhật trạng thái nút).
    /// </summary>
    public event EventHandler? ListChanged;

    public ProductionOrdersViewModel(
        ProductionOrderService po,
        IUserActionLogger actionLog,
        PoHttpServer poServer)
    {
        _po = po;
        _actionLog = actionLog;
        _poServer = poServer;

        _po.Changed += OnPoChanged;

        // PO nhận từ MES -> tự động làm mới danh sách.
        _poServer.PoReceived += OnPoReceivedFromServer;

        RefreshServerStatus();
    }

    // ---------- Danh sách PO ----------

    /// <summary>Tất cả PO đã có trong DB local (mới nhất trước).</summary>
    public ObservableCollection<ProductionOrderRowViewModel> Orders { get; } = [];

    /// <summary>Danh sách PO sau khi áp dụng từ khoá tìm kiếm.</summary>
    public ObservableCollection<ProductionOrderRowViewModel> FilteredOrders { get; } = [];

    /// <summary>Từ khoá lọc danh sách PO (không phân biệt hoa thường).</summary>
    [ObservableProperty]
    public partial string SearchText { get; set; } = string.Empty;

    /// <summary>
    /// Các PO có thể chọn để chạy.
    ///
    /// <para>
    /// PO đã huỷ hoặc đã hoàn tất vẫn hiện trong bảng quản lý
    /// <see cref="Orders"/> để người dùng tra cứu, nhưng không được chọn để chạy lại.
    /// </para>
    /// </summary>
    public ObservableCollection<ProductionOrderRowViewModel> SelectableOrders { get; } = [];

    /// <summary>
    /// True khi bảng danh sách PO đang trống (dùng cho trạng thái rỗng UI-016).
    /// </summary>
    public bool IsEmpty => Orders.Count == 0;

    /// <summary>True khi có PO nhưng từ khoá tìm kiếm không khớp PO nào.</summary>
    public bool HasNoSearchResults => Orders.Count > 0 && FilteredOrders.Count == 0;

    /// <summary>Tóm tắt số PO đang hiển thị.</summary>
    public string OrderCountText => SearchText?.Trim() is { Length: > 0 } keyword
        ? $"{FilteredOrders.Count} / {Orders.Count} lệnh sản xuất (tìm “{keyword}”)"
        : $"{Orders.Count} lệnh sản xuất";

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

    /// <summary>True khi <see cref="StatusMessage"/> là thông báo lỗi (hiện màu đỏ).</summary>
    [ObservableProperty]
    public partial bool IsStatusError { get; set; }

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
    /// True khi hệ thống đang giữ trạng thái dừng để bảo vệ dữ liệu sản xuất.
    /// </summary>
    [ObservableProperty]
    public partial bool IsHalted { get; set; }

    /// <summary>True khi có thể gửi kích hoạt thủ công (đang có PO chạy).</summary>

    /// <summary>
    /// True khi có thể bắt đầu sản xuất (có PO hợp lệ được chọn và chưa có PO chạy).
    /// </summary>
    public bool CanStart =>
        !IsBusy && !HasRunningOrder && !IsHalted && SelectedOrder?.CanRun == true;

    /// <summary>
    /// True khi có thể huỷ PO (đang có PO chạy, chưa chạy xong 100%).
    /// </summary>
    public bool CanCancel => !IsBusy && HasRunningOrder && !IsHalted;

    /// <summary>
    /// True khi PO đang chạy đã quét đủ 100% (sẽ tự hoàn tất).
    /// </summary>
    public bool IsComplete => _po.ProgressPercent >= 100.0;

    // ---------- Trạng thái HTTP server nhận PO từ MES ----------

    /// <summary>
    /// Trạng thái HTTP server nhận PO (tiếng Việt) — ví dụ
    /// "Đang lắng nghe http://127.0.0.1:8085/".
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPoServerRunning))]
    [NotifyPropertyChangedFor(nameof(PoServerBadgeClass))]
    public partial string PoServerStatusText { get; set; } = "Chưa chạy.";

    /// <summary>True khi trạng thái server là lỗi (hiện màu đỏ).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPoServerRunning))]
    [NotifyPropertyChangedFor(nameof(PoServerBadgeClass))]
    public partial bool IsPoServerStatusError { get; set; }

    /// <summary>
    /// True khi HTTP server nhận PO đang chạy.
    /// </summary>
    public bool IsPoServerRunning =>
        !IsPoServerStatusError
        && !PoServerStatusText.StartsWith("Đã tắt", StringComparison.Ordinal)
        && !PoServerStatusText.StartsWith("Chưa chạy", StringComparison.Ordinal);

    /// <summary>
    /// Class style cho badge trạng thái server (xanh khi chạy, đỏ khi lỗi).
    /// </summary>
    public string PoServerBadgeClass => IsPoServerStatusError ? "fault"
        : IsPoServerRunning ? "running"
        : "";

    /// <summary>
    /// Cập nhật lại trạng thái HTTP server nhận PO từ MES.
    /// </summary>
    public void RefreshServerStatus()
    {
        var state = _poServer.State;

        PoServerStatusText = state.Status;
        IsPoServerStatusError = state.IsError;
    }

    // ---------- Lệnh ----------

    /// <summary>
    /// Cào PO + tập mã từ server xuống DB local (có thể bấm nhiều lần).
    /// </summary>
    [RelayCommand]
    private async Task DownloadOrdersAsync()
    {
        if (IsBusy) return;

        IsBusy = true;
        IsStatusError = false;
        StatusMessage = "Đang cào PO từ server...";

        try
        {
            var sync = await _po.SyncFromServerAsync().ConfigureAwait(true);
            await ReloadLocalAsync().ConfigureAwait(true);

            IsStatusError = sync.HasValidationErrors;
            StatusMessage = DescribeDownloadResult(sync);

            _actionLog.Log(
                "Po.Download",
                "ProductionOrder",
                $"Đã xử lý {sync.PoCount} PO: {sync.AcceptedCount}/{sync.PlannedCount} mã mới/kế hoạch; " +
                $"{sync.InvalidCodes.Count} mã sai, {sync.DuplicateCodes.Count} mã trùng; lỗi xác thực={sync.HasValidationErrors}.",
                sync.HasValidationErrors ? UserActionResults.Failed : UserActionResults.Success);

            ListChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            StatusMessage = DescribeDownloadError(ex);
            IsStatusError = true;

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

    /// <summary>Phân biệt kết quả nhận đủ, nhận một phần và PO bị từ chối xác thực.</summary>
    private static string DescribeDownloadResult(PoSyncResult sync)
    {
        if (sync.PoCount == 0) return "Server không có PO để tải.";
        var message = $"Đã xử lý {sync.PoCount} PO từ server ({sync.NewPoCount} PO mới). " +
            $"Chấp nhận {sync.AcceptedCount} mã mới / {sync.PlannedCount} mã theo kế hoạch; " +
            $"loại {sync.InvalidCodes.Count} mã sai và {sync.DuplicateCodes.Count} mã trùng.";
        var partial = sync.Orders.Count(order => order.ValidationError is null && order.InputCount < order.PlannedCount);
        if (partial > 0)
            message += $" {partial} PO có snapshot nguồn chưa đủ số lượng kế hoạch.";
        var errors = sync.Orders.Where(order => order.ValidationError is not null)
            .Select(order => $"{order.OrderNo}: {order.ValidationError}").ToArray();
        if (errors.Length > 0) message += " PO chưa được nhập: " + string.Join("; ", errors) + ".";
        var structure = sync.InvalidCodes.Count(code => code.Reason == "invalidStructure");
        var mismatch = sync.InvalidCodes.Count(code => code.Reason == "gtinMismatch");
        if (structure > 0 || mismatch > 0)
            message += $" Chi tiết mã loại: {structure} sai cấu trúc, {mismatch} khác GTIN.";
        return message;
    }

    /// <summary>Chuyển lỗi khi cào PO thành thông báo dễ hiểu cho người dùng.</summary>
    private static string DescribeDownloadError(Exception ex) => ex switch
        {
        ClassLibraryMT.Po.PoServerException => ex.Message,
        HttpRequestException { StatusCode: { } code } =>
            $"Server PO trả về lỗi HTTP {(int)code}. Vui lòng thử lại sau.",
        HttpRequestException =>
            "Không kết nối được tới server PO. Kiểm tra server đã chạy chưa và mạng/địa chỉ (Po:BaseUrl).",
        TaskCanceledException or TimeoutException =>
            "Server PO không phản hồi (quá thời gian chờ).",
        System.Text.Json.JsonException =>
            "Dữ liệu server PO trả về không hợp lệ.",
        _ => "Không cào được PO: " + ex.Message,
    };

    /// <summary>
    /// Bắt đầu sản xuất cho PO được chọn.
    /// </summary>
    [RelayCommand]
    private async Task StartProductionAsync()
    {
        var order = SelectedOrder;
        if (order is null || IsBusy) return;

        // Chặn ở ViewModel để PO đã huỷ/hoàn tất không thể chạy lại,
        // kể cả khi trạng thái được chọn từ nguồn khác ngoài ComboBox.
        if (!order.CanRun)
        {
            StatusMessage = $"PO {order.OrderNo} đã {order.StatusText.ToLowerInvariant()}, không thể chạy lại.";
            IsStatusError = true;
            return;
        }

        IsBusy = true;
        StatusMessage = $"Đang bắt đầu sản xuất PO {order.OrderNo}...";

        try
        {
            var ok = await _po.StartProductionAsync(order.OrderNo).ConfigureAwait(true);
            StatusMessage = ok
                ? $"Đã bắt đầu sản xuất PO {order.OrderNo}."
                : $"Không thể bắt đầu PO {order.OrderNo} " +
                  "(đã có PO đang chạy, PO đã kết thúc, hoặc PO chưa được cào về máy).";

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
            FilteredOrders.Clear();

            foreach (var summary in summaries)
            {
                var row = new ProductionOrderRowViewModel(summary);
                Orders.Add(row);

                if (row.CanRun)
                {
                    SelectableOrders.Add(row);
                }
            }

            // Giữ lại PO đang chọn nếu vẫn còn hợp lệ.
            SelectedOrder = SelectableOrders.FirstOrDefault(
                p => string.Equals(p.OrderNo, previous, StringComparison.OrdinalIgnoreCase))
                ?? SelectableOrders.FirstOrDefault();

            ApplyFilter();
            OnPropertyChanged(nameof(IsEmpty));
            OnPropertyChanged(nameof(HasNoSearchResults));
            OnPropertyChanged(nameof(OrderCountText));
        });
    }

    /// <summary>
    /// Lọc danh sách PO theo mã PO, trạng thái và các trường mở rộng
    /// (tên sản phẩm, khách hàng...) nếu server có cung cấp.
    /// </summary>
    partial void OnSearchTextChanged(string value)
    {
        ApplyFilter();
        OnPropertyChanged(nameof(OrderCountText));
    }

    /// <summary>Áp dụng từ khoá tìm kiếm cho danh sách tra cứu.</summary>
    private void ApplyFilter()
    {
        var keyword = SearchText?.Trim() ?? string.Empty;

        FilteredOrders.Clear();
        foreach (var row in Orders)
        {
            if (keyword.Length == 0 || row.Matches(keyword))
            {
                FilteredOrders.Add(row);
            }
        }

        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(HasNoSearchResults));
        OnPropertyChanged(nameof(OrderCountText));
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
    /// MES đẩy PO xuống máy (HTTP POST /api/po) -> làm mới danh sách và
    /// hiện thông báo cho người dùng.
    ///
    /// <para>
    /// Event phát từ luồng listener (nền) -> dispatch về luồng UI trước khi
    /// đụng tới ObservableCollection.
    /// </para>
    /// </summary>
    private void OnPoReceivedFromServer(object? sender, string poName)
    {
        Dispatcher.UIThread.Post(() =>
        {
            _ = ReloadAfterRemotePoAsync(poName);
        });
    }

    /// <summary>
    /// Tải lại danh sách PO sau khi nhận PO từ MES.
    /// </summary>
    private async Task ReloadAfterRemotePoAsync(string poName)
    {
        try
        {
            await ReloadLocalAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            StatusMessage = "Nhận PO từ MES xong nhưng không tải lại được danh sách: " + ex.Message;
            IsStatusError = true;
            return;
        }

        StatusMessage = $"Đã nhận PO {poName} từ MES.";
        IsStatusError = false;

        ListChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _po.Changed -= OnPoChanged;
        _poServer.PoReceived -= OnPoReceivedFromServer;
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

    /// <summary>Số lượng kế hoạch của PO.</summary>
    public int OrderQty => _summary.OrderQty;

    /// <summary>Số mã đã quét.</summary>
    public int ScannedCount => _summary.ScannedCount;

    /// <summary>Tiến độ trên số mã thực nhận; lượng kế hoạch vẫn ở OrderQty.</summary>
    public string ProgressText => $"{ScannedCount} / {_summary.TotalCodes}";

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

    /// <summary>
    /// True khi PO còn được phép bắt đầu sản xuất.
    /// PO đã huỷ hoặc đã hoàn tất chỉ dùng để tra cứu.
    /// </summary>
    public bool CanRun => !IsCancelled && !IsCompleted;

    /// <summary>
    /// Kiểm tra PO có khớp từ khoá tìm kiếm hay không.
    /// Tìm trong mã PO, trạng thái và các trường mở rộng do server gửi.
    /// </summary>
    public bool Matches(string keyword)
    {
        if (string.IsNullOrWhiteSpace(keyword)) return true;

        if (OrderNo.Contains(keyword, StringComparison.OrdinalIgnoreCase)
            || StatusText.Contains(keyword, StringComparison.OrdinalIgnoreCase)
            || ProgressText.Contains(keyword, StringComparison.OrdinalIgnoreCase)
            || CreatedText.Contains(keyword, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return AdditionalValues.Any(value =>
            value.Contains(keyword, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Các giá trị trường mở rộng của PO (tên sản phẩm, khách hàng...).</summary>
    public IEnumerable<string> AdditionalValues =>
        _summary.Order.Additional?.Values ?? Enumerable.Empty<string>();

    /// <summary>Nạp lại thông tin từ service (khi PO đổi trạng thái).</summary>
    public void Refresh()
    {
        OnPropertyChanged(nameof(ScannedCount));
        OnPropertyChanged(nameof(ProgressText));
    }
}
