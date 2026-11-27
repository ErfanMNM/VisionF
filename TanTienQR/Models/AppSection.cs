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

    /// <summary>
    /// Dev: trạng thái runtime, bộ đếm, bộ nhớ, service DI và log kỹ thuật.
    /// Chỉ dành cho vai trò Lập trình viên.
    /// </summary>
    Dev = 6,

    /// <summary>
    /// Tra cứu mã: tra cứu mã đã quét qua hệ thống (nhập tay hoặc handheld).
    /// </summary>
    CodeLookup = 7,

    /// <summary>
    /// Truy xuất Sản xuất (Local): quản lý các lệnh sản xuất (PO) tại máy.
    /// </summary>
    ProductionOrders = 8,

    /// <summary>Máy in: gửi mã từ PO, trạng thái và mức mực.</summary>
    Print = 9,

    /// <summary>
    /// Giới thiệu: phiên bản phần mềm, giấy phép sử dụng và cập nhật.
    /// </summary>
    About = 10,

    /// <summary>
    /// MultiConnect: dữ liệu vào/ra của các kênh chuyển giao thức sang TCP.
    /// </summary>
    MultiConnect = 11,

    /// <summary>Thêm mã: nhập tay hoặc nhận COM, kèm 10 lần nhận gần nhất.</summary>
    ManualActivation = 13,
}
