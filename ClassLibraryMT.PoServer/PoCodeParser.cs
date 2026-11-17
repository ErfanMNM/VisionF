using System.Text.RegularExpressions;

namespace ClassLibraryMT.PoServer;

/// <summary>
/// Tách mã quét thành (GTIN, serial).
///
/// <para>
/// Hỗ trợ 3 dạng mà MES / camera thường gửi:
/// <list type="bullet">
/// <item>Link TXNG Digital Link — <c>https://.../01/{gtin}/21/{serial}</c>.</item>
/// <item>GS1 có ngoặc — <c>(01)089...123(21)ABC123</c>.</item>
/// <item>Mã trần — chỉ có serial, GTIN lấy từ trường <c>gtin</c> của request.</item>
/// </list>
/// </para>
/// </summary>
public static partial class PoCodeParser
{
    /// <summary>Link TXNG Digital Link — /01/{gtin}/21/{serial}.</summary>
    [GeneratedRegex(@"/01/(?<gtin>\d{8,14})/21/(?<serial>[^/?#\s]+)", RegexOptions.IgnoreCase)]
    private static partial Regex DigitalLink();

    /// <summary>GS1 dạng có ngoặc — (01)gtin(21)serial.</summary>
    [GeneratedRegex(@"\(01\)(?<gtin>\d{8,14})\(21\)(?<serial>[^\(\s]+)")]
    private static partial Regex Gs1Paren();

    /// <summary>
    /// Tách một mã thành (gtin, serial).
    /// </summary>
    /// <param name="raw">Mã thô do MES gửi.</param>
    /// <param name="fallbackGtin">
    /// GTIN dùng khi mã thô không chứa GTIN (mã trần).
    /// </param>
    /// <param name="gtin">Kết quả: GTIN (có thể rỗng).</param>
    /// <param name="serial">Kết quả: serial (luôn khác rỗng nếu hợp lệ).</param>
    /// <returns>True nếu tách được serial.</returns>
    public static bool TryParse(
        string? raw,
        string? fallbackGtin,
        out string gtin,
        out string serial)
    {
        gtin = string.Empty;
        serial = string.Empty;

        var text = (raw ?? string.Empty).Trim();
        if (text.Length == 0)
        {
            return false;
        }

        var m = DigitalLink().Match(text);
        if (m.Success)
        {
            gtin = m.Groups["gtin"].Value;
            serial = m.Groups["serial"].Value;
            return true;
        }

        m = Gs1Paren().Match(text);
        if (m.Success)
        {
            gtin = m.Groups["gtin"].Value;
            serial = m.Groups["serial"].Value;
            return true;
        }

        // Mã trần -> dùng GTIN mặc định của PO (nếu có).
        serial = text;
        gtin = (fallbackGtin ?? string.Empty).Trim();
        return true;
    }
}
