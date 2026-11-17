using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace ClassLibraryMT.PoServer;

/// <summary>
/// Extension đăng ký HTTP server nhận Production Order từ MES vào DI container.
///
/// <para>
/// <see cref="PoHttpServer"/> là singleton (chỉ một listener cho cả app) và
/// được đăng ký kèm <c>AddHostedService</c> bởi ứng dụng — vì vậy listener
/// bắt đầu / dừng theo vòng đời của host.
/// </para>
/// </summary>
public static class PoServerServiceCollectionExtensions
{
    /// <summary>
    /// Đăng ký toàn bộ dịch vụ của HTTP server nhận PO:
    /// <list type="bullet">
    /// <item><see cref="PoServerOptions"/> — bind thủ công (AOT-safe).</item>
    /// <item><see cref="PoHttpServer"/> — listener + xử lý yêu cầu nhận PO.</item>
    /// </list>
    /// </summary>
    /// <param name="services">Service collection.</param>
    /// <param name="configuration">Cấu hình ứng dụng.</param>
    /// <returns>Chính <paramref name="services"/> để gọi chuỗi.</returns>
    public static IServiceCollection AddPoServer(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<PoServerOptions>()
            .Configure<IConfiguration>((o, cfg) =>
            {
                o.Enabled = PoServerConfigReader.GetBool(
                    cfg, $"{PoServerOptions.SectionName}:Enabled", o.Enabled);
                o.Port = PoServerConfigReader.GetInt(
                    cfg, $"{PoServerOptions.SectionName}:Port", o.Port);
                o.BindAddress = PoServerConfigReader.GetString(
                    cfg, $"{PoServerOptions.SectionName}:BindAddress", o.BindAddress);
                o.ApiKey = PoServerConfigReader.GetString(
                    cfg, $"{PoServerOptions.SectionName}:ApiKey", o.ApiKey);
                o.MaxBodyMB = PoServerConfigReader.GetInt(
                    cfg, $"{PoServerOptions.SectionName}:MaxBodyMB", o.MaxBodyMB);
                o.RequestTimeoutSeconds = PoServerConfigReader.GetInt(
                    cfg, $"{PoServerOptions.SectionName}:RequestTimeoutSeconds", o.RequestTimeoutSeconds);
                o.MaxConcurrentRequests = PoServerConfigReader.GetInt(
                    cfg, $"{PoServerOptions.SectionName}:MaxConcurrentRequests", o.MaxConcurrentRequests);
            })
            .Validate(o => o.Port is > 0 and <= 65535, "PoServer:Port phai tu 1 den 65535.")
            .Validate(o => o.MaxBodyMB > 0, "PoServer:MaxBodyMB phai lon hon 0.")
            .Validate(o => o.RequestTimeoutSeconds > 0, "PoServer:RequestTimeoutSeconds phai lon hon 0.")
            .Validate(o => o.MaxConcurrentRequests > 0, "PoServer:MaxConcurrentRequests phai lon hon 0.")
            .ValidateOnStart();

        // Singleton: chỉ một listener cho cả vòng đời app.
        services.TryAddSingleton<PoHttpServer>();

        return services;
    }
}

/// <summary>
/// Đọc giá trị cấu hình theo kiểu AOT-safe (không dùng reflection).
/// </summary>
internal static class PoServerConfigReader
{
    /// <summary>Lấy chuỗi, trả fallback nếu thiếu hoặc rỗng.</summary>
    public static string GetString(IConfiguration config, string key, string fallback)
    {
        var value = config[key];
        return string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
    }

    /// <summary>Lấy số nguyên, trả fallback nếu thiếu hoặc không parse được.</summary>
    public static int GetInt(IConfiguration config, string key, int fallback)
    {
        var value = config[key];
        return int.TryParse(value, out var parsed) ? parsed : fallback;
    }

    /// <summary>Lấy bool, trả fallback nếu thiếu hoặc không parse được.</summary>
    public static bool GetBool(IConfiguration config, string key, bool fallback)
    {
        var value = config[key];
        return bool.TryParse(value, out var parsed) ? parsed : fallback;
    }
}
