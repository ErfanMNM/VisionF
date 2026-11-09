namespace ClassLibraryMT.Po;

/// <summary>
/// API gọi server quản lý Production Order.
///
/// <para>
/// Tách interface khỏi <see cref="PoClient"/> để có thể thay thế bằng bản
/// giả trong test (không cần mạng thật).
/// </para>
/// </summary>
public interface IPoClient
{
    /// <summary>
    /// Lấy danh sách PO từ server.
    /// </summary>
    /// <param name="includeCompleted">True nếu muốn lấy cả PO đã hoàn tất / đã đóng.</param>
    /// <param name="ct">Token hủy.</param>
    Task<IReadOnlyList<ProductionOrder>> GetOrdersAsync(
        bool includeCompleted = false,
        CancellationToken ct = default);

    /// <summary>
    /// Lấy tập mã của một PO.
    /// </summary>
    /// <param name="orderNo">Mã PO.</param>
    /// <param name="ct">Token hủy.</param>
    Task<IReadOnlyList<PoCode>> GetCodesAsync(
        string orderNo,
        CancellationToken ct = default);

    /// <summary>
    /// Gửi danh sách serial cần kích hoạt lên server.
    /// </summary>
    /// <param name="orderNo">Mã PO.</param>
    /// <param name="serials">Danh sách serial (hoặc mã đầy đủ).</param>
    /// <param name="ct">Token hủy.</param>
    Task<PoActivateResult> ActivateAsync(
        string orderNo,
        IReadOnlyList<string> serials,
        CancellationToken ct = default);

    /// <summary>
    /// Tạo PO mới trên server.
    /// </summary>
    /// <param name="orderNo">Mã PO.</param>
    /// <param name="orderQty">Số lượng mã.</param>
    /// <param name="ct">Token hủy.</param>
    Task<ProductionOrder?> CreateOrderAsync(
        string orderNo,
        int orderQty,
        CancellationToken ct = default);
}
