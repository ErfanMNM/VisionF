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
}

/// <summary>
/// Điều phối toàn bộ luồng quản lý Production Order.
///
/// <para>
/// Trách nhiệm:
/// <list type="bullet">
/// <item>Tải PO từ server về và tạo file DB riêng cho từng PO.</item>
/// <item>Bắt đầu / hoàn tất sản xuất (khóa - mở khóa PO).</item>
/// <item>Kiểm tra mã quét so với tập mã hợp lệ của PO đang chạy.</item>
/// <item>Gửi mã kích hoạt lên server.</item>
/// </list>
/// </para>
/// </summary>
public sealed class ProductionOrderService
{
    private readonly PoClient _client;
    private readonly PoStore _store;
    private readonly PoCodeCache _cache;
    private readonly ILogger? _logger;

    /// <summary>Lock bảo vệ trạng thái PO đang chạy (đọc/ghi từ nhiều luồng).</summary>
    private readonly Lock _gate = new();

    private ProductionOrder? _current;
    private ProductionOrderStatus _currentStatus = ProductionOrderStatus.Available;

    /// <summary>
    /// Khởi tạo service.
    /// </summary>
    public ProductionOrderService(
        PoClient client,
        PoStore store,
        PoCodeCache cache,
        ILogger<ProductionOrderService>? logger = null)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        _logger = logger;
    }

    /// <summary>Event phát khi PO đang chạy / trạng thái thay đổi.</summary>
    public event EventHandler? Changed;

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

    /// <summary>Số mã đã quét của PO đang chạy.</summary>
    public int ScannedCount
    {
        get
        {
            var order = CurrentOrder;
            return order is null ? 0 : _cache.ScannedCount(order.OrderNo);
        }
    }

    /// <summary>Tổng số mã của PO đang chạy.</summary>
    public int TotalCount
    {
        get
        {
            var order = CurrentOrder;
            return order is null ? 0 : _cache.ValidCount(order.OrderNo);
        }
    }

    /// <summary>
    /// Tải danh sách PO từ server, tạo file DB riêng cho các PO mới.
    /// </summary>
    /// <returns>Danh sách PO đã tải về (kèm số mã đã quét nếu có).</returns>
    public async Task<IReadOnlyList<ProductionOrderSummary>> DownloadOrdersAsync(
        CancellationToken ct = default)
    {
        var orders = await _client.GetOrdersAsync(ct).ConfigureAwait(false);
        var result = new List<ProductionOrderSummary>(orders.Count);

        foreach (var order in orders)
        {
            // PO đang chạy giữ nguyên trạng thái trong bộ nhớ.
            var running = CurrentOrder;
            if (running is not null
                && string.Equals(running.OrderNo, order.OrderNo, StringComparison.OrdinalIgnoreCase))
            {
                result.Add(new ProductionOrderSummary(order, ScannedCount, CurrentStatus));
                continue;
            }

            var codes = await _client.GetCodesAsync(order.OrderNo, ct).ConfigureAwait(false);
            await _store.EnsurePoDbAsync(order, codes, ct).ConfigureAwait(false);

            var scanned = await _store
                .GetScannedCodesAsync(order.OrderNo, order.CreatedUtc, ct)
                .ConfigureAwait(false);

            _cache.SetValidCodes(order.OrderNo, codes.Select(c => BuildCodeKey(c.Gtin, c.Serial)));
            _cache.SetScannedCodes(order.OrderNo, scanned.Select(c => BuildCodeKey(c.Gtin, c.Serial)));

            var status = await ReadStatusAsync(order, ct).ConfigureAwait(false);
            result.Add(new ProductionOrderSummary(order, scanned.Count, status));
        }

        RaiseChanged();
        return result;
    }

    /// <summary>
    /// Bắt đầu sản xuất cho một PO: khóa PO, nạp tập mã hợp lệ vào RAM cache.
    /// </summary>
    public async Task<bool> StartProductionAsync(
        string orderNo,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(orderNo))
            return false;

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

        // Tải lại tập mã từ server (đảm bảo mới nhất) rồi ghi xuống DB PO.
        var codes = await _client.GetCodesAsync(orderNo, ct).ConfigureAwait(false);
        if (codes.Count == 0)
        {
            _logger?.LogWarning("PO {OrderNo} không có mã nào, không thể bắt đầu.", orderNo);
            return false;
        }

        // Giữ đúng PO đã tải về (CreatedUtc từ server) để đường dẫn file DB
        // khớp với file đã tạo lúc tải PO — nếu tạo PO mới với thời điểm
        // hiện tại thì sẽ trỏ sang file khác và mất lịch sử đã quét.
        var order = await FindOrderAsync(orderNo, ct).ConfigureAwait(false)
            ?? new ProductionOrder
            {
                OrderNo = orderNo,
                OrderQty = codes.Count,
                CreatedUtc = DateTime.UtcNow,
            };

        order.OrderQty = codes.Count;

        await _store.EnsurePoDbAsync(order, codes, ct).ConfigureAwait(false);

        var scanned = await _store
            .GetScannedCodesAsync(order.OrderNo, order.CreatedUtc, ct)
            .ConfigureAwait(false);

        _cache.SetValidCodes(order.OrderNo, codes.Select(c => BuildCodeKey(c.Gtin, c.Serial)));
        _cache.SetScannedCodes(order.OrderNo, scanned.Select(c => BuildCodeKey(c.Gtin, c.Serial)));

        await _store.SetMetaAsync(
            order.OrderNo, order.CreatedUtc, PoStore.MetaStatus,
            ProductionOrderStatus.Running.ToString(), ct).ConfigureAwait(false);
        await _store.SetMetaAsync(
            order.OrderNo, order.CreatedUtc, PoStore.MetaStartedUtc,
            DateTime.UtcNow.ToString("O"), ct).ConfigureAwait(false);

        lock (_gate)
        {
            _current = order;
            _currentStatus = ProductionOrderStatus.Running;
        }

        _logger?.LogInformation(
            "Bắt đầu sản xuất PO {OrderNo} với {Count} mã.",
            order.OrderNo, codes.Count);

        RaiseChanged();
        return true;
    }

    /// <summary>
    /// Hoàn tất sản xuất: giải phóng khóa PO, đánh dấu Completed.
    /// </summary>
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

        lock (_gate)
        {
            _current = null;
            _currentStatus = ProductionOrderStatus.Available;
        }

        _logger?.LogInformation("Hoàn tất sản xuất PO {OrderNo}.", order.OrderNo);

        RaiseChanged();
        return true;
    }

    /// <summary>
    /// Kiểm tra mã quét so với PO đang chạy.
    ///
    /// <para>
    /// - Chưa có PO chạy → <see cref="PoCodeValidation.NoProductionOrder"/>
    ///   (camera loại mã, KHÔNG ghi PLC).
    /// - Mã không thuộc PO → <see cref="PoCodeValidation.InvalidForOrder"/> (Fail).
    /// - Mã đã quét trong PO → <see cref="PoCodeValidation.Duplicate"/> (Fail).
    /// </para>
    /// </summary>
    public PoCodeValidation ValidateCode(string gtin, string serial)
    {
        ProductionOrder? order;
        lock (_gate)
        {
            order = _current;
            if (order is null || _currentStatus != ProductionOrderStatus.Running)
                return PoCodeValidation.NoProductionOrder;
        }

        var code = BuildCodeKey(gtin, serial);

        if (_cache.IsScanned(order.OrderNo, code))
            return PoCodeValidation.Duplicate;

        if (!_cache.IsValidCode(order.OrderNo, code))
            return PoCodeValidation.InvalidForOrder;

        return PoCodeValidation.Valid;
    }

    /// <summary>
    /// Đánh dấu mã đã quét hợp lệ (ghi vào cache RAM + DB PO).
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
        _cache.MarkScanned(order.OrderNo, code);

        await _store
            .MarkScannedAsync(order.OrderNo, order.CreatedUtc, code, ct)
            .ConfigureAwait(false);

        RaiseChanged();
    }

    /// <summary>
    /// Gửi các mã chưa kích hoạt của PO đang chạy lên server.
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

        var pending = await _store
            .GetPendingActivationCodesAsync(order.OrderNo, order.CreatedUtc, ct)
            .ConfigureAwait(false);

        if (pending.Count == 0)
            return 0;

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

        _logger?.LogInformation(
            "Đã kích hoạt {Count} mã của PO {OrderNo}.", pending.Count, order.OrderNo);

        RaiseChanged();
        return result.Activated > 0 ? result.Activated : pending.Count;
    }

    /// <summary>
    /// Nạp lại cache từ các file DB PO đã tồn tại (dùng lúc khởi động app).
    /// </summary>
    public async Task RebuildCacheAsync(CancellationToken ct = default)
    {
        foreach (var file in _store.ListDbFiles())
        {
            await using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={file}");
            await connection.OpenAsync(ct).ConfigureAwait(false);

            string orderNo = "";
            string status = ProductionOrderStatus.Downloaded.ToString();
            DateTime createdUtc = DateTime.UtcNow;

            await using (var meta = connection.CreateCommand())
            {
                meta.CommandText = "SELECT Key, Value FROM PoMeta;";
                await using var reader = await meta.ExecuteReaderAsync(ct).ConfigureAwait(false);
                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    var key = reader.GetString(0);
                    var value = reader.GetString(1);
                    if (string.Equals(key, PoStore.MetaOrderNo, StringComparison.OrdinalIgnoreCase))
                        orderNo = value;
                    else if (string.Equals(key, PoStore.MetaStatus, StringComparison.OrdinalIgnoreCase))
                        status = value;
                    else if (string.Equals(key, PoStore.MetaCreatedUtc, StringComparison.OrdinalIgnoreCase)
                             && DateTime.TryParse(
                                 value, null,
                                 System.Globalization.DateTimeStyles.RoundtripKind, out var parsed))
                        createdUtc = parsed;
                }
            }

            if (string.IsNullOrWhiteSpace(orderNo))
                continue;

            var valid = new List<string>();
            var scanned = new List<string>();

            await using (var codes = connection.CreateCommand())
            {
                codes.CommandText = "SELECT Gtin, Serial, ScannedUtc FROM PoCodes;";
                await using var reader = await codes.ExecuteReaderAsync(ct).ConfigureAwait(false);
                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    var key = BuildCodeKey(reader.GetString(0), reader.GetString(1));
                    valid.Add(key);
                    if (!reader.IsDBNull(2))
                        scanned.Add(key);
                }
            }

            _cache.SetValidCodes(orderNo, valid);
            _cache.SetScannedCodes(orderNo, scanned);

            _logger?.LogInformation(
                "Nạp cache PO {OrderNo}: {Total} mã, {Scanned} đã quét, trạng thái {Status}.",
                orderNo, valid.Count, scanned.Count, status);
        }
    }

    /// <summary>
    /// Tìm thông tin PO (kèm <c>CreatedUtc</c> từ server) theo mã PO.
    /// </summary>
    /// <remarks>
    /// Ưu tiên đọc từ server để <c>CreatedUtc</c> khớp với lúc PO được tạo;
    /// nếu server không còn trả về PO này thì đọc từ file DB PO cục bộ.
    /// </remarks>
    private async Task<ProductionOrder?> FindOrderAsync(
        string orderNo,
        CancellationToken ct)
    {
        try
        {
            var orders = await _client.GetOrdersAsync(ct).ConfigureAwait(false);
            var match = orders.FirstOrDefault(o =>
                string.Equals(o.OrderNo, orderNo, StringComparison.OrdinalIgnoreCase));

            if (match is not null)
                return match;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Không lấy được danh sách PO từ server: {OrderNo}", orderNo);
        }

        // Fallback: đọc CreatedUtc từ file DB PO cục bộ.
        var dbPath = _store.FindExistingDbPath(orderNo);
        if (dbPath is null)
            return null;

        var meta = await _store.GetMetaAsync(orderNo, DateTime.UtcNow, ct).ConfigureAwait(false);
        if (meta.TryGetValue(PoStore.MetaCreatedUtc, out var raw)
            && DateTime.TryParse(
                raw, null,
                System.Globalization.DateTimeStyles.RoundtripKind, out var created))
        {
            return new ProductionOrder
            {
                OrderNo = orderNo,
                OrderQty = 0,
                CreatedUtc = created,
            };
        }

        return null;
    }

    /// <summary>
    /// Đọc trạng thái PO từ file DB (fallback <see cref="ProductionOrderStatus.Downloaded"/>).
    /// </summary>
    private async Task<ProductionOrderStatus> ReadStatusAsync(
        ProductionOrder order,
        CancellationToken ct)
    {
        var meta = await _store.GetMetaAsync(order.OrderNo, order.CreatedUtc, ct).ConfigureAwait(false);
        if (meta.TryGetValue(PoStore.MetaStatus, out var raw)
            && Enum.TryParse<ProductionOrderStatus>(raw, ignoreCase: true, out var status))
        {
            return status;
        }

        return ProductionOrderStatus.Downloaded;
    }

    /// <summary>Tạo khóa mã chuẩn (GTIN + serial) để so sánh.</summary>
    private static string BuildCodeKey(string gtin, string serial)
        => $"{gtin}|{serial}";

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
}
