namespace ClassLibraryMT.Logging;

/// <summary>
/// Cấu hình trang Lịch sử thao tác (section <c>Logging:History</c>).
/// </summary>
public sealed class LoggingHistoryOptions
{
    /// <summary>
    /// Có hiển thị mục nhà phát triển (trang Lịch sử) hay không.
    /// Mặc định <c>true</c>.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Số bản ghi tải mỗi trang.
    /// Mặc định <c>200</c>.
    /// </summary>
    public int PageSize { get; set; } = 200;
}
