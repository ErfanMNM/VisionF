using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ClassLibraryMT.Po;

namespace ClassLibraryMT.Print;

public static class PrintServiceCollectionExtensions
{
    public static IServiceCollection AddPrinting(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<PrintOptions>()
            .Configure<IConfiguration>((options, cfg) =>
            {
                options.Enabled = ReadBool(cfg, "Print:Enabled", options.Enabled);
                options.DefaultPrinterId = ReadString(cfg, "Print:DefaultPrinterId", options.DefaultPrinterId);
                options.BatchSize = ReadInt(cfg, "Print:BatchSize", options.BatchSize);
                options.ResponseTimeoutMs = ReadInt(cfg, "Print:ResponseTimeoutMs", options.ResponseTimeoutMs);
                options.ReconcileIntervalMs = ReadInt(cfg, "Print:ReconcileIntervalMs", options.ReconcileIntervalMs);

                var configured = cfg.GetSection("Print:Printers").GetChildren().ToList();
                if (configured.Count > 0)
                {
                    options.Printers = configured.Select((section, index) => new PrinterOptions
                    {
                        Id = ReadString(section, "Id", $"g10-{index + 1}"),
                        Name = ReadString(section, "Name", $"Máy in {index + 1}"),
                        Protocol = ReadString(section, "Protocol", "G10Tcp"),
                        Ip = ReadString(section, "Ip", "192.168.0.3"),
                        Port = ReadInt(section, "Port", 6007),
                        BufferCapacity = ReadInt(section, "BufferCapacity", options.BatchSize),
                        MfdName = ReadString(section, "MfdName", ReadString(cfg, "Print:MfdName", "POD01")),
                        Enabled = ReadBool(section, "Enabled", true),
                    }).ToList();
                }
                if (options.Printers.Count == 0)
                    options.Printers.Add(new PrinterOptions
                    {
                        Id = ReadString(cfg, "Print:DefaultPrinterId", options.DefaultPrinterId),
                        Name = ReadString(cfg, "Print:PrinterName", "Máy in G10"),
                        Ip = ReadString(cfg, "Print:Ip", "192.168.0.3"),
                        Port = ReadInt(cfg, "Print:Port", 6007),
                        BufferCapacity = ReadInt(cfg, "Print:BufferCapacity", options.BatchSize),
                        MfdName = ReadString(cfg, "Print:MfdName", "POD01"),
                    });
                options.DefaultPrinterId = ReadString(cfg, "Print:DefaultPrinterId", options.DefaultPrinterId);
                var primary = options.Printers.FirstOrDefault(p => p.Id.Equals(options.DefaultPrinterId, StringComparison.OrdinalIgnoreCase));
                if (primary is not null)
                {
                    primary.Name = ReadString(cfg, "Print:PrinterName", primary.Name);
                    primary.Ip = ReadString(cfg, "Print:Ip", primary.Ip);
                    primary.Port = ReadInt(cfg, "Print:Port", primary.Port);
                    primary.BufferCapacity = ReadInt(cfg, "Print:BufferCapacity", primary.BufferCapacity);
                    primary.MfdName = ReadString(cfg, "Print:MfdName", primary.MfdName);
                }
            })
            .Validate(o => o.Printers.All(p => !string.IsNullOrWhiteSpace(p.MfdName)), "Print:MfdName không được để trống.")
            .Validate(o => o.BatchSize is > 0 and <= 1000, "Print:BatchSize phải từ 1 đến 1000.")
            .Validate(o => o.ResponseTimeoutMs > 0, "Print:ResponseTimeoutMs phải lớn hơn 0.")
            .Validate(o => o.Printers.All(p => p.Port is > 0 and <= 65535), "Print:Printers:Port không hợp lệ.")
            .ValidateOnStart();

        services.AddSingleton(sp => sp.GetRequiredService<IOptions<PrintOptions>>().Value);
        services.AddSingleton<PrintQueueStore>();
        services.AddSingleton<IPrinterClient>(sp =>
        {
            var options = sp.GetRequiredService<PrintOptions>();
            var printer = options.Printers.FirstOrDefault(p => p.Id.Equals(options.DefaultPrinterId, StringComparison.OrdinalIgnoreCase))
                ?? options.Printers.First();
            return new G10PrinterClient(printer, options.ResponseTimeoutMs, sp.GetService<ILogger<G10PrinterClient>>());
        });
        services.AddSingleton<PrintService>();
        services.AddHostedService<PrintReconcileWorker>();
        return services;
    }

    private static string ReadString(IConfiguration config, string key, string fallback) =>
        string.IsNullOrWhiteSpace(config[key]) ? fallback : config[key]!.Trim();
    private static int ReadInt(IConfiguration config, string key, int fallback) =>
        int.TryParse(config[key], out var value) ? value : fallback;
    private static bool ReadBool(IConfiguration config, string key, bool fallback) =>
        bool.TryParse(config[key], out var value) ? value : fallback;
}
