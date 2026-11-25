namespace ClassLibraryMT.Scans;

/// <summary>
/// Trạng thái gửi mã lên Checkee (lưu ở cột <c>SendStatus</c> của bảng
/// <c>ScanRecords</c>).
///
/// <para>
/// Dùng chuỗi ổn định (không lưu số) để sau này đổi thứ tự enum không làm
/// hỏng dữ liệu cũ.
/// </para>
/// </summary>
public static class ScanSendStatuses
{
    /// <summary>
    /// Chưa gửi (mới quét, hoặc còn nợ sau một lần gửi lỗi mạng).
    /// </summary>
    public const string Pending = "Pending";

    /// <summary>
    /// Đã gửi thành công lên Checkee (HTTP 200).
    /// </summary>
    public const string Sent = "Sent";

    /// <summary>
    /// Gửi thất bại (server đã phản hồi nhưng HTTP != 200) — KHÔNG tự thử lại,
    /// người dùng xem trong bảng "Mã gửi lỗi" và bấm "Gửi lại" nếu muốn.
    /// </summary>
    public const string Failed = "Failed";

    /// <summary>Chuẩn hoá giá trị lưu trong DB về trạng thái hợp lệ.</summary>
    /// <param name="value">Giá trị đọc từ DB.</param>
    /// <returns>Trạng thái hợp lệ (mặc định <see cref="Pending"/>).</returns>
    public static string Normalize(string? value) => value switch
    {
        Sent => Sent,
        Failed => Failed,
        _ => Pending,
    };
}

/// <summary>
/// Bộ lọc lấy danh sách mã theo trạng thái gửi Checkee.
/// </summary>
public sealed class ScanSendFilter
{
    /// <summary>
    /// Lọc theo trạng thái gửi (null = tất cả).
    /// </summary>
    public string? SendStatus { get; set; }

    /// <summary>
    /// Lọc theo mã chứa chuỗi (null = tất cả).
    /// </summary>
    public string? CodeContains { get; set; }

    /// <summary>Đối chiếu nguyên CodeContent chính xác, phân biệt hoa thường; null = không lọc.</summary>
    public string? CodeContentEquals { get; set; }

    /// <summary>
    /// Chỉ lấy bản ghi có kết quả quét <c>Pass</c> hay không.
    /// </summary>
    public bool? OnlyPass { get; set; }

    /// <summary>
    /// Giới hạn số bản ghi trả về (mặc định 200).
    /// </summary>
    public int Limit { get; set; } = 200;
}
