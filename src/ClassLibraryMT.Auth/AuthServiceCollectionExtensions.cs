using ClassLibraryMT.Logging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace ClassLibraryMT.Auth;

/// <summary>
/// Extension đăng ký dịch vụ xác thực vào DI container.
/// </summary>
public static class AuthServiceCollectionExtensions
{
    /// <summary>Tên section cấu hình chính.</summary>
    public const string AuthSectionName = "Auth";

    /// <summary>
    /// Đăng ký toàn bộ dịch vụ xác thực:
    /// <list type="bullet">
    /// <item><see cref="AuthOptions"/> — bind thủ công (AOT-safe).</item>
    /// <item><see cref="IUserService"/> — API quản lý tài khoản.</item>
    /// <item><see cref="ISessionContext"/> — phiên đăng nhập hiện tại.</item>
    /// </list>
    ///
    /// Nếu ứng dụng đã đăng ký <see cref="IUserContext"/> (lib Logging) thì sẽ
    /// dùng luôn implementation đó; nếu chưa thì đăng ký <c>SessionContext</c>
    /// làm <see cref="IUserContext"/> để nhật ký thao tác có tên người dùng.
    /// </summary>
    /// <param name="services">Service collection.</param>
    /// <param name="configuration">Cấu hình ứng dụng.</param>
    /// <returns>Chính <paramref name="services"/> để gọi chuỗi.</returns>
    public static IServiceCollection AddAuth(
        this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        // ---- Bind cấu hình thủ công (tương thích Native AOT) ----
        services.AddOptions<AuthOptions>()
            .Configure<IConfiguration>((o, cfg) =>
            {
                o.DbPath = AuthConfigReader.GetString(
                    cfg, $"{AuthSectionName}:DbPath", o.DbPath);
                o.MaxFailedAttempts = AuthConfigReader.GetInt(
                    cfg, $"{AuthSectionName}:MaxFailedAttempts", o.MaxFailedAttempts);
                o.LockoutMinutes = AuthConfigReader.GetInt(
                    cfg, $"{AuthSectionName}:LockoutMinutes", o.LockoutMinutes);
                o.Pbkdf2Iterations = AuthConfigReader.GetInt(
                    cfg, $"{AuthSectionName}:Pbkdf2Iterations", o.Pbkdf2Iterations);
                o.SeedAdminUser = AuthConfigReader.GetString(
                    cfg, $"{AuthSectionName}:SeedAdminUser", o.SeedAdminUser);
                o.SeedAdminPassword = AuthConfigReader.GetString(
                    cfg, $"{AuthSectionName}:SeedAdminPassword", o.SeedAdminPassword);
            })
            .Validate(o => !string.IsNullOrWhiteSpace(o.DbPath), "Auth:DbPath khong duoc de trong.")
            .Validate(o => o.MaxFailedAttempts > 0, "Auth:MaxFailedAttempts phai lon hon 0.")
            .Validate(o => o.LockoutMinutes > 0, "Auth:LockoutMinutes phai lon hon 0.")
            .Validate(o => o.Pbkdf2Iterations >= 10_000, "Auth:Pbkdf2Iterations phai it nhat 10000.")
            .ValidateOnStart();

        // ---- Session: đăng ký trước để các service khác dùng được ----
        services.TryAddSingleton<SessionContext>();

        // SessionContext implement cả ISessionContext lẫn IUserContext.
        services.TryAddSingleton<ISessionContext>(sp => sp.GetRequiredService<SessionContext>());

        // Nếu app chưa đăng ký IUserContext thì dùng SessionContext cho việc ghi log.
        services.TryAddSingleton<IUserContext>(sp => sp.GetRequiredService<SessionContext>());

        // ---- API quản lý tài khoản ----
        services.TryAddSingleton<IUserService, UserService>();

        return services;
    }
}

/// <summary>
/// Đọc giá trị cấu hình theo kiểu AOT-safe.
///
/// Không dùng <c>ConfigurationBinder.GetValue&lt;T&gt;</c> vì chúng dựa trên
/// reflection, sinh cảnh báo IL2026/IL3050 khi build Native AOT.
/// </summary>
internal static class AuthConfigReader
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
