using Avalonia.Controls;
using Avalonia.Interactivity;
using TanTienQR.ViewModels;

namespace TanTienQR.Views;

/// <summary>
/// Hộp thoại tạo / sửa tài khoản người dùng.
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
    }

    protected override void OnClosed(EventArgs e)
    {
        Unhook(DataContext as UserEditViewModel);
        DataContextChanged -= OnDataContextChanged;

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
