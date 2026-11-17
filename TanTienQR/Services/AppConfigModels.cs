using System.Text.Json.Serialization;
using TanTienQR.Services.Config;

namespace TanTienQR.Services;

// ---------------------------------------------------------------- Model
// Cấu trúc dữ liệu tương ứng với file appsettings.json.
//
// Dùng class với property cụ thể (không dùng Dictionary) để
// System.Text.Json sinh code AOT-safe từ JsonSerializerContext.
//
// Mỗi section gắn [ConfigSection] và mỗi property gắn [ConfigField] để
// form Cấu hình được SINH ĐỘNG (xem ConfigSchemaProvider). Thêm setting mới
// chỉ cần: thêm property + [ConfigField] (+ default trong appsettings.json).
/// <summary>Cấu hình đầu đọc mã (mục "Camera").</summary>
[ConfigSection(
    Key = "Camera",
    Title = "CAMERA · ĐẦU ĐỌC MÃ",
    Order = 10,
    Icon = "Camera",
    Description = "Kết nối TCP tới đầu đọc mã. Được nạp một lần khi app khởi động.")]
public sealed class CameraConfig
{
    [ConfigField("IP đầu đọc", Order = 1, Placeholder = "VD: 192.168.1.50", RequiresRestart = true, Validation = "IpOrHost", HelpText = "Địa chỉ IPv4 hoặc hostname của đầu đọc mã.")]
    [JsonPropertyName("Ip")] public string Ip { get; set; } = "127.0.0.1";

    [ConfigField("Port TCP", Order = 2, Min = 1, Max = 65535, RequiresRestart = true)]
    [JsonPropertyName("Port")] public int Port { get; set; } = 9000;

    [ConfigField("Cửa sổ trùng mã (giây)", Order = 3, Min = 1, Max = 3600, RequiresRestart = true, HelpText = "Mã quét trùng trong cửa sổ này được coi là trùng và gửi Fail (D12 = 0).")]
    [JsonPropertyName("DuplicateWindowSeconds")] public int DuplicateWindowSeconds { get; set; } = 30;
}

/// <summary>Cấu hình PLC Omron (mục "Plc").</summary>
[ConfigSection(
    Key = "Plc",
    Title = "PLC · OMERON FINS/UDP",
    Order = 20,
    Icon = "CpuChip",
    Description = "Kết nối FINS/UDP tới PLC. Được nạp một lần khi app khởi động.")]
public sealed class PlcConfig
{
    [ConfigField("IP PLC", Order = 1, Placeholder = "VD: 192.168.1.10", RequiresRestart = true, Validation = "IpOrHost")]
    [JsonPropertyName("Ip")] public string Ip { get; set; } = "127.0.0.1";

    [ConfigField("Port FINS", Order = 2, Min = 1, Max = 65535, RequiresRestart = true)]
    [JsonPropertyName("Port")] public int Port { get; set; } = 9600;

    [ConfigField("Remote node (DA1)", Order = 3, Min = 0, Max = 255, RequiresRestart = true)]
    [JsonPropertyName("RemoteNode")] public int RemoteNode { get; set; }

    [ConfigField("Local node (SA1)", Order = 4, Min = 0, Max = 255, RequiresRestart = true)]
    [JsonPropertyName("LocalNode")] public int LocalNode { get; set; } = 1;

    [ConfigField("Timeout (ms)", Order = 5, Min = 1, Max = 60000, RequiresRestart = true)]
    [JsonPropertyName("TimeoutMs")] public int TimeoutMs { get; set; } = 1000;

    [ConfigField("Số lần thử lại", Order = 6, Min = 0, Max = 10, RequiresRestart = true)]
    [JsonPropertyName("Retries")] public int Retries { get; set; }

    [ConfigField("Kết quả (1/0)", Order = 7, Placeholder = "VD: D12", RequiresRestart = true, HelpText = "D12 = 1 khi đọc mã hợp lệ, D12 = 0 khi NoRead / FAIL / sai định dạng / trùng mã.")]
    [JsonPropertyName("ResultAddress")] public string ResultAddress { get; set; } = "D12";

