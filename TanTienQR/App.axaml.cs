using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Media.Fonts;
using ClassLibraryMT.Auth;
using ClassLibraryMT.Logging;
using ClassLibraryMT.Po;
using ClassLibraryMT.Scans;
using ClassLibraryMT.Updates;
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
    private Task? _seedTask;

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
            if (UpdateProtocol.IsHealthCheck)
            {
                _host.Services.GetRequiredService<ScanIntakeGate>().SetSuspended(true);
                _host.Services.GetRequiredService<ProductionOrderService>().HoldForUpdateHealthCheck();
            }

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
            _seedTask = EnsureSeedAdminAsync();

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
            mainWindow.IsEnabled = !UpdateProtocol.IsHealthCheck;
            var updates = _host.Services.GetRequiredService<UpdateClient>();
            updates.InstallRequested = version => ConfirmUpdateAsync(mainWindow, version);
            Avalonia.Threading.Dispatcher.UIThread.Post(() => _ = FinishUpdateStartupAsync(updates),
                Avalonia.Threading.DispatcherPriority.Loaded);
        }

        base.OnFrameworkInitializationCompleted();
    }

    private async Task FinishUpdateStartupAsync(UpdateClient updates)
    {
        if (_host is null) return;
        var trialPending = UpdateProtocol.IsHealthCheck;
        try
        {
            if (UpdateProtocol.IsHealthCheck)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(50));
                if (_seedTask is not null) await _seedTask.WaitAsync(timeout.Token);
                // Khởi động thử không đợi PLC/camera/MES, nhưng phải mở được dữ liệu tại máy.
                if (!_host.Services.GetRequiredService<LicenseManager>().Current.Ok)
                    throw new InvalidOperationException("Bản mới không xác minh được license tại máy.");
                _ = await _host.Services.GetRequiredService<IUserService>().GetAllAsync(timeout.Token);
                _ = await _host.Services.GetRequiredService<IScanRecordQuery>().GetAsync(pageSize: 1, cancellationToken: timeout.Token);
                _ = await _host.Services.GetRequiredService<PoHistoryStore>().GetRunsAsync(timeout.Token);
                await _host.Services.GetRequiredService<IScanRecordWriter>().VerifyHealthyAsync(timeout.Token);
                await _host.Services.GetRequiredService<SqliteLogWriter>().VerifyHealthyAsync(timeout.Token);
                if (!UpdateProtocol.TryReportHealthy() || !await UpdateProtocol.WaitForCommitAsync(timeout.Token))
                    throw new InvalidOperationException("Launcher chưa xác nhận bản cập nhật.");
                trialPending = false;
                _host.Services.GetRequiredService<ProductionOrderService>().LeaveUpdateMaintenance();
                _host.Services.GetRequiredService<ScanIntakeGate>().SetSuspended(false);
                if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
                    desktop.MainWindow!.IsEnabled = true;
            }
            await updates.CheckAsync();
        }
        catch (Exception ex)
        {
            updates.Status = trialPending
                ? "Khởi động thử thất bại, đang trở về phiên bản trước: " + ex.Message
                : "Không hoàn tất kiểm tra cập nhật: " + ex.Message;
            if (trialPending)
                Environment.Exit(1);
        }
    }

    private async Task ConfirmUpdateAsync(Window owner, string version)
    {
        if (_host is null || ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
            throw new InvalidOperationException("Ứng dụng đang dừng.");
        var updates = _host.Services.GetRequiredService<UpdateClient>();
        updates.EnsureAdmin();
        if (UpdateProtocol.LauncherSession is null)
            throw new InvalidOperationException("Cần mở ứng dụng bằng TanTienLauncher trước khi cài cập nhật.");
        var dialog = new Window
        {
            Title = "Xác nhận cập nhật", Width = 470, Height = 230,
            CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        var cancel = new Button { Content = "Để sau" };
        var install = new Button { Content = "Dừng ứng dụng và cài đặt" };
        cancel.Click += (_, _) => dialog.Close(false);
        install.Click += (_, _) => dialog.Close(true);
        dialog.Content = new StackPanel
        {
            Margin = new Thickness(24), Spacing = 18,
            Children =
            {
                new TextBlock { Text = $"Cài phiên bản {version} và khởi động lại?\nChỉ thực hiện khi dây chuyền đã dừng.",
                    TextWrapping = TextWrapping.Wrap },
                new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 12,
                    Children = { cancel, install } }
            }
        };
        if (!await dialog.ShowDialog<bool>(owner)) return;
        updates.EnsureAdmin();
        owner.IsEnabled = false;
        var host = _host;
        var po = host.Services.GetRequiredService<ProductionOrderService>();
        var intake = host.Services.GetRequiredService<ScanIntakeGate>();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var stopping = false;
        var enteredMaintenance = false;
        try
        {
            await updates.VerifyInstallAuthorizationAsync(timeout.Token);
            if (!await po.TryEnterUpdateMaintenanceAsync(timeout.Token))
                throw new InvalidOperationException("Còn lệnh sản xuất đang chạy hoặc dở dang. Hãy hoàn tất hoặc hủy lệnh trước.");
            enteredMaintenance = true;
            intake.SetSuspended(true);
            updates.Status = "Đang dừng an toàn và ghi hết dữ liệu…";
            var plc = host.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<PlcOptions>>().Value;
            if (plc.Enabled && !await host.Services.GetRequiredService<PlcWriter>().WriteReadyAsync(0, timeout.Token))
                throw new InvalidOperationException("Chưa xác nhận được PLC Ready = 0. Đã hủy cài cập nhật.");
            await host.Services.GetRequiredService<CameraSignalWorker>().DrainAsync(timeout.Token);
            await host.Services.GetRequiredService<CheckeeSendWorker>().DrainAsync(timeout.Token);
            await host.Services.GetRequiredService<IScanRecordWriter>().VerifyHealthyAsync(timeout.Token);
            await host.Services.GetRequiredService<SqliteLogWriter>().VerifyHealthyAsync(timeout.Token);
            // Helper phải nhả EXE trước khi launcher thay bộ chương trình.
            await host.Services.GetRequiredService<PrinterRemoteViewModel>().ShutdownAsync().WaitAsync(timeout.Token);
            stopping = true;
            await host.StopAsync(timeout.Token);
            await host.Services.GetRequiredService<IScanRecordWriter>().VerifyHealthyAsync(timeout.Token);
            await host.Services.GetRequiredService<SqliteLogWriter>().VerifyHealthyAsync(timeout.Token);
            await DisposeHostAsync(host);
            _host = null;
            UpdateProtocol.RequestInstall(version);
            desktop.Shutdown();
        }
        catch
        {
            if (!stopping)
            {
                if (enteredMaintenance)
                {
                    intake.SetSuspended(false);
                    po.LeaveUpdateMaintenance();
                }
                owner.IsEnabled = true;
            }
            else
            {
                // Đã dừng một phần host: đóng bản cũ, không bàn giao giao dịch thay tệp.
                desktop.Shutdown(1);
            }
            throw;
        }
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
            _host?.Services.GetRequiredService<PrinterRemoteViewModel>().ShutdownAsync()
                .WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
            _host?.StopAsync(TimeSpan.FromSeconds(3)).GetAwaiter().GetResult();
        }
        catch
        {
            // Bỏ qua lỗi khi shutdown.
        }

        if (_host is { } host)
            DisposeHostAsync(host).GetAwaiter().GetResult();
        _host = null;
    }

    private static async Task DisposeHostAsync(IHost host)
    {
        // Container có client TCP/remote chỉ triển khai IAsyncDisposable.
        if (host is IAsyncDisposable asynchronous)
            await asynchronous.DisposeAsync().ConfigureAwait(false);
        else
            host.Dispose();
    }
}
