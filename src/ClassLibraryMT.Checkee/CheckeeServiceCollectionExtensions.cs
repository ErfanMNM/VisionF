using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace ClassLibraryMT.Checkee;

/// <summary>
/// Extension đăng ký các dịch vụ Checkee vào DI container.
/// </summary>
public static class CheckeeServiceCollectionExtensions
{
    /// <summary>
    /// Đăng ký:
    /// <list type="bullet">
    /// <item><see cref="CheckeeOptions"/> — bind thủ công (AOT-safe).</item>
    /// <item><see cref="CheckeeClient"/> — gọi Checkee Middleware API.</item>
    /// </list>
    /// </summary>
    /// <param name="services">Service collection.</param>
    /// <param name="configuration">Cấu hình ứng dụng.</param>
    /// <returns>Chính <paramref name="services"/> để gọi chuỗi.</returns>
    public static IServiceCollection AddCheckee(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<CheckeeOptions>()
            .Configure<IConfiguration>((o, cfg) =>
            {
                o.BaseUrl = CheckeeConfigReader.GetString(
                    cfg, $"{CheckeeOptions.SectionName}:BaseUrl", o.BaseUrl);
                o.ApiKey = CheckeeConfigReader.GetString(
                    cfg, $"{CheckeeOptions.SectionName}:ApiKey", o.ApiKey);
                o.ClientId = CheckeeConfigReader.GetString(
                    cfg, $"{CheckeeOptions.SectionName}:ClientId", o.ClientId);
                o.Enabled = CheckeeConfigReader.GetBool(
                    cfg, $"{CheckeeOptions.SectionName}:Enabled", o.Enabled);
                // Đơn vị ms. Nếu file config cũ còn SendIntervalMinutes (phút)
                // và chưa có SendIntervalMs thì quy đổi sang ms để không mất
                // chu kỳ đã cài đặt trên máy.
                var sendIntervalMs = CheckeeConfigReader.GetInt(
                    cfg, $"{CheckeeOptions.SectionName}:SendIntervalMs", -1);
                if (sendIntervalMs < 0)
                {
                    var legacyMinutes = CheckeeConfigReader.GetInt(
                        cfg, $"{CheckeeOptions.SectionName}:SendIntervalMinutes", -1);
                    sendIntervalMs = legacyMinutes > 0
                        ? legacyMinutes * 60_000
                        : o.SendIntervalMs;
                }
                o.SendIntervalMs = sendIntervalMs;
                o.BatchSize = CheckeeConfigReader.GetInt(
                    cfg, $"{CheckeeOptions.SectionName}:BatchSize", o.BatchSize);
                o.TimeoutSeconds = CheckeeConfigReader.GetInt(
                    cfg, $"{CheckeeOptions.SectionName}:TimeoutSeconds", o.TimeoutSeconds);
                o.OnlyPassCodes = CheckeeConfigReader.GetBool(
                    cfg, $"{CheckeeOptions.SectionName}:OnlyPassCodes", o.OnlyPassCodes);
                o.BatchCode = CheckeeConfigReader.GetString(
                    cfg, $"{CheckeeOptions.SectionName}:BatchCode", o.BatchCode);
                o.SerialType = CheckeeConfigReader.GetString(
                    cfg, $"{CheckeeOptions.SectionName}:SerialType", o.SerialType);
            })
            .Validate(o => !string.IsNullOrWhiteSpace(o.BaseUrl),
                "Checkee:BaseUrl khong duoc de trong.")
            .Validate(o => o.SendIntervalMs >= 100,
                "Checkee:SendIntervalMs phai lon hon hoac bang 100 ms.")
            .Validate(o => o.BatchSize > 0,
                "Checkee:BatchSize phai lon hon 0.")
            .Validate(o => o.TimeoutSeconds > 0,
                "Checkee:TimeoutSeconds phai lon hon 0.")
            .ValidateOnStart();

        services.TryAddSingleton(sp =>
        {
            var options = sp.GetRequiredService<IOptions<CheckeeOptions>>().Value;
            return new CheckeeClient(options);
        });

        return services;
    }
}

/// <summary>
/// Đọc giá trị cấu hình theo kiểu AOT-safe (không dùng reflection).
/// </summary>
internal static class CheckeeConfigReader
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