    [ConfigField("Ready (1/0)", Order = 8, Placeholder = "VD: D16", RequiresRestart = true, HelpText = "D16 = 1 mỗi chu kỳ khi camera + PLC + license đều sẵn sàng.")]
    [JsonPropertyName("ReadyAddress")] public string ReadyAddress { get; set; } = "D16";

    [ConfigField("Chu kỳ heartbeat (ms)", Order = 9, Min = 100, Max = 60000, RequiresRestart = true)]
    [JsonPropertyName("RefreshIntervalMs")] public int RefreshIntervalMs { get; set; } = 1000;
}

/// <summary>Cấu hình trang web nhúng (mục "Web").</summary>
[ConfigSection(
    Key = "Web",
    Title = "WEB · CHECKEE",
    Order = 50,
    Icon = "Link",
    Description = "Trang web nhúng trong tab Sản xuất.")]
public sealed class WebConfig
{
    [ConfigField("URL trang", Order = 1, Placeholder = "https://estrace.checkee.vn", RequiresRestart = true, Validation = "Url", SkipWhenDisabledBy = "Web:Enabled")]
    [JsonPropertyName("Url")] public string Url { get; set; } = "https://estrace.checkee.vn";

    [ConfigField("Cho phép mở web", Order = 2, RequiresRestart = true, HelpText = "Bật tab Sản xuất.")]
    [JsonPropertyName("Enabled")] public bool Enabled { get; set; } = true;
}

/// <summary>
/// Cấu hình máy quét cầm tay (mục "Scanner").
///
/// Khớp với <c>HandheldOptions</c> — chế độ TCP server/client, host/port,
/// cổng COM và baud rate. Cấu hình này được bind một lần khi app khởi động
/// nên thay đổi cần restart ứng dụng.
/// </summary>
[ConfigSection(
    Key = "Scanner",
    Title = "MÁY QUÉT CẦM TAY · SCANNER",
    Order = 30,
    Icon = "Scan",
    Description = "Máy quét cầm tay kết nối TCP hoặc COM. Được nạp một lần khi app khởi động.")]
public sealed class ScannerConfig
{
    [ConfigField("Bật máy quét", Order = 1, RequiresRestart = true, HelpText = "Lắng nghe / kết nối thiết bị quét.")]
    [JsonPropertyName("Enabled")] public bool Enabled { get; set; } = true;

    [ConfigField("Chế độ", Order = 2, Options = new[] { "TcpServer", "TcpClient" }, RequiresRestart = true)]
    [JsonPropertyName("Mode")] public string Mode { get; set; } = "TcpServer";

    [ConfigField("Host TCP", Order = 3, Placeholder = "VD: 127.0.0.1", RequiresRestart = true, Validation = "IpOrHost", SkipWhenDisabledBy = "Scanner:Enabled")]
    [JsonPropertyName("TcpHost")] public string TcpHost { get; set; } = "127.0.0.1";

    [ConfigField("Port TCP", Order = 4, Min = 1, Max = 65535, RequiresRestart = true, SkipWhenDisabledBy = "Scanner:Enabled")]
    [JsonPropertyName("TcpPort")] public int TcpPort { get; set; } = 6999;

    [ConfigField("Cổng COM", Order = 5, Placeholder = "VD: COM3", RequiresRestart = true, SkipWhenDisabledBy = "Scanner:Enabled")]
    [JsonPropertyName("ComPort")] public string ComPort { get; set; } = "COM3";

    [ConfigField("Baud rate", Order = 6, Min = 1200, Max = 115200, Increment = 1200, RequiresRestart = true, SkipWhenDisabledBy = "Scanner:Enabled")]
    [JsonPropertyName("BaudRate")] public int BaudRate { get; set; } = 9600;
}

/// <summary>
/// Cấu hình server quản lý Production Order (mục "Po").
///
/// Khớp với <c>PoOptions</c>: địa chỉ server, thư mục DB, chu kỳ gửi mã
/// kích hoạt và số mã mẫu sinh cho mỗi PO.
/// </summary>
[ConfigSection(
    Key = "Po",
    Title = "SERVER PRODUCTION ORDER · PO",
    Order = 40,
    Icon = "Clipboard",
    Description = "Kết nối server quản lý Production Order. Được nạp một lần khi app khởi động.")]
