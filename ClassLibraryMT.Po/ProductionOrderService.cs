using Microsoft.Extensions.Logging;

namespace ClassLibraryMT.Po;

/// <summary>
/// Kết quả kiểm tra một mã quét so với PO đang chạy.
/// </summary>
public enum PoCodeValidation
{
    /// <summary>Mã hợp lệ, chưa quét lần nào trong PO này.</summary>
    Valid = 0,

    /// <summary>Chưa bắt đầu sản xuất (không có PO chạy).</summary>
    NoProductionOrder = 1,

    /// <summary>Mã không thuộc tập mã hợp lệ của PO đang chạy.</summary>
    InvalidForOrder = 2,

    /// <summary>Mã đã quét trong PO này rồi (trùng).</summary>
    Duplicate = 3,

    /// <summary>
    /// Máy đã bị dừng vì phát hiện mã đã từng xuất hiện trong lịch sử quét.
    ///
    /// <para>
    /// Trạng thái này chỉ xảy ra khi cơ chế chống trùng lịch sử toàn cục phát
    /// hiện một mã của PO đang chạy đã từng được quét trong hệ thống → hệ
    /// thống dừng máy, không ghi kết quả xuống PLC nữa.
    /// </para>
    /// </summary>
    Halted = 4,
}

/// <summary>
/// Kết quả cào dữ liệu PO từ server xuống local.
/// </summary>
/// <param name="PoCount">Tổng số PO trên server.</param>
/// <param name="NewPoCount">Số PO mới (chưa có trong DB local).</param>
/// <param name="NewCodeCount">Số mã mới được thêm vào DB local.</param>
public sealed record PoSyncResult(int PoCount, int NewPoCount, int NewCodeCount);

/// <summary>
/// Điều phối toàn bộ luồng quản lý Production Order.
///
/// <para>
/// <b>Mô hình local-first:</b> server chỉ là nguồn để <i>cào</i> (sync một
/// chiều vào máy). Mọi lần đọc danh sách PO cho UI đều đến từ DB local
/// (<c>logs/po/{yyyy-MM}/{orderNo}.db</c>).
/// </para>
///
/// <para>
/// Trách nhiệm:
/// <list type="bullet">
/// <item>Cào PO + tập mã từ server, merge vào DB local (không ghi đè mã cũ).</item>
/// <item>Đọc danh sách PO từ DB local.</item>
/// <item>Bắt đầu / hoàn tất / huỷ sản xuất.</item>
/// <item>Kiểm tra mã quét so với tập mã của PO đang chạy.</item>
/// <item>Gửi mã kích hoạt lên server, kèm kiểm tra trùng lịch sử toàn cục.</item>
/// </list>
/// </para>
/// </summary>
public sealed class ProductionOrderService
{
    private readonly IPoClient _client;
    private readonly PoStore _store;
    private readonly PoHistoryStore _history;
    private readonly PoCodeCache _cache;
    private readonly IPoScanHistoryReader? _scanLog;
    private readonly ILogger? _logger;

    /// <summary>Lock bảo vệ trạng thái PO đang chạy (đọc/ghi từ nhiều luồng).</summary>
    private readonly Lock _gate = new();

    /// <summary>Cờ chống gọi hoàn tất lồng nhau (auto-complete 100%).</summary>
    private int _completing;

    /// <summary>Cờ dừng máy do phát hiện mã đã từng xuất hiện trong lịch sử.</summary>
    private volatile bool _halted;

    private ProductionOrder? _current;
    private ProductionOrderStatus _currentStatus = ProductionOrderStatus.Available;

