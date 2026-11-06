using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using System.Text.Unicode;

namespace ClassLibraryMT.Logging;

/// <summary>
/// Context JSON source-generated cho các kiểu dữ liệu đơn giản được serialize
/// trong thư viện (chuỗi, số, bool, mảng chuỗi).
///
/// Dùng để tránh cảnh báo IL2026/IL3050 khi publish Native AOT — không dùng
/// reflection lúc runtime.
/// </summary>
[JsonSourceGenerationOptions(WriteIndented = false)]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(int))]
[JsonSerializable(typeof(bool))]
[JsonSerializable(typeof(string[]))]
[JsonSerializable(typeof(object))]
[JsonSerializable(typeof(List<string>))]
[JsonSerializable(typeof(Dictionary<string, string>))]
public sealed partial class LogJsonContext : JsonSerializerContext
{
}

/// <summary>
/// Encoder JSON dùng chung cho thư viện log.
///
/// Mặc định <see cref="JavaScriptEncoder"/> escape toàn bộ ký tự ngoài ASCII
/// (thành <c>\uXXXX</c>) nên tiếng Việt có dấu bị mất dạng đọc được trong
/// báo cáo xuất ra. Encoder này cho phép toàn bộ dải Unicode nên vẫn tạo ra
/// JSON hợp lệ mà giữ nguyên chữ tiếng Việt.
/// </summary>
public static class LogJsonEncoder
{
    /// <summary>Bản thể singleton dùng chung.</summary>
    public static JavaScriptEncoder Shared { get; } = JavaScriptEncoder.Create(UnicodeRanges.All);
}

/// <summary>
/// Tùy chọn serialize JSON dùng chung cho thư viện log.
///
/// Giữ nguyên chữ tiếng Việt (không escape thành <c>\uXXXX</c>) và dùng
/// chung với source-generated context để tương thích Native AOT.
/// </summary>
public static class LogJsonOptions
{
    /// <summary>
    /// Tùy chọn mặc định (không thụt lề) kèm encoder giữ nguyên Unicode.
    /// </summary>
    public static JsonSerializerOptions Default { get; } = new()
    {
        Encoder = LogJsonEncoder.Shared,
    };

    /// <summary>
    /// Tùy chọn có thụt lề, dùng cho báo cáo xuất ra cho người dùng.
    /// </summary>
    public static JsonSerializerOptions Indented { get; } = new()
    {
        Encoder = LogJsonEncoder.Shared,
        WriteIndented = true,
    };

    /// <summary>
    /// Context source-generated dùng chung với encoder giữ nguyên Unicode.
    /// </summary>
    public static LogJsonContext Context { get; } = new(Default);

    /// <summary>
    /// Serialize một chuỗi bằng source-generated context (AOT-safe) với encoder
    /// giữ nguyên chữ tiếng Việt.
    /// </summary>
    public static string SerializeString(string value) =>
        JsonSerializer.Serialize(value, Context.String);
}
