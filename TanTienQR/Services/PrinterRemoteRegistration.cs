using ClassLibraryMT.Print;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TanTienQR.Services.PrinterRemote;
using TanTienQR.ViewModels;

namespace TanTienQR.Services;

/// <summary>Đăng ký remote độc lập với kết nối TCP và dùng cùng snapshot cấu hình lúc khởi động.</summary>
internal static class PrinterRemoteRegistration
{
    public static IServiceCollection AddPrinterRemote(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton(sp =>
        {
            var print = sp.GetRequiredService<PrintOptions>();
            var primary = print.Printers.FirstOrDefault(p => p.Id.Equals(print.DefaultPrinterId, StringComparison.OrdinalIgnoreCase))
                ?? print.Printers.First();
            var section = configuration.GetSection("Print:Remote");
            var host = section["Host"]?.Trim();
            var target = section["CredentialTarget"]?.Trim();
            var options = new PrinterRemoteOptions
            {
                Enabled = bool.TryParse(section["Enabled"], out var enabled) && enabled,
                Host = string.IsNullOrWhiteSpace(host) ? primary.Ip : host,
                Port = int.TryParse(section["Port"], out var port) ? port : 3389,
                Username = section["Username"]?.Trim() ?? "",
                CredentialTarget = string.IsNullOrWhiteSpace(target) ? $"TanTienQR/Printer/{primary.Id}" : target,
                AutoConnect = !bool.TryParse(section["AutoConnect"], out var autoConnect) || autoConnect,
                ConnectTimeoutSeconds = int.TryParse(section["ConnectTimeoutSeconds"], out var timeout) ? timeout : 15,
            };
            if (options.Enabled && (!ConfigValidation.IsValidIpOrHost(options.Host)
                || options.Port is < 1 or > 65535 || options.ConnectTimeoutSeconds is < 3 or > 120
                || string.IsNullOrWhiteSpace(options.Username)))
                throw new InvalidOperationException("Cấu hình màn hình máy in không hợp lệ: kiểm tra host, cổng, tài khoản và timeout.");
            return options;
        });
        services.AddSingleton<IPrinterCredentialStore, WindowsPrinterCredentialStore>();
        services.AddSingleton<IPrinterRemoteSession>(sp => OperatingSystem.IsWindows()
            ? new WindowsPrinterRemoteSession(sp.GetRequiredService<PrinterRemoteOptions>())
            : new UnsupportedPrinterRemoteSession());
        services.AddSingleton<PrinterRemoteViewModel>();
        return services;
    }
}
