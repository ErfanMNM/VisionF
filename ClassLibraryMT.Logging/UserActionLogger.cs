using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ClassLibraryMT.Logging;

/// <summary>
/// Cài đặt <see cref="IUserActionLogger"/> ghi vào bảng <c>UserActions</c>.
///
/// Ghi bất đồng bộ (enqueue) nên không block UI thread.
/// </summary>
internal sealed class UserActionLogger : IUserActionLogger
{
    private readonly SqliteLogWriter _writer;
    private readonly SqliteLoggingOptions _opt;

    public UserActionLogger(SqliteLogWriter writer, IOptions<SqliteLoggingOptions> options)
    {
        _writer = writer;
        _opt = options.Value;
    }

    public void Log(string action, string? target = null, string? detail = null, string? result = null)
    {
        if (!_opt.EnableUserActions || string.IsNullOrWhiteSpace(action))
        {
            return;
        }

        _writer.Enqueue(new UserActionEntry
        {
            TimestampUtc = DateTime.UtcNow,
            Action = action.Trim(),
            Target = target?.Trim() ?? string.Empty,
            DetailJson = NormalizeDetail(detail),
            Result = result?.Trim() ?? UserActionResults.Success,
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