public sealed class PoConfig
{
    [ConfigField("Địa chỉ server", Order = 1, Placeholder = "VD: http://127.0.0.1:5080", RequiresRestart = true, Validation = "Url")]
    [JsonPropertyName("BaseUrl")] public string BaseUrl { get; set; } = "http://127.0.0.1:5080";

    [ConfigField("Thư mục lưu PO", Order = 2, Placeholder = "VD: logs/po", RequiresRestart = true)]
    [JsonPropertyName("DbRoot")] public string DbRoot { get; set; } = "logs/po";

    [ConfigField("Chu kỳ kích hoạt (ms)", Order = 3, Min = 200, Max = 600000, Increment = 100, RequiresRestart = true)]
    [JsonPropertyName("ActivationIntervalMs")] public int ActivationIntervalMs { get; set; } = 2500;

    [ConfigField("Tự động kích hoạt", Order = 4, RequiresRestart = true, HelpText = "Gửi mã kích hoạt định kỳ.")]
    [JsonPropertyName("AutoActivate")] public bool AutoActivate { get; set; } = true;

    [ConfigField("Số mã mẫu / PO", Order = 5, Min = 0, Max = 10000, RequiresRestart = true)]
    [JsonPropertyName("CodeSampleSize")] public int CodeSampleSize { get; set; } = 100;
}

/// <summary>
/// Cấu hình kết nối Checkee Middleware API (mục "Checkee").
///
/// Khớp với <c>ClassLibraryMT.Checkee.CheckeeOptions</c> — địa chỉ host,
/// API key / client id, chu kỳ gửi mã (phút), số mã mỗi batch và các thông số
/// phụ (GTIN / loại mã / mã lô).
/// </summary>
[ConfigSection(
    Key = "Checkee",
    Title = "CHECKEE · GỬI MÃ KÍCH HOẠT",
    Order = 45,
    Icon = "Upload",
    Description = "Kết nối Checkee Middleware API để gửi mã đã quét. Được nạp một lần khi app khởi động.")]
public sealed class CheckeeConfig
{
    [ConfigField("Địa chỉ host", Order = 1, Placeholder = "VD: https://beta-api-eco.checkee.vn", RequiresRestart = true, Validation = "Url")]
    [JsonPropertyName("BaseUrl")] public string BaseUrl { get; set; } = "https://beta-api-eco.checkee.vn";

    [ConfigField("API key", Order = 2, Control = ConfigControlKind.Password, RequiresRestart = true, HelpText = "API Key do Checkee cấp cho doanh nghiệp (header x-api-key).")]
    [JsonPropertyName("ApiKey")] public string ApiKey { get; set; } = string.Empty;

    [ConfigField("Client ID", Order = 3, Control = ConfigControlKind.Password, RequiresRestart = true, HelpText = "Client Key / Client ID do Checkee cấp (header x-client-id).")]
    [JsonPropertyName("ClientId")] public string ClientId { get; set; } = string.Empty;

    [ConfigField("Bật gửi mã", Order = 4, RequiresRestart = true, HelpText = "Bật worker gửi mã đã quét lên Checkee theo chu kỳ.")]
    [JsonPropertyName("Enabled")] public bool Enabled { get; set; } = true;

    [ConfigField("Chu kỳ gửi (phút)", Order = 5, Min = 1, Max = 1440, RequiresRestart = true, HelpText = "Số phút giữa hai lần gửi mã lên Checkee.")]
    [JsonPropertyName("SendIntervalMinutes")] public int SendIntervalMinutes { get; set; } = 15;

    [ConfigField("Số mã / lần gửi", Order = 6, Min = 1, Max = 500, RequiresRestart = true, HelpText = "Số mã tối đa gửi trong một chu kỳ.")]
    [JsonPropertyName("BatchSize")] public int BatchSize { get; set; } = 100;

    [ConfigField("Timeout (giây)", Order = 7, Min = 5, Max = 600, RequiresRestart = true)]
    [JsonPropertyName("TimeoutSeconds")] public int TimeoutSeconds { get; set; } = 30;

