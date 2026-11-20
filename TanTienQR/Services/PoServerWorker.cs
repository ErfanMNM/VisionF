using ClassLibraryMT.PoServer;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace TanTienQR.Services;

/// <summary>
/// Worker nền khởi động HTTP server nhận Production Order từ MES.
///
/// <para>
/// Vòng đời worker bám theo host: <see cref="StartAsync"/> bật listener,
/// <see cref="StopAsync"/> tắt listener. Listener chạy trên luồng nền nên
/// app KHÔNG bị treo khi MES gửi PO.
/// </para>
///
/// <para>
/// Lỗi bind (port đã dùng, thiếu quyền) KHÔNG làm crash app — trạng thái
/// được ghi vào <see cref="PoHttpServer.State"/> và hiển thị trên UI.
/// </para>
/// </summary>
internal sealed class PoServerWorker : BackgroundService
{
    private readonly PoHttpServer _server;
    private readonly ILogger<PoServerWorker> _logger;
    private readonly ScanIntakeGate _intake;

    public PoServerWorker(
        PoHttpServer server,
        ScanIntakeGate intake,
        ILogger<PoServerWorker> logger)
    {
        _server = server;
        _intake = intake;
        _logger = logger;
    }

    /// <inheritdoc/>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (_intake.IsSuspended)
            await Task.Delay(100, stoppingToken);
        // Bật listener. Mọi lỗi đã được PoHttpServer bắt và ghi log.
        _server.Start();

        if (_server.State.IsError)
        {
            _logger.LogError(
                "HTTP server nhận PO không khởi động được: {Status}",
                _server.State.Status);
        }

        // Listener tự chạy nền; StopAsync chờ kết thúc toàn bộ request đã nhận.
    }

    /// <inheritdoc/>
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _server.StopAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Lỗi khi dừng HTTP server nhận PO.");
            throw;
        }

        await base.StopAsync(cancellationToken).ConfigureAwait(false);
    }
}
