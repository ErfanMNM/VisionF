using Avalonia.Controls;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;

namespace TanTienQR.ViewModels;

/// <summary>
/// Phần overlay đăng nhập của <see cref="MainViewModel"/>.
///
/// Màn hình đăng nhập KHÔNG còn là cửa sổ riêng nữa — nó là một overlay
/// (UserControl) phủ lên <see cref="Views.MainWindow"/>. Nhờ vậy:
/// - Không tạo/đóng 2 cửa sổ -> hết giật (1 frame trống) khi bấm đăng nhập.
/// - Không chuyển WindowState 2 lần (Normal -> FullScreen -> FullScreen).
/// - Đăng xuất chỉ hiện lại overlay, không dựng lại cửa sổ.
///
/// File này tách riêng khỏi <see cref="MainViewModel"/> để file không quá dài.
/// </summary>
internal partial class MainViewModel
{
    /// <summary>
    /// True khi overlay đăng nhập đang hiện (đang chờ / đã đăng nhập xong).
    ///
    /// Điều khiển <c>IsVisible</c> của Border overlay trong XAML.
    /// </summary>
    [ObservableProperty]
    public partial bool IsLoginOverlayVisible { get; set; } = true;

    /// <summary>
    /// Độ mờ của overlay (0 = tàng hình, 1 = hiện hoàn toàn).
    ///
    /// Transition trên XAML sẽ mờ dần giá trị này (180ms) -> bấm "Đăng nhập"
    /// chỉ fade overlay đi, KHÔNG nháy cửa sổ.
    /// </summary>
    [ObservableProperty]
    public partial double LoginOverlayOpacity { get; set; } = 1d;

    /// <summary>
    /// Hủy lệnh trì hoãn việc gỡ overlay (nếu đang chờ).
    /// </summary>
    private CancellationTokenSource? _overlayHideCts;

    /// <summary>
    /// ViewModel đăng nhập dùng cho overlay.
    /// </summary>
    public LoginViewModel Login => _login;

    /// <summary>
    /// Gắn chủ sở hữu overlay và đăng ký sự kiện đăng nhập thành công.
    ///
    /// Gọi 1 lần khi MainWindow mở. Không dùng polling (Task.Delay) — sự kiện
    /// báo trực tiếp nên overlay ẩn ngay khi đăng nhập xong.
    /// </summary>
    /// <param name="loginControl">Control cha chứa overlay đăng nhập.</param>
    public void AttachLoginOverlay(Control loginControl)
    {
        ArgumentNullException.ThrowIfNull(loginControl);

        // Chủ sở hữu = control cha (UserControl) -> LoginViewModel KHÔNG đóng
        // cửa sổ khi đăng nhập thành công (chỉ báo sự kiện).
        _login.Attach(loginControl);

        _login.RequestAuthenticated -= OnLoginAuthenticated;
        _login.RequestAuthenticated += OnLoginAuthenticated;

        _login.RequestClose -= OnLoginCloseRequested;
        _login.RequestClose += OnLoginCloseRequested;
    }

    /// <summary>
    /// Người dùng đăng nhập thành công -> fade overlay đi + cập nhật quyền.
    /// </summary>
    /// <param name="sender">VM đăng nhập.</param>
    /// <param name="e">Tham số sự kiện.</param>
    private void OnLoginAuthenticated(object? sender, EventArgs e)
    {
        // Đảm bảo chạy trên luồng UI.
        Dispatcher.UIThread.Post(() =>
        {
            HideLoginOverlay();

            // Cập nhật tên/role hiển thị + cờ quyền hiện tab (session vừa đổi).
            RefreshCurrentUser();
        });
    }

    /// <summary>
    /// Người dùng bấm "Thoát ứng dụng" ở màn hình đăng nhập -> đóng app.
    /// </summary>
    /// <param name="sender">VM đăng nhập.</param>
    /// <param name="e">Tham số sự kiện.</param>
    private void OnLoginCloseRequested(object? sender, EventArgs e)
    {
        // Chỉ thoát app khi đăng nhập KHÔNG thành công (nút "Thoát ứng dụng").
        // Khi đăng nhập thành công, RequestClose cũng được phát nhưng
        // OnLoginAuthenticated đã ẩn overlay -> không được thoát.
        if (_login.IsAuthenticated)
        {
            return;
        }

        Dispatcher.UIThread.Post(() => ShutdownApp());
    }

    /// <summary>
    /// Ẩn overlay đăng nhập (sau khi đăng nhập thành công).
    ///
    /// Fade mờ trước (Transition 180ms trong XAML), sau đó mới tắt hẳn
    /// (IsVisible = false) để overlay không chặn chuột/bàn phím.
    /// </summary>
    public void HideLoginOverlay()
    {
        // Fade mờ trước (Transition 180ms trong XAML).
        LoginOverlayOpacity = 0d;

        // Đợi hết hiệu ứng mờ rồi mới gỡ overlay khỏi cây hình ảnh
        // (để overlay không chặn chuột/bàn phím).
        //
        // Dùng Task.Delay + Dispatcher.Post (KHÔNG dùng DispatcherTimer):
        // DispatcherTimer chạy theo render timer của platform, nên trong chế
        // độ headless (test) nó KHÔNG bao giờ chạy. Task.Delay chạy độc lập
        // -> luôn hoạt động, kể cả khi app sắp đóng.
        _overlayHideCts?.Cancel();
        _overlayHideCts?.Dispose();
        _overlayHideCts = new CancellationTokenSource();

        var token = _overlayHideCts.Token;
        _ = Task.Delay(200, token).ContinueWith(
            t =>
            {
                if (t.IsCanceled)
                {
                    return;
                }

                Dispatcher.UIThread.Post(() => IsLoginOverlayVisible = false);
            },
            TaskScheduler.Default);
    }

    /// <summary>
    /// Hiện lại overlay đăng nhập (khi đăng xuất).
    ///
    /// Form được reset về trạng thái sạch (tên đăng nhập / mật khẩu / lỗi rỗng,
    /// về bước 1) và app tạm dừng (dừng poll + timer tab Dev) cho tới khi
    /// đăng nhập lại.
    /// </summary>
    public void ShowLoginOverlay()
    {
        // Hủy lệnh trì hoãn việc gỡ overlay đang chờ (nếu có) -> tránh nó tắt
        // overlay ngay sau khi ta hiện lại.
        _overlayHideCts?.Cancel();

        _login.Reset();

        // Hiện overlay trước (kèm độ mờ 1) -> hiệu ứng mờ dần khi hiện lại.
        IsLoginOverlayVisible = true;
        LoginOverlayOpacity = 1d;

        // Tạm dừng hoạt động app (đồng hồ, trạng thái, timer tab Dev).
        Stop();
    }

    /// <summary>
    /// Giải phóng tài nguyên của phần overlay đăng nhập.
    /// </summary>
    internal void DisposeLoginOverlay()
    {
        _overlayHideCts?.Cancel();
        _overlayHideCts?.Dispose();
        _overlayHideCts = null;
    }
}
