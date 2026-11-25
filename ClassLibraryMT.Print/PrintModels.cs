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
    public int ResponseTimeoutMs { get; set; } = 5000;
    public int ReconcileIntervalMs { get; set; } = 2500;
    public List<PrinterOptions> Printers { get; set; } = [];
}

public sealed class PrinterOptions
{
    public string Id { get; set; } = "g10-1";
    public string Name { get; set; } = "Máy in G10";
    /// <summary>Tên thông tin network port (MFD_NAME) đã cấu hình trên máy in.</summary>
    public string MfdName { get; set; } = "POD01";
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

/// <summary>Bản tin được máy in trả về qua GFILE; Id dùng để đối chiếu CU.</summary>
public sealed record PrinterFileSnapshot(string Id, string Name);

/// <summary>Danh sách bản tin và Id đang dùng trên máy in.</summary>
public sealed record PrinterFilesSnapshot(string CurrentId, IReadOnlyList<PrinterFileSnapshot> Files)
{
    public string CurrentJob => Files.FirstOrDefault(file =>
        file.Id.Equals(CurrentId, StringComparison.OrdinalIgnoreCase))?.Name ?? "";
}

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
    /// <summary>Nội dung mã đầy đủ, dùng nguyên vẹn để gửi in và đối chiếu trạng thái.</summary>
    public string CodeContent { get; init; } = "";
    public PrintCodeState State { get; init; }
    public int Attempts { get; init; }
}

public sealed record PrinterSendResult(string Code, bool Accepted, string Response);

/// <summary>Một dòng lệnh G10 đã gửi kèm phản hồi, dùng để hiển thị lịch sử trên UI.</summary>
public sealed record PrinterCommandLog(
    DateTimeOffset At,
    string Command,
    string Response,
    bool Accepted);

/// <summary>Kết quả của một lệnh điều khiển máy in (START/STOP/CLR/RESET/…).</summary>
public sealed record PrinterCommandResult(bool Ok, string Command, string Response, string Message);

public interface IPrinterClient : IAsyncDisposable
{
    string PrinterId { get; }
    PrinterStatusSnapshot Status { get; }
    PrinterFilesSnapshot Files { get; }
    event Func<string, Task>? PrintConfirmed;
    Task ConnectAsync(CancellationToken ct = default);
    Task DisconnectAsync(CancellationToken ct = default);
    Task<PrinterStatusSnapshot> RefreshStatusAsync(CancellationToken ct = default);
    Task<PrinterSendResult> SendCodeAsync(string code, CancellationToken ct = default);

    /// <summary>Sinh ra mỗi khi có lệnh G10 và phản hồi tương ứng.</summary>
    event Action<PrinterCommandLog>? CommandObserved;

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
