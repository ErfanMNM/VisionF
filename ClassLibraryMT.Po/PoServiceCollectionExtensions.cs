using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace ClassLibraryMT.Po;

/// <summary>
/// Extension đăng ký các dịch vụ quản lý Production Order vào DI container.
/// </summary>
public static class PoServiceCollectionExtensions
{
    /// <summary>
    /// Đăng ký toàn bộ dịch vụ PO:
    /// <list type="bullet">
    /// <item><see cref="PoOptions"/> — bind thủ công (AOT-safe).</item>
    /// <item><see cref="PoClient"/> — gọi API server PO.</item>
    /// <item><see cref="PoStore"/> — lưu trữ file DB theo PO.</item>
    /// <item><see cref="PoCodeCache"/> — cache RAM tập mã.</item>
    /// <item><see cref="ProductionOrderService"/> — điều phối luồng PO.</item>
    /// </list>
    /// </summary>
    public static IServiceCollection AddProductionOrders(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<PoOptions>()
            .Configure<IConfiguration>((o, cfg) =>
            {
                o.BaseUrl = PoConfigReader.GetString(
                    cfg, $"{PoOptions.SectionName}:BaseUrl", o.BaseUrl);
                o.DbRoot = PoConfigReader.GetString(
                    cfg, $"{PoOptions.SectionName}:DbRoot", o.DbRoot);
                o.ActivationIntervalMs = PoConfigReader.GetInt(
                    cfg, $"{PoOptions.SectionName}:ActivationIntervalMs", o.ActivationIntervalMs);
                o.AutoActivate = PoConfigReader.GetBool(
                    cfg, $"{PoOptions.SectionName}:AutoActivate", o.AutoActivate);
                o.CodeSampleSize = PoConfigReader.GetInt(
                    cfg, $"{PoOptions.SectionName}:CodeSampleSize", o.CodeSampleSize);
            })
            .Validate(o => !string.IsNullOrWhiteSpace(o.BaseUrl), "Po:BaseUrl khong duoc de trong.")
            .Validate(o => o.ActivationIntervalMs > 2000, "Po:ActivationIntervalMs phai lon hon 2000.")
            .Validate(o => o.CodeSampleSize > 0, "Po:CodeSampleSize phai lon hon 0.")
            .ValidateOnStart();

        services.TryAddSingleton(sp =>
        {
            var options = sp.GetRequiredService<IOptions<PoOptions>>().Value;
            return new PoClient(options.BaseUrl);
        });

        services.TryAddSingleton(sp =>
        {
            var options = sp.GetRequiredService<IOptions<PoOptions>>().Value;
            return new PoStore(options.ResolveDbRoot());
        });

        services.TryAddSingleton<PoCodeCache>();

        services.TryAddSingleton<ProductionOrderService>();

        return services;
    }
}

/// <summary>
/// Đọc giá trị cấu hình theo kiểu AOT-safe (không dùng reflection).
/// </summary>
internal static class PoConfigReader
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
