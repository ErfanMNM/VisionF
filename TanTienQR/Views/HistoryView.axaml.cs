using Avalonia.Controls;

namespace TanTienQR.Views;

/// <summary>
/// Tab Lịch sử: nhật ký thao tác người dùng (mục nhà phát triển).
///
/// Dùng chung DataContext với <see cref="MainWindow"/> (chính là
/// <see cref="ViewModels.MainViewModel"/>), nên không cần gán DataContext riêng.
/// </summary>
public partial class HistoryView : UserControl
{
    public HistoryView()
    {
        InitializeComponent();
    }
}
