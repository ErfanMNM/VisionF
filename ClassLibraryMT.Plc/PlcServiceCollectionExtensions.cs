using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ClassLibraryMT.Plc;

/// <summary>
/// Extension đăng ký dịch vụ quản lý tham số PLC vào DI container.
/// </summary>
public static class PlcServiceCollectionExtensions
{
    /// <summary>Tên section cấu hình chính.</summary>
    public const string PlcSettingsSectionName = "PlcSettings";

    /// <summary>
    /// Đăng ký toàn bộ dịch vụ quản lý tham số PLC:
    /// <list type="bullet">
    /// <item><see cref="PlcSettingsOptions"/> — bind thủ công (AOT-safe).</item>
    /// <item><see cref="IPlcSettingsService"/> — CRUD tham số/recipe + đọc/ghi PLC.</item>
    /// <item><see cref="IPlcMemoryClientFactory"/> — tạo client đọc/ghi ô nhớ.</item>
    /// </list>
    /// </summary>
    /// <param name="services">Service collection.</param>
    /// <param name="configuration">Cấu hình ứng dụng.</param>
    /// <returns>Chính <paramref name="services"/> để gọi chuỗi.</returns>
    public static IServiceCollection AddPlcSettings(
        this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        // ---- Bind cấu hình thủ công (tương thích Native AOT) ----
        services.AddOptions<PlcSettingsOptions>()
            .Configure<IConfiguration>((o, cfg) =>
            {
                o.DbPath = PlcConfigReader.GetString(
                    cfg, $"{PlcSettingsSectionName}:DbPath", o.DbPath);
            })
            .Validate(
                o => !string.IsNullOrWhiteSpace(o.DbPath),
                "PlcSettings:DbPath khong duoc de trong.")
            .ValidateOnStart();

        // ---- Cấu hình kết nối PLC cho client thủ công ----
        // Đọc từ section "Plc" (dùng chung với PLC heartbeat) để không
        // phải cấu hình IP/port hai lần.
        services.AddOptions<PlcClientOptions>()
            .Configure<IConfiguration>((o, cfg) =>
            {
                o.Ip = PlcConfigReader.GetString(cfg, "Plc:Ip", o.Ip);
                o.Port = PlcConfigReader.GetInt(cfg, "Plc:Port", o.Port);
                o.RemoteNode = (byte)PlcConfigReader.GetInt(
                    cfg, "Plc:RemoteNode", o.RemoteNode);
                o.LocalNode = (byte)PlcConfigReader.GetInt(
                    cfg, "Plc:LocalNode", o.LocalNode);
                o.TimeoutMs = PlcConfigReader.GetInt(cfg, "Plc:TimeoutMs", o.TimeoutMs);
                o.Retries = PlcConfigReader.GetInt(cfg, "Plc:Retries", o.Retries);
            })
            .Validate(o => !string.IsNullOrWhiteSpace(o.Ip), "Plc:Ip khong duoc de trong.")
            .Validate(o => o.Port is > 0 and <= 65535, "Plc:Port phai tu 1 den 65535.")
            .Validate(o => o.TimeoutMs > 0, "Plc:TimeoutMs phai lon hon 0.")
            .Validate(o => o.Retries >= 0, "Plc:Retries khong duoc am.")
            .ValidateOnStart();

        // ---- Store + client factory + service ----
        services.TryAddSingleton(sp =>
        {
            var options = sp.GetRequiredService<
                Microsoft.Extensions.Options.IOptions<PlcSettingsOptions>>().Value;
            return new PlcStore(options.ResolveFullPath());
        });

        services.TryAddSingleton<IPlcMemoryClientFactory, PlcMemoryClientFactory>();
        services.TryAddSingleton<IPlcSettingsService, PlcSettingsService>();

        return services;
    }
}

/// <summary>
/// Đọc giá trị cấu hình theo kiểu AOT-safe.
///
/// Không dùng <c>ConfigurationBinder.GetValue&lt;T&gt;</c> vì chúng dựa trên
/// reflection, sinh cảnh báo IL2026/IL3050 khi build Native AOT.
/// </summary>
internal static class PlcConfigReader
{
    /// <summary>Lấy chuỗi, trả về fallback nếu thiếu hoặc rỗng.</summary>
    public static string GetString(IConfiguration config, string key, string fallback)
    {
        var value = config[key];
        return string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
    }

    /// <summary>Lấy số nguyên, trả về fallback nếu thiếu hoặc không parse được.</summary>
    public static int GetInt(IConfiguration config, string key, int fallback)
    {
        var value = config[key];
        return int.TryParse(value, out var parsed) ? parsed : fallback;
    }
}