    [ConfigField("Chỉ gửi mã Pass", Order = 8, RequiresRestart = true, HelpText = "Chỉ gửi những mã quét có kết quả hợp lệ.")]
    [JsonPropertyName("OnlyPassCodes")] public bool OnlyPassCodes { get; set; } = true;

    [ConfigField("Mã lô (batch code)", Order = 9, Placeholder = "VD: LOT-2026-01", RequiresRestart = true, HelpText = "Mã lô gửi kèm khi kích hoạt mã (bắt buộc theo API Checkee).")]
    [JsonPropertyName("BatchCode")] public string BatchCode { get; set; } = string.Empty;

    [ConfigField("Loại mã TXNG", Order = 10, Options = new[] { "DECLARE", "RANDOM", "INCREASING" }, RequiresRestart = true)]
    [JsonPropertyName("SerialType")] public string SerialType { get; set; } = "DECLARE";
}

/// <summary>Cấu hình ghi log SQLite (mục "Logging:Sqlite").</summary>
[ConfigSection(
    Key = "Sqlite",
    Title = "LỊCH SỬ · GHI LOG",
    Order = 60,
    Icon = "File",
    Description = "Nơi lưu log kỹ thuật và thao tác người dùng.")]
public sealed class SqliteLoggingConfig
{
    [ConfigField("File log SQLite", Order = 1, Placeholder = "VD: logs/app.db", Control = ConfigControlKind.Path, RequiresRestart = true)]
    [JsonPropertyName("Path")] public string Path { get; set; } = "logs/app.db";

    [ConfigField("Mức log", Order = 2, Options = new[] { "Trace", "Debug", "Information", "Warning", "Error", "Critical", "None" }, RequiresRestart = true)]
    [JsonPropertyName("MinLevel")] public string MinLevel { get; set; } = "Information";

    [ConfigField("Số ngày lưu log", Order = 3, Min = 0, Max = 3650, RequiresRestart = true)]
    [JsonPropertyName("RetentionDays")] public int RetentionDays { get; set; } = 30;

    [ConfigField("Ghi thao tác người dùng", Order = 4, HelpText = "Bật audit thao tác.", RequiresRestart = true)]
    [JsonPropertyName("EnableUserActions")] public bool EnableUserActions { get; set; } = true;

    [ConfigField("Batch ghi log", Order = 5, Min = 1, Max = 100000, RequiresRestart = true)]
    [JsonPropertyName("BatchSize")] public int BatchSize { get; set; } = 100;

    [ConfigField("Chu kỳ flush log (ms)", Order = 6, Min = 1, Max = 600000, RequiresRestart = true)]
    [JsonPropertyName("FlushIntervalMs")] public int FlushIntervalMs { get; set; } = 1000;

    [ConfigField("Chu kỳ dọn log (ms)", Order = 7, Min = 1, Max = 31536000000, RequiresRestart = true)]
    [JsonPropertyName("PruneIntervalMs")] public long PruneIntervalMs { get; set; } = 3_600_000;
}

/// <summary>Cấu hình trang Lịch sử (mục "Logging:History").</summary>
[ConfigSection(
    Key = "History",
    Title = "LỊCH SỬ · TRA CỨU",
    Order = 70,
    Icon = "Clock",
    Description = "Tuỳ chọn trang tra cứu lịch sử quét mã.")]
public sealed class LoggingHistoryConfig
{
    [ConfigField("Bật lịch sử", Order = 1, HelpText = "Cho phép tra cứu lịch sử.", RequiresRestart = true)]
    [JsonPropertyName("Enabled")] public bool Enabled { get; set; } = true;

    [ConfigField("Số dòng / trang", Order = 2, Min = 1, Max = 10000, RequiresRestart = true)]
    [JsonPropertyName("PageSize")] public int PageSize { get; set; } = 200;
}

