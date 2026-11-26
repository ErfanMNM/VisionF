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
public sealed record PoSyncResult(int PoCount, int NewPoCount, int NewCodeCount)
{
    public IReadOnlyList<PoSyncOrderResult> Orders { get; init; } = [];
    public int AcceptedCount => Orders.Sum(o => o.AcceptedCount);
    public int PlannedCount => Orders.Sum(o => o.PlannedCount);
    public IReadOnlyList<PoCodeRejection> InvalidCodes => Orders.SelectMany(o => o.InvalidCodes).ToArray();
    public IReadOnlyList<string> DuplicateCodes => Orders.SelectMany(o => o.DuplicateCodes).ToArray();
    public bool HasValidationErrors => Orders.Any(o => o.ValidationError is not null || o.InvalidCodes.Count > 0);
}

/// <summary>Mã bị loại vẫn mang nguyên CodeContent đầu vào.</summary>
public sealed record PoCodeRejection(string CodeContent, string Reason);

/// <summary>Kết quả validation của một snapshot tải xuống, tách khỏi lỗi vận chuyển.</summary>
public sealed record PoSyncOrderResult(
    string OrderNo,
    int PlannedCount,
    int InputCount,
    int AcceptedCount,
    IReadOnlyList<PoCodeRejection> InvalidCodes,
    IReadOnlyList<string> DuplicateCodes,
    string? ValidationError = null);

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
/// <item>Chấp nhận mã nguyên tử trong RAM; coordinator lưu batch độc lập.</item>
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
    private bool _lifecycleTransition;
    private bool _completionPending;

    /// <summary>
    /// App-level barrier covering reserved scans, history, and PO outbox writes.
    /// It must not require the PO lifecycle mutex to apply a batch.
    /// </summary>
    public Func<CancellationToken, Task>? PersistenceBarrier { get; set; }

    /// <summary>The final accepted code is awaiting durable batch completion.</summary>
    public bool IsCompletionPending { get { lock (_gate) return _completionPending; } }

    /// <summary>Cờ dừng máy do kết quả PLC hoặc trạng thái PO chưa được xác nhận.</summary>
    private volatile bool _halted;
    private volatile bool _updateMaintenance;

    private ProductionOrder? _current;
    // Legacy run history may preserve caller casing; PO mutations always use
    // canonical identity from PoMeta, while run completion addresses its source.
    private string _currentRunOrderNo = string.Empty;
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

    /// <summary>A consistent detached snapshot for outbound eligibility decisions.</summary>
    public ProductionOrder? GetRunningOrderSnapshot()
    {
        lock (_gate)
        {
            if (_current is not { } order || _currentStatus != ProductionOrderStatus.Running)
                return null;
            return new ProductionOrder
            {
                OrderNo = order.OrderNo, CreatedUtc = order.CreatedUtc, OrderQty = order.OrderQty,
                Gtin = order.Gtin,
                Additional = new Dictionary<string, string>(order.Additional, order.Additional.Comparer),
            };
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
        var outcomes = new List<PoSyncOrderResult>();

        foreach (var order in orders)
        {
            var local = await FindLocalOrderAsync(order.OrderNo, ct).ConfigureAwait(false);
            if (local?.IsFinished == true)
                continue;
            var codes = await _client.GetCodesAsync(order.OrderNo, ct).ConfigureAwait(false);
            var invalid = new List<PoCodeRejection>();
            var duplicates = new List<string>();
            var accepted = new List<PoCode>();
            var metadataGtin = order.Gtin;
            if (string.IsNullOrEmpty(metadataGtin) && order.Additional is not null)
                metadataGtin = order.Additional.FirstOrDefault(e => string.Equals(e.Key,
                    PoStore.MetaGtin, StringComparison.OrdinalIgnoreCase)).Value;
            var validGtin = PoCodeParser.TryNormalizeGtin(metadataGtin, out var gtin);
            string? validationError = null;
            if (string.IsNullOrWhiteSpace(order.OrderNo))
                validationError = "PO thiếu orderNo.";
            else if (!validGtin)
                validationError = "PO phải có đúng một GTIN hợp lệ và đúng checksum.";
            // Snapshot download có thể chỉ chứa phần accepted trên server.
            // Planned vẫn là target, không bị hạ về số dòng thực nhận.
            else if (order.OrderQty <= 0 || codes.Count == 0 || codes.Count > order.OrderQty)
                validationError = "Số record tải xuống phải lớn hơn 0 và không vượt số lượng kế hoạch.";
            else if (local is not null && (local.Gtin.Length == 0
                || !string.Equals(local.Gtin, gtin, StringComparison.Ordinal)))
                validationError = "GTIN không khớp PO local hoặc PO legacy chưa xác định được một GTIN duy nhất.";

            if (validationError is not null)
            {
                outcomes.Add(new(order.OrderNo, order.OrderQty, codes.Count, 0, invalid, duplicates, validationError));
                continue;
            }

            var seen = new HashSet<string>(StringComparer.Ordinal);
            if (local is not null)
            {
                var existingCodes = await _store.GetCodesWithFlagsAsync(local.OrderNo, local.CreatedUtc, ct)
                    .ConfigureAwait(false);
                if (existingCodes.Any(c => !PoCodeParser.TryGetGtin(c.Key, out var existingGtin)
                    || !string.Equals(existingGtin, gtin, StringComparison.Ordinal)))
                {
                    outcomes.Add(new(order.OrderNo, order.OrderQty, codes.Count, 0, invalid, duplicates,
                        "PO legacy chứa mã sai cấu trúc hoặc trộn GTIN; không thể bổ sung tự động."));
                    continue;
                }
                seen.UnionWith(existingCodes.Select(c => c.Key));
            }
            foreach (var code in codes)
            {
                var raw = code.CodeContent ?? string.Empty;
                if (!PoCodeParser.TryGetGtin(raw, out var codeGtin))
                    invalid.Add(new(raw, "invalidStructure"));
                else if (!string.Equals(gtin, codeGtin, StringComparison.Ordinal))
                    invalid.Add(new(raw, "gtinMismatch"));
                else if (!seen.Add(raw))
                    duplicates.Add(raw);
                else
                    accepted.Add(code);
            }

            if (accepted.Count == 0 && duplicates.Count == 0)
            {
                outcomes.Add(new(order.OrderNo, order.OrderQty, codes.Count, 0, invalid, duplicates,
                    "Không có mã hợp lệ để nhận."));
                continue;
            }

            var additional = new Dictionary<string, string>(local?.Additional ?? [], StringComparer.OrdinalIgnoreCase);
            foreach (var entry in order.Additional ?? [])
                additional[entry.Key] = entry.Value;
            var saved = new ProductionOrder
            {
                OrderNo = order.OrderNo,
                OrderQty = Math.Max(local?.OrderQty ?? 0, order.OrderQty),
                Gtin = gtin,
                CreatedUtc = local?.CreatedUtc ?? order.CreatedUtc,
                Additional = additional,
            };
            var before = local?.TotalCodes ?? 0;
            await _store.EnsurePoDbAsync(saved, accepted, ct).ConfigureAwait(false);
            var after = await CountCodesAsync(saved, ct).ConfigureAwait(false);
            if (local is null) newPoCount++;
            newCodeCount += Math.Max(0, after - before);
            outcomes.Add(new(order.OrderNo, saved.OrderQty, codes.Count,
                Math.Max(0, after - before), invalid, duplicates));
        }

        _logger?.LogInformation(
            "Cào PO từ server: {PoCount} PO ({NewPo} mới, {NewCode} mã mới).",
            orders.Count, newPoCount, newCodeCount);

        RaiseChanged();
        return new PoSyncResult(orders.Count, newPoCount, newCodeCount) { Orders = outcomes };
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
                    running, ScannedCount, CurrentStatus) { TotalCodes = TotalCount });
                continue;
            }

            result.Add(new ProductionOrderSummary(
                new ProductionOrder
                {
                    OrderNo = local.OrderNo,
                    OrderQty = local.OrderQty,
                    Gtin = local.Gtin,
                    CreatedUtc = local.CreatedUtc,
                    Additional = local.Additional,
                },
                local.ScannedCodes,
                local.Status,
                local.StartedUtc,
                local.CompletedUtc,
                local.CancelledUtc) { TotalCodes = local.TotalCodes });
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

        // The immutable mutation must capture the persisted identity, including
        // its canonical casing, even when a UI lookup supplied another casing.
        orderNo = local.OrderNo;
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
            OrderQty = local.OrderQty,
            Gtin = local.Gtin,
            CreatedUtc = local.CreatedUtc,
            Additional = local.Additional,
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
            _currentRunOrderNo = order.OrderNo;
            _currentStatus = ProductionOrderStatus.Running;
            _completionPending = false;
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
        lock (_gate) _lifecycleTransition = true;
        try
        {
            if (PersistenceBarrier is { } barrier)
                await barrier(ct).ConfigureAwait(false);
            return await CompleteProductionUnderLockAsync(expectedOrderNo, ct).ConfigureAwait(false);
        }
        finally
        {
            lock (_gate) _lifecycleTransition = false;
        }
    }

    /// <summary>
    /// Called only after the coordinator has committed its PO batch and outbox.
    /// Never wait for the lifecycle mutex: a manual action may own it while
    /// waiting for a later persistence barrier on this same worker.
    /// </summary>
    public async Task<bool> CompletePendingAsync(CancellationToken ct = default)
    {
        lock (_gate)
            if (!_completionPending || _lifecycleTransition || _halted)
                return false;
        if (!_store.TryAcquireMutationLock(out var mutationLock))
            return false;
        using (mutationLock)
        {
            ProductionOrder? order;
            lock (_gate)
            {
                if (!_completionPending || _lifecycleTransition || _halted)
                    return false;
                order = _current;
                _lifecycleTransition = true;
            }
            try
            {
                // A final scan may have reached RAM while this worker was
                // flushing an earlier batch. Its reserved envelope can still
                // be waiting for PLC ACK; defer until its own batch is durable.
                if (order is not null && await _store.CountScannedAsync(order.OrderNo, order.CreatedUtc, ct)
                    .ConfigureAwait(false) < _cache.ScannedCount)
                    return false;
                return order is not null && await CompleteProductionUnderLockAsync(order.OrderNo, ct)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _halted = true;
                if (order is not null)
                    await PersistHaltBestEffortAsync(order, ex).ConfigureAwait(false);
                RaiseChanged();
                throw;
            }
            finally
            {
                lock (_gate) _lifecycleTransition = false;
            }
        }
    }

    // Caller holds the shared lifecycle mutation lock.
    private async Task<bool> CompleteProductionUnderLockAsync(
        string? expectedOrderNo,
        CancellationToken ct)
    {
        ProductionOrder? order;
        string runOrderNo;
        lock (_gate)
        {
            order = _current;
            if (order is null || (expectedOrderNo is not null
                && !string.Equals(order.OrderNo, expectedOrderNo, StringComparison.OrdinalIgnoreCase)))
                return false;
            runOrderNo = _currentRunOrderNo;
        }

        var scannedCount = await _store
            .CountScannedAsync(order.OrderNo, order.CreatedUtc, ct).ConfigureAwait(false);
        var totalCount = await _store
            .CountCodesAsync(order.OrderNo, order.CreatedUtc, ct).ConfigureAwait(false);
        // A caller cannot clear accepted RAM state before the final PO batch.
        if (scannedCount < _cache.ScannedCount)
            throw new IOException("PO completion is waiting for accepted scans to be persisted.");
        var poMarkedCompleted = false;
        try
        {
            await _store.MarkCompletedAsync(order.OrderNo, order.CreatedUtc, ct).ConfigureAwait(false);
            poMarkedCompleted = true;
            await _history.CompleteRunAsync(runOrderNo, ct).ConfigureAwait(false);
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
            _currentRunOrderNo = string.Empty;
            _currentStatus = ProductionOrderStatus.Available;
            _completionPending = false;
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
        lock (_gate) _lifecycleTransition = true;
        try
        {
            if (PersistenceBarrier is { } barrier)
                await barrier(ct).ConfigureAwait(false);
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
                    Gtin = local.Gtin,
                    CreatedUtc = local.CreatedUtc,
                    Additional = local.Additional,
                };
            }

            if (order is not null && await _store.CountScannedAsync(target.OrderNo, target.CreatedUtc, ct)
                .ConfigureAwait(false) < _cache.ScannedCount)
                throw new IOException("PO cancellation is waiting for accepted scans to be persisted.");

            await _store.MarkCancelledAsync(target.OrderNo, target.CreatedUtc, ct)
                .ConfigureAwait(false);
            string runOrderNo;
            lock (_gate) runOrderNo = order is not null ? _currentRunOrderNo : target.OrderNo;
            await _history.CancelRunAsync(runOrderNo, ct).ConfigureAwait(false);

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
                    _currentRunOrderNo = string.Empty;
                    _currentStatus = ProductionOrderStatus.Available;
                    _completionPending = false;
                    clearActiveCache = true;
                }
            }

            if (clearActiveCache)
                _cache.Clear();

            _logger?.LogInformation("Đã huỷ PO {OrderNo}.", target.OrderNo);

            RaiseChanged();
            return true;
        }
        finally
        {
            lock (_gate) _lifecycleTransition = false;
        }
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

        var meta = await _store.GetMetaAsync(local.OrderNo, local.CreatedUtc, ct).ConfigureAwait(false);
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
            .GetCodesWithFlagsAsync(local.OrderNo, local.CreatedUtc, ct)
            .ConfigureAwait(false);

        if (codes.Count == 0)
        {
            _logger?.LogWarning(
                "PO {OrderNo} không có mã nào, không khôi phục được.", run.OrderNo);
            return false;
        }

        _cache.LoadOrder(local.OrderNo, codes);
        if (_cache.HasPendingPass || meta.ContainsKey(PoStore.MetaHalted))
        {
            _halted = true;
            _logger?.LogCritical("PO {OrderNo} resumed with an unresolved PLC Pass; scans are halted.", run.OrderNo);
        }

        lock (_gate)
        {
            _current = new ProductionOrder
            {
                OrderNo = local.OrderNo,
                OrderQty = local.OrderQty,
                Gtin = local.Gtin,
                CreatedUtc = local.CreatedUtc,
                Additional = local.Additional,
            };
            _currentRunOrderNo = run.OrderNo;
            _currentStatus = ProductionOrderStatus.Running;
            _completionPending = false;
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
        lock (_gate)
        {
            if (_halted || _updateMaintenance || _lifecycleTransition || _cache.HasPendingPass)
                return PoCodeValidation.Halted;
            if (_current is null || _currentStatus != ProductionOrderStatus.Running)
                return PoCodeValidation.NoProductionOrder;
            var validation = _cache.ValidateAndAccept(_current.OrderNo, codeContent, accept: false);
            return validation == PoCodeValidation.Valid && _completionPending
                ? PoCodeValidation.Halted : validation;
        }
    }

    /// <summary>Legacy compatibility overload for the old GTIN/serial caller.</summary>
    [Obsolete("Pass the complete CodeContent to ValidateCode.")]
    public PoCodeValidation ValidateCode(string gtin, string serial) =>
        ValidateCode(PoStore.BuildCodeKey(gtin, serial));

    /// <summary>
    /// Manual intake: reserved persistence capacity is required before calling.
    /// Validates GTIN and exact dictionary identity, then consumes RAM atomically.
    /// Does not acquire the PO lifecycle mutex, open SQLite, or perform PLC I/O.
    /// The caller MUST publish Mutation to its reservation before any audit await.
    /// </summary>
    public PoScanResult AcceptManualCode(string codeContent, DateTime receivedUtc, string eventId)
    {
        ArgumentNullException.ThrowIfNull(codeContent);
        ArgumentException.ThrowIfNullOrWhiteSpace(eventId);
        var hasGtin = PoCodeParser.TryGetGtin(codeContent, out var gtin);
        PoScanResult result;
        lock (_gate)
        {
            var order = _current;
            var validation = ValidateCode(codeContent);
            if (validation != PoCodeValidation.Valid || order is null)
                return new PoScanResult(validation, null, false, order?.OrderNo ?? string.Empty);
            if (!hasGtin || !PoCodeParser.TryNormalizeGtin(order.Gtin, out var orderGtin)
                || !string.Equals(gtin, orderGtin, StringComparison.Ordinal))
                return new PoScanResult(PoCodeValidation.InvalidForOrder, null, false, order.OrderNo);
            validation = _cache.ValidateAndAccept(order.OrderNo, codeContent, accept: true);
            if (validation != PoCodeValidation.Valid)
                return new PoScanResult(validation, null, false, order.OrderNo);
            _completionPending = _cache.ValidCount > 0 && _cache.ScannedCount >= _cache.ValidCount;
            var mutation = new PoScanMutation(eventId, order.OrderNo, order.CreatedUtc, codeContent, receivedUtc);
            result = new PoScanResult(PoCodeValidation.Valid, null, true, order.OrderNo) { Mutation = mutation };
        }
        RaiseChanged();
        return result;
    }

    /// <summary>
    /// Consume an eligible code atomically in RAM before sending PLC Pass.
    /// The caller must reserve persistence capacity first, then publish Mutation
    /// even when PLC fails. No SQLite or network operation precedes sendPass.
    /// </summary>
    public async Task<PoScanResult> ProcessCodeAsync(
        string codeContent,
        Func<CancellationToken, Task<bool>> sendPass,
        CancellationToken ct = default,
        DateTime? receivedUtc = null,
        string? eventId = null)
    {
        ArgumentNullException.ThrowIfNull(codeContent);
        ArgumentNullException.ThrowIfNull(sendPass);
        if (eventId is not null && string.IsNullOrWhiteSpace(eventId))
            throw new ArgumentException("A scan event identity cannot be empty.", nameof(eventId));

        ct.ThrowIfCancellationRequested();
        PoScanMutation mutation;
        lock (_gate)
        {
            var order = _current;
            var validation = ValidateCode(codeContent);
            if (validation != PoCodeValidation.Valid || order is null)
                return new PoScanResult(validation, false, false, order?.OrderNo ?? string.Empty);
            validation = _cache.ValidateAndAccept(order.OrderNo, codeContent, accept: true);
            if (validation != PoCodeValidation.Valid)
                return new PoScanResult(validation, false, false, order.OrderNo);
            mutation = new PoScanMutation(eventId ?? Guid.NewGuid().ToString("N"),
                order.OrderNo, order.CreatedUtc, codeContent, receivedUtc ?? DateTime.UtcNow);
            _completionPending = _cache.ValidCount > 0 && _cache.ScannedCount >= _cache.ValidCount;
        }

        bool? plcWritten;
        string? plcMessage = null;
        try
        {
            plcWritten = await sendPass(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            plcWritten = null;
            plcMessage = ex.Message;
        }

        RaiseChanged();
        return new PoScanResult(PoCodeValidation.Valid, plcWritten, true, mutation.OrderNo,
            Message: plcMessage) { Mutation = mutation };
    }

    [Obsolete("Use ProcessCodeAsync with reserved app persistence capacity.")]
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
    private void RaiseChanged()
    {
        // UI observers must never prevent the caller from publishing an already
        // accepted scan mutation after physical PLC I/O.
        if (Changed is not { } changed)
            return;
        foreach (EventHandler observer in changed.GetInvocationList())
        {
            try { observer(this, EventArgs.Empty); }
            catch (Exception error)
            {
                _logger?.LogError(error, "PO Changed observer failed.");
            }
        }
    }
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
    /// <summary>Số mã thực nhận; tiến độ sản xuất dựa vào tập mã thực này.</summary>
    public int TotalCodes { get; init; } = Order.OrderQty;

    /// <summary>Mã PO.</summary>
    public string OrderNo => Order.OrderNo;

    /// <summary>Tổng số mã của PO.</summary>
    public int OrderQty => Order.OrderQty;

    /// <summary>Thời điểm tạo PO.</summary>
    public DateTime CreatedUtc => Order.CreatedUtc;

    /// <summary>True khi PO đã bị huỷ (không được chạy lại).</summary>
    public bool IsCancelled => Status == ProductionOrderStatus.Cancelled;
}
