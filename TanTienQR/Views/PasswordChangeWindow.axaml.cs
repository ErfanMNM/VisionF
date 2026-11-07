using Avalonia.Controls;
using TanTienQR.Controls;
using TanTienQR.ViewModels;

namespace TanTienQR.Views;

/// <summary>
/// Hộp thoại đổi mật khẩu — bắt buộc ở lần đăng nhập đầu tiên (tài khoản
/// seed) hoặc sau khi quản trị đặt lại mật khẩu.
///
/// Bàn phím ảo nằm ở hàng riêng phía dưới (không phủ lên form). Khi bàn phím
/// mở, cửa sổ tự giãn chiều cao để vừa bàn phím mà form vẫn nhìn thấy.
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

        // Bàn phím ảo: theo dõi ô nhập đang focus trong hộp thoại này.
        VirtualKeyboardHost.Attach(this);

        // Tự giãn / thu cửa sổ theo trạng thái bàn phím.
        KeyboardDialogSizer.Attach(this, VirtualKeyboardHost.ViewModel);
    }

    protected override void OnClosed(EventArgs e)
    {
        Unhook(DataContext as PasswordChangeViewModel);
        DataContextChanged -= OnDataContextChanged;

        // Gỡ handler bàn phím + trả chiều cao cửa sổ về ban đầu.
        KeyboardDialogSizer.Detach(VirtualKeyboardHost.ViewModel);
        VirtualKeyboardHost.Detach(this);

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
