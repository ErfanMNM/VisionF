using Avalonia.Controls;
using Avalonia.Interactivity;
using TanTienQR.Controls;
using TanTienQR.ViewModels;

namespace TanTienQR.Views;

/// <summary>
/// Hộp thoại tạo / sửa tài khoản người dùng.
///
/// Bàn phím ảo nằm ở hàng riêng phía dưới (không phủ lên form). Khi bàn phím
/// mở, cửa sổ tự giãn chiều cao để vừa bàn phím mà form vẫn nhìn thấy.
/// </summary>
public partial class UserEditWindow : Window
{
    /// <summary>True nếu người dùng đã lưu thành công.</summary>
    public bool IsSaved { get; private set; }

    public UserEditWindow()
    {
        InitializeComponent();

        DataContextChanged += OnDataContextChanged;
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        Hook(DataContext as UserEditViewModel);

        // Bàn phím ảo: theo dõi ô nhập đang focus trong hộp thoại này.
        VirtualKeyboardHost.Attach(this);

        // Tự giãn / thu cửa sổ theo trạng thái bàn phím.
        KeyboardDialogSizer.Attach(this, VirtualKeyboardHost.ViewModel);
    }

    protected override void OnClosed(EventArgs e)
    {
        Unhook(DataContext as UserEditViewModel);
        DataContextChanged -= OnDataContextChanged;

        // Gỡ handler bàn phím + trả chiều cao cửa sổ về ban đầu.
        KeyboardDialogSizer.Detach(VirtualKeyboardHost.ViewModel);
        VirtualKeyboardHost.Detach(this);

        base.OnClosed(e);
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        Unhook(DataContext as UserEditViewModel);
        Hook(DataContext as UserEditViewModel);
    }

    private void Hook(UserEditViewModel? vm)
    {
        if (vm is not null)
        {
            vm.OnSaved += OnSaved;
        }
    }

    private void Unhook(UserEditViewModel? vm)
    {
        if (vm is not null)
        {
            vm.OnSaved -= OnSaved;
        }
    }

    private void OnSaved(object? sender, EventArgs e)
    {
        IsSaved = true;
        Close();
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e)
    {
        IsSaved = false;
        Close();
    }
}
