using Avalonia.Controls;
using Avalonia.Interactivity;

namespace TanTienQR.Views;

/// <summary>
/// Hộp thoại quản lý license: nhập file .lic, xem thông tin, hủy license.
///
/// Tách riêng thành modal thay vì nhét vào sidebar — sidebar không đủ chỗ
/// cho cả panel trạng thái, thiết bị, license lẫn đồng hồ.
/// </summary>
public partial class LicenseWindow : Window
{
    public LicenseWindow()
    {
        InitializeComponent();
        Closing += OnClosing;
    }

    /// <summary>
    /// Chưa có license hợp lệ -> không cho đóng hộp thoại (Alt+F4, nút X...).
    /// Vẫn cho đóng khi cửa sổ chính/ứng dụng đang tắt để người dùng thoát được app.
    /// </summary>
    private void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (e.CloseReason != WindowCloseReason.WindowClosing)
        {
            return;
        }

        if (DataContext is ViewModels.MainViewModel { IsLicenseValid: false })
        {
            e.Cancel = true;
        }
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e)
    {
        Close();
    }
}
