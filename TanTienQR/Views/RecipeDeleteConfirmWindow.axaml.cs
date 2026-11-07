using Avalonia.Controls;
using Avalonia.Interactivity;
using TanTienQR.Controls;
using TanTienQR.ViewModels;

namespace TanTienQR.Views;

/// <summary>
/// Hộp thoại xác nhận xoá recipe bằng mật khẩu.
///
/// Bàn phím ảo nằm ở hàng riêng phía dưới (không phủ lên form). Khi bàn phím
/// mở, cửa sổ tự giãn chiều cao để vừa bàn phím mà form vẫn nhìn thấy.
/// </summary>
public partial class RecipeDeleteConfirmWindow : Window
{
    /// <summary>True nếu người dùng đã xác nhận (mật khẩu đúng).</summary>
    public bool IsConfirmed { get; private set; }

    public RecipeDeleteConfirmWindow()
    {
        InitializeComponent();

        DataContextChanged += OnDataContextChanged;
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        Hook(DataContext as RecipeDeleteConfirmViewModel);

        // Bàn phím ảo: theo dõi ô nhập đang focus trong hộp thoại này.
        VirtualKeyboardHost.Attach(this);

        // Tự giãn / thu cửa sổ theo trạng thái bàn phím.
        KeyboardDialogSizer.Attach(this, VirtualKeyboardHost.ViewModel);
    }

    protected override void OnClosed(EventArgs e)
    {
        Unhook(DataContext as RecipeDeleteConfirmViewModel);
        DataContextChanged -= OnDataContextChanged;

        // Gỡ handler bàn phím + trả chiều cao cửa sổ về ban đầu.
        KeyboardDialogSizer.Detach(VirtualKeyboardHost.ViewModel);
        VirtualKeyboardHost.Detach(this);

        base.OnClosed(e);
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        Unhook(DataContext as RecipeDeleteConfirmViewModel);
        Hook(DataContext as RecipeDeleteConfirmViewModel);
    }

    private void Hook(RecipeDeleteConfirmViewModel? vm)
    {
        if (vm is not null)
        {
            vm.OnConfirmed += OnConfirmed;
        }
    }

    private void Unhook(RecipeDeleteConfirmViewModel? vm)
    {
        if (vm is not null)
        {
            vm.OnConfirmed -= OnConfirmed;
        }
    }

    private void OnConfirmed(object? sender, EventArgs e)
    {
        IsConfirmed = true;
        Close();
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e)
    {
        IsConfirmed = false;
        Close();
    }
}
