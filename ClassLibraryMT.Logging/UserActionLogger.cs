using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ClassLibraryMT.Logging;

/// <summary>
/// Cài đặt <see cref="IUserActionLogger"/> ghi vào bảng <c>UserActions</c>.
///
/// Ghi bất đồng bộ (enqueue) nên không block UI thread.
///
/// Nếu ứng dụng đăng ký <see cref="IUserContext"/> (ví dụ qua lib Auth) thì
/// mỗi bản ghi sẽ kèm tên người dùng đang đăng nhập. Nếu không có thì cột
/// <c>Username</c> để rỗng — app vẫn chạy bình thường.
/// </summary>
internal sealed class UserActionLogger : IUserActionLogger
{
    private readonly SqliteLogWriter _writer;
    private readonly SqliteLoggingOptions _opt;
    private readonly IUserContext? _userContext;

    /// <summary>
    /// Khởi tạo logger thao tác.
    /// </summary>
    /// <param name="writer">Writer ghi log.</param>
    /// <param name="options">Cấu hình ghi log.</param>
    /// <param name="serviceProvider">
    /// Service provider — dùng để lấy <see cref="IUserContext"/> (tuỳ chọn).
    /// Được lấy qua <c>GetService&lt;IUserContext&gt;()</c> vì DI của
    /// Microsoft.Extensions không tự hỗ trợ tham số nullable.
    /// </param>
    public UserActionLogger(
        SqliteLogWriter writer,
        IOptions<SqliteLoggingOptions> options,
        IServiceProvider serviceProvider)
    {
        _writer = writer;
        _opt = options.Value;
        _userContext = serviceProvider.GetService<IUserContext>();
    }

    /// <summary>
    /// Khởi tạo logger thao tác không có ngữ cảnh người dùng.
    ///
    /// Dùng khi app không đăng nhập / không có lib Auth — các bản ghi sẽ có
    /// cột <c>Username</c> rỗng.
    /// </summary>
    /// <param name="writer">Writer ghi log.</param>
    /// <param name="options">Cấu hình ghi log.</param>
    public UserActionLogger(
        SqliteLogWriter writer,
        IOptions<SqliteLoggingOptions> options)
    {
        _writer = writer;
        _opt = options.Value;
        _userContext = null;
    }

    public void Log(string action, string? target = null, string? detail = null, string? result = null)
    {
        if (!_opt.EnableUserActions || string.IsNullOrWhiteSpace(action))
        {
            return;
        }

        // Lấy thông tin người dùng ở thời điểm ghi (có thể đã đăng xuất sau đó).
        var username = _userContext?.Username;
        var userId = _userContext?.UserId;

        _writer.Enqueue(new UserActionEntry
        {
            TimestampUtc = DateTime.UtcNow,
            Action = action.Trim(),
            Target = target?.Trim() ?? string.Empty,
            DetailJson = NormalizeDetail(detail),
            Result = result?.Trim() ?? UserActionResults.Success,
            Username = username?.Trim() ?? string.Empty,
            UserId = userId,
        });
    }

    public async Task LogAsync(
        string action,
        string? target = null,
        string? detail = null,
        string? result = null,
        CancellationToken cancellationToken = default)
    {
        Log(action, target, detail, result);

        // Chờ ghi xuống DB để bản ghi chắc chắn nằm trong file (dùng khi tắt app).
        await _writer.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Chuẩn hóa chi tiết: nếu là JSON hợp lệ thì giữ nguyên, ngược lại bọc
    /// thành chuỗi JSON để truy vấn đều có cấu trúc.
    /// </summary>
    private static string? NormalizeDetail(string? detail)
    {
        if (string.IsNullOrWhiteSpace(detail))
        {
            return null;
        }

        var trimmed = detail.Trim();

        // Đã là JSON -> giữ nguyên.
        if (trimmed.StartsWith('{') || trimmed.StartsWith('['))
        {
            try
            {
                using var _ = JsonDocument.Parse(trimmed);
                return trimmed;
            }
            catch (JsonException)
            {
                // Không phải JSON hợp lệ -> xử lý như văn bản bên dưới.
            }
        }

        // Dùng chung encoder nên chữ tiếng Việt giữ nguyên dạng đọc được
        // (không bị escape thành \uXXXX).
        return LogJsonOptions.SerializeString(trimmed);
    }
}
