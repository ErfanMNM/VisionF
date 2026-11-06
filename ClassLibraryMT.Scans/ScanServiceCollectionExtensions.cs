using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ClassLibraryMT.Scans;

/// <summary>
/// Extension đăng ký dịch vụ lưu bản ghi quét mã vào DI container.
/// </summary>
public static class ScanServiceCollectionExtensions
{
    /// <summary>Tên section cấu hình chính.</summary>
    public const string ScansSectionName = "Scans";

    /// <summary>
    /// Đăng ký toàn bộ dịch vụ lưu bản ghi quét mã:
    /// <list type="bullet">
    /// <item><see cref="ScanStoreOptions"/> — bind thủ công (AOT-safe).</item>
    /// <item><see cref="IScanRecordWriter"/> — ghi bất đồng bộ qua hàng đợi.</item>
    /// <item><see cref="IScanRecordQuery"/> — đọc lại bản ghi.</item>
    /// </list>
    /// </summary>
    /// <param name="services">Service collection.</param>
    /// <param name="configuration">Cấu hình ứng dụng.</param>
    /// <returns>Chính <paramref name="services"/> để gọi chuỗi.</returns>
    public static IServiceCollection AddScanStore(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        // ---- Bind cấu hình thủ công (tương thích Native AOT) ----
        services.AddOptions<ScanStoreOptions>()
            .Configure<IConfiguration>((o, cfg) =>
            {
                o.Path = ScanConfigReader.GetString(
                    cfg, $"{ScansSectionName}:DbPath", o.Path);
                o.BatchSize = ScanConfigReader.GetInt(
                    cfg, $"{ScansSectionName}:BatchSize", o.BatchSize);
                o.FlushIntervalMs = ScanConfigReader.GetInt(
                    cfg, $"{ScansSectionName}:FlushIntervalMs", o.FlushIntervalMs);
                o.QueueCapacity = ScanConfigReader.GetInt(
                    cfg, $"{ScansSectionName}:QueueCapacity", o.QueueCapacity);
                o.RetentionDays = ScanConfigReader.GetInt(
                    cfg, $"{ScansSectionName}:RetentionDays", o.RetentionDays);
                o.PruneIntervalMs = ScanConfigReader.GetInt(
                    cfg, $"{ScansSectionName}:PruneIntervalMs", o.PruneIntervalMs);
            })
            .Validate(o => !string.IsNullOrWhiteSpace(o.Path), "Scans:DbPath khong duoc de trong.")
            .Validate(o => o.BatchSize > 0, "Scans:BatchSize phai lon hon 0.")
            .Validate(o => o.FlushIntervalMs > 0, "Scans:FlushIntervalMs phai lon hon 0.")
            .Validate(o => o.QueueCapacity > 0, "Scans:QueueCapacity phai lon hon 0.")
            .Validate(o => o.RetentionDays >= 0, "Scans:RetentionDays khong duoc am.")
            .Validate(o => o.PruneIntervalMs > 0, "Scans:PruneIntervalMs phai lon hon 0.")
            .ValidateOnStart();

        // ---- Store + writer + query ----
        services.TryAddSingleton(sp =>
        {
            var options = sp.GetRequiredService<
                Microsoft.Extensions.Options.IOptions<ScanStoreOptions>>().Value;
            return new ScanStore(options.ResolveFullPath());
        });

        services.TryAddSingleton<IScanRecordWriter, ScanRecordWriter>();
        services.TryAddSingleton<IScanRecordQuery, ScanRecordQuery>();

        return services;
    }
}

/// <summary>
/// Đọc giá trị cấu hình theo kiểu AOT-safe.
///
/// Không dùng <c>ConfigurationBinder.GetValue&lt;T&gt;</c> vì chúng dựa trên
/// reflection, sinh cảnh báo IL2026/IL3050 khi build Native AOT.
/// </summary>
internal static class ScanConfigReader
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
