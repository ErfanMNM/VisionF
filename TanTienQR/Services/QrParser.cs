using System.Text.RegularExpressions;

namespace TanTienQR.Services;

/// <summary>Kiểm tra cấu trúc GS1 AI 01/21; không thay đổi CodeContent dùng để đối chiếu PO.</summary>
internal static partial class QrParser
{
    public const string NoReadToken = "NoRead";
    public const string FailToken = "FAIL";

    public static bool IsNoRead(string raw) =>
        string.Equals(raw?.Trim(), NoReadToken, StringComparison.OrdinalIgnoreCase)
        || string.Equals(raw?.Trim(), FailToken, StringComparison.OrdinalIgnoreCase);

    // Cho phép GTIN ngắn trong Digital Link để tương thích dữ liệu eTrace hiện có.
    // Neo toàn chuỗi để không nhận mã chỉ chứa một đoạn GS1 giữa nội dung rác.
    [GeneratedRegex(@"\A(?:(?<origin>https?://[^/?#\s\\]+)(?:/[^/?#\s\\]+)*)?/01/(?:[0-9]{8}|[0-9]{12,14})/21/(?<serial>[^/?#\s\\]+)(?:\?[^#\s\\]*)?(?:#[^\s\\]*)?\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DigitalLink();

    [GeneratedRegex(@"\A\(01\)[0-9]{14}\(21\)(?<serial>[^\r\n]{1,20})\z", RegexOptions.CultureInvariant)]
    private static partial Regex Gs1Parenthesized();

    [GeneratedRegex(@"\A(?:\]C1|\]d2|\]Q3)?\x1d?01[0-9]{14}\x1d?21(?<serial>[^\x1d\r\n]{1,20})\x1d?\z", RegexOptions.CultureInvariant)]
    private static partial Regex Gs1ElementString();

    /// <summary>
    /// Nhận Digital Link, dạng (01)…(21)… hoặc element string từ đầu đọc GS1.
    /// Chỉ kiểm tra cấu trúc, không kiểm checksum GTIN hoặc tách serial làm khóa PO.
    /// </summary>
    public static bool HasGs1Structure(string raw)
    {
        if (string.IsNullOrEmpty(raw)) return false;

        var match = DigitalLink().Match(raw);
        if (match.Success)
        {
            if (match.Groups["origin"].Success
                && (!Uri.TryCreate(raw, UriKind.Absolute, out var uri)
                    || string.IsNullOrEmpty(uri.Host) || !string.IsNullOrEmpty(uri.UserInfo)))
                return false;

            var serial = match.Groups["serial"].Value;
            // Kiểm toàn Digital Link để query, fragment hoặc resolver prefix cũng
            // không lọt escape hỏng; chỉ decode serial, giữ nguyên CodeContent.
            for (var i = 0; i < raw.Length; i++)
            {
                if (raw[i] != '%') continue;
                if (i + 2 >= raw.Length || !Uri.IsHexDigit(raw[i + 1]) || !Uri.IsHexDigit(raw[i + 2]))
                    return false;
                i += 2;
            }
            return IsSerial(Uri.UnescapeDataString(serial));
        }

        match = Gs1Parenthesized().Match(raw);
        if (!match.Success) match = Gs1ElementString().Match(raw);
        return match.Success && IsSerial(match.Groups["serial"].Value);
    }

    private static bool IsSerial(string serial)
    {
        if (serial.Length is < 1 or > 20) return false;
        foreach (var c in serial)
        {
            // Bộ ký tự GS1 AI encodable character set 82, dùng cho AI (21).
            if (c is >= '0' and <= '9' or >= 'A' and <= 'Z' or >= 'a' and <= 'z') continue;
            if (!"!\"%&'()*+,-./:;<=>?_".Contains(c)) return false;
        }
        return true;
    }
}
