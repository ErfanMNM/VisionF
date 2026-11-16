using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Media.Fonts;
using ClassLibraryMT.Auth;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TanTienQR.Controls;
using TanTienQR.Services;
using TanTienQR.ViewModels;
using TanTienQR.Views;

namespace TanTienQR;

public partial class App : Application
{
    private IHost? _host;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);

        // ===== Font nhung trong app =====
        // Space Grotesk: font UI chinh (khuong giong mau Dashboard 4).
        //   Bien bien (variable font) -> 1 file du cho moi weight 400..700.
        // JetBrains Mono: font cho so lieu / ma don / dong ho.
        // Font Inter dung lam fallback khi may khong co font he thong.
        //
        // EmbeddedFontCollection nap file .ttf truc tiep tu avares:// nen
        // app khong can cai font vao may (vuot qua gioi han Native AOT).
        // EmbeddedFontCollection(Uri key, Uri source):
        //   key    = dinh danh collection (phai khac nhau giua cac font)
        //   source = duong dan file .ttf trong avares://
        // Font .ttf duoc nhung vao app -> khong can cai font vao may,
        // va khong dung reflection nen giu duoc kha nang Native AOT.
        //
        // LUU Y: key cua EmbeddedFontCollection phai theo scheme "fonts:"
        // (Avalonia 12 yeu cau). Neu key tro thang avares:// app se nem
        // ArgumentException "Font collection Key should follow the fonts: scheme"
        // ngay luc khoi dong.
        FontManager.Current.AddFontCollection(
            new EmbeddedFontCollection(
                new Uri("fonts:SpaceGrotesk"),
                new Uri("avares://TanTienQR/Assets/Fonts/SpaceGrotesk-Regular.ttf")));

        FontManager.Current.AddFontCollection(
            new EmbeddedFontCollection(
                new Uri("fonts:JetBrainsMono"),
                new Uri("avares://TanTienQR/Assets/Fonts/JetBrainsMono-Regular.ttf")));

        FontManager.Current.AddFontCollection(
            new EmbeddedFontCollection(
                new Uri("fonts:JetBrainsMonoBold"),
                new Uri("avares://TanTienQR/Assets/Fonts/JetBrainsMono-Bold.ttf")));

        FontManager.Current.AddFontCollection(
            new EmbeddedFontCollection(
                new Uri("fonts:JetBrainsMedium"),
                new Uri("avares://TanTienQR/Assets/Fonts/JetBrainsMedium-Regular.ttf")));
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Dùng Host thay vì ServiceCollection thuần: Host cung cấp runner cho
            // BackgroundService (camera worker + PLC heartbeat).
            var builder = Host.CreateApplicationBuilder();

            builder.Configuration
                .SetBasePath(AppContext.BaseDirectory)
                .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false)
                .AddJsonFile("appsettings.Development.json", optional: true, reloadOnChange: false)
                .AddEnvironmentVariables("TANTIEN_");

            builder.Logging.AddConsole();
            builder.Logging.SetMinimumLevel(LogLevel.Information);

            builder.Services.AddTanTienServices(builder.Configuration);

            _host = builder.Build();

            // Kiểm tra license TRƯỚC khi tạo cửa sổ: license hợp lệ là điều kiện
            // để app chuyển sang trạng thái Ready (D16 = 1).
            _host.Services.GetRequiredService<LicenseManager>().Load();

            // Nạp cấu hình vào form của tab Cấu hình.
            _host.Services.GetRequiredService<ConfigEditorViewModel>().Load();

            // BẮT BUỘC phải Start() — Build() CHỈ tạo container, KHÔNG tự chạy
            // BackgroundService. Nếu thiếu dòng này:
            //   - PlcHeartbeatService KHÔNG chạy -> không ghi D16 xuống PLC
            //     -> PLC mất kết nối rồi bật lại sẽ không thấy app "online".
            //   - CameraSignalWorker KHÔNG chạy -> không nhận mã quét.
            //
            // Đặt SAU license + config để tick đầu tiên của heartbeat đã đúng
            // trạng thái (license hợp lệ hay chưa), tránh ghi D16 sai 1 nhịp.
            //
            // Start() chỉ gọi 1 lần cho cả vòng đời app: heartbeat vẫn ghi
            // D16 = 0 trong lúc chưa đăng nhập — đúng ý nghĩa (PLC biết app chưa
            // sẵn sàng). Khi PLC bật lại, heartbeat tự phát hiện kết nối lại và
            // chuyển PLCStatus về Connected.
            _host.Start();

            // ---- Máy quét cầm tay (handheld) ----
            // Bật lắng nghe TCP (mặc định 127.0.0.1:6999) để nhận mã quét.
            // Bắt đầu ở đây (ngoài vòng lặp đăng nhập) để không mở/đóng
            // listener mỗi lần đăng xuất.
            _host.Services.GetRequiredService<HandheldScannerService>().Start();

            // Đăng nhập là BẮT BUỘC trước khi vào ứng dụng.
            // ShutdownMode = OnExplicitShutdown để đóng cửa sổ KHÔNG tự tắt app
            // (đăng xuất chỉ hiện lại overlay đăng nhập, không đóng cửa sổ).
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;

            // Seed tài khoản quản trị mặc định (admin/admin) ở lần chạy đầu.
            // Tài khoản này bắt buộc đổi mật khẩu ở lần đăng nhập đầu tiên.
            // Chạy nền (fire-and-forget) để không chặn framework khởi tạo —
            // đăng nhập thật sự chỉ có thể xảy ra sau khi seed xong.
            _ = EnsureSeedAdminAsync();

            // ---- Bàn phím ảo: đăng ký singleton toàn cục ----
            // Mọi cửa sổ (kể cả hộp thoại con) dùng chung một bàn phím +
            // một ô nhập đang focus.
            var keyboardService = _host.Services.GetRequiredService<KeyboardService>();
            VirtualKeyboardHost.Register(keyboardService);

            // =================================================================
            //  CỬA SỔ DUY NHẤT + OVERLAY ĐĂNG NHẬP
            // =================================================================
            // Trước đây: tạo LoginShellWindow -> poll IsVisible -> đóng ->
            // tạo MainWindow -> Show -> FullScreen (2 lần chuyển state) ->
            // poll IsVisible. Kết quả: giật 1 frame trống + trễ ~120ms.
            //
            // Nay: chỉ tạo MainWindow 1 lần, hiện NGAY. Màn hình đăng nhập là
            // một OVERLAY (LoginView) phủ lên cửa sổ chính (MainViewModel.
            // IsLoginOverlayVisible = true). Bấm "Đăng nhập" chỉ fade overlay
            // đi — không đổi cửa sổ, không giật.
            //
            // Việc chờ đăng nhập KHÔNG dùng polling: MainViewModel nhận sự kiện
            // RequestAuthenticated từ LoginViewModel và tự ẩn overlay.
            // =================================================================
            var mainVm = _host.Services.GetRequiredService<MainViewModel>();
            var mainWindow = new MainWindow
            {
                DataContext = mainVm,
            };

            // Bàn phím ảo: theo dõi ô nhập đang focus trong cửa sổ này.
            // Overlay đăng nhập nằm trong cùng cửa sổ -> cùng được theo dõi.
            keyboardService.Attach(mainWindow);

            desktop.MainWindow = mainWindow;

            // Đóng cửa sổ chính = THOÁT ứng dụng.
            //
            // ShutdownMode = OnExplicitShutdown nên đóng cửa sổ KHÔNG tự tắt
            // app (cần thiết cho đăng xuất: overlay đăng nhập hiện lại mà cửa
            // sổ vẫn còn). Vì vậy phải tự gọi Shutdown() khi cửa sổ đóng.
            mainWindow.Closed += (_, _) => desktop.Shutdown();

            desktop.ShutdownRequested += OnShutdownRequested;

            // Hiện cửa sổ ngay. Overlay đăng nhập đã bật sẵn trong
            // MainViewModel nên người dùng thấy màn hình đăng nhập ngay.
            mainWindow.Show();
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// Tạo tài khoản quản trị mặc định nếu database chưa có tài khoản nào.
    ///
    /// Chạy nền (fire-and-forget) vì không được chặn framework khởi tạo.
    /// </summary>
    private async Task EnsureSeedAdminAsync()
    {
        try
        {
            var seeded = await _host!.Services
                .GetRequiredService<IUserService>()
                .EnsureSeedAdminAsync()
                .ConfigureAwait(true);

            if (seeded)
            {
                Console.WriteLine(
                    "[TanTienQR] Da tao tai khoan quan tri mac dinh admin/admin. "
                    + "Hay doi mat khau ngay khi dang nhap.");
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(
                "[TanTienQR] Khong tao duoc tai khoan quan tri mac dinh: " + ex.Message);
        }
    }

    private void OnShutdownRequested(object? sender, ShutdownRequestedEventArgs e)
    {
        // Tắt worker/heartbeat trước khi đóng cửa sổ.
        try
        {
            _host?.StopAsync(TimeSpan.FromSeconds(3)).GetAwaiter().GetResult();
        }
        catch
        {
            // Bỏ qua lỗi khi shutdown.
        }

        _host?.Dispose();
        _host = null;
    }
}
