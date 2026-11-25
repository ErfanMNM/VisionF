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
    /// Máy dừng vì kết quả PLC Pass hoặc lưu trạng thái PO chưa được xác nhận.
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
/// <item>Gửi các mã đã quét lên server, độc lập theo từng PO.</item>
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
    private readonly SemaphoreSlim _activationGate = new(1, 1);

    /// <summary>Cờ dừng máy do kết quả PLC hoặc trạng thái PO chưa được xác nhận.</summary>
    private volatile bool _halted;
    private volatile bool _updateMaintenance;

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

    public async Task<bool> TryEnterUpdateMaintenanceAsync(CancellationToken ct)
    {
        using var mutationLock = await _store.AcquireMutationLockAsync(ct).ConfigureAwait(false);
        if (_updateMaintenance || IsProductionRunning ||
            await _store.GetUnfinishedOrderNoAsync(_history, ct).ConfigureAwait(false) is not null)
            return false;

        _updateMaintenance = true;
        return true;
    }

    public void LeaveUpdateMaintenance() => _updateMaintenance = false;

    // Chỉ gọi trước khi khởi động các worker để kiểm tra sức khỏe bản mới.
    public void HoldForUpdateHealthCheck() => _updateMaintenance = true;

    /// <summary>
    /// True khi trạng thái PLC Pass hoặc PO cần được đối soát trước khi chạy tiếp.
    /// </summary>
    public bool IsHalted => _halted || _cache.HasPendingPass;

    /// <summary>
    /// PO vừa kết thúc (đã hoàn tất hoặc đã huỷ) — giữ lại để UI hiển thị
    /// thông tin lần chạy vừa rồi thay vì trống trơn.
    /// </summary>
    public ProductionOrder? LastFinishedOrder { get; private set; }

    /// <summary>Trạng thái của <see cref="LastFinishedOrder"/>.</summary>
    public ProductionOrderStatus LastFinishedStatus { get; private set; } =
        ProductionOrderStatus.Available;

    /// <summary>Số mã đã quét của PO vừa kết thúc.</summary>
    public int LastFinishedScannedCount { get; private set; }

    /// <summary>Tổng số mã của PO vừa kết thúc.</summary>
    public int LastFinishedTotalCount { get; private set; }

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
        using var mutationLock = await _store.AcquireMutationLockAsync(ct).ConfigureAwait(false);
        var unfinished = await _store.GetUnfinishedOrderNoAsync(_history, ct).ConfigureAwait(false);
        if (unfinished is not null)
            throw new PoServerException($"Không thể tải PO khi PO '{unfinished}' đang sản xuất dở dang.");

        var orders = await _client.GetOrdersAsync(includeCompleted: true, ct).ConfigureAwait(false);

        var newPoCount = 0;
        var newCodeCount = 0;

        foreach (var order in orders)
        {
            var local = await FindLocalOrderAsync(order.OrderNo, ct).ConfigureAwait(false);
            if (local?.IsFinished == true)
                continue;
            var existed = local is not null;
            if (local is not null)
                order.CreatedUtc = local.CreatedUtc;

            var codes = await _client.GetCodesAsync(order.OrderNo, ct).ConfigureAwait(false);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            codes = codes.Where(code =>
            {
                var key = code.CodeContent;
                return key.Length > 0 && seen.Add(key);
            }).ToArray();
            if (codes.Count == 0)
                continue;

            order.OrderQty = (local?.TotalCodes ?? 0) + codes.Count;
            if (local is not null)
            {
                var additional = new Dictionary<string, string>(local.Additional, StringComparer.OrdinalIgnoreCase);
                foreach (var entry in order.Additional)
                    additional[entry.Key] = entry.Value;
                order.Additional = additional;
            }

            var before = await CountCodesAsync(order, ct).ConfigureAwait(false);
            await _store.EnsurePoDbAsync(order, codes, ct).ConfigureAwait(false);
            var after = await CountCodesAsync(order, ct).ConfigureAwait(false);
            // Repeated snapshots can include existing rows; metadata follows the
            // actual per-PO union rather than adding the snapshot size twice.
            await _store.SetMetaAsync(order.OrderNo, order.CreatedUtc,
                PoStore.MetaOrderQty, after.ToString(System.Globalization.CultureInfo.InvariantCulture), ct)
                .ConfigureAwait(false);

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
                    Additional = local.Additional,
                },
                local.ScannedCodes,
                local.Status,
                local.StartedUtc,
                local.CompletedUtc,
                local.CancelledUtc));
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

        using var mutationLock = await _store.AcquireMutationLockAsync(ct).ConfigureAwait(false);

        if (_updateMaintenance)
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

        var unfinished = await _store.GetUnfinishedOrderNoAsync(_history, ct).ConfigureAwait(false);
        if (unfinished is not null && !string.Equals(unfinished, orderNo, StringComparison.OrdinalIgnoreCase))
        {
            _logger?.LogWarning("PO {OrderNo} còn dở dang, không thể bắt đầu PO khác.", unfinished);
            return false;
        }

        // Tìm PO trong DB local (đường dẫn file + CreatedUtc đã lưu sẵn).
        var local = await FindLocalOrderAsync(orderNo, ct).ConfigureAwait(false);
        if (local is null)
        {
            _logger?.LogWarning(
                "PO {OrderNo} chưa có trong DB local, hãy cào PO từ server trước.", orderNo);
            return false;
        }

        var meta = await _store.GetMetaAsync(orderNo, local.CreatedUtc, ct).ConfigureAwait(false);
        if (meta.ContainsKey(PoStore.MetaHalted))
        {
            _halted = true;
            _logger?.LogCritical("PO {OrderNo} has a persisted production halt marker.", orderNo);
            return false;
        }

        // PO đã kết thúc vòng sản xuất (đã huỷ hoặc đã hoàn tất) → không được chạy lại.
        if (local.IsFinished)
        {
            _logger?.LogWarning(
                "PO {OrderNo} đã {Status}, không được chạy lại.", orderNo, local.Status);
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
        if (_cache.HasPendingPass || meta.ContainsKey(PoStore.MetaHalted))
        {
            _halted = true;
            _cache.Clear();
            _logger?.LogCritical("PO {OrderNo} has an unresolved PLC Pass; production start is blocked.", orderNo);
            return false;
        }

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

        await CompleteIfAlreadyProcessedUnderLockAsync(order).ConfigureAwait(false);
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
        => await CompleteProductionCoreAsync(expectedOrderNo: null, ct).ConfigureAwait(false);

    private async Task<bool> CompleteProductionCoreAsync(
        string? expectedOrderNo,
        CancellationToken ct)
    {
        using var mutationLock = await _store.AcquireMutationLockAsync(ct).ConfigureAwait(false);
        return await CompleteProductionUnderLockAsync(expectedOrderNo, ct).ConfigureAwait(false);
    }

    // Caller holds the shared lifecycle mutation lock.
    private async Task<bool> CompleteProductionUnderLockAsync(
        string? expectedOrderNo,
        CancellationToken ct)
    {
        ProductionOrder? order;
        lock (_gate)
        {
            order = _current;
            if (order is null || (expectedOrderNo is not null
                && !string.Equals(order.OrderNo, expectedOrderNo, StringComparison.OrdinalIgnoreCase)))
                return false;
        }

        var scannedCount = await _store
            .CountScannedAsync(order.OrderNo, order.CreatedUtc, ct).ConfigureAwait(false);
        var totalCount = await _store
            .CountCodesAsync(order.OrderNo, order.CreatedUtc, ct).ConfigureAwait(false);
        var poMarkedCompleted = false;
        try
        {
            await _store.MarkCompletedAsync(order.OrderNo, order.CreatedUtc, ct).ConfigureAwait(false);
            poMarkedCompleted = true;
            await _history.CompleteRunAsync(order.OrderNo, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            if (poMarkedCompleted)
            {
                _halted = true;
                await PersistHaltBestEffortAsync(order, ex).ConfigureAwait(false);
                RaiseChanged();
            }

            throw;
        }

        // Ghi nhớ PO vừa hoàn tất để UI vẫn hiện thông tin lần chạy cuối.
        LastFinishedOrder = order;
        LastFinishedStatus = ProductionOrderStatus.Completed;
        LastFinishedScannedCount = scannedCount;
        LastFinishedTotalCount = totalCount;

        lock (_gate)
        {
            _currentStatus = ProductionOrderStatus.Completed;
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
        using var mutationLock = await _store.AcquireMutationLockAsync(ct).ConfigureAwait(false);
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

        // Ghi nhớ PO vừa huỷ để UI vẫn hiện thông tin lần chạy cuối.
        LastFinishedOrder = target;
        LastFinishedStatus = ProductionOrderStatus.Cancelled;
        LastFinishedScannedCount = await _store
            .CountScannedAsync(target.OrderNo, target.CreatedUtc, ct).ConfigureAwait(false);
        LastFinishedTotalCount = await _store
            .CountCodesAsync(target.OrderNo, target.CreatedUtc, ct).ConfigureAwait(false);

        var clearActiveCache = false;
        lock (_gate)
        {
            if (_current is not null
                && string.Equals(
                    _current.OrderNo, target.OrderNo, StringComparison.OrdinalIgnoreCase))
            {
                _current = null;
                _currentStatus = ProductionOrderStatus.Available;
                clearActiveCache = true;
            }
        }

        if (clearActiveCache)
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
        using var mutationLock = await _store.AcquireMutationLockAsync(ct).ConfigureAwait(false);
        if (_updateMaintenance)
            return false;

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

        var meta = await _store.GetMetaAsync(run.OrderNo, local.CreatedUtc, ct).ConfigureAwait(false);
        if (meta.ContainsKey(PoStore.MetaHalted))
        {
            _halted = true;
            _logger?.LogCritical("PO {OrderNo} has a persisted production halt marker.", run.OrderNo);
        }

        if (local.IsFinished)
        {
            _logger?.LogInformation(
                "PO {OrderNo} đã {Status}, không khôi phục.", run.OrderNo, local.Status);
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
        if (_cache.HasPendingPass || meta.ContainsKey(PoStore.MetaHalted))
        {
            _halted = true;
            _logger?.LogCritical("PO {OrderNo} resumed with an unresolved PLC Pass; scans are halted.", run.OrderNo);
        }

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

        await CompleteIfAlreadyProcessedUnderLockAsync(CurrentOrder!).ConfigureAwait(false);
        RaiseChanged();
        return true;
    }

    private async Task CompleteIfAlreadyProcessedUnderLockAsync(ProductionOrder order)
    {
        if (IsHalted || _cache.ValidCount == 0 || _cache.ScannedCount < _cache.ValidCount)
            return;

        try
        {
            // Running state/history are durable now. Caller cancellation must not
            // turn this completion bookkeeping into a production halt.
            await CompleteProductionUnderLockAsync(order.OrderNo, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _halted = true;
            await PersistHaltBestEffortAsync(order, ex).ConfigureAwait(false);
            RaiseChanged();
            throw;
        }
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
    public PoCodeValidation ValidateCode(string codeContent)
    {
        if (_halted || _cache.HasPendingPass)
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

        if (_cache.IsScanned(codeContent) || _cache.IsActivated(codeContent))
            return PoCodeValidation.Duplicate;

        if (!_cache.Contains(codeContent))
            return PoCodeValidation.InvalidForOrder;

        return PoCodeValidation.Valid;
    }

    /// <summary>Legacy compatibility overload for the old GTIN/serial caller.</summary>
    [Obsolete("Pass the complete CodeContent to ValidateCode.")]
    public PoCodeValidation ValidateCode(string gtin, string serial) =>
        ValidateCode(PoStore.BuildCodeKey(gtin, serial));

    /// <summary>
    /// Đánh dấu mã đã quét hợp lệ (ghi vào cache RAM + DB PO).
    ///
    /// <para>
    /// Khi PO đã quét đủ 100% thì <b>tự động hoàn tất</b> — UI về trạng thái
    /// "chưa chọn PO".
    /// </para>
    /// </summary>
    public async Task<PoScanResult> ProcessCodeAsync(
        string codeContent,
        Func<CancellationToken, Task<bool>> sendPass,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(codeContent);
        ArgumentNullException.ThrowIfNull(sendPass);

        var mutationLock = await _store.AcquireMutationLockAsync(ct).ConfigureAwait(false);
        ProductionOrder? order = null;
        int total;
        int scanned;
        PoScanResult outcome;
        try
        {
            lock (_gate)
            {
                order = _current;
            }

            var validation = ValidateCode(codeContent);
            if (validation != PoCodeValidation.Valid || order is null)
                return new PoScanResult(validation, false, false, order?.OrderNo ?? string.Empty);

            ct.ThrowIfCancellationRequested();
            var pending = await _store
                .BeginPendingPassAsync(order.OrderNo, order.CreatedUtc, codeContent, ct)
                .ConfigureAwait(false);
            if (!pending)
            {
                var latestValidation = _cache.IsScanned(codeContent) || _cache.IsActivated(codeContent)
                    ? PoCodeValidation.Duplicate
                    : _cache.Contains(codeContent)
                        ? PoCodeValidation.Halted
                        : PoCodeValidation.InvalidForOrder;
                if (latestValidation == PoCodeValidation.Halted)
                    _halted = true;
                return new PoScanResult(latestValidation, false, false, order.OrderNo);
            }

            if (!_cache.MarkPassPending(codeContent))
            {
                await _store.ClearPendingPassAsync(
                    order.OrderNo, order.CreatedUtc, codeContent, CancellationToken.None).ConfigureAwait(false);
                return new PoScanResult(PoCodeValidation.Duplicate, false, false, order.OrderNo);
            }

            if (ct.IsCancellationRequested)
            {
                var cleared = await _store.ClearPendingPassAsync(
                    order.OrderNo, order.CreatedUtc, codeContent, CancellationToken.None).ConfigureAwait(false);
                _cache.ClearPassPending(codeContent);
                if (!cleared)
                {
                    _halted = true;
                    var clearException = new InvalidOperationException(
                        "Could not clear the cancelled PLC Pass intent.");
                    await PersistHaltBestEffortAsync(order, clearException).ConfigureAwait(false);
                    RaiseChanged();
                    throw new PoScanProcessingException(
                        "Cancellation arrived before PLC Pass, but its durable intent could not be cleared. Production was halted.",
                        new PoScanResult(PoCodeValidation.Halted, false, false, order.OrderNo), clearException);
                }
                ct.ThrowIfCancellationRequested();
            }
            bool plcWritten;
            try
            {
                plcWritten = await sendPass(ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _halted = true;
                await PersistHaltBestEffortAsync(order, ex).ConfigureAwait(false);
                RaiseChanged();
                throw new PoScanProcessingException(
                    "PLC Pass outcome is uncertain. Production was halted and the pending code was quarantined.",
                    new PoScanResult(PoCodeValidation.Halted, null, false, order.OrderNo), ex);
            }

            if (!plcWritten)
            {
                try
                {
                    var cleared = await _store.ClearPendingPassAsync(
                        order.OrderNo, order.CreatedUtc, codeContent, CancellationToken.None).ConfigureAwait(false);
                    if (!cleared)
                        throw new InvalidOperationException("Could not clear the failed PLC Pass intent.");
                    _cache.ClearPassPending(codeContent);
                }
                catch (Exception ex)
                {
                    _halted = true;
                    await PersistHaltBestEffortAsync(order, ex).ConfigureAwait(false);
                    RaiseChanged();
                    throw new PoScanProcessingException(
                        "PLC Pass failed, but its durable intent could not be cleared. Production was halted.",
                        new PoScanResult(PoCodeValidation.Halted, false, false, order.OrderNo), ex);
                }

                ct.ThrowIfCancellationRequested();
                return new PoScanResult(PoCodeValidation.Valid, false, false, order.OrderNo);
            }

            try
            {
                var committed = await _store.CommitPendingPassAsync(
                    order.OrderNo, order.CreatedUtc, codeContent, CancellationToken.None).ConfigureAwait(false);
                if (!committed)
                    throw new InvalidOperationException("The pending PO code could not be committed as scanned.");
            }
            catch (Exception ex)
            {
                _halted = true;
                await PersistHaltBestEffortAsync(order, ex).ConfigureAwait(false);
                RaiseChanged();
                throw new PoScanProcessingException(
                    "PLC Pass was written but PO scan state could not be committed. Production was halted to prevent replay.",
                    new PoScanResult(PoCodeValidation.Halted, true, false, order.OrderNo), ex);
            }

            _cache.MarkScanned(codeContent);
            total = _cache.ValidCount;
            scanned = _cache.ScannedCount;
            outcome = new PoScanResult(PoCodeValidation.Valid, true, true, order.OrderNo);
        }
        finally
        {
            mutationLock.Dispose();
        }

        if (total > 0 && scanned >= total)
        {
            try
            {
                await TryAutoCompleteAsync(order, total, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _halted = true;
                await PersistHaltBestEffortAsync(order, ex).ConfigureAwait(false);
                outcome = outcome with
                {
                    ProductionHalted = true,
                    Message = "Pass was committed, but PO completion failed; production was halted.",
                };
                RaiseChanged();
            }
        }

        RaiseChanged();
        return outcome;
    }

    [Obsolete("Use ProcessCodeAsync so only a confirmed PLC Pass consumes the code.")]
    public async Task MarkCodeScannedAsync(string codeContent, CancellationToken ct = default)
    {
        using var mutationLock = await _store.AcquireMutationLockAsync(ct).ConfigureAwait(false);
        var order = CurrentOrder;
        if (order is null)
            throw new InvalidOperationException("Cannot mark a PO code without a running order.");
        if (!string.Equals(_cache.OrderNo, order.OrderNo, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The active PO cache no longer matches the current order.");

        var validation = ValidateCode(codeContent);
        if (validation != PoCodeValidation.Valid)
            throw new InvalidOperationException($"Cannot mark code as scanned: {validation}.");

        var committed = await _store.MarkScannedAsync(order.OrderNo, order.CreatedUtc, codeContent, ct)
            .ConfigureAwait(false);
        if (!committed)
            throw new InvalidOperationException("The PO code was not committed as scanned.");

        _cache.MarkScanned(codeContent);
        var total = _cache.ValidCount;
        var scanned = _cache.ScannedCount;
        mutationLock.Dispose();

        if (total > 0 && scanned >= total)
            await TryAutoCompleteAsync(order, total, ct).ConfigureAwait(false);
        RaiseChanged();
    }

    private async Task PersistHaltBestEffortAsync(ProductionOrder order, Exception cause)
    {
        try
        {
            await _store.SetMetaAsync(
                order.OrderNo, order.CreatedUtc, PoStore.MetaHalted, "true", CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception persistException)
        {
            _logger?.LogCritical(
                persistException,
                "Could not persist the PO halt marker for {OrderNo}; the pending pass marker remains the recovery guard.",
                order.OrderNo);
        }

        _logger?.LogCritical(cause, "PO {OrderNo} halted after an unresolved PLC Pass.", order.OrderNo);
    }

    /// <summary>Legacy compatibility overload for the old GTIN/serial caller.</summary>
    [Obsolete("Pass the complete CodeContent to MarkCodeScannedAsync.")]
    public Task MarkCodeScannedAsync(string gtin, string serial, CancellationToken ct = default) =>
        MarkCodeScannedAsync(PoStore.BuildCodeKey(gtin, serial), ct);

    /// <summary>
    /// Gửi các mã đã quét nhưng chưa kích hoạt từ mọi PO local lên server.
    /// </summary>
    /// <returns>Số mã đã kích hoạt thành công.</returns>
    public async Task<int> ActivatePendingAsync(CancellationToken ct = default)
    {
        await _activationGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var activatedCount = 0;
            var localOrders = await _store.ListLocalOrdersAsync(ct).ConfigureAwait(false);
            foreach (var local in localOrders)
            {
                ct.ThrowIfCancellationRequested();
                var pending = await _store
                    .GetPendingActivationCodesAsync(local.OrderNo, local.CreatedUtc, ct)
                    .ConfigureAwait(false);
                if (pending.Count == 0)
                    continue;

                PoActivateResult result;
                try
                {
                    result = await _client
                        .ActivateAsync(local.OrderNo, pending, ct)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex,
                        "Kích hoạt PO {OrderNo} thất bại; mã sẽ được thử lại ở chu kỳ sau.", local.OrderNo);
                    continue;
                }

                if (!result.Ok)
                {
                    _logger?.LogWarning(
                        "Gửi kích hoạt PO {OrderNo} thất bại: {Message}", local.OrderNo, result.Message);
                    continue;
                }

                // Persist the acknowledgement against its source PO. A network await
                // may have switched the active cache to another order.
                var mutationLock = await _store.AcquireMutationLockAsync(CancellationToken.None)
                    .ConfigureAwait(false);
                try
                {
                    await _store
                        .MarkActivatedAsync(local.OrderNo, local.CreatedUtc, pending, CancellationToken.None)
                        .ConfigureAwait(false);

                    var cacheStillMatches = false;
                    lock (_gate)
                    {
                        cacheStillMatches = _current is not null
                            && string.Equals(_current.OrderNo, local.OrderNo, StringComparison.OrdinalIgnoreCase)
                            && string.Equals(_cache.OrderNo, local.OrderNo, StringComparison.OrdinalIgnoreCase);
                    }

                    if (cacheStillMatches)
                    {
                        foreach (var code in pending)
                            _cache.MarkActivated(code);
                    }
                }
                finally
                {
                    mutationLock.Dispose();
                }

                var count = result.Activated > 0 ? result.Activated : pending.Count;
                activatedCount += count;
                _logger?.LogInformation(
                    "Đã kích hoạt {Count} mã của PO {OrderNo}.", count, local.OrderNo);
                RaiseChanged();
            }

            return activatedCount;
        }
        finally
        {
            _activationGate.Release();
        }
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
        var seen = await GetSeenCodeContentsAsync(orderNo, ct).ConfigureAwait(false);

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
    private async Task<HashSet<string>> GetSeenCodeContentsAsync(
        string orderNo,
        CancellationToken ct)
    {
        if (_scanLog is null)
        {
            return new HashSet<string>(StringComparer.Ordinal);
        }

        var codeContents = await _scanLog.GetSeenCodeContentsAsync(orderNo, ct).ConfigureAwait(false);
        return new HashSet<string>(codeContents, StringComparer.Ordinal);
    }

    /// <summary>
    /// Dừng máy: đặt cờ halt, huỷ PO và phát sự kiện cảnh báo.
    /// </summary>
    private async Task HaltAsync(
        ProductionOrder order,
        IReadOnlyList<string> conflicts,
        CancellationToken ct)
    {
        using var mutationLock = await _store.AcquireMutationLockAsync(ct).ConfigureAwait(false);
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
            _logger?.LogInformation(
                "PO {OrderNo} đã quét đủ {Total} mã, tự động hoàn tất.",
                order.OrderNo, total);

            await CompleteProductionCoreAsync(order.OrderNo, ct).ConfigureAwait(false);
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
        return await _store.CountCodesAsync(order.OrderNo, order.CreatedUtc, ct)
            .ConfigureAwait(false);
    }

    /// <summary>Báo cho UI biết PO đã thay đổi.</summary>
    private void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);
}

/// <summary>
/// Tóm tắt một PO để hiển thị trên UI.
/// </summary>
/// <param name="Order">Thông tin PO gốc.</param>
/// <param name="ScannedCount">Số mã đã quét.</param>
/// <param name="Status">Trạng thái PO.</param>
/// <param name="StartedUtc">Thời điểm bắt đầu (null nếu chưa bắt đầu).</param>
/// <param name="CompletedUtc">Thời điểm hoàn tất (null nếu chưa hoàn tất).</param>
/// <param name="CancelledUtc">Thời điểm huỷ (null nếu chưa huỷ).</param>
public sealed record ProductionOrderSummary(
    ProductionOrder Order,
    int ScannedCount,
    ProductionOrderStatus Status,
    DateTime? StartedUtc = null,
    DateTime? CompletedUtc = null,
    DateTime? CancelledUtc = null)
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
