using Avalonia.Controls;

namespace TanTienQR.Views;

/// <summary>
/// Tab Cấu hình: tham số camera, PLC, vùng nhớ D, web Checkee và license.
///
/// Dùng chung DataContext với <see cref="MainWindow"/> (chính là
/// <see cref="MainViewModel"/>), nên không cần gán DataContext riêng.
/// </summary>
public partial class ConfigView : UserControl
{
    public ConfigView()
    {
        InitializeComponent();
    }
}
