using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Reflection;
using TanTienQR.Services.Config;

namespace TanTienQR.Services;

/// <summary>
/// Kiểm tra dữ liệu cấu hình (IP/host, port, số liệu) trước khi lưu.
///
/// Tách riêng khỏi ViewModel để có thể kiểm thử bằng unit test mà không
/// cần khởi tạo Avalonia / DI container.
///
/// Kiểm tra được SINH ĐỘNG từ schema (<see cref="ConfigSchemaProvider"/>):
/// mỗi field có <c>[ConfigField]</c> được kiểm tra theo các quy tắc khai báo
/// (Required / Min / Max / Validation = IpOrHost|Url|Path).
/// </summary>
public static class ConfigValidation
{
    /// <summary>
    /// Kiểm tra toàn bộ snapshot cấu hình.
    /// </summary>
    /// <param name="snapshot">Cấu hình cần kiểm tra.</param>
    /// <param name="error">Thông báo lỗi đầu tiên (null nếu hợp lệ).</param>
    /// <returns>True nếu cấu hình hợp lệ.</returns>
    public static bool Validate(AppConfigSnapshot snapshot, out string? error)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        error = null;

        foreach (var field in ConfigSchemaProvider.AllFields)
        {
            var message = ValidateField(field, snapshot);
            if (message is not null)
            {
                error = message;
                return false;
            }
        }

        // ---- Quy tắc liên-section (không thể suy ra từ 1 field) ----

        // Scanner chỉ kiểm tra TCP/COM khi đang bật.
        if (snapshot.Scanner.Enabled)
        {
            if (!IsValidIpOrHost(snapshot.Scanner.TcpHost))
            {
                error = "Host TCP của máy quét không hợp lệ.";
                return false;
            }
        }

        // Web chỉ kiểm tra URL khi đang bật.
        if (snapshot.Web.Enabled
            && !Uri.TryCreate(snapshot.Web.Url, UriKind.Absolute, out _))
        {
            error = "URL web không hợp lệ.";
            return false;
        }

        // PO server: bắt buộc là http/https.
        if (!Uri.TryCreate(snapshot.Po.BaseUrl?.Trim(), UriKind.Absolute, out var poUri)
            || (poUri.Scheme != Uri.UriSchemeHttp && poUri.Scheme != Uri.UriSchemeHttps))
        {
            error = "Địa chỉ server PO không hợp lệ (cần URL http:// hoặc https://).";
            return false;
        }

        // Số vòng PBKDF2 tối thiểu.
        if (snapshot.Auth.Pbkdf2Iterations < 10_000)
        {
            error = "Giới hạn đăng nhập phải dương và số vòng PBKDF2 tối thiểu là 10000.";
            return false;
        }

