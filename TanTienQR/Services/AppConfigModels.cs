using System.Text.Json.Serialization;

namespace TanTienQR.Services;

// ---------------------------------------------------------------- Model
// Cấu trúc dữ liệu tương ứng với file appsettings.json.
//
// Dùng class với property cụ thể (không dùng Dictionary) để
// System.Text.Json sinh code AOT-safe từ JsonSerializerContext.

/// <summary>Cấu hình đầu đọc mã (mục "Camera").</summary>
public sealed class CameraConfig
{
    [JsonPropertyName("Ip")] public string Ip { get; set; } = "127.0.0.1";
    [JsonPropertyName("Port")] public int Port { get; set; } = 9000;
    [JsonPropertyName("DuplicateWindowSeconds")] public int DuplicateWindowSeconds { get; set; } = 30;
}

/// <summary>Cấu hình PLC Omron (mục "Plc").</summary>
public sealed class PlcConfig
{
    [JsonPropertyName("Ip")] public string Ip { get; set; } = "127.0.0.1";
    [JsonPropertyName("Port")] public int Port { get; set; } = 9600;
    [JsonPropertyName("RemoteNode")] public int RemoteNode { get; set; }
    [JsonPropertyName("LocalNode")] public int LocalNode { get; set; } = 1;
    [JsonPropertyName("TimeoutMs")] public int TimeoutMs { get; set; } = 1000;
    [JsonPropertyName("Retries")] public int Retries { get; set; }
    [JsonPropertyName("ResultAddress")] public string ResultAddress { get; set; } = "D12";
    [JsonPropertyName("ReadyAddress")] public string ReadyAddress { get; set; } = "D16";
    [JsonPropertyName("RefreshIntervalMs")] public int RefreshIntervalMs { get; set; } = 1000;
}

/// <summary>Cấu hình trang web nhúng (mục "Web").</summary>
public sealed class WebConfig
{
    [JsonPropertyName("Url")] public string Url { get; set; } = "https://estrace.checkee.vn";
    [JsonPropertyName("Enabled")] public bool Enabled { get; set; } = true;
}

/// <summary>Cấu hình ghi log SQLite (mục "Logging:Sqlite").</summary>
public sealed class SqliteLoggingConfig
{
    [JsonPropertyName("Path")] public string Path { get; set; } = "logs/app.db";
    [JsonPropertyName("MinLevel")] public string MinLevel { get; set; } = "Information";
    [JsonPropertyName("RetentionDays")] public int RetentionDays { get; set; } = 30;
    [JsonPropertyName("EnableUserActions")] public bool EnableUserActions { get; set; } = true;
    [JsonPropertyName("BatchSize")] public int BatchSize { get; set; } = 100;
    [JsonPropertyName("FlushIntervalMs")] public int FlushIntervalMs { get; set; } = 1000;
    [JsonPropertyName("PruneIntervalMs")] public int PruneIntervalMs { get; set; } = 3_600_000;
}

/// <summary>Cấu hình trang Lịch sử (mục "Logging:History").</summary>
public sealed class LoggingHistoryConfig
{
    [JsonPropertyName("Enabled")] public bool Enabled { get; set; } = true;
    [JsonPropertyName("PageSize")] public int PageSize { get; set; } = 200;
}

/// <summary>Cấu hình ghi log (mục "Logging").</summary>
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
public sealed class AuthConfig
{
    [JsonPropertyName("DbPath")] public string DbPath { get; set; } = "logs/users.db";
    [JsonPropertyName("MaxFailedAttempts")] public int MaxFailedAttempts { get; set; } = 5;
    [JsonPropertyName("LockoutMinutes")] public int LockoutMinutes { get; set; } = 5;
    [JsonPropertyName("Pbkdf2Iterations")] public int Pbkdf2Iterations { get; set; } = 210_000;
    [JsonPropertyName("SeedAdminUser")] public string SeedAdminUser { get; set; } = "admin";
    [JsonPropertyName("SeedAdminPassword")] public string SeedAdminPassword { get; set; } = "admin";
}

/// <summary>
/// Toàn bộ nội dung file appsettings.json.
///
/// Các thuộc tính được đặt tên "Camera", "Plc", "Web", "Logging", "Auth" để khớp đúng
/// key trong file JSON (không dùng camelCase mặc định của System.Text.Json).
///
/// Phần "Logging" và "Auth" được giữ nguyên khi lưu cấu hình từ UI — nếu không có nó,
/// thao tác lưu cấu hình sẽ xoá mất thiết lập ghi log và xác thực.
/// </summary>
public sealed class AppConfigSnapshot
{
    [JsonPropertyName("Camera")] public CameraConfig Camera { get; set; } = new();
    [JsonPropertyName("Plc")] public PlcConfig Plc { get; set; } = new();
    [JsonPropertyName("Web")] public WebConfig Web { get; set; } = new();
    [JsonPropertyName("Logging")] public LoggingConfig Logging { get; set; } = new();
    [JsonPropertyName("Auth")] public AuthConfig Auth { get; set; } = new();
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
public sealed partial class AppConfigJsonContext : JsonSerializerContext
{
}
