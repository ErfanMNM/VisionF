using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;

namespace TanTienQR.Services;

/// <summary>
/// Kiểm tra dữ liệu cấu hình (IP/host, port, số liệu) trước khi lưu.
///
/// Tách riêng khỏi ViewModel để có thể kiểm thử bằng unit test mà không
/// cần khởi tạo Avalonia / DI container.
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

        // ---- Camera ----
        if (string.IsNullOrWhiteSpace(snapshot.Camera.Ip))
        {
            error = "IP camera không được để trống.";
            return false;
        }

        if (!IsValidIpOrHost(snapshot.Camera.Ip))
        {
            error = "IP camera không hợp lệ (chỉ nhận địa chỉ IPv4 hoặc hostname).";
            return false;
        }

        if (snapshot.Camera.Port is <= 0 or > 65535)
        {
            error = "Port camera phải từ 1 đến 65535.";
            return false;
        }

        if (snapshot.Camera.DuplicateWindowSeconds <= 0)
        {
            error = "Cửa sổ trùng mã phải lớn hơn 0 (giây).";
            return false;
        }

        // ---- PLC ----
        if (string.IsNullOrWhiteSpace(snapshot.Plc.Ip))
        {
            error = "IP PLC không được để trống.";
            return false;
        }

        if (!IsValidIpOrHost(snapshot.Plc.Ip))
        {
            error = "IP PLC không hợp lệ (chỉ nhận địa chỉ IPv4 hoặc hostname).";
            return false;
        }

        if (snapshot.Plc.Port is <= 0 or > 65535)
        {
            error = "Port PLC phải từ 1 đến 65535.";
            return false;
        }

        if (snapshot.Plc.RemoteNode is < 0 or > 255 || snapshot.Plc.LocalNode is < 0 or > 255)
        {
            error = "Node PLC phải từ 0 đến 255.";
            return false;
        }

        if (snapshot.Plc.TimeoutMs <= 0)
        {
            error = "Timeout PLC phải lớn hơn 0 (ms).";
            return false;
        }

        if (snapshot.Plc.Retries < 0)
        {
            error = "Số lần thử lại không được âm.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(snapshot.Plc.ResultAddress)
            || string.IsNullOrWhiteSpace(snapshot.Plc.ReadyAddress))
        {
            error = "Địa chỉ vùng D (D12 / D16) không được để trống.";
            return false;
        }

        if (snapshot.Plc.RefreshIntervalMs <= 0)
        {
            error = "Chu kỳ heartbeat phải lớn hơn 0 (ms).";
            return false;
        }

        // ---- Scanner ----
        if (snapshot.Scanner.Enabled)
        {
            if (string.IsNullOrWhiteSpace(snapshot.Scanner.TcpHost))
            {
                error = "Host TCP của máy quét không được để trống.";
                return false;
            }

            if (!IsValidIpOrHost(snapshot.Scanner.TcpHost))
            {
                error = "Host TCP của máy quét không hợp lệ.";
                return false;
            }

            if (snapshot.Scanner.TcpPort is <= 0 or > 65535)
            {
                error = "Port TCP của máy quét phải từ 1 đến 65535.";
                return false;
            }

            if (string.IsNullOrWhiteSpace(snapshot.Scanner.ComPort))
            {
                error = "Cổng COM của máy quét không được để trống.";
                return false;
            }

            if (snapshot.Scanner.BaudRate <= 0)
            {
                error = "Baud rate của máy quét phải lớn hơn 0.";
                return false;
            }
        }

        // ---- Production Order server ----
        if (string.IsNullOrWhiteSpace(snapshot.Po.BaseUrl)
            || !Uri.TryCreate(snapshot.Po.BaseUrl.Trim(), UriKind.Absolute, out var poUri)
            || (poUri.Scheme != Uri.UriSchemeHttp && poUri.Scheme != Uri.UriSchemeHttps))
        {
            error = "Địa chỉ server PO không hợp lệ (cần URL http:// hoặc https://).";
            return false;
        }

        if (string.IsNullOrWhiteSpace(snapshot.Po.DbRoot))
        {
            error = "Thư mục lưu dữ liệu PO không được để trống.";
            return false;
        }

        if (snapshot.Po.ActivationIntervalMs <= 0)
        {
            error = "Chu kỳ gửi mã kích hoạt PO phải lớn hơn 0 (ms).";
            return false;
        }

        if (snapshot.Po.CodeSampleSize < 0)
        {
            error = "Số mã mẫu PO không được âm.";
            return false;
        }

        // ---- Web ----
        if (snapshot.Web.Enabled
            && !Uri.TryCreate(snapshot.Web.Url, UriKind.Absolute, out _))
        {
            error = "URL web không hợp lệ.";
            return false;
        }

        // ---- Logging / history ----
        if (string.IsNullOrWhiteSpace(snapshot.Logging.Sqlite.Path)
            || string.IsNullOrWhiteSpace(snapshot.Logging.Sqlite.MinLevel))
        {
            error = "Đường dẫn và mức log không được để trống.";
            return false;
        }
        if (snapshot.Logging.Sqlite.RetentionDays < 0 || snapshot.Logging.Sqlite.BatchSize <= 0
            || snapshot.Logging.Sqlite.FlushIntervalMs <= 0 || snapshot.Logging.Sqlite.PruneIntervalMs <= 0)
        {
            error = "Thông số lưu log phải có giá trị hợp lệ và dương (số ngày lưu có thể bằng 0).";
            return false;
        }
        if (snapshot.Logging.History.PageSize <= 0)
        {
            error = "Kích thước trang lịch sử phải lớn hơn 0.";
            return false;
        }

        // ---- Auth / data stores ----
        if (string.IsNullOrWhiteSpace(snapshot.Auth.DbPath)
            || string.IsNullOrWhiteSpace(snapshot.Auth.SeedAdminUser)
            || string.IsNullOrWhiteSpace(snapshot.Auth.SeedAdminPassword))
        {
            error = "Đường dẫn cơ sở dữ liệu tài khoản và thông tin admin không được để trống.";
            return false;
        }
        if (snapshot.Auth.MaxFailedAttempts <= 0 || snapshot.Auth.LockoutMinutes <= 0
            || snapshot.Auth.Pbkdf2Iterations < 10_000)
        {
            error = "Giới hạn đăng nhập phải dương và số vòng PBKDF2 tối thiểu là 10000.";
            return false;
        }
        if (string.IsNullOrWhiteSpace(snapshot.PlcSettings.DbPath)
            || string.IsNullOrWhiteSpace(snapshot.Scans.DbPath))
        {
            error = "Đường dẫn cơ sở dữ liệu PLC và bản ghi quét không được để trống.";
            return false;
        }
        if (snapshot.Scans.BatchSize <= 0 || snapshot.Scans.FlushIntervalMs <= 0
            || snapshot.Scans.QueueCapacity <= 0 || snapshot.Scans.RetentionDays < 0
            || snapshot.Scans.PruneIntervalMs <= 0)
        {
            error = "Thông số lưu bản ghi quét không hợp lệ.";
            return false;
        }

        return true;
    }

    /// <summary>
    /// Kiểm tra chuỗi là IPv4 hợp lệ hoặc hostname hợp lệ.
    /// </summary>
    /// <param name="value">Chuỗi cần kiểm tra.</param>
    /// <returns>True nếu hợp lệ.</returns>
    public static bool IsValidIpOrHost(string? value)
    {
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
}

