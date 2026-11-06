using ClassLibraryMT.Auth;
using ClassLibraryMT.Communications.CodeReaderProtocol;
using ClassLibraryMT.Communications.Plc;
using ClassLibraryMT.Configuration;
using ClassLibraryMT.Logging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TanTienQR.ViewModels;

namespace TanTienQR.Services;

/// <summary>
/// Đăng ký toàn bộ dịch vụ của TanTienQR vào DI container.
///
/// KHÔNG đăng ký Checkee — dự án này không gọi API Checkee.
/// </summary>
public static class TanTienAppServices
{
    public const string CameraSectionName = "Camera";
    public const string PlcSectionName = "Plc";
    public const string WebSectionName = "Web";

    public static IServiceCollection AddTanTienServices(
        this IServiceCollection services, IConfiguration configuration)
    {
        // ---- Log SQLite (log kỹ thuật + thao tác người dùng) ----
        // Đăng ký TRƯỚC các worker để mọi ILogger<T> của worker đều được
        // ghi xuống SQLite ngay từ đầu.
        services.AddSqliteLogging(configuration);

        // ---- Xác thực & phân quyền (tài khoản, phiên đăng nhập) ----
        // Đăng ký SAU AddSqliteLogging vì AddAuth cần implement IUserContext
        // (interface của lib Logging) để nhật ký thao tác có tên người dùng.
        services.AddAuth(configuration);

        // ---- Camera: bind cấu hình thủ công (tương thích Native AOT) ----
        services.AddOptions<CameraOptions>()
            .Configure<IConfiguration>((o, cfg) =>
            {
                o.Ip = ConfigReader.GetString(cfg, $"{CameraSectionName}:Ip", o.Ip);
                o.Port = ConfigReader.GetInt(cfg, $"{CameraSectionName}:Port", o.Port);
                o.DuplicateWindowSeconds = ConfigReader.GetInt(
                    cfg, $"{CameraSectionName}:DuplicateWindowSeconds", o.DuplicateWindowSeconds);
            })
            .Validate(o => !string.IsNullOrWhiteSpace(o.Ip), "Camera:Ip khong duoc de trong.")
            .Validate(o => o.Port is > 0 and <= 65535, "Camera:Port phai tu 1 den 65535.")
            .Validate(o => o.DuplicateWindowSeconds > 0, "Camera:DuplicateWindowSeconds phai lon hon 0.")
            .ValidateOnStart();

        // Reader: singleton, dùng chung một kết nối socket.
        services.AddSingleton(sp =>
        {
            var cam = sp.GetRequiredService<IOptions<CameraOptions>>().Value;
            return new Reader(cam.Ip, cam.Port);
        });

        // ---- PLC Omron: options + client singleton + writer ----
        services.AddOptions<PlcOptions>()
            .Configure<IConfiguration>((o, cfg) =>
            {
                o.Ip = ConfigReader.GetString(cfg, $"{PlcSectionName}:Ip", o.Ip);
                o.Port = ConfigReader.GetInt(cfg, $"{PlcSectionName}:Port", o.Port);
                o.RemoteNode = (byte)ConfigReader.GetInt(cfg, $"{PlcSectionName}:RemoteNode", o.RemoteNode);
                o.LocalNode = (byte)ConfigReader.GetInt(cfg, $"{PlcSectionName}:LocalNode", o.LocalNode);
                o.TimeoutMs = ConfigReader.GetInt(cfg, $"{PlcSectionName}:TimeoutMs", o.TimeoutMs);
                o.Retries = ConfigReader.GetInt(cfg, $"{PlcSectionName}:Retries", o.Retries);
                o.ResultAddress = ConfigReader.GetString(cfg, $"{PlcSectionName}:ResultAddress", o.ResultAddress);
                o.ReadyAddress = ConfigReader.GetString(cfg, $"{PlcSectionName}:ReadyAddress", o.ReadyAddress);
                o.RefreshIntervalMs = ConfigReader.GetInt(cfg, $"{PlcSectionName}:RefreshIntervalMs", o.RefreshIntervalMs);
            })
            .Validate(o => !string.IsNullOrWhiteSpace(o.Ip), "Plc:Ip khong duoc de trong.")
            .Validate(o => o.Port is > 0 and <= 65535, "Plc:Port phai tu 1 den 65535.")
            .Validate(o => o.TimeoutMs > 0, "Plc:TimeoutMs phai lon hon 0.")
            .Validate(o => o.Retries >= 0, "Plc:Retries khong duoc am.")
            .Validate(o => o.RefreshIntervalMs > 0, "Plc:RefreshIntervalMs phai lon hon 0.")
            .Validate(o => !string.IsNullOrWhiteSpace(o.ResultAddress), "Plc:ResultAddress khong duoc de trong.")
            .Validate(o => !string.IsNullOrWhiteSpace(o.ReadyAddress), "Plc:ReadyAddress khong duoc de trong.")
            .ValidateOnStart();

        // OmronPlc: singleton, dùng chung một kết nối UDP cho cả ghi kết quả
        // (D12) và heartbeat (D16).
        services.AddSingleton(sp =>
        {
            var opt = sp.GetRequiredService<IOptions<PlcOptions>>().Value;
            return new OmronPlc(
                opt.Ip,
                opt.Port,
                opt.RemoteNode,
                opt.LocalNode,
                opt.TimeoutMs,
                opt.Retries);
        });

        // PlcWriter: serialize các lệnh ghi vì OmronPlc không thread-safe.
        services.AddSingleton<PlcWriter>();

        // ---- Kênh kết quả quét mã (worker -> UI) ----
        services.AddSingleton<ScanFeed>();

        // ---- Web Checkee nhúng (tab Sản xuất) ----
        services.AddOptions<WebOptions>()
            .Configure<IConfiguration>((o, cfg) =>
            {
                o.Url = ConfigReader.GetString(cfg, $"{WebSectionName}:Url", o.Url);
                o.Enabled = ConfigReader.GetBool(cfg, $"{WebSectionName}:Enabled", o.Enabled);
            })
            .Validate(o => !string.IsNullOrWhiteSpace(o.Url), "Web:Url khong duoc de trong.")
            .ValidateOnStart();

        // ---- Quản lý license ----
        services.AddSingleton<LicenseManager>();

        // ---- Đọc/ghi file cấu hình (tab Cấu hình) ----
        services.AddSingleton<ConfigStore>();
        services.AddSingleton<ConfigEditorViewModel>();

        // ---- Worker chạy nền ----
        services.AddSingleton<CameraSignalWorker>();
        services.AddHostedService(sp => sp.GetRequiredService<CameraSignalWorker>());

        // Heartbeat PLC: ghi trạng thái Ready xuống D16 mỗi chu kỳ.
        services.AddHostedService<PlcHeartbeatService>();

        // ---- ViewModel ----
        services.AddSingleton<MainViewModel>();
        services.AddSingleton<HistoryViewModel>();
        services.AddSingleton<AccountsViewModel>();

        // LoginViewModel: Transient vì mỗi lần đăng nhập cần trạng thái
        // form sạch (tên đăng nhập / mật khẩu / lỗi) — không dùng lại
        // instance của lần trước.
        services.AddTransient<LoginViewModel>();

        return services;
    }
}
