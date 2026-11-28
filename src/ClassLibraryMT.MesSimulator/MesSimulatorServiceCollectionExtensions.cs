using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ClassLibraryMT.MesSimulator;

/// <summary>Đăng ký plugin giả lập MES vào DI.</summary>
public static class MesSimulatorServiceCollectionExtensions
{
    public static IServiceCollection AddMesSimulator(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<MesSimulatorOptions>()
            .Configure<IConfiguration>((o, cfg) =>
            {
                o.Enabled = GetBool(cfg, "MesSimulator:Enabled", o.Enabled);
                o.TargetUrl = GetString(cfg, "MesSimulator:TargetUrl", o.TargetUrl);
                o.ApiKey = GetString(cfg, "MesSimulator:ApiKey", o.ApiKey);
                o.IntervalSeconds = GetInt(cfg, "MesSimulator:IntervalSeconds", o.IntervalSeconds);
                o.OrderQty = GetInt(cfg, "MesSimulator:OrderQty", o.OrderQty);
                o.Gtin = GetString(cfg, "MesSimulator:Gtin", o.Gtin);
                o.PoPrefix = GetString(cfg, "MesSimulator:PoPrefix", o.PoPrefix);
                o.CustomFilePath = GetString(cfg, "MesSimulator:CustomFilePath", o.CustomFilePath);
                o.CustomPoName = GetString(cfg, "MesSimulator:CustomPoName", o.CustomPoName);
                o.CustomOrderNo = GetString(cfg, "MesSimulator:CustomOrderNo", o.CustomOrderNo);
                o.TimeoutSeconds = GetInt(cfg, "MesSimulator:TimeoutSeconds", o.TimeoutSeconds);
            })
            .Validate(o => o.IntervalSeconds > 0, "MesSimulator:IntervalSeconds phải lớn hơn 0.")
            .Validate(o => o.OrderQty > 0, "MesSimulator:OrderQty phải lớn hơn 0.")
            .Validate(o => !string.IsNullOrWhiteSpace(o.TargetUrl), "MesSimulator:TargetUrl không được để trống.")
            .Validate(o => !string.IsNullOrWhiteSpace(o.PoPrefix), "MesSimulator:PoPrefix không được để trống.")
            .Validate(o => !o.Enabled || !string.IsNullOrWhiteSpace(o.CustomFilePath),
                "MesSimulator:CustomFilePath phải trỏ tới file CodeContent khi bật simulator.")
            .ValidateOnStart();

        services.AddHttpClient<MesSimulatorClient>((sp, http) =>
        {
            var options = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<MesSimulatorOptions>>().Value;
            http.Timeout = options.ResolveTimeout();
        });
        services.TryAddSingleton<MesSimulatorWorker>();
        services.AddHostedService(sp => sp.GetRequiredService<MesSimulatorWorker>());
        return services;
    }

    private static string GetString(IConfiguration cfg, string key, string fallback) =>
        string.IsNullOrWhiteSpace(cfg[key]) ? fallback : cfg[key]!.Trim();

    private static int GetInt(IConfiguration cfg, string key, int fallback) =>
        int.TryParse(cfg[key], out var value) ? value : fallback;

    private static bool GetBool(IConfiguration cfg, string key, bool fallback) =>
        bool.TryParse(cfg[key], out var value) ? value : fallback;
}
