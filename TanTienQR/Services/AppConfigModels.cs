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

/// <summary>
/// Toàn bộ nội dung file appsettings.json.
///
/// Các thuộc tính được đặt tên "Camera", "Plc", "Web" để khớp đúng key
/// trong file JSON (không dùng camelCase mặc định của System.Text.Json).
/// </summary>
public sealed class AppConfigSnapshot
{
    [JsonPropertyName("Camera")] public CameraConfig Camera { get; set; } = new();
    [JsonPropertyName("Plc")] public PlcConfig Plc { get; set; } = new();
    [JsonPropertyName("Web")] public WebConfig Web { get; set; } = new();
}

// ---------------------------------------------------------------- JsonContext
// Source-generated context: không dùng reflection nên an toàn với Native AOT.

[JsonSourceGenerationOptions(
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(AppConfigSnapshot))]
public sealed partial class AppConfigJsonContext : JsonSerializerContext
{
}
