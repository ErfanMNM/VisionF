using System.Globalization;

namespace TanTienQR.ViewModels;

/// <summary>
/// Một dòng "tên → giá trị" trong các bảng của tab Dev.
///
/// Dùng chung cho trạng thái global, bộ đếm runtime, môi trường, cấu hình,
/// lỗi hiện tại và bộ nhớ — nên chỉ có 1 kiểu dòng duy nhất thay vì 6 kiểu.
/// </summary>
public sealed class DevKeyValueRow
{
    /// <summary>
    /// Tạo một dòng.
    /// </summary>
    /// <param name="name">Tên biến / khoá.</param>
    /// <param name="value">Giá trị hiển thị.</param>
    /// <param name="detail">Chi tiết bổ sung (mô tả, thời điểm...).</param>
    /// <param name="valueClass">Class style cho giá trị (running / warning / fault / ok).</param>
    public DevKeyValueRow(string name, string value, string? detail = null, string? valueClass = null)
    {
        Name = name;
        Value = value;
        Detail = detail ?? string.Empty;
        ValueClass = valueClass ?? string.Empty;
    }

    /// <summary>Tên biến / khoá.</summary>
    public string Name { get; }

    /// <summary>Giá trị hiển thị.</summary>
    public string Value { get; }

    /// <summary>Chi tiết bổ sung.</summary>
    public string Detail { get; }

    /// <summary>Class style cho giá trị.</summary>
    public string ValueClass { get; }

    /// <summary>True khi có chi tiết để hiện.</summary>
    public bool HasDetail => !string.IsNullOrWhiteSpace(Detail);
}

/// <summary>
/// Một dòng log kỹ thuật trong tab Dev (đọc từ bảng <c>AppLogs</c>).
/// </summary>
public sealed class DevLogRow
{
    private static readonly CultureInfo ViCulture = CreateViCulture();

    /// <summary>
    /// Tạo một dòng log.
    /// </summary>
    /// <param name="timestampUtc">Thời điểm ghi (UTC).</param>
    /// <param name="level">Mức log (Information / Warning / Error...).</param>
    /// <param name="category">Category của logger.</param>
    /// <param name="message">Nội dung log.</param>
    public DevLogRow(DateTime timestampUtc, string level, string category, string message)
    {
        TimeText = timestampUtc.ToLocalTime().ToString("HH:mm:ss.fff", ViCulture);
        Level = string.IsNullOrWhiteSpace(level) ? "—" : level;
        Category = string.IsNullOrWhiteSpace(category) ? "—" : category;
        Message = message ?? string.Empty;
    }

    /// <summary>Giờ ghi log (địa phương).</summary>
    public string TimeText { get; }

    /// <summary>Mức log.</summary>
    public string Level { get; }

    /// <summary>Category của logger.</summary>
    public string Category { get; }

    /// <summary>Nội dung log.</summary>
    public string Message { get; }

    /// <summary>Class style cho badge mức log.</summary>
    public string LevelClass => Level switch
    {
        "Error" or "Critical" => "fault",
        "Warning" => "warning",
        "Information" => "running",
        "Debug" or "Trace" => "idle",
        _ => "",
    };

    /// <summary>True khi mức log là lỗi (badge đỏ).</summary>
    public bool IsError => Level is "Error" or "Critical";

    /// <summary>True khi mức log là cảnh báo (badge vàng).</summary>
    public bool IsWarning => Level == "Warning";

    /// <summary>True khi mức log là thông tin (badge xanh).</summary>
    public bool IsInformation => Level == "Information";

    /// <summary>True khi mức log là chi tiết (debug/trace) — hiển thị chìm.</summary>
    public bool IsVerbose => Level is "Debug" or "Trace";

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

/// <summary>
/// Một dòng mô tả service đã đăng ký trong DI container.
/// </summary>
public sealed class DevServiceRow
{
    /// <summary>
    /// Tạo một dòng service.
    /// </summary>
    /// <param name="serviceType">Kiểu service.</param>
    /// <param name="lifetime">Lifetime (Singleton / Scoped / Transient).</param>
    /// <param name="implementation">Lớp implementation.</param>
    public DevServiceRow(string serviceType, string lifetime, string implementation)
    {
        ServiceType = serviceType;
        Lifetime = lifetime;
        Implementation = implementation;
    }

    /// <summary>Kiểu service (tên class).</summary>
    public string ServiceType { get; }

    /// <summary>Lifetime của đăng ký.</summary>
    public string Lifetime { get; }

    /// <summary>Lớp implementation.</summary>
    public string Implementation { get; }
}
