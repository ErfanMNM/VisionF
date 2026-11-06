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

    /// <summary>Mã / serial quét được (rỗng nếu không đọc được).</summary>
    public string Code { get; set; } = string.Empty;

    /// <summary>Mã GTIN nếu parse được (rỗng nếu không).</summary>
    public string Gtin { get; set; } = string.Empty;

    /// <summary>Số serial nếu parse được (rỗng nếu không).</summary>
    public string Serial { get; set; } = string.Empty;

    /// <summary>Trạng thái: Pass / Fail / Overload.</summary>
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

    /// <summary>Lọc theo mã/serial chứa chuỗi (null = tất cả).</summary>
    public string? CodeContains { get; set; }

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
