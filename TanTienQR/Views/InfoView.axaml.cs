using Avalonia.Controls;

namespace TanTienQR.Views;

/// <summary>
/// Tab Thông tin: trạng thái app/thiết bị, KPI, lịch sử mã quét và sự kiện.
///
/// Dùng chung DataContext với <see cref="MainWindow"/> (chính là
/// <see cref="MainViewModel"/>), nên không cần gán DataContext riêng.
/// </summary>
public partial class InfoView : UserControl
{
    public InfoView()
    {
        InitializeComponent();
    }
}
