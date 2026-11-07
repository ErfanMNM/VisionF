using Avalonia.Controls;
using TanTienQR.Services;
using TanTienQR.ViewModels;

namespace TanTienQR.Controls;

/// <summary>
/// Điểm truy cập tĩnh tới bàn phím ảo.
///
/// Bàn phím là singleton dùng chung cho MỌI cửa sổ trong ứng dụng (cửa sổ
/// chính, cửa sổ đăng nhập và các hộp thoại con). Để các hộp thoại không
/// phải khai báo DI riêng, service được đăng ký một lần lúc khởi động app
/// qua <see cref="Register"/>.
///
/// Lưu ý: chỉ có một bàn phím ảo tại một thời điểm — đúng với thực tế vì
/// chỉ một ô nhập có focus duy nhất.
/// </summary>
public static class VirtualKeyboardHost
{
    /// <summary>Service bàn phím (null khi app chưa khởi tạo xong).</summary>
    private static KeyboardService? _service;

    /// <summary>
    /// Đăng ký service bàn phím (gọi 1 lần lúc khởi động app).
    /// </summary>
    /// <param name="service">Service bàn phím ảo.</param>
    public static void Register(KeyboardService service) => _service = service;

    /// <summary>
    /// Lấy service bàn phím (null nếu app chưa đăng ký).
    /// </summary>
    public static KeyboardService? Service => _service;

    /// <summary>
    /// Lấy ViewModel bàn phím (null nếu app chưa đăng ký).
    /// </summary>
    public static VirtualKeyboardViewModel? ViewModel => _service?.Keyboard;

    /// <summary>
    /// Gắn theo dõi focus cho một cửa sổ (dùng cho hộp thoại con).
    /// </summary>
    /// <param name="topLevel">Cửa sổ cần theo dõi.</param>
    public static void Attach(TopLevel topLevel) => _service?.Attach(topLevel);

    /// <summary>
    /// Gỡ theo dõi focus cho một cửa sổ (gọi khi hộp thoại con đóng).
    /// </summary>
    /// <param name="topLevel">Cửa sổ cần gỡ.</param>
    public static void Detach(TopLevel topLevel) => _service?.Detach(topLevel);
}
