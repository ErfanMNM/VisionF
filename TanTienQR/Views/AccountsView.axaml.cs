using Avalonia.Controls;

namespace TanTienQR.Views;

/// <summary>
/// Trang quản lý tài khoản người dùng (chỉ hiển thị cho Quản trị /
/// Lập trình viên — quyền <c>ManageUsers</c>).
/// </summary>
public partial class AccountsView : UserControl
{
    public AccountsView()
    {
        InitializeComponent();
    }
}
