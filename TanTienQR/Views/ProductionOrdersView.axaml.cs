using Avalonia.Controls;

namespace TanTienQR.Views;

/// <summary>
/// Tab "Truy xuất Sản xuất (Local)" — quản lý lệnh sản xuất (PO) tại máy.
///
/// Dùng chung DataContext với <see cref="MainWindow"/> (chính là
/// <see cref="ViewModels.MainViewModel"/>), nên không cần gán DataContext riêng.
/// </summary>
public partial class ProductionOrdersView : UserControl
{
    public ProductionOrdersView()
    {
        InitializeComponent();
    }
}