    /// <summary>
    /// Khởi tạo service.
    /// </summary>
    public ProductionOrderService(
        IPoClient client,
        PoStore store,
        PoHistoryStore history,
        PoCodeCache cache,
        IPoScanHistoryReader? scanLog = null,
        ILogger<ProductionOrderService>? logger = null)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _history = history ?? throw new ArgumentNullException(nameof(history));
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        _scanLog = scanLog;
        _logger = logger;
    }

    /// <summary>Event phát khi PO đang chạy / trạng thái thay đổi.</summary>
    public event EventHandler? Changed;

    /// <summary>
    /// Event phát khi phát hiện mã đã từng xuất hiện trong lịch sử quét.
    /// Tham số là danh sách serial bị trùng.
    /// </summary>
    public event EventHandler<IReadOnlyList<string>>? HistoryConflict;

    /// <summary>PO hiện tại đang chạy (null nếu chưa bắt đầu sản xuất).</summary>
    public ProductionOrder? CurrentOrder
    {
        get
        {
            lock (_gate)
                return _current;
        }
    }

    /// <summary>Trạng thái hiện tại của PO.</summary>
    public ProductionOrderStatus CurrentStatus
    {
        get
        {
            lock (_gate)
                return _currentStatus;
        }
    }

    /// <summary>True khi đang có PO chạy (camera được phép ghi kết quả).</summary>
    public bool IsProductionRunning
    {
        get
        {
            lock (_gate)
                return _current is not null && _currentStatus == ProductionOrderStatus.Running;
        }
    }

    /// <summary>
    /// True khi máy đã bị dừng do phát hiện mã trùng lịch sử.
    /// </summary>
    public bool IsHalted => _halted;

    /// <summary>Số mã đã quét của PO đang chạy.</summary>
    public int ScannedCount => _cache.ScannedCount;

    /// <summary>Tổng số mã của PO đang chạy.</summary>
    public int TotalCount => _cache.ValidCount;

    /// <summary>Phần trăm đã quét của PO đang chạy (0-100).</summary>
    public double ProgressPercent =>
        TotalCount > 0 ? Math.Round(ScannedCount * 100.0 / TotalCount, 1) : 0;

    /// <summary>
    /// Cào (đồng bộ) danh sách PO + tập mã từ server xuống DB local.
    ///
    /// <para>
    /// Có thể gọi nhiều lần: PO mới được thêm vào, mã mới được thêm vào, còn mã
    /// cũ và lịch sử đã quét được giữ nguyên (<c>INSERT OR IGNORE</c>).
    /// </para>
    /// </summary>
    public async Task<PoSyncResult> SyncFromServerAsync(CancellationToken ct = default)
    {
        var orders = await _client.GetOrdersAsync(includeCompleted: true, ct).ConfigureAwait(false);

        var newPoCount = 0;
        var newCodeCount = 0;

        foreach (var order in orders)
        {
            var existed = _store.FindExistingDbPath(order.OrderNo) is not null;

            var codes = await _client.GetCodesAsync(order.OrderNo, ct).ConfigureAwait(false);

            var before = await CountCodesAsync(order, ct).ConfigureAwait(false);
            await _store.EnsurePoDbAsync(order, codes, ct).ConfigureAwait(false);
            var after = await CountCodesAsync(order, ct).ConfigureAwait(false);

            if (!existed)
            {
                newPoCount++;
            }

            if (after > before)
            {
                newCodeCount += after - before;
            }
        }

        _logger?.LogInformation(
            "Cào PO từ server: {PoCount} PO ({NewPo} mới, {NewCode} mã mới).",
            orders.Count, newPoCount, newCodeCount);

        RaiseChanged();
        return new PoSyncResult(orders.Count, newPoCount, newCodeCount);
    }

    /// <summary>
    /// Đọc danh sách PO từ DB local (không gọi mạng).
    /// </summary>
    public async Task<IReadOnlyList<ProductionOrderSummary>> GetLocalOrdersAsync(
        CancellationToken ct = default)
    {
        var locals = await _store.ListLocalOrdersAsync(ct).ConfigureAwait(false);
        var result = new List<ProductionOrderSummary>(locals.Count);

        foreach (var local in locals)
        {
            // PO đang chạy: lấy số liệu trực tiếp từ cache RAM (chính xác nhất).
            var running = CurrentOrder;
            if (running is not null
                && string.Equals(
                    running.OrderNo, local.OrderNo, StringComparison.OrdinalIgnoreCase))
            {
                result.Add(new ProductionOrderSummary(
                    running, ScannedCount, CurrentStatus));
                continue;
            }

            result.Add(new ProductionOrderSummary(
                new ProductionOrder
                {
                    OrderNo = local.OrderNo,
                    OrderQty = local.OrderQty,
                    CreatedUtc = local.CreatedUtc,
                },
                local.ScannedCodes,
                local.Status));
        }

        return result;
    }

    /// <summary>
    /// Bắt đầu sản xuất cho một PO: nạp tập mã từ DB local vào cache RAM.
    /// </summary>
    /// <remarks>
    /// Không gọi mạng — PO phải đã được cào về local trước đó.
    /// </remarks>
    public async Task<bool> StartProductionAsync(
        string orderNo,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(orderNo))
            return false;

        if (_halted)
        {
            _logger?.LogWarning(
                "Máy đang dừng, không thể bắt đầu PO {OrderNo}.", orderNo);
            return false;
        }

        lock (_gate)
        {
            if (_current is not null && _currentStatus == ProductionOrderStatus.Running)
            {
                _logger?.LogWarning(
                    "PO {Current} đang chạy, không thể bắt đầu PO {New}.",
                    _current.OrderNo, orderNo);
                return false;
            }
        }

        // Tìm PO trong DB local (đường dẫn file + CreatedUtc đã lưu sẵn).
        var local = await FindLocalOrderAsync(orderNo, ct).ConfigureAwait(false);
        if (local is null)
        {
            _logger?.LogWarning(
                "PO {OrderNo} chưa có trong DB local, hãy cào PO từ server trước.", orderNo);
            return false;
        }

        // PO đã bị huỷ → không được chạy lại.
        if (local.IsCancelled)
        {
            _logger?.LogWarning(
                "PO {OrderNo} đã bị huỷ, không được chạy lại.", orderNo);
            return false;
        }

        // Nạp tập mã từ file DB local vào cache RAM.
        var codes = await _store
            .GetCodesWithFlagsAsync(orderNo, local.CreatedUtc, ct)
            .ConfigureAwait(false);

        if (codes.Count == 0)
        {
            _logger?.LogWarning("PO {OrderNo} không có mã nào, không thể bắt đầu.", orderNo);
            return false;
        }

        var order = new ProductionOrder
        {
            OrderNo = orderNo,
            OrderQty = codes.Count,
            CreatedUtc = local.CreatedUtc,
        };

        _cache.LoadOrder(orderNo, codes);

        await _store.SetMetaAsync(
            orderNo, local.CreatedUtc, PoStore.MetaStatus,
            ProductionOrderStatus.Running.ToString(), ct).ConfigureAwait(false);
        await _store.SetMetaAsync(
            orderNo, local.CreatedUtc, PoStore.MetaStartedUtc,
            DateTime.UtcNow.ToString("O"), ct).ConfigureAwait(false);

        await _history
            .StartRunAsync(orderNo, codes.Count, local.CreatedUtc, ct)
            .ConfigureAwait(false);

        lock (_gate)
        {
            _current = order;
            _currentStatus = ProductionOrderStatus.Running;
        }

        _logger?.LogInformation(
            "Bắt đầu sản xuất PO {OrderNo} với {Count} mã.",
            orderNo, codes.Count);

        RaiseChanged();
        return true;
    }

    /// <summary>
    /// Hoàn tất sản xuất: giải phóng khóa PO, đánh dấu Completed.
    /// </summary>
    /// <remarks>
    /// Idempotent — gọi nhiều lần cũng không sao.
    /// </remarks>
    public async Task<bool> CompleteProductionAsync(CancellationToken ct = default)
    {
        ProductionOrder? order;
        lock (_gate)
        {
            order = _current;
            if (order is null)
                return false;

            _currentStatus = ProductionOrderStatus.Completed;
        }

        await _store.SetMetaAsync(
            order.OrderNo, order.CreatedUtc, PoStore.MetaStatus,
            ProductionOrderStatus.Completed.ToString(), ct).ConfigureAwait(false);
        await _store.SetMetaAsync(
            order.OrderNo, order.CreatedUtc, PoStore.MetaCompletedUtc,
            DateTime.UtcNow.ToString("O"), ct).ConfigureAwait(false);

        await _history.CompleteRunAsync(order.OrderNo, ct).ConfigureAwait(false);

        lock (_gate)
        {
            _current = null;
            _currentStatus = ProductionOrderStatus.Available;
        }

        // Giải phóng RAM: chỉ giữ cache của PO đang chạy.
        _cache.Clear();

        _logger?.LogInformation("Hoàn tất sản xuất PO {OrderNo}.", order.OrderNo);

        RaiseChanged();
        return true;
    }

    /// <summary>
    /// Huỷ một PO: dừng sản xuất và đánh dấu đã huỷ (không được chạy lại).
    /// </summary>
    /// <param name="orderNo">
    /// Mã PO. Nếu rỗng thì huỷ PO đang chạy.
    /// </param>
    /// <param name="ct">Token hủy.</param>
    public async Task<bool> CancelProductionAsync(
        string? orderNo = null,
        CancellationToken ct = default)
    {
        ProductionOrder? order;
        lock (_gate)
        {
            order = _current;

            if (string.IsNullOrWhiteSpace(orderNo))
            {
                // Huỷ PO đang chạy.
                if (order is null)
                    return false;
            }
            else
            {
                // Huỷ một PO cụ thể (kể cả khi không đang chạy).
                if (order is not null
                    && string.Equals(
                        order.OrderNo, orderNo, StringComparison.OrdinalIgnoreCase))
                {
                    // Dùng PO đang chạy.
                }
                else
                {
                    order = null;
                }
            }
        }

        // Xác định PO cần huỷ (kể cả khi không đang chạy).
        var target = order;
        if (target is null)
        {
            var local = await FindLocalOrderAsync(orderNo!, ct).ConfigureAwait(false);
            if (local is null)
                return false;

            target = new ProductionOrder
            {
                OrderNo = local.OrderNo,
                OrderQty = local.OrderQty,
                CreatedUtc = local.CreatedUtc,
            };
        }

        await _store.MarkCancelledAsync(target.OrderNo, target.CreatedUtc, ct)
            .ConfigureAwait(false);
        await _history.CancelRunAsync(target.OrderNo, ct).ConfigureAwait(false);

        lock (_gate)
        {
            if (_current is not null
                && string.Equals(
                    _current.OrderNo, target.OrderNo, StringComparison.OrdinalIgnoreCase))
            {
                _current = null;
                _currentStatus = ProductionOrderStatus.Available;
            }
        }

        _cache.Clear();

        _logger?.LogInformation("Đã huỷ PO {OrderNo}.", target.OrderNo);

        RaiseChanged();
        return true;
    }

    /// <summary>
    /// Khôi phục PO đang chạy từ lần trước (dùng lúc khởi động app).
    ///
    /// <para>
    /// Đọc lịch sử trong <c>po-history.db</c> để tìm lần chạy đang
    /// <c>Running</c>, nạp lại tập mã từ file DB PO vào cache RAM và đặt lại
    /// trạng thái. Camera sẽ tiếp tục ghi kết quả xuống PLC ngay.
    /// </para>
    /// </summary>
    public async Task<bool> RestoreRunningOrderAsync(CancellationToken ct = default)
    {
        var run = await _history.GetRunningRunAsync(ct).ConfigureAwait(false);
        if (run is null)
        {
            _logger?.LogInformation("Không có PO nào đang chạy để khôi phục.");
            return false;
        }

        var local = await FindLocalOrderAsync(run.OrderNo, ct).ConfigureAwait(false);
        if (local is null)
        {
            _logger?.LogWarning(
                "PO {OrderNo} đang chạy nhưng không có file DB local, không khôi phục được.",
                run.OrderNo);
            return false;
        }

        if (local.IsCancelled)
        {
            _logger?.LogInformation(
                "PO {OrderNo} đã bị huỷ, không khôi phục.", run.OrderNo);
            return false;
        }

        var codes = await _store
            .GetCodesWithFlagsAsync(run.OrderNo, local.CreatedUtc, ct)
            .ConfigureAwait(false);

        if (codes.Count == 0)
        {
            _logger?.LogWarning(
                "PO {OrderNo} không có mã nào, không khôi phục được.", run.OrderNo);
            return false;
        }

        _cache.LoadOrder(run.OrderNo, codes);

        lock (_gate)
        {
            _current = new ProductionOrder
            {
                OrderNo = run.OrderNo,
                OrderQty = codes.Count,
                CreatedUtc = local.CreatedUtc,
            };
            _currentStatus = ProductionOrderStatus.Running;
        }

        _logger?.LogInformation(
            "Khôi phục PO {OrderNo} đang chạy từ lần trước ({Scanned}/{Total} mã đã quét).",
            run.OrderNo, _cache.ScannedCount, _cache.ValidCount);

        RaiseChanged();
        return true;
    }

    /// <summary>
    /// Kiểm tra mã quét so với PO đang chạy.
    ///
    /// <para>
    /// - Chưa có PO chạy → <see cref="PoCodeValidation.NoProductionOrder"/>
    ///   (camera loại mã, KHÔNG ghi PLC).
    /// - Máy đang dừng → <see cref="PoCodeValidation.Halted"/>.
    /// - Mã không thuộc PO → <see cref="PoCodeValidation.InvalidForOrder"/> (Fail).
    /// - Mã đã quét trong PO → <see cref="PoCodeValidation.Duplicate"/> (Fail).
    /// </para>
    /// </summary>
    public PoCodeValidation ValidateCode(string gtin, string serial)
    {
        if (_halted)
        {
            return PoCodeValidation.Halted;
        }

        ProductionOrder? order;
        lock (_gate)
        {
            order = _current;
            if (order is null || _currentStatus != ProductionOrderStatus.Running)
                return PoCodeValidation.NoProductionOrder;
        }

        var code = BuildCodeKey(gtin, serial);

        if (_cache.IsScanned(code))
            return PoCodeValidation.Duplicate;

        if (!_cache.Contains(code))
            return PoCodeValidation.InvalidForOrder;

        return PoCodeValidation.Valid;
    }

    /// <summary>
    /// Đánh dấu mã đã quét hợp lệ (ghi vào cache RAM + DB PO).
    ///
    /// <para>
    /// Khi PO đã quét đủ 100% thì <b>tự động hoàn tất</b> — UI về trạng thái
    /// "chưa chọn PO".
    /// </para>
    /// </summary>
    public async Task MarkCodeScannedAsync(string gtin, string serial, CancellationToken ct = default)
    {
        ProductionOrder? order;
        lock (_gate)
        {
            order = _current;
        }

        if (order is null)
            return;

        var code = BuildCodeKey(gtin, serial);

        if (!_cache.MarkScanned(code))
        {
            // Mã đã được đánh dấu rồi (race giữa 2 luồng camera).
            return;
        }

        await _store
            .MarkScannedAsync(order.OrderNo, order.CreatedUtc, code, ct)
            .ConfigureAwait(false);

        // Tự động hoàn tất khi đủ 100%.
        var total = _cache.ValidCount;
        var scanned = _cache.ScannedCount;

        if (total > 0 && scanned >= total)
        {
            await TryAutoCompleteAsync(order, total, ct).ConfigureAwait(false);
        }

        RaiseChanged();
    }

    /// <summary>
    /// Gửi các mã chưa kích hoạt của PO đang chạy lên server.
    ///
    /// <para>
    /// Trước khi gửi, kiểm tra từng mã xem đã từng xuất hiện trong lịch sử quét
    /// của hệ thống hay chưa. Nếu có → <b>dừng máy</b> và huỷ PO.
    /// </para>
    /// </summary>
    /// <returns>Số mã đã kích hoạt thành công.</returns>
    public async Task<int> ActivatePendingAsync(CancellationToken ct = default)
    {
        ProductionOrder? order;
        lock (_gate)
        {
            order = _current;
            if (order is null || _currentStatus != ProductionOrderStatus.Running)
                return 0;
        }

        if (_halted)
        {
            return 0;
        }

        var pending = await _store
            .GetPendingActivationCodesAsync(order.OrderNo, order.CreatedUtc, ct)
            .ConfigureAwait(false);

        if (pending.Count == 0)
            return 0;

        // ===== Kiểm tra trùng lịch sử toàn cục trước khi gửi lên server =====
        var conflicts = await FindHistoryConflictsAsync(order.OrderNo, pending, ct)
            .ConfigureAwait(false);

        if (conflicts.Count > 0)
        {
            await HaltAsync(order, conflicts, ct).ConfigureAwait(false);
            return 0;
        }

        var result = await _client
            .ActivateAsync(order.OrderNo, pending, ct)
            .ConfigureAwait(false);

        if (!result.Ok)
        {
            _logger?.LogWarning(
                "Gửi kích hoạt PO {OrderNo} thất bại: {Message}",
                order.OrderNo, result.Message);
            return 0;
        }

        await _store
            .MarkActivatedAsync(order.OrderNo, order.CreatedUtc, pending, ct)
            .ConfigureAwait(false);

        foreach (var code in pending)
        {
            _cache.MarkActivated(code);
        }

        _logger?.LogInformation(
            "Đã kích hoạt {Count} mã của PO {OrderNo}.", pending.Count, order.OrderNo);

        RaiseChanged();
        return result.Activated > 0 ? result.Activated : pending.Count;
    }

    /// <summary>
    /// Tìm các mã trong batch đã từng xuất hiện trong lịch sử quét của hệ thống.
    ///
    /// <para>
    /// Đọc tập serial đã thấy từ DB log quét rồi đối chiếu với batch mã chờ
    /// kích hoạt — tập serial đã thấy nhỏ hơn nhiều so với toàn bộ mã PO.
    /// </para>
    /// </summary>
    private async Task<IReadOnlyList<string>> FindHistoryConflictsAsync(
        string orderNo,
        IReadOnlyList<string> pending,
        CancellationToken ct)
    {
        var seen = await GetSeenSerialsAsync(orderNo, ct).ConfigureAwait(false);

        var conflicts = new List<string>();
        foreach (var serial in pending)
        {
            if (seen.Contains(serial))
            {
                conflicts.Add(serial);
            }
        }

        return conflicts;
    }

    /// <summary>
    /// Lấy tập serial đã từng xuất hiện trong lịch sử quét (trừ PO hiện tại).
    /// </summary>
    private async Task<HashSet<string>> GetSeenSerialsAsync(
        string orderNo,
        CancellationToken ct)
    {
        if (_scanLog is null)
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        var serials = await _scanLog.GetSeenSerialsAsync(orderNo, ct).ConfigureAwait(false);
        return new HashSet<string>(serials, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Dừng máy: đặt cờ halt, huỷ PO và phát sự kiện cảnh báo.
    /// </summary>
    private async Task HaltAsync(
        ProductionOrder order,
        IReadOnlyList<string> conflicts,
        CancellationToken ct)
    {
        _halted = true;

        _logger?.LogError(
            "Phát hiện {Count} mã đã từng quét trong lịch sử hệ thống: {Serials}. Đã dừng máy.",
            conflicts.Count, string.Join(", ", conflicts));

        await _store.MarkCancelledAsync(order.OrderNo, order.CreatedUtc, ct)
            .ConfigureAwait(false);
        await _history.CancelRunAsync(order.OrderNo, ct).ConfigureAwait(false);

        lock (_gate)
        {
            _current = null;
            _currentStatus = ProductionOrderStatus.Available;
        }

        _cache.Clear();

        HistoryConflict?.Invoke(this, conflicts);
        RaiseChanged();
    }

    /// <summary>
    /// Tự động hoàn tất PO khi đã quét đủ 100%.
    /// </summary>
    private async Task TryAutoCompleteAsync(
        ProductionOrder order,
        int total,
        CancellationToken ct)
    {
        // Chỉ một luồng xử lý hoàn tất tại một thời điểm.
        if (Interlocked.CompareExchange(ref _completing, 1, 0) != 0)
        {
            return;
        }

        try
        {
            // Kiểm tra lại: PO có còn đang chạy không (có thể đã bị huỷ).
            lock (_gate)
            {
                if (_current is null
                    || !string.Equals(
                        _current.OrderNo, order.OrderNo, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }
            }

            _logger?.LogInformation(
                "PO {OrderNo} đã quét đủ {Total} mã, tự động hoàn tất.",
                order.OrderNo, total);

            await CompleteProductionAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            Interlocked.Exchange(ref _completing, 0);
        }
    }

    /// <summary>
    /// Tìm PO trong DB local theo mã PO.
    /// </summary>
    private async Task<LocalPoInfo?> FindLocalOrderAsync(
        string orderNo,
        CancellationToken ct)
    {
        var locals = await _store.ListLocalOrdersAsync(ct).ConfigureAwait(false);
        return locals.FirstOrDefault(p =>
            string.Equals(p.OrderNo, orderNo, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Đếm số mã hiện có trong file DB của một PO (dùng khi cào để thống kê
    /// số mã mới).
    /// </summary>
    private async Task<int> CountCodesAsync(ProductionOrder order, CancellationToken ct)
    {
        var path = _store.FindExistingDbPath(order.OrderNo);
        if (path is null)
            return 0;

        var codes = await _store
            .GetCodesWithFlagsAsync(order.OrderNo, order.CreatedUtc, ct)
            .ConfigureAwait(false);

        return codes.Count;
    }

    /// <summary>Tạo khóa mã chuẩn (GTIN + serial) để so sánh.</summary>
    private static string BuildCodeKey(string gtin, string serial) => $"{gtin}|{serial}";

    /// <summary>Báo cho UI biết PO đã thay đổi.</summary>
    private void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);
}

/// <summary>
/// Tóm tắt một PO để hiển thị trên UI.
/// </summary>
/// <param name="Order">Thông tin PO gốc.</param>
/// <param name="ScannedCount">Số mã đã quét.</param>
/// <param name="Status">Trạng thái PO.</param>
public sealed record ProductionOrderSummary(
    ProductionOrder Order,
    int ScannedCount,
    ProductionOrderStatus Status)
{
    /// <summary>Mã PO.</summary>
    public string OrderNo => Order.OrderNo;

    /// <summary>Tổng số mã của PO.</summary>
    public int OrderQty => Order.OrderQty;

    /// <summary>Thời điểm tạo PO.</summary>
    public DateTime CreatedUtc => Order.CreatedUtc;

    /// <summary>True khi PO đã bị huỷ (không được chạy lại).</summary>
    public bool IsCancelled => Status == ProductionOrderStatus.Cancelled;
}
