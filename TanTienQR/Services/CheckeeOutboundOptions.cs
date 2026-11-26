namespace TanTienQR.Services;

/// <summary>Đường dẫn audit gửi Checkee; metadata sản phẩm/lô lấy từ PO.</summary>
internal sealed class CheckeeOutboundOptions
{
    /// <summary>
    /// File JSONL append-only, ghi bền intent và kết quả từng batch, không chứa credential.
    /// </summary>
    public string AuditLogPath { get; set; } = Path.Combine("logs", "checkee-send-audit.jsonl");
}
