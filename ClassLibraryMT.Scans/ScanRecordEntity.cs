namespace ClassLibraryMT.Scans;

/// <summary>
/// Một bản ghi quét mã từ camera, lưu vào bảng <c>ScanRecords</c>.
/// </summary>
public sealed class ScanRecordEntity
{
    /// <summary>Khoá chính tự tăng (do database sinh).</summary>
    public long Id { get; set; }

    /// <summary>Thời điểm nhận mã từ camera (UTC).</summary>
    public DateTime ReceivedUtc { get; set; }

    /// <summary>Nội dung mã đầy đủ, giữ nguyên định danh camera (rỗng nếu không đọc được).</summary>
    public string CodeContent { get; set; } = string.Empty;

    /// <summary>Tên cũ của <see cref="CodeContent"/>, chỉ giữ cho tích hợp tương thích.</summary>
    [Obsolete("Use CodeContent; the complete scanned code is the identity.")]
    public string Code { get => CodeContent; set => CodeContent = value; }

    /// <summary>Metadata GTIN cũ; không dùng làm định danh hoặc kiểm tra mã sản xuất.</summary>
    public string Gtin { get; set; } = string.Empty;

    /// <summary>Metadata serial cũ; không dùng làm định danh hoặc kiểm tra mã sản xuất.</summary>
    public string Serial { get; set; } = string.Empty;

    /// <summary>Pass / Duplicate / InvalidForOrder / NoRead; Fail và Overload tương thích lịch sử.</summary>
    public string Status { get; set; } = ScanStatuses.Fail;

    /// <summary>
    /// Thời gian xử lý (ms) — tính từ lúc nhận mã từ camera đến trước khi
    /// gửi xuống PLC.
    ///
    /// Kiểu <see cref="double"/> để giữ phần thập phân (ví dụ <c>0.333 ms</c>);
    /// lưu trong SQLite dưới kiểu <c>REAL</c>.
    /// </summary>
    public double ProcessingTimeMs { get; set; }

    /// <summary>Kết quả gửi xuống PLC: Ok / Failed / Skipped.</summary>
    public string PlcResult { get; set; } = ScanPlcResults.Skipped;

    /// <summary>Số batch sản xuất (mặc định <c>NoBatch</c>).</summary>
    public string BatchNo { get; set; } = ScanBatchDefaults.NoBatch;

    /// <summary>Nội dung gốc camera gửi lên.</summary>
    public string Raw { get; set; } = string.Empty;

    /// <summary>
    /// Mã lệnh sản xuất (PO) mà mã này thuộc về (rỗng nếu không trong PO nào).
    ///
    /// <para>
    /// Dùng cho cơ chế chống trùng lịch sử toàn cục: biết mã đã từng quét
    /// thuộc PO nào để đối chiếu khi gửi mã lên server.
    /// </para>
    /// </summary>
    public string OrderNo { get; set; } = string.Empty;

    /// <summary>
    /// Trạng thái gửi mã lên Checkee: <c>Pending</c> / <c>Sent</c> / <c>Failed</c>.
    /// Mã cũ (trước khi có tính năng này) có giá trị rỗng -> coi như chưa gửi.
    /// </summary>
    public string SendStatus { get; set; } = string.Empty;

    /// <summary>
    /// Mã HTTP Checkee trả về khi gửi thất bại (0 nếu không có phản hồi).
    /// </summary>
    public int SendStatusCode { get; set; }

    /// <summary>
    /// Thông điệp lỗi / kết quả của lần gửi gần nhất (tiếng Việt).
    /// </summary>
    public string SendMessage { get; set; } = string.Empty;

    /// <summary>
    /// Số lần đã thử gửi mã này lên Checkee.
    /// </summary>
    public int SendAttempts { get; set; }

    /// <summary>
    /// Thời điểm gửi thành công gần nhất (UTC) — null nếu chưa gửi thành công.
    /// </summary>
    public DateTime? SendTimeUtc { get; set; }

    /// <summary>
    /// Thời điểm thử gửi gần nhất (UTC) — null nếu chưa thử gửi.
    /// </summary>
    public DateTime? SendAttemptUtc { get; set; }
}

/// <summary>
/// Bộ lọc khi truy vấn bản ghi quét mã.
/// </summary>
public sealed class ScanRecordFilter
{
    /// <summary>Từ thời điểm (UTC), null = không giới hạn.</summary>
    public DateTime? FromUtc { get; set; }

    /// <summary>Đến thời điểm (UTC), null = không giới hạn.</summary>
    public DateTime? ToUtc { get; set; }

    /// <summary>Lọc theo trạng thái (null = tất cả).</summary>
    public ScanStatus? Status { get; set; }

    /// <summary>Lọc theo nội dung mã đầy đủ chứa chuỗi (null = tất cả).</summary>
    public string? CodeContains { get; set; }

    /// <summary>Đối chiếu nguyên CodeContent chính xác, phân biệt hoa thường; null = không lọc.</summary>
    public string? CodeContentEquals { get; set; }

    /// <summary>Lọc theo số batch (null = tất cả).</summary>
    public string? BatchNo { get; set; }
}

/// <summary>
/// Kết quả truy vấn có phân trang.
/// </summary>
/// <typeparam name="T">Kiểu bản ghi.</typeparam>
public sealed class PagedScanResult<T>
{
    /// <summary>Các bản ghi của trang hiện tại.</summary>
    public IReadOnlyList<T> Items { get; init; } = [];

    /// <summary>Tổng số bản ghi khớp bộ lọc (không tính phân trang).</summary>
    public long TotalCount { get; init; }

    /// <summary>Số trang đang xem (bắt đầu từ 1).</summary>
    public int Page { get; init; } = 1;

    /// <summary>Số bản ghi mỗi trang.</summary>
    public int PageSize { get; init; } = 200;

    /// <summary>Tổng số trang.</summary>
    public int TotalPages =>
        PageSize <= 0 ? 0 : (int)Math.Max(1, (TotalCount + PageSize - 1) / PageSize);

    /// <summary>True khi còn trang sau.</summary>
    public bool HasNextPage => Page < TotalPages;

    /// <summary>True khi còn trang trước.</summary>
    public bool HasPreviousPage => Page > 1;
}
