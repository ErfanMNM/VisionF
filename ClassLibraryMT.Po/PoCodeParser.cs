using System.Text.RegularExpressions;

namespace ClassLibraryMT.Po;

/// <summary>Kiểm tra cấu trúc mã GS1 và lấy GTIN AI (01) từ CodeContent.</summary>
public static partial class PoCodeParser
{
    private static readonly Regex DigitalLink = new(
        @"\A(?:(?:https?://[^/?#\s\\]+)(?:/(?![0-9]{2,4}(?:/|$))[^/?#\s\\]+)*)?/01/(?<gtin>[0-9]{8}|[0-9]{12,14})/21/(?<serial>[^/?#\s\\]+)(?:\?[^#\s\\]*)?(?:#[^\s\\]*)?\z",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex Parenthesized = new(
        @"\A\(01\)(?<gtin>[0-9]{14})\(21\)(?<serial>[^\r\n]{1,20})\z",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex ElementString = new(
        @"\A(?:\]C1|\]d2|\]Q3)?\x1d?01(?<gtin>[0-9]{14})\x1d?21(?<serial>[^\x1d\r\n]{1,20})\x1d?\z",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>Trả về GTIN AI (01) nếu mã có cấu trúc GS1 hợp lệ.</summary>
    public static bool TryGetGtin(string? code, out string gtin)
    {
        gtin = string.Empty;
        return TryGetSourceGtin(code, out var sourceGtin) && TryNormalizeGtin(sourceGtin, out gtin);
    }

    /// <summary>Lấy nguyên GTIN AI (01), không thêm hoặc bỏ số 0 đầu.</summary>
    public static bool TryGetSourceGtin(string? code, out string gtin)
    {
        gtin = string.Empty;
        if (string.IsNullOrEmpty(code)) return false;
        var match = DigitalLink.Match(code);
        if (match.Success && HasRepeatedQueryAi(code)) return false;
        if (!match.Success) match = Parenthesized.Match(code);
        if (!match.Success) match = ElementString.Match(code);
        if (!match.Success) return false;
        var serial = match.Groups["serial"].Value;
        if (!IsSerial(serial) || serial.Contains("(01)", StringComparison.Ordinal)
            || serial.Contains("(21)", StringComparison.Ordinal)) return false;
        var value = match.Groups["gtin"].Value;
        if (!IsValidGtin(value)) return false;
        gtin = value;
        return true;
    }

    private static bool HasRepeatedQueryAi(string code)
    {
        var queryStart = code.IndexOf('?');
        if (queryStart < 0) return false;
        var fragmentStart = code.IndexOf('#');
        if (fragmentStart >= 0 && fragmentStart < queryStart) return false;
        var query = code.AsSpan(queryStart + 1,
            (fragmentStart < 0 ? code.Length : fragmentStart) - queryStart - 1);
        foreach (var item in query.ToString().Split('&'))
        {
            var separator = item.IndexOf('=');
            var key = Uri.UnescapeDataString(separator < 0 ? item : item[..separator]);
            if (key is "01" or "21") return true;
        }
        return false;
    }

    /// <summary>Tạo khóa GTIN 14 số chỉ để so khớp nội bộ; không dùng làm GTIN lưu hoặc gửi.</summary>
    public static bool TryNormalizeGtin(string? value, out string gtin)
    {
        gtin = string.Empty;
        if (!IsValidGtin(value)) return false;
        gtin = value!.PadLeft(14, '0');
        return true;
    }

    /// <summary>Kiểm tra GTIN dạng số 8, 12, 13 hoặc 14 chữ số và checksum.</summary>
    public static bool IsValidGtin(string? value)
    {
        if (value is null || value.Length is not (8 or 12 or 13 or 14)
            || value.Any(c => c is < '0' or > '9')) return false;
        var sum = 0;
        var checkIndex = value.Length - 1;
        for (var i = checkIndex - 1; i >= 0; i--)
        {
            var digit = value[i] - '0';
            var positionFromRight = checkIndex - i;
            sum += digit * (positionFromRight % 2 == 1 ? 3 : 1);
        }
        return (10 - sum % 10) % 10 == value[checkIndex] - '0';
    }

    private static bool IsSerial(string serial)
    {
        if (serial.Length is < 1 or > 20) return false;
        foreach (var c in serial)
        {
            if (c is >= '0' and <= '9' or >= 'A' and <= 'Z' or >= 'a' and <= 'z') continue;
            if (!"!\"%&'()*+,-./:;<=>?_".Contains(c)) return false;
        }
        return true;
    }
}