/// <summary>
/// Cấu hình ghi log (mục "Logging").
///
/// Đây là section CHỈA chứa: các section con (Sqlite / History) được khai báo
/// ở property bên trong, mỗi property lại mang <c>[ConfigSection]</c> riêng.
/// </summary>
[ConfigSection(
    Key = "Logging",
    Title = "GHI LOG",
    Order = 55,
    Icon = "File",
    Description = "Cấu hình ghi log kỹ thuật và thao tác người dùng.")]
public sealed class LoggingConfig
{
    [JsonPropertyName("Sqlite")] public SqliteLoggingConfig Sqlite { get; set; } = new();
    [JsonPropertyName("History")] public LoggingHistoryConfig History { get; set; } = new();
}

/// <summary>
/// Cấu hình xác thực (mục "Auth").
///
/// Khớp với <see cref="ClassLibraryMT.Auth.AuthOptions"/> — chỉ dùng để đọc
/// và giữ lại khi lưu cấu hình từ UI, phần bind thực tế do lib Auth đảm nhiệm.
/// </summary>
[ConfigSection(
    Key = "Auth",
    Title = "TÀI KHOẢN · BẢO MẬT",
    Order = 80,
    Icon = "Lock",
    Description = "Cơ sở dữ liệu tài khoản và chính sách đăng nhập.")]
public sealed class AuthConfig
{
    [ConfigField("Database tài khoản", Order = 1, Placeholder = "VD: logs/users.db", Control = ConfigControlKind.Path, RequiresRestart = true)]
    [JsonPropertyName("DbPath")] public string DbPath { get; set; } = "logs/users.db";

    [ConfigField("Số lần đăng nhập sai", Order = 2, Min = 1, Max = 100, RequiresRestart = true)]
    [JsonPropertyName("MaxFailedAttempts")] public int MaxFailedAttempts { get; set; } = 5;

    [ConfigField("Khóa tài khoản (phút)", Order = 3, Min = 1, Max = 10080, RequiresRestart = true)]
    [JsonPropertyName("LockoutMinutes")] public int LockoutMinutes { get; set; } = 5;

    [ConfigField("PBKDF2 iterations", Order = 4, Min = 10000, Max = 10000000, Increment = 10000, RequiresRestart = true)]
    [JsonPropertyName("Pbkdf2Iterations")] public int Pbkdf2Iterations { get; set; } = 210_000;

    [ConfigField("Admin mặc định", Order = 5, RequiresRestart = true)]
    [JsonPropertyName("SeedAdminUser")] public string SeedAdminUser { get; set; } = "admin";

    [ConfigField("Mật khẩu seed", Order = 6, Control = ConfigControlKind.Password, RequiresRestart = true)]
    [JsonPropertyName("SeedAdminPassword")] public string SeedAdminPassword { get; set; } = "admin";
}

/// <summary>
/// Cấu hình quản lý tham số PLC (mục "PlcSettings").
///
/// Khớp với <see cref="ClassLibraryMT.Plc.PlcSettingsOptions"/> — chỉ dùng để đọc
/// và giữ lại khi lưu cấu hình từ UI, phần bind thực tế do lib Plc đảm nhiệm.
/// </summary>
[ConfigSection(
    Key = "PlcSettings",
    Title = "THAM SỐ PLC",
    Order = 90,
    Icon = "Table",
    Description = "Nơi lưu tham số / recipe của PLC.")]
public sealed class PlcSettingsConfig
{
    [ConfigField("Database tham số PLC", Order = 1, Placeholder = "VD: logs/plc.db", Control = ConfigControlKind.Path, RequiresRestart = true)]
    [JsonPropertyName("DbPath")] public string DbPath { get; set; } = "logs/plc.db";
}

/// <summary>
/// Cấu hình lưu bản ghi quét mã (mục "Scans").
///
/// Khớp với <c>ClassLibraryMT.Scans.ScanStoreOptions</c> — chỉ dùng để đọc
/// và giữ lại khi lưu cấu hình từ UI, phần bind thực tế do lib Scans đảm nhiệm.
/// </summary>
[ConfigSection(
    Key = "Scans",
    Title = "LƯU BẢN GHI QUÉT",
    Order = 100,
    Icon = "Database",
    Description = "Nơi lưu bản ghi quét mã và chính sách dọn dữ liệu.")]
