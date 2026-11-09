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
        // Khôi phục PO đang chạy từ lần trước: nạp tập mã của PO đó vào cache
        // RAM và đặt lại trạng thái. Camera sẽ tiếp tục ghi kết quả xuống PLC
        // ngay vì ValidateCode đã thấy trạng thái Running.
        try
        {
            await _po.RestoreRunningOrderAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Lỗi khi khôi phục PO đang chạy.");
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
                    // Máy đã dừng do phát hiện mã trùng lịch sử → ngừng gửi.
                    if (_po.IsHalted)
                    {
                        continue;
                    }

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
