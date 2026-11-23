using System.Text.Json.Serialization;

namespace ClassLibraryMT.Print;

public enum PrintCodeState
{
    Pending = 0,
    Sent = 1,
    Printed = 2,
    NoResponse = 3,
}

public enum PrinterConnectionState
{
    Disconnected = 0,
    Connecting = 1,
    Connected = 2,
    Faulted = 3,
}

public sealed class PrintOptions
{
    public const string SectionName = "Print";

    public bool Enabled { get; set; }
    public string DefaultPrinterId { get; set; } = "g10-1";
    public int BatchSize { get; set; } = 100;
    public int ResponseTimeoutMs { get; set; } = 1500;
    public int ReconcileIntervalMs { get; set; } = 2500;
    public List<PrinterOptions> Printers { get; set; } = [];
}

public sealed class PrinterOptions
{
    public string Id { get; set; } = "g10-1";
    public string Name { get; set; } = "Máy in G10";
    public string Protocol { get; set; } = "G10Tcp";
    public string Ip { get; set; } = "192.168.0.3";
    public int Port { get; set; } = 6007;
    public int BufferCapacity { get; set; } = 100;
    public bool Enabled { get; set; } = true;
}

public sealed record PrinterStatusSnapshot(
    PrinterConnectionState Connection,
    bool IsPrinting,
    string Job,
    double? Ink1Percent,
    double? Ink2Percent,
    int? BufferLength,
    string Message,
    DateTime ObservedUtc);

public sealed record PrintQueueSummary(
    int Pending,
    int Sent,
    int Printed,
    int NoResponse);

public sealed record PrintCodeBatch(
    string BatchId,
    string OrderNo,
    DateTime CreatedUtc,
    string PrinterId,
    IReadOnlyList<PrintQueueItem> Items);

public sealed class PrintQueueItem
{
    public long Id { get; init; }
    public string Code { get; init; } = "";
    public string Gtin { get; init; } = "";
    public string Serial { get; init; } = "";
    public PrintCodeState State { get; init; }
    public int Attempts { get; init; }
}

public sealed record PrinterSendResult(string Code, bool Accepted, string Response);

/// <summary>Kết quả của một lệnh điều khiển máy in (START/STOP/CLR/RESET/…).</summary>
public sealed record PrinterCommandResult(bool Ok, string Command, string Response, string Message);

public interface IPrinterClient : IAsyncDisposable
{
    string PrinterId { get; }
    PrinterStatusSnapshot Status { get; }
    event Func<string, Task>? PrintConfirmed;
    Task ConnectAsync(CancellationToken ct = default);
    Task DisconnectAsync(CancellationToken ct = default);
    Task<PrinterStatusSnapshot> RefreshStatusAsync(CancellationToken ct = default);
    Task<PrinterSendResult> SendCodeAsync(string code, CancellationToken ct = default);

    /// <summary>
    /// Gửi một lệnh điều khiển thô (START, STOP, CLR, …) và đợi phản hồi có prefix tương ứng.
    /// </summary>
    Task<string> SendRawCommandAsync(
        string command,
        string expectedPrefix,
        int timeoutMs = 5000,
        CancellationToken ct = default);
}

public sealed class PrintBatchResult
{
    public required PrintCodeBatch Batch { get; init; }
    public int AcceptedCount { get; init; }
    public int WaitingCount { get; init; }
    public int NoResponseCount { get; init; }
    public string Message { get; init; } = "";
}
