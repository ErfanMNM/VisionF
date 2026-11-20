using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ClassLibraryMT.Print;

public sealed class PrintReconcileWorker : BackgroundService
{
    private readonly PrintService _print;
    private readonly PrintOptions _options;
    private readonly IPrintLicenseGate _license;
    private readonly ILogger<PrintReconcileWorker> _logger;

    public PrintReconcileWorker(PrintService print, PrintOptions options, IPrintLicenseGate license, ILogger<PrintReconcileWorker> logger)
    {
        _print = print; _options = options; _license = license; _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(Math.Max(1000, _options.ReconcileIntervalMs), stoppingToken).ConfigureAwait(false);
                if (_options.Enabled && _license.CanUse && _print.CurrentOrder is not null)
                    await _print.ReconcileAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { _logger.LogWarning(ex, "Đối soát trạng thái mã in thất bại."); }
        }
    }
}