/// <summary>
/// So sánh hai snapshot cấu hình để phát hiện thay đổi.
///
/// Tách riêng khỏi ViewModel để có thể kiểm thử bằng unit test.
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

        // ---- Camera (Reader singleton) ----
        if (!string.Equals(before.Camera.Ip?.Trim(), after.Camera.Ip?.Trim(), StringComparison.Ordinal))
        {
            changes.Add("IP camera");
        }

        if (before.Camera.Port != after.Camera.Port)
        {
            changes.Add("Port camera");
        }

        // DuplicateWindowSeconds nằm trong CameraOptions -> bind lúc start.
        if (before.Camera.DuplicateWindowSeconds != after.Camera.DuplicateWindowSeconds)
        {
            changes.Add("Cửa sổ trùng mã của camera");
        }

        // ---- PLC (OmronPlc singleton + PlcClientOptions) ----
        if (!string.Equals(before.Plc.Ip?.Trim(), after.Plc.Ip?.Trim(), StringComparison.Ordinal))
        {
            changes.Add("IP PLC");
        }

        if (before.Plc.Port != after.Plc.Port)
        {
            changes.Add("Port PLC");
        }

        if (before.Plc.RemoteNode != after.Plc.RemoteNode)
        {
            changes.Add("Remote node PLC");
        }

        if (before.Plc.LocalNode != after.Plc.LocalNode)
        {
            changes.Add("Local node PLC");
        }

        if (before.Plc.TimeoutMs != after.Plc.TimeoutMs)
        {
            changes.Add("Timeout PLC");
        }

        if (before.Plc.Retries != after.Plc.Retries)
        {
            changes.Add("Số lần thử lại PLC");
        }

        if (!string.Equals(before.Plc.ResultAddress?.Trim(), after.Plc.ResultAddress?.Trim(), StringComparison.OrdinalIgnoreCase)
            || !string.Equals(before.Plc.ReadyAddress?.Trim(), after.Plc.ReadyAddress?.Trim(), StringComparison.OrdinalIgnoreCase)
            || before.Plc.RefreshIntervalMs != after.Plc.RefreshIntervalMs)
        {
            changes.Add("Thông số vùng nhớ D / heartbeat PLC");
        }

        // ---- Scanner (HandheldScannerService singleton) ----
        if (before.Scanner.Enabled != after.Scanner.Enabled)
        {
            changes.Add("Bật/tắt máy quét");
        }

        if (!string.Equals(
                before.Scanner.Mode?.Trim(),
                after.Scanner.Mode?.Trim(),
                StringComparison.OrdinalIgnoreCase))
        {
            changes.Add("Chế độ kết nối máy quét");
        }

        if (!string.Equals(before.Scanner.TcpHost?.Trim(), after.Scanner.TcpHost?.Trim(), StringComparison.Ordinal))
        {
            changes.Add("Host TCP máy quét");
        }

        if (before.Scanner.TcpPort != after.Scanner.TcpPort)
        {
            changes.Add("Port TCP máy quét");
        }

        if (!string.Equals(before.Scanner.ComPort?.Trim(), after.Scanner.ComPort?.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            changes.Add("Cổng COM máy quét");
        }

        if (before.Scanner.BaudRate != after.Scanner.BaudRate)
        {
            changes.Add("Baud rate máy quét");
        }

        // ---- Production Order server ----
        if (!string.Equals(before.Po.BaseUrl?.Trim(), after.Po.BaseUrl?.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            changes.Add("Địa chỉ server PO");
        }

        if (!string.Equals(before.Po.DbRoot?.Trim(), after.Po.DbRoot?.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            changes.Add("Thư mục lưu PO");
        }

        if (before.Po.ActivationIntervalMs != after.Po.ActivationIntervalMs)
        {
            changes.Add("Chu kỳ gửi mã kích hoạt PO");
        }

        if (before.Po.AutoActivate != after.Po.AutoActivate)
        {
            changes.Add("Tự động gửi mã kích hoạt PO");
        }

        if (before.Po.CodeSampleSize != after.Po.CodeSampleSize)
        {
            changes.Add("Số mã mẫu PO");
        }

        // ---- Web (WebOptions bind lúc start) ----
        if (!string.Equals(before.Web.Url?.Trim(), after.Web.Url?.Trim(), StringComparison.OrdinalIgnoreCase)
            || before.Web.Enabled != after.Web.Enabled)
        {
            changes.Add("Cấu hình web Checkee");
        }

        if (!AreInternalSettingsEquivalent(before, after))
        {
            changes.Add("Cấu hình log, xác thực hoặc lưu dữ liệu");
        }

        return changes;
    }

    private static bool AreInternalSettingsEquivalent(AppConfigSnapshot a, AppConfigSnapshot b) =>
        a.Logging.Sqlite.Path == b.Logging.Sqlite.Path
        && a.Logging.Sqlite.MinLevel == b.Logging.Sqlite.MinLevel
        && a.Logging.Sqlite.RetentionDays == b.Logging.Sqlite.RetentionDays
        && a.Logging.Sqlite.EnableUserActions == b.Logging.Sqlite.EnableUserActions
        && a.Logging.Sqlite.BatchSize == b.Logging.Sqlite.BatchSize
        && a.Logging.Sqlite.FlushIntervalMs == b.Logging.Sqlite.FlushIntervalMs
        && a.Logging.Sqlite.PruneIntervalMs == b.Logging.Sqlite.PruneIntervalMs
        && a.Logging.History.Enabled == b.Logging.History.Enabled
        && a.Logging.History.PageSize == b.Logging.History.PageSize
        && a.Auth.DbPath == b.Auth.DbPath
        && a.Auth.MaxFailedAttempts == b.Auth.MaxFailedAttempts
        && a.Auth.LockoutMinutes == b.Auth.LockoutMinutes
        && a.Auth.Pbkdf2Iterations == b.Auth.Pbkdf2Iterations
        && a.Auth.SeedAdminUser == b.Auth.SeedAdminUser
        && a.Auth.SeedAdminPassword == b.Auth.SeedAdminPassword
        && a.PlcSettings.DbPath == b.PlcSettings.DbPath
        && a.Scans.DbPath == b.Scans.DbPath
        && a.Scans.BatchSize == b.Scans.BatchSize
        && a.Scans.FlushIntervalMs == b.Scans.FlushIntervalMs
        && a.Scans.QueueCapacity == b.Scans.QueueCapacity
        && a.Scans.RetentionDays == b.Scans.RetentionDays
        && a.Scans.PruneIntervalMs == b.Scans.PruneIntervalMs;

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

        return string.Equals(a.Camera.Ip?.Trim(), b.Camera.Ip?.Trim(), StringComparison.Ordinal)
            && a.Camera.Port == b.Camera.Port
            && a.Camera.DuplicateWindowSeconds == b.Camera.DuplicateWindowSeconds
            && string.Equals(a.Plc.Ip?.Trim(), b.Plc.Ip?.Trim(), StringComparison.Ordinal)
            && a.Plc.Port == b.Plc.Port
            && a.Plc.RemoteNode == b.Plc.RemoteNode
            && a.Plc.LocalNode == b.Plc.LocalNode
            && a.Plc.TimeoutMs == b.Plc.TimeoutMs
            && a.Plc.Retries == b.Plc.Retries
            && string.Equals(a.Plc.ResultAddress?.Trim(), b.Plc.ResultAddress?.Trim(), StringComparison.OrdinalIgnoreCase)
            && string.Equals(a.Plc.ReadyAddress?.Trim(), b.Plc.ReadyAddress?.Trim(), StringComparison.OrdinalIgnoreCase)
            && a.Plc.RefreshIntervalMs == b.Plc.RefreshIntervalMs
            && a.Scanner.Enabled == b.Scanner.Enabled
            && string.Equals(
                a.Scanner.Mode?.Trim(),
                b.Scanner.Mode?.Trim(),
                StringComparison.OrdinalIgnoreCase)
            && string.Equals(a.Scanner.TcpHost?.Trim(), b.Scanner.TcpHost?.Trim(), StringComparison.Ordinal)
            && a.Scanner.TcpPort == b.Scanner.TcpPort
            && string.Equals(a.Scanner.ComPort?.Trim(), b.Scanner.ComPort?.Trim(), StringComparison.OrdinalIgnoreCase)
            && a.Scanner.BaudRate == b.Scanner.BaudRate
            && string.Equals(a.Po.BaseUrl?.Trim(), b.Po.BaseUrl?.Trim(), StringComparison.OrdinalIgnoreCase)
            && string.Equals(a.Po.DbRoot?.Trim(), b.Po.DbRoot?.Trim(), StringComparison.OrdinalIgnoreCase)
            && a.Po.ActivationIntervalMs == b.Po.ActivationIntervalMs
            && a.Po.AutoActivate == b.Po.AutoActivate
            && a.Po.CodeSampleSize == b.Po.CodeSampleSize
            && string.Equals(a.Web.Url?.Trim(), b.Web.Url?.Trim(), StringComparison.OrdinalIgnoreCase)
            && a.Web.Enabled == b.Web.Enabled
            && AreInternalSettingsEquivalent(a, b);
    }
}
