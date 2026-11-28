using Microsoft.Extensions.Logging;

namespace ClassLibraryMT.Logging;

/// <summary>
/// API ghi thao tác người dùng (sự kiện nghiệp vụ) vào bảng <c>UserActions</c>.
///
/// Đây là log nghiệp vụ, tách khỏi log kỹ thuật (<see cref="ILogger"/>)
/// vì người dùng cần xem lại "ai đã làm gì" chứ không phải stack trace.
///
/// Triển khai mặc định ghi bất đồng bộ (enqueue) nên không block UI thread.
/// </summary>
public interface IUserActionLogger
{
    /// <summary>
    /// Ghi một thao tác người dùng.
    /// </summary>
    /// <param name="action">
    /// Tên hành động, ví dụ <c>Config.Save</c>, <c>License.Import</c>, <c>Tab.Switch</c>.
    /// </param>
    /// <param name="target">
    /// Đối tượng bị tác động, ví dụ <c>Tab</c>, <c>License</c>, <c>Config</c>.
    /// </param>
    /// <param name="detail">
    /// Chi tiết bổ sung (dạng JSON hoặc văn bản). Không nên chứa dữ liệu nhạy cảm.
    /// </param>
    /// <param name="result">
    /// Kết quả thao tác. Xem <see cref="UserActionResults"/> cho các giá trị chuẩn.
    /// </param>
    void Log(string action, string? target = null, string? detail = null, string? result = null);

    /// <summary>
    /// Ghi một thao tác người dùng và chờ ghi hoàn tất (flush).
    /// Dùng khi cần bảo đảm bản ghi đã nằm trong database (ví dụ trước khi tắt app).
    /// </summary>
    /// <param name="action">Tên hành động.</param>
    /// <param name="target">Đối tượng bị tác động.</param>
    /// <param name="detail">Chi tiết bổ sung.</param>
    /// <param name="result">Kết quả thao tác.</param>
    /// <param name="cancellationToken">Token hủy.</param>
    Task LogAsync(
        string action,
        string? target = null,
        string? detail = null,
        string? result = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Các giá trị kết quả chuẩn cho thao tác người dùng.
/// </summary>
public static class UserActionResults
{
    /// <summary>Thao tác thành công.</summary>
    public const string Success = "Success";

    /// <summary>Thao tác thất bại.</summary>
    public const string Failed = "Failed";

    /// <summary>Người dùng hủy thao tác.</summary>
    public const string Cancelled = "Cancelled";
}

/// <summary>
/// API đọc log để hiển thị trên trang Lịch sử.
/// </summary>
public interface ILogQueryService
{
    /// <summary>
    /// Truy vấn thao tác người dùng, có lọc và phân trang.
    /// </summary>
    /// <param name="filter">Bộ lọc (null = không lọc).</param>
    /// <param name="page">Số trang, bắt đầu từ 1.</param>
    /// <param name="pageSize">Số bản ghi mỗi trang.</param>
    /// <param name="cancellationToken">Token hủy.</param>
    Task<PagedResult<UserActionEntry>> GetUserActionsAsync(
        UserActionFilter? filter = null,
        int page = 1,
        int pageSize = 200,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Truy vấn log kỹ thuật, có lọc và phân trang.
    /// </summary>
    /// <param name="filter">Bộ lọc (null = không lọc).</param>
    /// <param name="page">Số trang, bắt đầu từ 1.</param>
    /// <param name="pageSize">Số bản ghi mỗi trang.</param>
    /// <param name="cancellationToken">Token hủy.</param>
    Task<PagedResult<AppLogEntry>> GetAppLogsAsync(
        AppLogFilter? filter = null,
        int page = 1,
        int pageSize = 200,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Đếm số thao tác người dùng theo tên hành động (dùng để lọc nhanh).
    /// </summary>
    /// <param name="cancellationToken">Token hủy.</param>
    Task<Dictionary<string, long>> GetUserActionCountsAsync(CancellationToken cancellationToken = default);
}
