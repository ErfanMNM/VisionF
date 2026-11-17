namespace ClassLibraryMT.Scans;

/// <summary>
/// API quản lý trạng thái gửi mã lên Checkee.
///
/// Tách riêng khỏi <see cref="IScanRecordQuery"/> để không phá vỡ các nơi
/// đang dùng (luồng PO, trang lịch sử) — chỉ luồng gửi Checkee và UI cần
/// trạng thái gửi.
/// </summary>
public interface IScanSendStatusStore
{
    /// <summary>
    /// Lấy danh sách mã CHƯA gửi lên Checkee (trạng thái <c>Pending</c> hoặc
    /// rỗng — tức là bản ghi tạo trước khi có tính năng gửi Checkee).
    ///
    /// <para>
    /// Bản ghi có kết quả quét không phải <c>Pass</c> bị loại nếu
    /// <paramref name="onlyPass"/> = true.
    /// </para>
    /// </summary>
    /// <param name="onlyPass">Chỉ lấy mã có kết quả Pass.</param>
    /// <param name="limit">Số bản ghi tối đa trả về.</param>
    /// <param name="cancellationToken">Token hủy.</param>
    /// <returns>Danh sách bản ghi chờ gửi (mới nhất trước).</returns>
    Task<IReadOnlyList<ScanRecordEntity>> GetPendingAsync(
        bool onlyPass = true,
        int limit = 100,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Lấy danh sách mã theo trạng thái gửi (dùng cho bảng "Mã gửi lỗi").
    /// </summary>
    /// <param name="filter">Bộ lọc (null = tất cả).</param>
    /// <param name="cancellationToken">Token hủy.</param>
    /// <returns>Danh sách bản ghi khớp bộ lọc.</returns>
    Task<IReadOnlyList<ScanRecordEntity>> GetBySendStatusAsync(
        ScanSendFilter? filter = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Đánh dấu một mã đã gửi thành công lên Checkee.
    /// </summary>
    /// <param name="id">Khoá bản ghi.</param>
    /// <param name="message">Thông điệp kết quả (tiếng Việt).</param>
    /// <param name="cancellationToken">Token hủy.</param>
    Task MarkSentAsync(long id, string message, CancellationToken cancellationToken = default);

    /// <summary>
    /// Đánh dấu một mã gửi thất bại (server đã phản hồi nhưng HTTP != 200).
    /// Mã sẽ KHÔNG được tự thử lại ở chu kỳ sau.
    /// </summary>
    /// <param name="id">Khoá bản ghi.</param>
    /// <param name="statusCode">Mã HTTP trả về (0 nếu không có phản hồi).</param>
    /// <param name="message">Thông điệp lỗi (tiếng Việt).</param>
    /// <param name="cancellationToken">Token hủy.</param>
    Task MarkFailedAsync(
        long id,
        int statusCode,
        string message,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Ghi nhận một lần thử gửi không thành công nhưng KHÔNG phải do server
    /// từ chối (timeout / lỗi mạng) — mã vẫn ở trạng thái chờ gửi để thử lại
    /// ở chu kỳ sau.
    /// </summary>
    /// <param name="id">Khoá bản ghi.</param>
    /// <param name="message">Thông điệp lỗi (tiếng Việt).</param>
    /// <param name="cancellationToken">Token hủy.</param>
    Task MarkPendingAsync(
        long id,
        string message,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Đưa một mã đã gửi lỗi về lại trạng thái chờ gửi (dùng cho nút
    /// "Gửi lại" trong bảng "Mã gửi lỗi").
    /// </summary>
    /// <param name="ids">Danh sách khoá bản ghi.</param>
    /// <param name="cancellationToken">Token hủy.</param>
    /// <returns>Số bản ghi được đặt lại.</returns>
    Task<int> ResetToPendingAsync(
        IReadOnlyList<long> ids,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Đếm số mã theo từng trạng thái gửi (<c>Pending</c> / <c>Sent</c> /
    /// <c>Failed</c>).
    /// </summary>
    /// <param name="cancellationToken">Token hủy.</param>
    /// <returns>Dictionary trạng thái → số lượng.</returns>
    Task<Dictionary<string, long>> CountBySendStatusAsync(
        CancellationToken cancellationToken = default);
}