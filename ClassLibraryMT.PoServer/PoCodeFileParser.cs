using System.Globalization;
using System.Text;
using System.Text.Json;

namespace ClassLibraryMT.PoServer;

/// <summary>
/// Đọc tập mã từ file CSV / JSON do MES tải lên.
///
/// <para>
/// Mọi hàm đọc theo kiểu <b>streaming</b> (đọc từng dòng / phần tử) để không
/// bao giờ nạp cả file vào RAM. File được stream sẵn vào file tạm trên đĩa
/// (xem <see cref="PoUploadStore"/>) rồi mới parse.
/// </para>
/// </summary>
public static class PoCodeFileParser
{
    /// <summary>
    /// Các tên cột thường dùng trong file mã (không phân biệt hoa thường).
    /// </summary>
    internal static readonly string[] HeaderNames =
    [
        "serial",
        "serials",
        "serialnumber",
        "serialno",
        "code",
        "codes",
        "ma",
        "mas",
        "barcode",
        "gtin",
        "product",
        "productname",
        "customer",
        "batch",
        "batchcode",
        "lot",
    ];

    /// <summary>
    /// Đọc mã từ file CSV (mỗi dòng một mã, dòng đầu có thể là tiêu đề).
    /// </summary>
    /// <param name="path">Đường dẫn file tạm.</param>
    /// <param name="cancellationToken">Token hủy.</param>
    /// <returns>Danh sách mã.</returns>
    public static async Task<IReadOnlyList<string>> ReadCsvAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var result = new List<string>();
        var headerChecked = false;
        var isFirstLine = true;

        await using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 64 * 1024, useAsync: true);

        using var reader = new StreamReader(
            stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), detectEncodingFromByteOrderMarks: true);

        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            var text = line.Trim(' ', '\t', '\r', '\n');

            // Bỏ BOM ở đầu file.
            if (isFirstLine)
            {
                isFirstLine = false;
                text = text.TrimStart('\uFEFF');
            }

            if (text.Length == 0)
            {
                continue;
            }

            // Chỉ dòng ĐẦU TIÊN có nội dung mới được coi là tiêu đề.
            if (!headerChecked)
            {
                headerChecked = true;

                if (IsHeaderLine(text))
                {
                    continue;
                }
            }

            result.Add(text);
        }

        return result;
    }

    /// <summary>
    /// Đọc mã từ file JSON.
    ///
    /// <para>
    /// Chấp nhận 3 dạng nội dung:
    /// <list type="bullet">
    /// <item>Mảng chuỗi: <c>["SER001", "SER002"]</c>.</item>
    /// <item>Mảng đối tượng: <c>[{"code":"SER001"},{"serial":"SER002"}]</c>.</item>
    /// <item>Mảng đối tượng có mã ở field khác: <c>[{"Code":"..."}]</c> — so khớp
    /// không phân biệt hoa thường.</item>
    /// </list>
    /// </para>
    /// </summary>
    /// <param name="path">Đường dẫn file tạm.</param>
    /// <param name="cancellationToken">Token hủy.</param>
    /// <returns>Danh sách mã.</returns>
    /// <exception cref="PoFileFormatException">JSON không hợp lệ.</exception>
    public static async Task<IReadOnlyList<string>> ReadJsonAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var result = new List<string>();
        var headerChecked = false;

        await using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 64 * 1024, useAsync: true);

        using var doc = await JsonDocument
            .ParseAsync(stream, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        var root = doc.RootElement;

        if (root.ValueKind != JsonValueKind.Array)
        {
            throw new PoFileFormatException(
                "File JSON phải là một mảng (array) các mã, ví dụ [\"SER001\",\"SER002\"] "
                + "hoặc [{\"code\":\"SER001\"}].");
        }

        foreach (var item in root.EnumerateArray())
        {
            cancellationToken.ThrowIfCancellationRequested();

            string? value = null;

            switch (item.ValueKind)
            {
                case JsonValueKind.String:
                    value = item.GetString();
                    break;

                case JsonValueKind.Number:
                    value = item.GetRawText();
                    break;

                case JsonValueKind.Object:
                    value = ExtractCodeFromObject(item);
                    break;

                default:
                    // null / bool / array -> bỏ qua.
                    break;
            }

            var text = (value ?? string.Empty).Trim();
            if (text.Length == 0)
            {
                continue;
            }

            if (!headerChecked)
            {
                headerChecked = true;

                if (IsHeaderLine(text))
                {
                    continue;
                }
            }

            result.Add(text);
        }

        return result;
    }

    /// <summary>
    /// Lấy giá trị mã từ một phần tử JSON dạng đối tượng.
    /// </summary>
    private static string? ExtractCodeFromObject(JsonElement element)
    {
        // Ưu tiên field rõ ràng, sau đó tới bất kỳ field nào khác.
        foreach (var preferred in CodeFieldNames)
        {
            foreach (var prop in element.EnumerateObject())
            {
                if (!string.Equals(prop.Name, preferred, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                switch (prop.Value.ValueKind)
                {
                    case JsonValueKind.String:
                        return prop.Value.GetString();
                    case JsonValueKind.Number:
                        return prop.Value.GetRawText();
                    default:
                        break;
                }
            }
        }

        // Không có field chuẩn -> lấy field đầu tiên có giá trị chuỗi.
        foreach (var prop in element.EnumerateObject())
        {
            if (prop.Value.ValueKind == JsonValueKind.String)
            {
                var s = prop.Value.GetString();
                if (!string.IsNullOrWhiteSpace(s))
                {
                    return s;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Các tên field JSON được coi là chứa mã (không phân biệt hoa thường).
    /// </summary>
    private static readonly string[] CodeFieldNames =
    [
        "code",
        "codes",
        "serial",
        "serials",
        "serialNumber",
        "serialNo",
        "barcode",
        "ma",
        "value",
        "gtin",
    ];

    /// <summary>
    /// Đoán xem một dòng có phải dòng tiêu đề của file mã hay không.
    ///
    /// <para>
    /// Điều kiện: KHÔNG chứa chữ số (mã luôn có số hoặc ký tự đặc biệt)
    /// và khớp một tên cột thường gặp.
    /// </para>
    /// </summary>
    /// <param name="line">Dòng cần kiểm tra.</param>
    /// <returns>True nếu là dòng tiêu đề.</returns>
    public static bool IsHeaderLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return false;
        }

        foreach (var c in line)
        {
            if (char.IsDigit(c))
            {
                // Có chữ số -> gần như chắc chắn là mã, không phải tiêu đề.
                return false;
            }
        }

        // Bỏ ký tự phân tách / khoảng trắng để so sánh.
        var normalized = line
            .Replace(" ", string.Empty, StringComparison.Ordinal)
            .Replace("_", string.Empty, StringComparison.Ordinal)
            .Replace("-", string.Empty, StringComparison.Ordinal)
            .Trim()
            .ToLowerInvariant();

        foreach (var name in HeaderNames)
        {
            if (string.Equals(normalized, name, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
