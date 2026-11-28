namespace ClassLibraryMT.Licensing;

/// <summary>
/// Các quyền module được ghi trong payload license đã ký.
/// </summary>
public static class LicenseFeatures
{
    /// <summary>Quyền dùng module máy in và kết nối máy in.</summary>
    public const string Printer = "printer";
}

/// <summary>Tiện ích kiểm tra quyền module của license hiện tại.</summary>
public static class LicenseEntitlements
{
    /// <summary>
    /// Chuẩn hoá danh sách quyền trước khi ký để payload có thứ tự ổn định.
    /// </summary>
    public static IReadOnlyList<string> Normalize(IEnumerable<string>? features)
    {
        if (features is null)
            return [];

        var result = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var feature in features)
        {
            var value = (feature ?? string.Empty).Trim();
            if (value.Length == 0)
                continue;
            if (value.Length > 64)
                throw new ArgumentException("Tên entitlement không được dài quá 64 ký tự.", nameof(features));

            foreach (var ch in value)
            {
                if (!(char.IsAsciiLetterOrDigit(ch) || ch is '.' or '-' or '_'))
                    throw new ArgumentException("Entitlement chỉ được chứa chữ, số, '.', '-' hoặc '_'.", nameof(features));
            }

            result.Add(value);
        }

        if (result.Count > 32)
            throw new ArgumentException("License chỉ được chứa tối đa 32 entitlement.", nameof(features));

        return result.ToArray();
    }

    /// <summary>Kiểm tra license hợp lệ có entitlement được yêu cầu hay không.</summary>
    public static bool HasFeature(LicenseCheck check, string feature)
    {
        if (!check.Ok || check.Payload is null || string.IsNullOrWhiteSpace(feature))
            return false;

        return check.Payload.Features.Any(item =>
            string.Equals(item, feature.Trim(), StringComparison.OrdinalIgnoreCase));
    }
}
