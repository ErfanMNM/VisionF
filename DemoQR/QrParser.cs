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
