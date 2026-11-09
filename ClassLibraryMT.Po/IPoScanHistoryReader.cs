namespace ClassLibraryMT.Po;

/// <summary>
/// Truy vấn lịch sử quét mã của hệ thống.
///
/// <para>
/// Được hiện thực bởi lớp lưu trữ log quét (nằm ở project khác) để
/// <c>ClassLibraryMT.Po</c> không phụ thuộc vào project ghi log — giữ cho
/// thư viện PO độc lập và tương thích Native AOT.
/// </para>
/// </summary>
public interface IPoScanHistoryReader
{
    /// <summary>
    /// Lấy tập serial đã từng xuất hiện trong lịch sử quét với kết quả Pass.
    /// </summary>
    /// <param name="orderNo">
    /// Mã PO hiện tại — các bản ghi thuộc PO này được bỏ qua (vì chính PO
    /// đang chạy thì mã của nó hợp lệ).
    /// </param>
    /// <param name="ct">Token hủy.</param>
    /// <returns>Danh sách serial đã từng quét.</returns>
    Task<IReadOnlyList<string>> GetSeenSerialsAsync(
        string orderNo,
        CancellationToken ct = default);
}
