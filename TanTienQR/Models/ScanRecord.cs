namespace TanTienQR.Models;

/// <summary>
/// Một lần quét mã từ camera và kết quả xử lý.
/// </summary>
/// <param name="Raw">Nội dung gốc camera gửi lên.</param>
/// <param name="Gtin">Mã GTIN nếu parse được, null nếu không.</param>
/// <param name="Serial">Số serial nếu parse được, null nếu không.</param>
/// <param name="Outcome">Kết quả xử lý.</param>
/// <param name="TimestampUtc">Thời điểm nhận mã (UTC).</param>
/// <param name="PlcWritten">true nếu đã ghi kết quả xuống PLC thành công.</param>
public sealed record ScanRecord(
    string Raw,
    string? Gtin,
    string? Serial,
    ScanOutcome Outcome,
    DateTime TimestampUtc,
    bool PlcWritten);
