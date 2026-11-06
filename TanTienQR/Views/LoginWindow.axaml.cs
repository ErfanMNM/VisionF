using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using TanTienQR.ViewModels;

namespace TanTienQR.Views;

/// <summary>
/// Cửa sổ đăng nhập — hiển thị TRƯỚC cửa sổ chính.
///
/// Đăng nhập là bắt buộc: nếu người dùng đóng cửa sổ này thì ứng dụng
/// cũng tắt (App kiểm tra qua <see cref="LoginViewModel.IsAuthenticated"/>).
/// </summary>
public partial class LoginWindow : Window
{
    public LoginWindow()
    {
        InitializeComponent();

        // Nhấn Enter ở ô mật khẩu -> đăng nhập.
        PasswordBox.KeyDown += OnPasswordKeyDown;
    }

    /// <summary>
    /// Được gọi khi cửa sổ mở: đưa con trỏ vào ô tên đăng nhập.
    /// </summary>
    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);

        if (UsernameBox is not null)
        {
            UsernameBox.Focus();
        }
    }

    private void OnPasswordKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
        {
            return;
        }

        e.Handled = true;

        if (DataContext is LoginViewModel vm && vm.LoginCommand.CanExecute(null))
        {
            vm.LoginCommand.Execute(null);
        }
    }
}
