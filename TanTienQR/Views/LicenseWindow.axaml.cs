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
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e)
    {
        Close();
    }
}
