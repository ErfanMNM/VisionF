using Avalonia.Controls;
using TanTienQR.ViewModels;

namespace TanTienQR.Views;

/// <summary>
/// Hộp thoại đổi mật khẩu — bắt buộc ở lần đăng nhập đầu tiên (tài khoản
/// seed) hoặc sau khi quản trị đặt lại mật khẩu.
/// </summary>
public partial class PasswordChangeWindow : Window
{
    /// <summary>True nếu người dùng đã lưu mật khẩu mới thành công.</summary>
    public bool IsSaved { get; private set; }

    public PasswordChangeWindow()
    {
        InitializeComponent();

        // Lắng nghe kết quả từ ViewModel.
        DataContextChanged += OnDataContextChanged;
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        Hook(DataContext as PasswordChangeViewModel);
    }

    protected override void OnClosed(EventArgs e)
    {
        Unhook(DataContext as PasswordChangeViewModel);
        DataContextChanged -= OnDataContextChanged;

        base.OnClosed(e);
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        Unhook(DataContext as PasswordChangeViewModel);
        Hook(DataContext as PasswordChangeViewModel);
    }

    private void Hook(PasswordChangeViewModel? vm)
    {
        if (vm is null)
        {
            return;
        }

        vm.OnSucceeded += OnPasswordChanged;
        vm.OnCancelled += OnCancelled;
    }

    private void Unhook(PasswordChangeViewModel? vm)
    {
        if (vm is null)
        {
            return;
        }

        vm.OnSucceeded -= OnPasswordChanged;
        vm.OnCancelled -= OnCancelled;
    }

    private void OnPasswordChanged(object? sender, EventArgs e)
    {
        IsSaved = true;
        Close();
    }

    private void OnCancelled(object? sender, EventArgs e)
    {
        IsSaved = false;
        Close();
    }
}
