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

    /// <summary>
    /// Lịch sử: nhật ký thao tác người dùng (mục nhà phát triển).
    /// </summary>
    History = 3,

    /// <summary>
    /// Tài khoản: quản lý tài khoản người dùng (chỉ Quản trị / Lập trình viên).
    /// </summary>
    Accounts = 4,

    /// <summary>
    /// Chỉnh PLC: webview camera, bảng tham số ô nhớ và khung recipe
    /// (Operator trở lên — dùng quyền EditConfig).
    /// </summary>
    PlcSettings = 5,
}
