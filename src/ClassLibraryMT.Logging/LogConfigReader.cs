using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace ClassLibraryMT.Logging;

/// <summary>
/// Đọc giá trị cấu hình theo kiểu AOT-safe.
///
/// Không dùng <c>ConfigurationBinder.GetValue&lt;T&gt;</c> vì chúng dựa trên
/// reflection, sinh cảnh báo IL2026/IL3050 khi build Native AOT.
/// Chỉ lấy string raw rồi tự parse lại.
/// </summary>
public static class LogConfigReader
{
    /// <summary>Lấy chuỗi, trả về fallback nếu thiếu hoặc rỗng.</summary>
    public static string GetString(IConfiguration config, string key, string fallback)
    {
        var value = config[key];
        return string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
    }

    /// <summary>Lấy số nguyên, trả về fallback nếu thiếu hoặc không parse được.</summary>
    public static int GetInt(IConfiguration config, string key, int fallback)
    {
        var value = config[key];
        return int.TryParse(value, out var parsed) ? parsed : fallback;
    }

    /// <summary>
    /// Lấy bool, trả về fallback nếu thiếu hoặc không parse được.
    ///
    /// JSON bool (<c>"true"</c>/<c>"false"</c>) khi đọc qua <see cref="IConfiguration"/>
    /// sẽ ra chuỗi <c>"True"</c>/<c>"False"</c>, nên phải dùng
    /// <see cref="bool.TryParse(string, out bool)"/> chứ không dùng <see cref="GetInt"/>.
    /// </summary>
    public static bool GetBool(IConfiguration config, string key, bool fallback)
    {
        var value = config[key];
        return bool.TryParse(value, out var parsed) ? parsed : fallback;
    }

    /// <summary>
    /// Parse tên mức log không phân biệt hoa thường.
    /// Trả về <paramref name="fallback"/> nếu không hợp lệ.
    /// </summary>
    public static LogLevel GetLogLevel(IConfiguration config, string key, LogLevel fallback)
    {
        var value = config[key];
        return Enum.TryParse<LogLevel>(value, ignoreCase: true, out var parsed) ? parsed : fallback;
    }
}
