namespace TanTienQR.Models;

/// <summary>Kết quả xử lý một lần quét mã từ camera.</summary>
public enum ScanOutcome
{
    /// <summary>Đọc được mã hợp lệ → Pass (gửi D12 = 1).</summary>
    Pass = 0,

    /// <summary>Camera báo lỗi đọc mã ("NoRead" / "FAIL") → Fail (gửi D12 = 0).</summary>
    NoRead = 1,

    /// <summary>Mã đã được quét/kích hoạt trong PO → Fail (gửi D12 = 0).</summary>
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

    /// <summary>
    /// Máy đã bị dừng vì phát hiện mã đã từng xuất hiện trong lịch sử quét.
    ///
    /// KHÔNG gửi xuống PLC — mã bị loại bỏ hoàn toàn.
    /// </summary>
    Halted = 7,

    /// <summary>Lỗi PLC cho kết quả không phải Pass đã commit; mã hợp lệ đã commit vẫn là Pass.</summary>
    PlcError = 8,

    /// <summary>Nội dung mã không đúng cấu trúc GS1 AI 01/21 → Fail (D12 = 0).</summary>
    InvalidStructure = 9,
}
