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
    /// <summary>
    /// True khi khung đăng nhập đang ở chế độ toàn màn hình thật (tương đương F11).
    ///
    /// Tách trạng thái "fullscreen" khỏi <see cref="WindowState"/> giống
    /// <see cref="MainWindow"/>: khi fullscreen thì <see cref="WindowState"/> là
    /// <see cref="WindowState.FullScreen"/>; khi thoát fullscreen, khung trở về
    /// <see cref="WindowState.Maximized"/> để vẫn chiếm hết màn hình làm việc.
    /// </summary>
    private bool _isFullScreen;

    public LoginShellWindow()
    {
        InitializeComponent();
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);

        // ===== Tự toàn màn hình khi app mở =====
        // Khung đăng nhập cũng phải fullscreen như cửa sổ chính (máy chạy
        // kiosk/quầy sản xuất, người dùng đăng nhập ngay sau khi bật app).
        // Dùng API chuẩn WindowState = FullScreen nên tương thích Native AOT.
        SetFullScreen(true);

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

    /// <summary>
    /// Bật/tắt toàn màn hình thật (tương đương nhấn F11) từ thanh tiêu đề.
    /// </summary>
    /// <param name="sender">Nút toàn màn hình.</param>
    /// <param name="e">Tham số sự kiện.</param>
    private void OnFullScreenClick(object? sender, RoutedEventArgs e)
    {
        SetFullScreen(!_isFullScreen);
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e)
    {
        // Đóng shell = thoát app (App kiểm tra IsAuthenticated).
        Close();
    }

    private void ToggleMaxRestore()
    {
        // Đang fullscreen -> thoát fullscreen về maximized (không phải Normal,
        // để người dùng vẫn thấy khung chiếm hết màn hình).
        if (_isFullScreen)
        {
            SetFullScreen(false);
            return;
        }

        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    }

    /// <summary>
    /// Bật / tắt chế độ toàn màn hình thật (tương đương nhấn F11).
    ///
    /// Khi bật: <see cref="WindowState"/> = <see cref="WindowState.FullScreen"/>.
    /// Khi tắt: trở về <see cref="WindowState.Maximized"/>.
    ///
    /// Hàm này dùng API chuẩn của Avalonia (không reflection, không P/Invoke)
    /// nên hoạt động bình thường khi publish Native AOT.
    /// </summary>
    /// <param name="enabled">True = bật fullscreen.</param>
    public void SetFullScreen(bool enabled)
    {
        _isFullScreen = enabled;
        WindowState = enabled ? WindowState.FullScreen : WindowState.Maximized;
    }

    /// <summary>
    /// Bắt phím tắt toàn màn hình: F11 (bật/tắt) và Esc (thoát fullscreen).
    ///
    /// Esc chỉ thoát fullscreen khi khung ĐANG fullscreen — nếu không thì Esc
    /// giữ nguyên hành vi cũ là đóng app.
    /// </summary>
    /// <param name="sender">Cửa sổ.</param>
    /// <param name="e">Tham số sự kiện.</param>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.F11)
        {
            SetFullScreen(!_isFullScreen);
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Escape && _isFullScreen)
        {
            SetFullScreen(false);
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
            return;
        }

        base.OnKeyDown(e);
    }
}
