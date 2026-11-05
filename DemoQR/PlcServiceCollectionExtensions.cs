using ClassLibraryMT.Communications.Plc;
using ClassLibraryMT.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace QRDemoCodeBackend;

// Helper dang ky dich vu PLC Omron vao DI container.
public static class PlcServiceCollectionExtensions
{
    public const string SectionName = "Plc";

    // Dang ky OmronPlc (singleton) + PlcWriter + binding cau hinh tu appsettings.
    public static IServiceCollection AddPlc(
        this IServiceCollection services, IConfiguration configuration)
    {
        // Bind thu cong (khong reflection) de tuong thich Native AOT.
        services.AddOptions<PlcOptions>()
            .Configure<IConfiguration>((o, cfg) =>
            {
                o.Ip = ConfigReader.GetString(cfg, $"{SectionName}:Ip", o.Ip);
                o.Port = ConfigReader.GetInt(cfg, $"{SectionName}:Port", o.Port);
                o.RemoteNode = (byte)ConfigReader.GetInt(cfg, $"{SectionName}:RemoteNode", o.RemoteNode);
                o.LocalNode = (byte)ConfigReader.GetInt(cfg, $"{SectionName}:LocalNode", o.LocalNode);
                o.TimeoutMs = ConfigReader.GetInt(cfg, $"{SectionName}:TimeoutMs", o.TimeoutMs);
                o.Retries = ConfigReader.GetInt(cfg, $"{SectionName}:Retries", o.Retries);
                o.ResultAddress = ConfigReader.GetString(cfg, $"{SectionName}:ResultAddress", o.ResultAddress);
                o.ReadyAddress = ConfigReader.GetString(cfg, $"{SectionName}:ReadyAddress", o.ReadyAddress);
                o.RefreshIntervalMs = ConfigReader.GetInt(cfg, $"{SectionName}:RefreshIntervalMs", o.RefreshIntervalMs);
            })
            .Validate(o => !string.IsNullOrWhiteSpace(o.Ip), "Plc:Ip khong duoc de trong.")
            .Validate(o => o.Port is > 0 and <= 65535, "Plc:Port phai tu 1 den 65535.")
            .Validate(o => o.TimeoutMs > 0, "Plc:TimeoutMs phai lon hon 0.")
            .Validate(o => o.Retries >= 0, "Plc:Retries khong duoc am.")
            .Validate(o => o.RefreshIntervalMs > 0, "Plc:RefreshIntervalMs phai lon hon 0.")
            .Validate(o => !string.IsNullOrWhiteSpace(o.ResultAddress), "Plc:ResultAddress khong duoc de trong.")
            .Validate(o => !string.IsNullOrWhiteSpace(o.ReadyAddress), "Plc:ReadyAddress khong duoc de trong.")
            // ValidateOnStart: fail ngay luc khoi dong thay vi chay toi luc quet ma.
            .ValidateOnStart();

        // OmronPlc: singleton, dung chung mot ket noi UDP cho ca hai luong
        // (ghi ket qua D12 va ghi heartbeat D16).
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

        // PlcWriter: serialize cac lenh ghi vi OmronPlc khong thread-safe.
        services.AddSingleton<PlcWriter>();

        return services;
    }
}