        return true;
    }

    /// <summary>
    /// Kiểm tra MỘT field theo schema (Required / Min / Max / quy tắc bổ sung).
    /// </summary>
    /// <param name="field">Schema của field.</param>
    /// <param name="snapshot">Snapshot cấu hình.</param>
    /// <returns>Thông báo lỗi (null nếu hợp lệ).</returns>
    public static string? ValidateField(ConfigFieldSchema field, AppConfigSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(field);
        ArgumentNullException.ThrowIfNull(snapshot);

        // Trường chỉ có ý nghĩa khi một công tắc khác đang bật -> bỏ qua.
        if (!ShouldValidate(field, snapshot))
        {
            return null;
        }

        var sectionValue = field.Section.ResolveOwner(snapshot);
        var value = field.GetValue(sectionValue);

        // ---- Số ----
        if (field.IsNumeric)
        {
            if (value is null)
            {
                return $"{field.Label} không được để trống.";
            }

            if (!TryToDouble(value, out var number))
            {
                return $"{field.Label} phải là số hợp lệ.";
            }

            if (field.Min > double.MinValue && number < field.Min)
            {
                return $"{field.Label} phải lớn hơn hoặc bằng {FormatNumber(field.Min)}.";
            }

            if (field.Max < double.MaxValue && number > field.Max)
            {
                return $"{field.Label} phải nhỏ hơn hoặc bằng {FormatNumber(field.Max)}.";
            }

            return null;
        }

        // ---- Bool: luôn hợp lệ ----
        if (field.Control == ConfigControlKind.Bool)
        {
            return null;
        }

        // ---- Chuỗi ----
        var text = value as string;
        var trimmed = text?.Trim() ?? string.Empty;

        if (field.Required && trimmed.Length == 0)
        {
            return $"{field.Label} không được để trống.";
        }

        if (trimmed.Length == 0)
        {
            return null;
        }

        switch (field.Validation)
        {
            case "IpOrHost":
                if (!IsValidIpOrHost(trimmed))
                {
                    return $"{field.Label} không hợp lệ (chỉ nhận địa chỉ IPv4 hoặc hostname).";
                }

                break;

            case "Url":
                if (!Uri.TryCreate(trimmed, UriKind.Absolute, out _))
                {
                    return $"{field.Label} không hợp lệ.";
                }

                break;

            case "PlcWordAddress":
            case "PlcCounterAddress":
                if (!IsValidPlcWordAddress(trimmed, field.Validation == "PlcCounterAddress" ? 10 : 1))
                    return $"{field.Label} phải là word D hợp lệ đủ vùng dữ liệu.";
                break;

            case "Path":
                // Đường dẫn chỉ cần khác rỗng (đã kiểm tra ở trên).
                break;
        }

        return null;
    }

    public static bool IsValidPlcWordAddress(string? address, int wordCount = 1)
    {
        var value = address?.Trim();
        if (string.IsNullOrEmpty(value)) return false;
        var prefix = value.StartsWith("DM", StringComparison.OrdinalIgnoreCase) ? 2
            : value.StartsWith("D", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        return prefix > 0 && int.TryParse(value.AsSpan(prefix), NumberStyles.None, CultureInfo.InvariantCulture,
            out var word) && word >= 0 && wordCount > 0 && word <= 32768 - wordCount;
    }

    /// <summary>
    /// Kiểm tra chuỗi là IPv4 hợp lệ hoặc hostname hợp lệ.
    /// </summary>
    /// <param name="value">Chuỗi cần kiểm tra.</param>
    /// <returns>True nếu hợp lệ.</returns>
    public static bool IsValidIpOrHost(string? value)    {
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return false;
        }

        if (IPAddress.TryParse(trimmed, out _))
        {
            return true;
        }

        // Hostname: chữ/số/gạch, mỗi nhãn không rỗng và không bắt đầu/kết thúc bằng '-'.
        if (trimmed.Length > 253)
        {
            return false;
        }

        var labels = trimmed.Split('.');

        // Nếu mọi nhãn đều là số thì đây là một địa chỉ IP viết sai
        // (VD "999.1.1.1") -> phải báo lỗi, không phải hostname.
        var allNumeric = labels.All(l => l.Length > 0 && l.All(char.IsAsciiDigit));

        foreach (var label in labels)
        {
            if (label.Length is 0 or > 63)
            {
                return false;
            }

            if (label[0] == '-' || label[^1] == '-')
            {
                return false;
            }

            foreach (var ch in label)
            {
                if (!(char.IsAsciiLetterOrDigit(ch) || ch == '-'))
                {
                    return false;
                }
            }
        }

        return !allNumeric;
    }

    /// <summary>
    /// Quyết định một trường có cần validate hay không.
    ///
    /// Nếu trường khai báo <c>SkipWhenDisabledBy</c> (VD: "Web:Enabled") thì
    /// khi trường đó đang FALSE, trường này được bỏ qua.
    /// </summary>
    /// <param name="field">Schema của field.</param>
    /// <param name="snapshot">Snapshot cấu hình.</param>
    /// <returns>True nếu cần validate.</returns>
    private static bool ShouldValidate(ConfigFieldSchema field, AppConfigSnapshot snapshot)
    {
        var gatePath = field.SkipWhenDisabledBy;
        if (string.IsNullOrWhiteSpace(gatePath))
        {
            return true;
        }

        var gateField = ConfigSchemaProvider.FindField(gatePath);
        if (gateField is null)
        {
            return true;
        }

        var gateSectionValue = gateField.Section.ResolveOwner(snapshot);
        var gateValue = gateField.GetValue(gateSectionValue);

        // Cổng bật/ tắt đang BẬT -> trường phụ thuộc được validate.
        return gateValue is not bool enabled || enabled;
    }

    /// <summary>
    /// Chuyển giá trị số (int/long/double/...) sang double để so sánh Min/Max.
    /// </summary>
    /// <param name="value">Giá trị gốc.</param>
    /// <param name="result">Kết quả.</param>
    /// <returns>True nếu chuyển đổi được.</returns>
    private static bool TryToDouble(object value, out double result)
    {
        switch (value)
        {
            case int i:
                result = i;
                return true;
            case long l:
                result = l;
                return true;
            case short s:
                result = s;
                return true;
            case byte b:
                result = b;
                return true;
            case uint ui:
                result = ui;
                return true;
            case ulong ul:
                result = ul;
                return true;
            case double d:
                result = d;
                return true;
            case float f:
                result = f;
                return true;
            case decimal m:
                result = (double)m;
                return true;
            default:
                result = 0;
                return false;
        }
    }

    /// <summary>
    /// Định dạng số giới hạn cho thông báo lỗi (bỏ phần thập phân thừa).
    /// </summary>
    /// <param name="value">Giá trị.</param>
    /// <returns>Chuỗi đã định dạng.</returns>
    private static string FormatNumber(double value) =>
        value == Math.Floor(value) && Math.Abs(value) < 1e15
            ? ((long)value).ToString(CultureInfo.InvariantCulture)
            : value.ToString(CultureInfo.InvariantCulture);
}

