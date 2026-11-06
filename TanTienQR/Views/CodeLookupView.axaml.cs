using Avalonia.Controls;

namespace TanTienQR.Views;

/// <summary>
/// Tab "Tra cứu mã" — tra cứu mã đã quét qua hệ thống.
///
/// Dùng chung DataContext với <see cref="MainWindow"/> (chính là
/// <see cref="ViewModels.MainViewModel"/>), nên không cần gán DataContext riêng.
/// </summary>
public partial class CodeLookupView : UserControl
{
    public CodeLookupView()
    {
        InitializeComponent();
    }
}
