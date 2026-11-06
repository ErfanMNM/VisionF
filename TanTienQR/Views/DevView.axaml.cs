using Avalonia.Controls;

namespace TanTienQR.Views;

/// <summary>
/// Tab "Dev": trạng thái global, bộ đếm runtime, bộ nhớ, service DI và log kỹ thuật.
///
/// Chỉ hiển thị cho vai trò Lập trình viên (Developer) — xem
/// <c>MainViewModel.CanViewDev</c>.
///
/// Dùng chung DataContext với <see cref="MainWindow"/> (chính là
/// <see cref="ViewModels.MainViewModel"/>), nên không cần gán DataContext riêng.
/// </summary>
public partial class DevView : UserControl
{
    public DevView()
    {
        InitializeComponent();
    }
}