/// <summary>
/// So sánh hai snapshot cấu hình để phát hiện thay đổi.
///
/// Tách riêng khỏi ViewModel để có thể kiểm thử bằng unit test.
///
/// Thay đổi được phát hiện theo SCHEMA: duyệt toàn bộ field, so sánh giá trị
/// cũ/mới; field nào có <c>RequiresRestart = true</c> mà giá trị đổi thì bắt
/// buộc phải khởi động lại ứng dụng.
/// </summary>
public static class ConfigDiff
{
    /// <summary>
    /// Trả về danh sách các thông số QUAN TRỌNG đã thay đổi giữa hai snapshot.
    ///
    /// "Quan trọng" nghĩa là thông số được bind một lần khi app khởi động
    /// (kết nối camera, PLC, máy quét, server PO, web) — thay đổi các thông số
    /// này BẮT BUỘC phải khởi động lại ứng dụng mới có hiệu lực.
    /// </summary>
    /// <param name="before">Cấu hình trước.</param>
    /// <param name="after">Cấu hình sau.</param>
    /// <returns>Danh sách nhãn thông số đã đổi (rỗng nếu không có).</returns>
    public static List<string> DescribeCriticalChanges(AppConfigSnapshot before, AppConfigSnapshot after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);

        var changes = new List<string>();

        foreach (var field in ConfigSchemaProvider.AllFields)
        {
            if (!field.RequiresRestart)
            {
                continue;
            }

            var beforeValue = GetFieldValue(field, before);
            var afterValue = GetFieldValue(field, after);

            if (!AreFieldValuesEquivalent(field, beforeValue, afterValue))
            {
                changes.Add(field.Label);
            }
        }

        return changes;
    }

    /// <summary>
    /// So sánh toàn bộ trường hiển thị trên form (dùng cho cờ "chưa lưu").
    /// </summary>
    /// <param name="a">Snapshot A.</param>
    /// <param name="b">Snapshot B.</param>
    /// <returns>True nếu hai snapshot giống nhau.</returns>
    public static bool AreEquivalent(AppConfigSnapshot a, AppConfigSnapshot b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);

        foreach (var field in ConfigSchemaProvider.AllFields)
        {
            var valueA = GetFieldValue(field, a);
            var valueB = GetFieldValue(field, b);

            if (!AreFieldValuesEquivalent(field, valueA, valueB))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Đọc giá trị của một field từ snapshot.
    /// </summary>
    /// <param name="field">Schema của field.</param>
    /// <param name="snapshot">Snapshot nguồn.</param>
    /// <returns>Giá trị (null nếu không đọc được).</returns>
    private static object? GetFieldValue(ConfigFieldSchema field, AppConfigSnapshot snapshot)
    {
        var sectionValue = field.Section.ResolveOwner(snapshot);
        return field.GetValue(sectionValue);
    }

    /// <summary>
    /// So sánh 2 giá trị của field: chuỗi so sánh không phân biệt hoa/thường
    /// và bỏ khoảng trắng thừa, số so sánh theo giá trị.
    /// </summary>
    /// <param name="field">Schema của field.</param>
    /// <param name="a">Giá trị A.</param>
    /// <param name="b">Giá trị B.</param>
    /// <returns>True nếu tương đương.</returns>
    private static bool AreFieldValuesEquivalent(ConfigFieldSchema field, object? a, object? b)
    {
        if (a is null && b is null)
        {
            return true;
        }

        if (a is null || b is null)
        {
            return false;
        }

        if (a is string sa && b is string sb)
        {
            // Địa chỉ IP/hostname phân biệt hoa thường; các chuỗi khác
            // (mode, mức log, đường dẫn) thì không.
            var caseSensitive = string.Equals(field.Validation, "IpOrHost", StringComparison.Ordinal);

            var comparison = caseSensitive
                ? StringComparison.Ordinal
                : StringComparison.OrdinalIgnoreCase;

            return string.Equals(sa.Trim(), sb.Trim(), comparison);
        }

        return a.Equals(b);
    }
}
