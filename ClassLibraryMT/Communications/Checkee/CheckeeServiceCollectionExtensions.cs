using ClassLibraryMT.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace ClassLibraryMT.Communications.Checkee;

// Helper dang ky dich vu Checkee vao DI container.
public static class CheckeeServiceCollectionExtensions
{
    public const string SectionName = "Checkee";

    // Dang ky CheckeeClient + HttpClient + binding cau hinh tu appsettings.
    public static IServiceCollection AddCheckee(
        this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<CheckeeOptions>()
            // Bind thu cong (khong reflection) de tuong thich Native AOT.
            .Configure<IConfiguration>((o, cfg) =>
            {
                o.BaseUrl = ConfigReader.GetString(cfg, $"{SectionName}:BaseUrl", o.BaseUrl);
                o.ClientId = ConfigReader.GetString(cfg, $"{SectionName}:ClientId", o.ClientId);
                o.ApiKey = ConfigReader.GetString(cfg, $"{SectionName}:ApiKey", o.ApiKey);
                o.BatchCode = ConfigReader.GetString(cfg, $"{SectionName}:BatchCode", o.BatchCode);
                o.SerialType = ConfigReader.GetString(cfg, $"{SectionName}:SerialType", o.SerialType);
                o.DuplicateWindowSeconds =
                    ConfigReader.GetInt(cfg, $"{SectionName}:DuplicateWindowSeconds", o.DuplicateWindowSeconds);
            })
            .Validate(
                o => !string.IsNullOrWhiteSpace(o.BaseUrl),
                "Checkee:BaseUrl khong duoc de trong.")
            .Validate(
                o => !string.IsNullOrWhiteSpace(o.ClientId),
                "Checkee:ClientId khong duoc de trong.")
            .Validate(
                o => !string.IsNullOrWhiteSpace(o.ApiKey),
                "Checkee:ApiKey khong duoc de trong.")
            .Validate(
                o => !string.IsNullOrWhiteSpace(o.BatchCode),
                "Checkee:BatchCode khong duoc de trong.")
            // ValidateOnStart: fail ngay luc khoi dong thay vi chay toi luc quet ma.
            .ValidateOnStart();

        // HttpClient qua factory (tu dong quan ly vong doi socket).
        services.AddHttpClient(CheckeeClient.HttpClientName, (sp, http) =>
        {
            var opt = sp.GetRequiredService<IOptions<CheckeeOptions>>().Value;
            http.BaseAddress = new Uri(opt.BaseUrl.TrimEnd('/') + "/");
            http.Timeout = TimeSpan.FromSeconds(20);
        });

        // Client dang singleton de giu duoc token da lay trong bo dem.
        services.AddSingleton(sp => new CheckeeClient(
            sp.GetRequiredService<IHttpClientFactory>().CreateClient(CheckeeClient.HttpClientName),
            sp.GetRequiredService<IOptions<CheckeeOptions>>(),
            sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<CheckeeClient>>()));

        return services;
    }
}
