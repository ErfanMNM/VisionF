using Microsoft.Extensions.Logging;

namespace ClassLibraryMT.Logging;

/// <summary>
/// Một bản ghi log kỹ thuật (bảng <c>AppLogs</c>).
/// </summary>
public sealed class AppLogEntry
{
    /// <summary>Khóa chính tự tăng.</summary>
    public long Id { get; set; }

    /// <summary>Thời điểm ghi (giờ UTC).</summary>
    public DateTime TimestampUtc { get; set; }

    /// <summary>Mức log: Trace / Debug / Information / Warning / Error / Critical.</summary>
    public string Level { get; set; } = string.Empty;

    /// <summary>Category của logger (thường là tên class).</summary>
    public string Category { get; set; } = string.Empty;

    /// <summary>EventId dạng số.</summary>
    public int EventId { get; set; }

    /// <summary>Nội dung log.</summary>
    public string Message { get; set; } = string.Empty;

    /// <summary>Stack trace nếu có exception (null nếu không).</summary>
    public string? Exception { get; set; }

    /// <summary>Thuộc tính bổ sung dạng JSON (null nếu không có).</summary>
    public string? PropertiesJson { get; set; }
}

/// <summary>
/// Một bản ghi thao tác người dùng (bảng <c>UserActions</c>).
/// </summary>
public sealed class UserActionEntry
{
    /// <summary>Khóa chính tự tăng.</summary>
    public long Id { get; set; }

    /// <summary>Thời điểm thao tác (giờ UTC).</summary>
    public DateTime TimestampUtc { get; set; }

    /// <summary>Tên hành động, ví dụ <c>Config.Save</c>.</summary>
    public string Action { get; set; } = string.Empty;

    /// <summary>Đối tượng bị tác động, ví dụ <c>Tab</c> hoặc <c>License</c>.</summary>
    public string Target { get; set; } = string.Empty;

    /// <summary>Chi tiết bổ sung dạng JSON (null nếu không có).</summary>
    public string? DetailJson { get; set; }

    /// <summary>Kết quả: <c>Success</c> / <c>Failed</c> / <c>Cancelled</c>.</summary>
    public string Result { get; set; } = string.Empty;

    /// <summary>
    /// Tên đăng nhập của người dùng thực hiện thao tác (rỗng nếu không có ai
    /// đăng nhập, ví dụ thao tác tự động của hệ thống).
    /// </summary>
    public string Username { get; set; } = string.Empty;

    /// <summary>
    /// Khoá chính của người dùng thực hiện thao tác, null nếu không có ai
    /// đăng nhập.
    /// </summary>
    public long? UserId { get; set; }
}

/// <summary>
/// Bộ lọc khi truy vấn log kỹ thuật.
/// </summary>
public sealed class AppLogFilter
{
    /// <summary>Từ thời điểm (giờ UTC), null = không giới hạn.</summary>
    public DateTime? FromUtc { get; set; }

    /// <summary>Đến thời điểm (giờ UTC), null = không giới hạn.</summary>
    public DateTime? ToUtc { get; set; }

    /// <summary>Lọc theo mức log (null = tất cả).</summary>
    public LogLevel? MinLevel { get; set; }

    /// <summary>Lọc theo category chứa chuỗi (null = tất cả).</summary>
    public string? CategoryContains { get; set; }

    /// <summary>Lọc theo message chứa chuỗi (null = tất cả).</summary>
    public string? MessageContains { get; set; }
}

/// <summary>
/// Bộ lọc khi truy vấn thao tác người dùng.
/// </summary>
public sealed class UserActionFilter
{
    /// <summary>Từ thời điểm (giờ UTC), null = không giới hạn.</summary>
    public DateTime? FromUtc { get; set; }

    /// <summary>Đến thời điểm (giờ UTC), null = không giới hạn.</summary>
    public DateTime? ToUtc { get; set; }

    /// <summary>Lọc theo tên hành động chứa chuỗi (null = tất cả).</summary>
    public string? ActionContains { get; set; }

    /// <summary>Lọc theo kết quả (null = tất cả).</summary>
    public string? Result { get; set; }

    /// <summary>
    /// Lọc theo tên đăng nhập của người thực hiện (null = tất cả).
    /// </summary>
    public string? Username { get; set; }
}

/// <summary>
/// Kết quả truy vấn có phân trang.
/// </summary>
/// <typeparam name="T">Kiểu bản ghi.</typeparam>
public sealed class PagedResult<T>
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
