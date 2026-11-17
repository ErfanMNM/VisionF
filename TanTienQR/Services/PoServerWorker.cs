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

    public PoServerWorker(
        PoHttpServer server,
        ILogger<PoServerWorker> logger)
    {
        _server = server;
        _logger = logger;
    }

    /// <inheritdoc/>
    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Bật listener. Mọi lỗi đã được PoHttpServer bắt và ghi log.
        _server.Start();

        if (_server.State.IsError)
        {
            _logger.LogError(
                "HTTP server nhận PO không khởi động được: {Status}",
                _server.State.Status);
        }

        // Không có việc gì để poll trong vòng lặp — listener tự chạy nền.
        // Trả về Task đã hoàn tất để host không coi worker là "bị treo".
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        try
        {
            _server.Stop();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Lỗi khi dừng HTTP server nhận PO.");
        }

        await base.StopAsync(cancellationToken).ConfigureAwait(false);
    }
}
