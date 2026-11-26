namespace ClassLibraryMT.Scans;

/// <summary>Một lần gửi batch thực tế; không dựng lại từ trạng thái hiện tại của mã.</summary>
public sealed class CheckeeSendAttempt
{
    public long Id { get; init; }
    public DateTime StartedUtc { get; init; }
    public DateTime? CompletedUtc { get; init; }
    public string ProductGtin { get; init; } = string.Empty;
    public string BatchCode { get; init; } = string.Empty;
    public string SerialType { get; init; } = string.Empty;
    public int TotalCount { get; init; }
    public int OkCount { get; init; }
    public int FailCount { get; init; }
    public int UnknownCount { get; init; }
    public string Status { get; init; } = string.Empty;
    public int StatusCode { get; init; }
    public string Message { get; init; } = string.Empty;
}

/// <summary>Snapshot nội dung và kết quả của một mã trong đúng lần gửi này.</summary>
public sealed class CheckeeSendAttemptItem
{
    public long ScanRecordId { get; init; }
    public string CodeContent { get; init; } = string.Empty;
    public string OrderNo { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
    public int StatusCode { get; init; }
    public string Message { get; init; } = string.Empty;
}

public static class CheckeeSendAttemptStatuses
{
    public const string InProgress = "InProgress";
    public const string Sent = "Sent";
    public const string Failed = "Failed";
    public const string RetryPending = "RetryPending";
    public const string Unknown = "Unknown";
}
