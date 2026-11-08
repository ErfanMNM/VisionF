namespace TanTienQR.Models;

/// <summary>Kết quả xử lý một lần quét mã từ camera.</summary>
public enum ScanOutcome
{
    /// <summary>Đọc được mã hợp lệ → Pass (gửi D12 = 1).</summary>
    Pass = 0,

    /// <summary>Camera báo lỗi đọc mã ("NoRead" / "FAIL") → Fail (gửi D12 = 0).</summary>
    NoRead = 1,

    /// <summary>Mã không khớp pattern TXNG/GS1 → Fail (gửi D12 = 0).</summary>
    InvalidFormat = 2,

    /// <summary>Mã trùng với lần quét gần đây → Fail (gửi D12 = 0).</summary>
    Duplicate = 3,

    /// <summary>
    /// Mã bị từ chối vì hệ thống đang xử lý mã trước đó (quá tải).
    ///
    /// KHÔNG gửi xuống PLC — mã bị bỏ qua hoàn toàn.
    /// </summary>
    Overload = 4,

    /// <summary>
    /// Chưa bắt đầu sản xuất (chưa chọn PO đang chạy) → mã bị loại bỏ.
    ///
    /// KHÔNG gửi xuống PLC — mã bị loại bỏ hoàn toàn.
    /// </summary>
    NoProductionOrder = 5,

    /// <summary>
    /// Mã không thuộc tập mã hợp lệ của PO đang chạy → Fail (gửi D12 = 0).
    /// </summary>
    InvalidForOrder = 6,
}
