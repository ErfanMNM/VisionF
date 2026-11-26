namespace TanTienQR.Services;

/// <summary>Immutable audit/transport result. Pass is RAM acceptance + published outbox envelope.</summary>
internal sealed record ManualActivationResult
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string CodeContent { get; init; } = "";
    public string Source { get; init; } = "";
    public string Status { get; init; } = "Pending";
    public string Message { get; init; } = "Đang chờ xử lý.";
    public string OrderNo { get; init; } = "";
    public DateTime ReceivedUtc { get; init; } = DateTime.UtcNow;
    public bool Success { get; init; }
    public bool AuditPersisted { get; init; } = true;
}
