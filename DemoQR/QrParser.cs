using System.Text.RegularExpressions;

namespace QRDemoCodeBackend;

// ---------------------------------------------------------------- Parser nội dung QR (source-generated regex, tuong thich AOT)
internal static partial class QrParser
{
    // Link TXNG: https://.../01/{gtin}/21/{serialNumber}
    [GeneratedRegex(@"/01/(?<gtin>\d{8,14})/21/(?<serial>[^/?#\s]+)", RegexOptions.IgnoreCase)]
    private static partial Regex DigitalLink();

    // GS1 dạng có ngoặc: (01)08934567890123(21)ABC123
    [GeneratedRegex(@"\(01\)(?<gtin>\d{8,14})\(21\)(?<serial>[^\(\s]+)")]
    private static partial Regex Gs1Paren();

    /// <summary>
    /// Chuỗi camera báo lỗi đọc mã (yêu cầu của khách hàng).
    /// </summary>
    public const string NoReadToken = "NoRead";

    /// <summary>
    /// Chuỗi camera báo lỗi đọc mã (dùng trong dự án TApp).
    /// </summary>
    public const string FailToken = "FAIL";

    /// <summary>
    /// Kiểm tra chuỗi do camera gửi có phải thông báo lỗi đọc mã không.
    ///
    /// Hỗ trợ cả "NoRead" và "FAIL", không phân biệt hoa thường.
    /// Phải kiểm tra trước <see cref="TryParse"/> vì 2 chuỗi này không khớp
    /// pattern TXNG/GS1 nên sẽ bị coi là mã sai định dạng.
    /// </summary>
    public static bool IsNoRead(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return false;

        var text = raw.Trim();
        return string.Equals(text, NoReadToken, StringComparison.OrdinalIgnoreCase)
            || string.Equals(text, FailToken, StringComparison.OrdinalIgnoreCase);
    }

    public static bool TryParse(string raw, out string gtin, out string serial)
    {
        gtin = serial = "";
        if (string.IsNullOrWhiteSpace(raw)) return false;

        var text = raw.Trim();
        var m = DigitalLink().Match(text);
        if (!m.Success) m = Gs1Paren().Match(text);
        if (!m.Success) return false;

        gtin = m.Groups["gtin"].Value;
        serial = Uri.UnescapeDataString(m.Groups["serial"].Value);
        return true;
    }
}
