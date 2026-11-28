using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace ClassLibraryMT.Logging;

/// <summary>
/// Extension đăng ký dịch vụ ghi log SQLite vào DI container.
/// </summary>
public static class SqliteLoggingServiceCollectionExtensions
{
    /// <summary>Tên section cấu hình chính.</summary>
    public const string LoggingSectionName = "Logging";

    /// <summary>Tên section cấu hình SQLite.</summary>
    public const string SqliteSectionName = "Sqlite";

    /// <summary>Tên section cấu hình trang Lịch sử.</summary>
    public const string HistorySectionName = "History";

    /// <summary>
    /// Đăng ký toàn bộ dịch vụ ghi log SQLite:
    /// <list type="bullet">
    /// <item><see cref="SqliteLoggingOptions"/> + <see cref="LoggingHistoryOptions"/> (bind thủ công, AOT-safe).</item>
    /// <item><see cref="SqliteLoggerProvider"/> — mọi <c>ILogger&lt;T&gt;</c> tự chảy vào SQLite.</item>
    /// <item><see cref="IUserActionLogger"/> — API ghi thao tác người dùng.</item>
    /// <item><see cref="ILogQueryService"/> — API đọc log cho trang Lịch sử.</item>
    /// </list>
    /// </summary>
    /// <param name="services">Service collection.</param>
    /// <param name="configuration">Cấu hình ứng dụng.</param>
    /// <returns>Chính <paramref name="services"/> để gọi chuỗi.</returns>
    public static IServiceCollection AddSqliteLogging(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        // ---- Bind cấu hình thủ công (tương thích Native AOT) ----
        services.AddOptions<SqliteLoggingOptions>()
            .Configure<IConfiguration>((o, cfg) =>
            {
                o.Path = LogConfigReader.GetString(
                    cfg, $"{LoggingSectionName}:{SqliteSectionName}:Path", o.Path);
                o.MinLevel = LogConfigReader.GetLogLevel(
                    cfg, $"{LoggingSectionName}:{SqliteSectionName}:MinLevel", o.MinLevel);
                o.RetentionDays = LogConfigReader.GetInt(
                    cfg, $"{LoggingSectionName}:{SqliteSectionName}:RetentionDays", o.RetentionDays);
                o.EnableUserActions = LogConfigReader.GetBool(
                    cfg, $"{LoggingSectionName}:{SqliteSectionName}:EnableUserActions", o.EnableUserActions);
                o.BatchSize = LogConfigReader.GetInt(
                    cfg, $"{LoggingSectionName}:{SqliteSectionName}:BatchSize", o.BatchSize);
                o.FlushIntervalMs = LogConfigReader.GetInt(
                    cfg, $"{LoggingSectionName}:{SqliteSectionName}:FlushIntervalMs", o.FlushIntervalMs);
                o.PruneIntervalMs = LogConfigReader.GetInt(
                    cfg, $"{LoggingSectionName}:{SqliteSectionName}:PruneIntervalMs", o.PruneIntervalMs);
            })
            .Validate(o => !string.IsNullOrWhiteSpace(o.Path), "Logging:Sqlite:Path khong duoc de trong.")
            .Validate(o => o.RetentionDays >= 0, "Logging:Sqlite:RetentionDays khong duoc am.")
            .Validate(o => o.BatchSize > 0, "Logging:Sqlite:BatchSize phai lon hon 0.")
            .Validate(o => o.FlushIntervalMs > 0, "Logging:Sqlite:FlushIntervalMs phai lon hon 0.")
            .Validate(o => o.PruneIntervalMs > 0, "Logging:Sqlite:PruneIntervalMs phai lon hon 0.")
            .ValidateOnStart();

        services.AddOptions<LoggingHistoryOptions>()
            .Configure<IConfiguration>((o, cfg) =>
            {
                o.Enabled = LogConfigReader.GetBool(
                    cfg, $"{LoggingSectionName}:{HistorySectionName}:Enabled", o.Enabled);
                o.PageSize = LogConfigReader.GetInt(
                    cfg, $"{LoggingSectionName}:{HistorySectionName}:PageSize", o.PageSize);
            })
            .Validate(o => o.PageSize > 0, "Logging:History:PageSize phai lon hon 0.")
            .ValidateOnStart();

        // ---- Worker ghi (singleton: 1 connection logic cho toàn app) ----
        services.TryAddSingleton<SqliteLogWriter>();

        // ---- Provider: nối vào pipeline ILogger hiện có ----
        services.AddSingleton<ILoggerProvider, SqliteLoggerProvider>();

        // ---- API thao tác người dùng + API đọc log ----
        services.TryAddSingleton<IUserActionLogger, UserActionLogger>();
        services.TryAddSingleton<ILogQueryService, LogQueryService>();

        return services;
    }
}
