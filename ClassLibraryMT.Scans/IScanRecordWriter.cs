namespace ClassLibraryMT.Scans;

/// <summary>
/// API ghi bản ghi quét mã vào SQLite.
///
/// Ghi bất đồng bộ (enqueue) nên không block luồng xử lý mã của camera.
/// Nếu hàng đợi đầy thì bản ghi cũ nhất bị bỏ — ưu tiên không làm chậm app.
/// </summary>
public interface IScanRecordWriter
{
    /// <summary>
    /// Đưa một bản ghi vào hàng đợi ghi (không chặn).
    /// </summary>
    /// <param name="record">Bản ghi cần lưu.</param>
    void Enqueue(ScanRecordEntity record);

    /// <summary>
    /// Chờ cho tới khi hàng đợi rỗng và dữ liệu đã nằm trong database.
    /// </summary>
    /// <param name="cancellationToken">Token hủy.</param>
    Task FlushAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// API đọc bản ghi quét mã (cho trang lịch sử / báo cáo).
/// </summary>
public interface IScanRecordQuery
{
    /// <summary>
    /// Truy vấn bản ghi theo bộ lọc, có phân trang.
    /// </summary>
    /// <param name="filter">Bộ lọc (null = không lọc).</param>
    /// <param name="page">Số trang, bắt đầu từ 1.</param>
    /// <param name="pageSize">Số bản ghi mỗi trang.</param>
    /// <param name="cancellationToken">Token hủy.</param>
    Task<PagedScanResult<ScanRecordEntity>> GetAsync(
        ScanRecordFilter? filter = null,
        int page = 1,
        int pageSize = 200,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Đếm số bản ghi theo từng trạng thái.
    /// </summary>
    /// <param name="cancellationToken">Token hủy.</param>
    Task<Dictionary<string, long>> CountByStatusAsync(CancellationToken cancellationToken = default);
}
