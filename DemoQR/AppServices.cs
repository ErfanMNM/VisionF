using ClassLibraryMT.Communications.Checkee;
using ClassLibraryMT.Communications.CodeReaderProtocol;
using ClassLibraryMT.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace QRDemoCodeBackend;

// Đăng ký toàn bộ dịch vụ của app vào DI container.
internal static class AppServices
{
    public const string CameraSectionName = "Camera";

    public static IServiceCollection AddAppServices(
        this IServiceCollection services, IConfiguration configuration)
    {
        // Camera: bind cau hinh thu cong (tuong thich Native AOT) + kiem tra gia tri
        // ngay luc khoi dong thay vi chay toi luc quet ma.
        services.AddOptions<CameraOptions>()
            .Configure<IConfiguration>((o, cfg) =>
            {
                o.Ip = ConfigReader.GetString(cfg, $"{CameraSectionName}:Ip", o.Ip);
                o.Port = ConfigReader.GetInt(cfg, $"{CameraSectionName}:Port", o.Port);
            })
            .Validate(o => !string.IsNullOrWhiteSpace(o.Ip), "Camera:Ip khong duoc de trong.")
            .Validate(o => o.Port is > 0 and <= 65535, "Camera:Port phai tu 1 den 65535.")
            .ValidateOnStart();

        // Reader: singleton, dung chung mot ket noi socket.
        services.AddSingleton(sp =>
        {
            var cam = sp.GetRequiredService<IOptions<CameraOptions>>().Value;
            return new Reader(cam.Ip, cam.Port);
        });

        // Checkee: options + HttpClient + client (tu AddCheckee).
        services.AddCheckee(configuration);

        // PLC Omron: options + OmronPlc (singleton) + PlcWriter.
        services.AddPlc(configuration);

        // Worker chay nền.
        services.AddSingleton<CameraRDWorker>();
        services.AddHostedService(sp => sp.GetRequiredService<CameraRDWorker>());

        // Heartbeat PLC: ghi trang thai Ready xuong D16 moi chu ky.
        services.AddHostedService<PlcHeartbeatService>();

        return services;
    }
}
