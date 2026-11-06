using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using ClassLibraryMT.Auth;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
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

            // Đăng nhập là BẮT BUỘC trước khi vào ứng dụng.
            // ShutdownMode = OnExplicitShutdown để đóng cửa sổ đăng nhập KHÔNG
            // tắt app (đăng xuất sẽ quay lại cửa sổ đăng nhập).
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;

            // Tạo tài khoản quản trị mặc định (admin/admin) ở lần chạy đầu.
            // Tài khoản này bắt buộc đổi mật khẩu ở lần đăng nhập đầu tiên.
            // Gọi bên trong RunLoginLoopAsync để chắc chắn seed xong mới
            // hiển thị cửa sổ đăng nhập.

            // Chạy vòng lặp: đăng nhập -> vào app -> đăng xuất -> đăng nhập lại.
            _ = RunLoginLoopAsync(desktop);
            desktop.ShutdownRequested += OnShutdownRequested;
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

    /// <summary>
    /// Vòng lặp đăng nhập: hiện cửa sổ đăng nhập, chờ kết quả; nếu thành công
    /// thì mở cửa sổ chính và chờ người dùng đóng (đăng xuất) rồi quay lại
    /// cửa sổ đăng nhập.
    /// </summary>
    /// <param name="desktop">Lifetime của ứng dụng.</param>
    private async Task RunLoginLoopAsync(IClassicDesktopStyleApplicationLifetime desktop)
    {
        try
        {
            // Seed tài khoản quản trị mặc định TRƯỚC khi hiện màn hình đăng nhập.
            await EnsureSeedAdminAsync().ConfigureAwait(true);

            while (true)
            {
                // ---- 1. Màn hình đăng nhập ----
                var loginVm = _host!.Services.GetRequiredService<LoginViewModel>();
                var loginWindow = new LoginWindow
                {
                    DataContext = loginVm,
                };

                loginVm.Attach(loginWindow);

                desktop.MainWindow = loginWindow;
                loginWindow.Show();

                // Chờ tới khi cửa sổ đăng nhập đóng.
                while (loginWindow.IsVisible)
                {
                    await Task.Delay(120).ConfigureAwait(true);
                }

                // Không đăng nhập được -> thoát ứng dụng.
                if (!loginVm.IsAuthenticated)
                {
                    desktop.Shutdown();
                    return;
                }

                // ---- 2. Cửa sổ chính ----
                var mainWindow = new MainWindow
                {
                    DataContext = _host.Services.GetRequiredService<MainViewModel>(),
                };

                desktop.MainWindow = mainWindow;
                mainWindow.Show();

                // Chờ tới khi cửa sổ chính đóng.
                while (mainWindow.IsVisible)
                {
                    await Task.Delay(120).ConfigureAwait(true);
                }

                // Người dùng đóng app (không phải đăng xuất) -> thoát.
                var mainVm = mainWindow.DataContext as MainViewModel;
                if (mainVm is null || !mainVm.IsLogoutRequested)
                {
                    desktop.Shutdown();
                    return;
                }

                // ---- 3. Đăng xuất: quay lại màn hình đăng nhập ----
                desktop.MainWindow = null;
            }
        }
        catch (Exception ex)
        {
            // Lỗi trong vòng lặp -> đóng app an toàn thay vì treo.
            Console.Error.WriteLine("[TanTienQR] Loi trong luong dang nhap: " + ex);
            desktop.Shutdown();
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
