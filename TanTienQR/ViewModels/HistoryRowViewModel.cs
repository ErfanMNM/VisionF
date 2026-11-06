using System.Globalization;
using System.Text.Json;
using ClassLibraryMT.Logging;

namespace TanTienQR.ViewModels;

/// <summary>
/// Một dòng trong bảng lịch sử thao tác.
///
/// Bọc <see cref="UserActionEntry"/> lại để có thuộc tính hiển thị sẵn (chuỗi đã
/// format, class style cho màu) — giúp XAML không phải dùng converter.
/// </summary>
public sealed class HistoryRowViewModel
{
    private static readonly CultureInfo ViCulture = CreateViCulture();

    /// <summary>
    /// Bọc một bản ghi thao tác.
    /// </summary>
    /// <param name="entry">Bản ghi gốc từ database.</param>
    public HistoryRowViewModel(UserActionEntry entry)
    {
        TimestampText = entry.TimestampUtc.ToLocalTime().ToString("dd/MM/yyyy HH:mm:ss", ViCulture);
        Action = entry.Action;
        Target = string.IsNullOrWhiteSpace(entry.Target) ? "—" : entry.Target;
        Detail = FormatDetail(entry.DetailJson);
        Result = FormatResult(entry.Result);
    }

    /// <summary>Thời điểm thao tác (giờ địa phương).</summary>
    public string TimestampText { get; }

    /// <summary>Tên hành động, ví dụ <c>Config.Save</c>.</summary>
    public string Action { get; }

    /// <summary>Đối tượng bị tác động.</summary>
    public string Target { get; }

    /// <summary>Chi tiết đã format để hiển thị.</summary>
    public string Detail { get; }

    /// <summary>Kết quả đã dịch sang tiếng Việt.</summary>
    public string Result { get; }

    /// <summary>True khi thao tác thành công (badge xanh).</summary>
    public bool IsSuccess => Result == "Thành công";

    /// <summary>True khi thao tác thất bại (badge đỏ).</summary>
    public bool IsFailed => Result == "Thất bại";

    /// <summary>True khi người dùng hủy (badge vàng).</summary>
    public bool IsCancelled => Result == "Đã hủy";

    /// <summary>
    /// Format chi tiết: nếu là JSON thì rút gọn cho dễ đọc, ngược lại bỏ dấu
    /// ngoặc kép JSON của chuỗi đơn.
    /// </summary>
    private static string FormatDetail(string? detailJson)
    {
        if (string.IsNullOrWhiteSpace(detailJson))
        {
            return "—";
        }

        var trimmed = detailJson.Trim();

        // JSON object -> gộp các cặp key:value thành "key: value, ...".
        if (trimmed.StartsWith('{'))
        {
            try
            {
                using var doc = JsonDocument.Parse(trimmed);
                var parts = new List<string>();

                foreach (var prop in doc.RootElement.EnumerateObject())
                {
                    parts.Add($"{prop.Name}: {prop.Value}");
                }

                return parts.Count > 0 ? string.Join(", ", parts) : trimmed;
            }
            catch (JsonException)
            {
                // Không phải JSON hợp lệ -> hiển thị nguyên bản.
            }
        }

        // Chuỗi đơn đã bị JsonSerializer bọc thành JSON -> bỏ ngoặc kép.
        if (trimmed.StartsWith('"') && trimmed.EndsWith('"') && trimmed.Length >= 2)
        {
            try
            {
                // Source-generated context -> AOT-safe, không dùng reflection.
                var value = JsonSerializer.Deserialize(trimmed, HistoryJsonContext.Default.String);
                if (!string.IsNullOrEmpty(value))
                {
                    return value;
                }
            }
            catch (JsonException)
            {
                // Bỏ qua -> hiển thị nguyên bản.
            }
        }

        return trimmed;
    }

    /// <summary>Dịch mã kết quả sang tiếng Việt để hiển thị.</summary>
    private static string FormatResult(string result) => result switch
    {
        UserActionResults.Success => "Thành công",
        UserActionResults.Failed => "Thất bại",
        UserActionResults.Cancelled => "Đã hủy",
        _ => string.IsNullOrWhiteSpace(result) ? "—" : result,
    };

    private static CultureInfo CreateViCulture()
    {
        try
        {
            return new CultureInfo("vi-VN");
        }
        catch (CultureNotFoundException)
        {
            return CultureInfo.InvariantCulture;
        }
    }
}
