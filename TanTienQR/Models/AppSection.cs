namespace TanTienQR.Models;

/// <summary>
/// Tab đang hiển thị trong ứng dụng.
///
/// Dùng enum thay vì chuỗi để View bind bằng compiled binding mà không
/// cần reflection (tương thích Native AOT).
/// </summary>
public enum AppSection
{
    /// <summary>Thông tin: trạng thái app/thiết bị, KPI, lịch sử quét mã.</summary>
    Info = 0,

    /// <summary>Sản xuất: web Checkee nhúng trong ứng dụng.</summary>
    Production = 1,

    /// <summary>Cấu hình: tham số runtime của camera, PLC và web.</summary>
    Configuration = 2,
}
