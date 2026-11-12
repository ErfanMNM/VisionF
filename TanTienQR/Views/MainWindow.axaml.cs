using System;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using TanTienQR.ViewModels;

namespace TanTienQR.Views;

public partial class MainWindow : Window
{
    /// <summary>
    /// True khi cửa sổ đang ở chế độ toàn màn hình thật (tương đương F11).
    ///
    /// Biến này tách trạng thái "fullscreen" khỏi <see cref="WindowState"/>:
    /// khi ở fullscreen, <see cref="WindowState"/> là
    /// <see cref="WindowState.FullScreen"/>; khi thoát fullscreen, cửa sổ trở về
    /// <see cref="WindowState.Maximized"/> để vẫn chiếm hết màn hình làm việc.
    /// </summary>
    private bool _isFullScreen;

    public MainWindow()
    {
        InitializeComponent();
    }

    // Kéo di chuyển cửa sổ từ thanh tiêu đề tự vẽ (bỏ qua khi bấm vào nút).
    private void OnHeaderPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Source is Control src && (src is Button || src.FindAncestorOfType<Button>() is not null))
            return;

        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;

        // Double-click thanh tiêu đề: phóng to / khôi phục.
        if (e.ClickCount == 2)
        {
            ToggleMaxRestore();
            return;
        }

        BeginMoveDrag(e);
    }

    // Bật/tắt menu cửa sổ (popup hamburger).
    private void OnWindowMenuClick(object? sender, RoutedEventArgs e)
    {
        if (this.FindControl<Avalonia.Controls.Primitives.Popup>("WindowMenuPopup") is { } popup)
            popup.IsOpen = !popup.IsOpen;
    }

    private void CloseWindowMenu()
    {
        if (this.FindControl<Avalonia.Controls.Primitives.Popup>("WindowMenuPopup") is { } popup)
            popup.IsOpen = false;
    }

    private void OnMinimizeClick(object? sender, RoutedEventArgs e)
    {
        CloseWindowMenu();
        WindowState = WindowState.Minimized;
    }

    private void OnMaxRestoreClick(object? sender, RoutedEventArgs e)
    {
        CloseWindowMenu();
        ToggleMaxRestore();
    }

    /// <summary>
    /// Menu cửa sổ -> bật/tắt toàn màn hình thật (tương đương F11).
    /// </summary>
    /// <param name="sender">Nút menu.</param>
    /// <param name="e">Tham số sự kiện.</param>
    private void OnFullScreenClick(object? sender, RoutedEventArgs e)
    {
        CloseWindowMenu();
        SetFullScreen(!_isFullScreen);
    }

    private void ToggleMaxRestore()
    {
        // Đang fullscreen -> thoát fullscreen về maximized (không phải Normal,
        // để người dùng vẫn thấy app chiếm hết màn hình).
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
    /// Esc chỉ thoát fullscreen khi cửa sổ ĐANG fullscreen — nếu không thì Esc
    /// giữ nguyên hành vi mặc định của các hộp thoại con (đóng popup).
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

        base.OnKeyDown(e);
    }

    private void OnShutdownClick(object? sender, RoutedEventArgs e)
    {
        CloseWindowMenu();
        if (DataContext is MainViewModel vm && vm.ShutdownAppCommand.CanExecute(null))
            vm.ShutdownAppCommand.Execute(null);
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);

        // ===== Tự toàn màn hình khi app mở =====
        // Cửa sổ chính luôn chiếm hết màn hình làm việc (máy chạy trong kiosk
        // / quầy sản xuất). Dùng API chuẩn WindowState = FullScreen nên tương
        // thích Native AOT (không reflection, không P/Invoke).
        SetFullScreen(true);

        // Chỉ chạy khi cửa sổ thật sự hiển thị (không chạy trong trình xem trước).
        if (DataContext is MainViewModel vm)
        {
            vm.Start(this);
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        if (DataContext is MainViewModel vm)
        {
            // Nếu người dùng bấm "Đăng xuất" thì chỉ dừng tạm (app còn chạy để
            // đăng nhập lại) — chỉ dispose khi cửa sổ đóng hẳn (thoát app).
            if (vm.IsLogoutRequested)
            {
                vm.Stop();
            }
            else
            {
                vm.Dispose();
            }
        }

        base.OnClosed(e);
    }
}