public sealed class ScanConfig
{
    [ConfigField("Database bản ghi", Order = 1, Placeholder = "VD: logs/scans.db", Control = ConfigControlKind.Path, RequiresRestart = true)]
    [JsonPropertyName("DbPath")] public string DbPath { get; set; } = "logs/scans.db";

    [ConfigField("Batch ghi bản ghi", Order = 2, Min = 1, Max = 100000, RequiresRestart = true)]
    [JsonPropertyName("BatchSize")] public int BatchSize { get; set; } = 100;

    [ConfigField("Chu kỳ flush (ms)", Order = 3, Min = 1, Max = 600000, RequiresRestart = true)]
    [JsonPropertyName("FlushIntervalMs")] public int FlushIntervalMs { get; set; } = 1000;

    [ConfigField("Dung lượng hàng đợi", Order = 4, Min = 1, Max = 10000000, RequiresRestart = true)]
    [JsonPropertyName("QueueCapacity")] public int QueueCapacity { get; set; } = 20_000;

    [ConfigField("Số ngày lưu", Order = 5, Min = 0, Max = 3650, RequiresRestart = true)]
    [JsonPropertyName("RetentionDays")] public int RetentionDays { get; set; }

    [ConfigField("Chu kỳ dọn dữ liệu (ms)", Order = 6, Min = 1, Max = 31536000000, RequiresRestart = true)]
    [JsonPropertyName("PruneIntervalMs")] public long PruneIntervalMs { get; set; } = 3_600_000;
}

/// <summary>
/// Toàn bộ nội dung file appsettings.json.
///
/// Các thuộc tính được đặt tên "Camera", "Plc", "Web", "Logging", "Auth",
/// "PlcSettings" để khớp đúng key trong file JSON (không dùng camelCase mặc định
/// của System.Text.Json).
///
/// Phần "Logging", "Auth", "PlcSettings" và "Scans" được giữ nguyên khi lưu cấu hình từ UI —
/// nếu không có nó, thao tác lưu cấu hình sẽ xoá mất thiết lập ghi log, xác thực,
/// tham số PLC và lưu bản ghi quét mã.
/// </summary>
public sealed class AppConfigSnapshot
{
    [JsonPropertyName("Camera")] public CameraConfig Camera { get; set; } = new();
    [JsonPropertyName("Plc")] public PlcConfig Plc { get; set; } = new();
    [JsonPropertyName("Web")] public WebConfig Web { get; set; } = new();
    [JsonPropertyName("Scanner")] public ScannerConfig Scanner { get; set; } = new();
    [JsonPropertyName("Po")] public PoConfig Po { get; set; } = new();
    [JsonPropertyName("Checkee")] public CheckeeConfig Checkee { get; set; } = new();
    [JsonPropertyName("Logging")] public LoggingConfig Logging { get; set; } = new();
    [JsonPropertyName("Auth")] public AuthConfig Auth { get; set; } = new();
    [JsonPropertyName("PlcSettings")] public PlcSettingsConfig PlcSettings { get; set; } = new();
    [JsonPropertyName("Scans")] public ScanConfig Scans { get; set; } = new();
}

// ---------------------------------------------------------------- JsonContext
// Source-generated context: không dùng reflection nên an toàn với Native AOT.

[JsonSourceGenerationOptions(
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(AppConfigSnapshot))]
[JsonSerializable(typeof(LoggingConfig))]
[JsonSerializable(typeof(SqliteLoggingConfig))]
[JsonSerializable(typeof(LoggingHistoryConfig))]
[JsonSerializable(typeof(AuthConfig))]
[JsonSerializable(typeof(PlcSettingsConfig))]
[JsonSerializable(typeof(ScanConfig))]
[JsonSerializable(typeof(ScannerConfig))]
[JsonSerializable(typeof(PoConfig))]
[JsonSerializable(typeof(CheckeeConfig))]
[JsonSerializable(typeof(CameraConfig))]
[JsonSerializable(typeof(PlcConfig))]
[JsonSerializable(typeof(WebConfig))]
public sealed partial class AppConfigJsonContext : JsonSerializerContext
{
}
