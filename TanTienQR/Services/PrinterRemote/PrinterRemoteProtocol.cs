using System.Text.Json.Serialization;

namespace TanTienQR.Services.PrinterRemote;

// Hợp đồng IPC dùng chung; secret chỉ đi trong pipe riêng của người dùng Windows.
public sealed class PrinterRemoteMessage
{
    public string Kind { get; set; } = "";
    public long ParentHandle { get; set; }
    public string? Host { get; set; }
    public int Port { get; set; }
    public string? Username { get; set; }
    public string? Password { get; set; }
    public bool Visible { get; set; }
    public int TimeoutSeconds { get; set; }
    public string? State { get; set; }
    public string? Failure { get; set; }
    public string? Message { get; set; }
}

[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(PrinterRemoteMessage))]
public sealed partial class PrinterRemoteJsonContext : JsonSerializerContext;
