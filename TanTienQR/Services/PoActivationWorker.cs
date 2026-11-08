using ClassLibraryMT.Po;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace TanTienQR.Services;

/// <summary>
/// Worker nền gửi mã kích hoạt định kỳ lên server.
///
/// <para>
/// Mỗi chu kỳ (<see cref="PoOptions.ActivationIntervalMs"/>, mặc định 2500ms —
/// lớn hơn 2 giây), nếu đang có PO ở trạng thái Running thì worker gửi toàn bộ
/// mã CHƯA kích hoạt của PO đó lên server qua <c>POST /api/activate</c>.
/// </para>
///
/// <para>
/// Worker tự dừng (không làm gì) khi <see cref="PoOptions.AutoActivate"/> =
/// false — khi đó người dùng có thể gửi thủ công từ tab "Truy xuất Sản xuất".
/// </para>
/// </summary>
internal sealed class PoActivationWorker : BackgroundService
{
    private readonly ProductionOrderService _po;
    private readonly PoOptions _opt;
    private readonly ILogger<PoActivationWorker> _logger;

    public PoActivationWorker(
        ProductionOrderService po,
        IOptions<PoOptions> options,
        ILogger<PoActivationWorker> logger)
    {
        _po = po;
        _opt = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Nạp lại cache từ các file DB PO đã tồn tại (PO đã quét trước khi
        // app tắt vẫn phải được nhận diện là trùng khi quét lại).
        try
        {
            await _po.RebuildCacheAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Lỗi khi nạp cache PO từ file DB.");
        }

        if (!_opt.AutoActivate)
        {
            _logger.LogInformation(
                "PoActivationWorker không chạy (Po:AutoActivate = false).");
            return;
        }

        // Chu kỳ phải > 2s theo yêu cầu.
        var interval = Math.Max(2001, _opt.ActivationIntervalMs);

        _logger.LogInformation(
            "PoActivationWorker bắt đầu, chu kỳ {Interval}ms.", interval);

        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(interval));

        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                try
                {
                    if (!_po.IsProductionRunning)
                        continue;

                    var count = await _po.ActivatePendingAsync(stoppingToken).ConfigureAwait(false);
                    if (count > 0)
                    {
                        _logger.LogInformation(
                            "PoActivationWorker đã gửi kích hoạt {Count} mã.", count);
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    // Lỗi mạng / server không được làm chết worker.
                    _logger.LogError(ex, "Lỗi khi gửi mã kích hoạt định kỳ.");
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutdown bình thường.
        }
        finally
        {
            _logger.LogInformation("PoActivationWorker đã dừng.");
        }
    }
}
