using ClassLibraryMT.DeviceStatus;
using ClassLibraryMT.Po;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace TanTienQR.Services;

/// <summary>Heartbeat Ready sống suốt vòng đời app, kể cả khi PLC bắt đầu ở chế độ tắt.</summary>
internal sealed class PlcHeartbeatService : BackgroundService
{
    private readonly PlcWriter _writer;
    private readonly ProductionOrderService? _po;
    private readonly ScanIntakeGate? _intake;
    private readonly ILogger<PlcHeartbeatService> _logger;

    public PlcHeartbeatService(PlcWriter writer, IOptions<PlcOptions> options,
        ILogger<PlcHeartbeatService> logger)
    {
        _writer = writer;
        _logger = logger;
    }

    public PlcHeartbeatService(PlcWriter writer, IOptions<PlcOptions> options,
        ILogger<PlcHeartbeatService> logger, ProductionOrderService po, ScanIntakeGate? intake = null) : this(writer, options, logger)
    {
        _po = po;
        _intake = intake;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                using var wait = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                var changed = _writer.WaitForConfigurationChangeAsync(wait.Token);
                try
                {
                    if (_writer.Enabled) await TickAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    wait.Cancel();
                    try { await changed.ConfigureAwait(false); } catch (OperationCanceledException) { }
                    break;
                }
                catch (Exception ex)
                {
                    // Lỗi ngoài dự kiến chỉ bỏ tick này; không làm worker chết.
                    _logger.LogError(ex, "Heartbeat PLC gặp lỗi; sẽ thử lại ở chu kỳ tiếp theo.");
                }

                var interval = _writer.Enabled
                    ? TimeSpan.FromMilliseconds(Math.Max(1, _writer.RefreshIntervalMs))
                    : Timeout.InfiniteTimeSpan;
                var delay = Task.Delay(interval, wait.Token);
                try
                {
                    await Task.WhenAny(changed, delay).ConfigureAwait(false);
                    stoppingToken.ThrowIfCancellationRequested();
                }
                finally
                {
                    // Hủy cả waiter và timer còn lại, không tích lũy callback
                    // qua nhiều lần bật/tắt hoặc thay chu kỳ heartbeat.
                    wait.Cancel();
                    try { await Task.WhenAll(changed, delay).ConfigureAwait(false); }
                    catch (OperationCanceledException) when (wait.IsCancellationRequested) { }
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }

    private async Task TickAsync(CancellationToken ct)
    {
        var before = GlobalVariables.PLCStatus;
        await _writer.WriteReadyAsync(_po?.IsHalted == true || _po?.IsCompletionPending == true || _intake?.IsSuspended == true ? 0 : GlobalVariables.Ready, ct).ConfigureAwait(false);
        await _writer.ReadCountersAsync(ct).ConfigureAwait(false);
        var after = GlobalVariables.PLCStatus;
        if (after == PLCStatus.Connected)
        {
            if (before != PLCStatus.Connected) _logger.LogInformation("PLC da ket noi.");
        }
        else if (before == PLCStatus.Connected)
            _logger.LogWarning("PLC mat ket noi.");
    }
}
