using ClassLibraryMT.DeviceStatus;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace QRDemoCodeBackend;

/// <summary>
/// Vòng lặp nền ghi trạng thái Ready xuống PLC (D16) — tương đương vòng
/// lặp <c>WK_Update</c> của dự án TApp.
///
/// Mỗi vòng luôn ghi giá trị hiện tại (0 hoặc 1) xuống D16. Chính kết quả
/// của lệnh ghi quyết định trạng thái kết nối PLC: ghi thành công thì
/// <see cref="PLCStatus.Connected"/>, ghi lỗi thì <see cref="PLCStatus.Disconnect"/>.
///
/// Vì vậy vòng lặp phải luôn chạy (kể cả khi chưa Ready) — nếu chỉ ghi lúc
/// Ready thì không bao giờ phát hiện được kết nối PLC.
/// </summary>
internal sealed class PlcHeartbeatService : BackgroundService
{
    private readonly PlcWriter _writer;
    private readonly PlcOptions _opt;
    private readonly ILogger<PlcHeartbeatService> _logger;

    public PlcHeartbeatService(
        PlcWriter writer,
        IOptions<PlcOptions> options,
        ILogger<PlcHeartbeatService> logger)
    {
        _writer = writer;
        _opt = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromMilliseconds(_opt.RefreshIntervalMs);

        // PeriodicTimer là BCL, không cần package thêm.
        using var timer = new PeriodicTimer(interval);

        // Ghi ngay một lần khi khởi động để xác định trạng thái kết nối PLC.
        await TickAsync(stoppingToken);

        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
                await TickAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutdown bình thường.
        }
    }

    private async Task TickAsync(CancellationToken ct)
    {
        // Ready được tính tự động từ e_AppState (license + camera + PLC).
        var ready = GlobalVariables.Ready;

        var before = GlobalVariables.PLCStatus;
        await _writer.WriteReadyAsync(ready, ct);
        var after = GlobalVariables.PLCStatus;

        // Không spam log mỗi vòng: chỉ log khi trạng thái vừa đổi.
        if (after == PLCStatus.Connected)
        {
            if (before != PLCStatus.Connected)
                _logger.LogInformation("PLC da ket noi.");
            return;
        }

        if (before == PLCStatus.Connected)
            _logger.LogWarning("PLC mat ket noi.");
    }
}
