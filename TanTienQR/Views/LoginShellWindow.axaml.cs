using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using TanTienQR.ViewModels;

namespace TanTienQR.Views;

/// <summary>
/// Khung đăng nhập full-size: dùng chung kích thước và custom chrome với
/// <see cref="MainWindow"/> (1360x820, WindowDecorations=None), layout 2 cột
/// branding + form, đổi mật khẩu lần đầu là bước 2 ngay trong khung.
/// </summary>
public partial class LoginShellWindow : Window
{
    public LoginShellWindow()
    {
        InitializeComponent();
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        FocusStepInput();
        if (DataContext is LoginViewModel vm)
        {
            vm.StepChanged += OnStepChanged;
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        if (DataContext is LoginViewModel vm)
        {
            vm.StepChanged -= OnStepChanged;
        }

        base.OnClosed(e);
    }

    private void OnStepChanged(object? sender, EventArgs e) => FocusStepInput();

    private void FocusStepInput()
    {
        if (DataContext is not LoginViewModel vm)
        {
            return;
        }

        Control? target = vm.IsChangePasswordStep
            ? this.FindControl<TextBox>("CurrentBox")
            : this.FindControl<TextBox>("UsernameBox");
        target?.Focus();
    }

    // Kéo di chuyển cửa sổ từ thanh tiêu đề tự vẽ (bỏ qua khi bấm vào nút).
    private void OnHeaderPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Source is Control src && (src is Button || src.FindAncestorOfType<Button>() is not null))
            return;

        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;

        if (e.ClickCount == 2)
        {
            ToggleMaxRestore();
            return;
        }

        BeginMoveDrag(e);
    }

    private void OnMinimizeClick(object? sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void OnMaxRestoreClick(object? sender, RoutedEventArgs e) => ToggleMaxRestore();

    private void ToggleMaxRestore()
    {
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e)
    {
        // Đóng shell = thoát app (App kiểm tra IsAuthenticated).
        Close();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
            return;
        }

        base.OnKeyDown(e);
    }
}
