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
/// <param name="ProcessingTimeMs">
/// Thời gian xử lý (ms) — tính từ lúc nhận mã từ camera đến trước khi gửi
/// xuống PLC. Với mã bị từ chối (Overload) thì đây là thời gian từ lúc nhận
/// đến lúc bị từ chối.
///
/// Kiểu <see cref="double"/> để giữ được phần thập phân (ví dụ <c>0.333 ms</c>);
/// đo bằng <see cref="System.Diagnostics.Stopwatch"/> vì <see cref="DateTime"/>
/// trên Windows chỉ có độ phân giải ~15ms nên không đo được dưới 1ms.
/// </param>
public sealed record ScanRecord(
    string Raw,
    string? Gtin,
    string? Serial,
    ScanOutcome Outcome,
    DateTime TimestampUtc,
    bool PlcWritten,
    double ProcessingTimeMs = 0);
