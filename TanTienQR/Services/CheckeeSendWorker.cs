using ClassLibraryMT.Checkee;
using ClassLibraryMT.Scans;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace TanTienQR.Services;

/// <summary>
/// Worker nền gửi mã đã quét (kích hoạt) lên Checkee Middleware API.
///
/// <para>
/// Mỗi chu kỳ (<see cref="CheckeeOptions.SendIntervalMinutes"/>, mặc định 15
/// phút), worker gom các mã CHƯA gửi (<c>SendStatus = Pending</c>) rồi gửi
/// lên <c>POST /middleware-api/CTEs/active</c> theo batch, nhóm theo GTIN +
/// batch code vì API Checkee yêu cầu một GTIN / batchCode cho mỗi request.
/// </para>
///
/// <para>
/// Quy tắc xử lý kết quả:
/// <list type="bullet">
/// <item>HTTP 200 -> đánh dấu <c>Sent</c> (kèm thời điểm gửi).</item>
/// <item>HTTP != 200 -> đánh dấu <c>Failed</c> kèm mã HTTP + thông điệp,
/// KHÔNG tự thử lại. Người dùng xem trong bảng "Mã gửi lỗi" và bấm "Gửi lại".</item>
/// <item>Không có phản hồi (timeout / lỗi mạng) -> giữ mã ở trạng thái
/// <c>Pending</c> để thử lại ở chu kỳ sau (KHÔNG bỏ mã).</item>
/// </list>
/// </para>
///
/// <para>
/// Worker không bao giờ làm app crash: mọi lỗi đều bị bắt và ghi log.
/// </para>
/// </summary>
internal sealed class CheckeeSendWorker : BackgroundService
{
    private readonly CheckeeClient _client;
    private readonly IScanSendStatusStore _sendStore;
    private readonly IScanRecordWriter _scanWriter;
    private readonly CheckeeOptions _opt;
    private readonly ILogger<CheckeeSendWorker> _logger;

    /// <summary>Trạng thái gần nhất (để UI hiển thị, đọc an toàn từ nhiều luồng).</summary>
    private readonly CheckeeSendState _state = new();

    public CheckeeSendWorker(
        CheckeeClient client,
        IScanSendStatusStore sendStore,
        IScanRecordWriter scanWriter,
        IOptions<CheckeeOptions> options,
        ILogger<CheckeeSendWorker> logger)
    {
        _client = client;
        _sendStore = sendStore;
        _scanWriter = scanWriter;
        _opt = options.Value;
        _logger = logger;
    }

    /// <summary>
    /// Trạng thái hiện tại của worker (dùng cho tab Tra cứu mã).
    /// </summary>
    public CheckeeSendState State => _state;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_opt.Enabled)
        {
            _logger.LogInformation(
                "CheckeeSendWorker không chạy (Checkee:Enabled = false).");
            return;
        }

        if (!_opt.IsConfigured)
        {
            _logger.LogWarning(
                "CheckeeSendWorker không chạy: thiếu cấu hình BaseUrl / ApiKey / ClientId. "
                + "Vào tab Cấu hình để bổ sung.");
            _state.SetStatus(
                "Chưa cấu hình thông tin Checkee (BaseUrl / ApiKey / ClientId).",
                isError: true);
            return;
        }

        var interval = _opt.ResolveSendInterval();

        _logger.LogInformation(
            "CheckeeSendWorker bắt đầu, chu kỳ {Interval} phút, batch tối đa {Batch} mã.",
            interval.TotalMinutes,
            _opt.BatchSize);

        _state.SetStatus(
            $"Đang chờ chu kỳ gửi mã ({interval.TotalMinutes:0} phút).",
            isError: false);

        using var timer = new PeriodicTimer(interval);

        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                try
                {
                    await SendPendingAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    // Lỗi mạng / server KHÔNG được làm chết worker.
                    _logger.LogError(ex, "Lỗi trong chu kỳ gửi mã lên Checkee.");
                    _state.SetStatus(
                        "Lỗi khi gửi mã lên Checkee: " + ex.Message,
                        isError: true);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutdown bình thường.
        }
        finally
        {
            _logger.LogInformation("CheckeeSendWorker đã dừng.");
        }
    }

    /// <summary>
    /// Gom và gửi toàn bộ mã đang chờ.
    /// </summary>
    /// <param name="ct">Token hủy.</param>
    /// <returns>Số mã đã gửi thành công.</returns>
    public async Task<int> SendPendingAsync(CancellationToken ct = default)
    {
        // Đẩy bản ghi đang chờ ghi xuống DB để không bỏ sót mã vừa quét.
        try
        {
            await _scanWriter.FlushAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Không đồng bộ được bản ghi quét mã trước khi gửi Checkee.");
        }

        var pending = await _sendStore
            .GetPendingAsync(_opt.OnlyPassCodes, _opt.BatchSize, ct)
            .ConfigureAwait(false);

        if (pending.Count == 0)
        {
            _state.SetStatus("Không có mã nào chờ gửi lên Checkee.", isError: false);
            return 0;
        }

        _state.SetStatus(
            $"Đang gửi {pending.Count} mã lên Checkee...",
            isError: false);

        // Gom theo (GTIN, batchCode) vì API Checkee nhận một GTIN + batchCode
        // cho mỗi request, kèm danh sách serialNumbers.
        var groups = pending
            .GroupBy(r => (Gtin: NormalizeGtin(r.Gtin), Batch: NormalizeBatch(r)))
            .ToList();

        var sentCount = 0;
        var failedCount = 0;

        foreach (var group in groups)
        {
            var gtin = group.Key.Gtin;
            var batchCode = group.Key.Batch;

            // Mã không có GTIN hoặc batch code -> không gửi được (API bắt buộc).
            // Đánh dấu lỗi để người dùng thấy và xử lý.
            if (string.IsNullOrWhiteSpace(gtin)
                || string.IsNullOrWhiteSpace(batchCode))
            {
                foreach (var record in group)
                {
                    var missing = string.IsNullOrWhiteSpace(gtin)
                        ? "thiếu GTIN"
                        : "thiếu mã lô (batch code)";

                    await _sendStore.MarkFailedAsync(
                        record.Id,
                        0,
                        $"Không gửi được: mã {missing}. Hãy cấu hình GTIN khi quét và mã lô trong tab Cấu hình.",
                        ct).ConfigureAwait(false);

                    failedCount++;
                }

                continue;
            }

            var serials = group
                .Select(r => string.IsNullOrWhiteSpace(r.Serial) ? r.Code : r.Serial)
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Distinct(StringComparer.Ordinal)
                .ToList();

            if (serials.Count == 0)
            {
                // Không có serial nào để gửi -> đánh dấu lỗi cho rõ ràng.
                foreach (var record in group)
                {
                    await _sendStore.MarkFailedAsync(
                        record.Id,
                        0,
                        "Không gửi được: bản ghi không có mã serial.",
                        ct).ConfigureAwait(false);

                    failedCount++;
                }

                continue;
            }

            CheckeeSendResult result;
            try
            {
                result = await _client
                    .ActivateAsync(gtin, batchCode, _opt.SerialType, serials, ct)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Lỗi không lường trước -> coi như lỗi tạm thời, giữ mã chờ gửi.
                _logger.LogError(ex, "Lỗi khi gọi API kích hoạt mã trên Checkee.");
                await MarkTransientAsync(group.ToList(), ex.Message, ct).ConfigureAwait(false);
                continue;
            }

            if (result.Ok)
            {
                foreach (var record in group)
                {
                    await _sendStore.MarkSentAsync(record.Id, result.Message, ct)
                        .ConfigureAwait(false);

                    sentCount++;
                }

                _logger.LogInformation(
                    "Đã gửi {Count} mã (GTIN {Gtin}, lô {Batch}) lên Checkee.",
                    group.Count(),
                    gtin,
                    batchCode);
            }
            else if (result.IsTransient)
            {
                // Server không phản hồi -> giữ mã ở trạng thái chờ gửi.
                await MarkTransientAsync(group.ToList(), result.Message, ct)
                    .ConfigureAwait(false);

                _logger.LogWarning(
                    "Checkee không phản hồi, giữ {Count} mã để thử lại ở chu kỳ sau: {Message}",
                    group.Count(),
                    result.Message);
            }
            else
            {
                // Server đã trả lỗi -> đánh dấu Failed, KHÔNG tự thử lại.
                foreach (var record in group)
                {
                    await _sendStore.MarkFailedAsync(
                        record.Id,
                        result.StatusCode,
                        result.Message,
                        ct).ConfigureAwait(false);

                    failedCount++;
                }

                _logger.LogWarning(
                    "Checkee từ chối {Count} mã (HTTP {Code}): {Message}",
                    group.Count(),
                    result.StatusCode,
                    result.Message);
            }
        }

        _state.SetStatus(
            $"Đã gửi {sentCount} mã thành công, {failedCount} mã lỗi.",
            isError: failedCount > 0);

        _state.SetLastRunUtc(DateTime.UtcNow);

        return sentCount;
    }

    /// <summary>
    /// Đánh dấu các mã là "chờ gửi" (không tự thử lại ngay) khi lỗi là tạm thời.
    /// </summary>
    private async Task MarkTransientAsync(
        IReadOnlyList<ScanRecordEntity> records,
        string message,
        CancellationToken ct)
    {
        foreach (var record in records)
        {
            await _sendStore.MarkPendingAsync(record.Id, message, ct)
                .ConfigureAwait(false);
        }
    }

    /// <summary>
    /// GTIN chuẩn hoá (bỏ khoảng trắng). Rỗng nghĩa là bản ghi không có GTIN.
    /// </summary>
    private static string NormalizeGtin(string? gtin) => gtin?.Trim() ?? string.Empty;

    /// <summary>
    /// Mã lô dùng cho request: ưu tiên mã lô của bản ghi, sau đó tới mã lô
    /// cấu hình trong tab Cấu hình, cuối cùng là mã PO của bản ghi.
    /// </summary>
    private string NormalizeBatch(ScanRecordEntity record)
    {
        if (!string.IsNullOrWhiteSpace(record.BatchNo)
            && !string.Equals(record.BatchNo, ScanBatchDefaults.NoBatch, StringComparison.Ordinal))
        {
            return record.BatchNo.Trim();
        }

        if (!string.IsNullOrWhiteSpace(_opt.BatchCode))
        {
            return _opt.BatchCode.Trim();
        }

        return record.OrderNo?.Trim() ?? string.Empty;
    }
}

/// <summary>
/// Trạng thái hiện tại của <see cref="CheckeeSendWorker"/> — dùng cho UI.
///
/// <para>
/// Các thuộc tính được cập nhật từ luồng worker nên UI đọc qua
/// <see cref="Status"/> / <see cref="LastRunUtc"/>; dùng
/// <see cref="System.Threading.Volatile"/> để đọc nhất quán.
/// </para>
/// </summary>
public sealed class CheckeeSendState
{
    private readonly object _gate = new();
    private string _status = "Chưa chạy.";
    private bool _isError;
    private DateTime? _lastRunUtc;

    /// <summary>Thông điệp trạng thái gần nhất.</summary>
    public string Status => Volatile.Read(ref _status);

    /// <summary>True nếu thông điệp hiện tại là lỗi.</summary>
    public bool IsError => Volatile.Read(ref _isError);

    /// <summary>Thời điểm chạy chu kỳ gửi gần nhất (UTC).</summary>
    public DateTime? LastRunUtc
    {
        get
        {
            lock (_gate)
            {
                return _lastRunUtc;
            }
        }
    }

    /// <summary>
    /// Cập nhật thông điệp trạng thái.
    /// </summary>
    /// <param name="message">Thông điệp tiếng Việt.</param>
    /// <param name="isError">True nếu là thông báo lỗi.</param>
    public void SetStatus(string message, bool isError)
    {
        Volatile.Write(ref _status, message ?? string.Empty);
        Volatile.Write(ref _isError, isError);
    }

    /// <summary>
    /// Ghi nhận thời điểm chạy gần nhất.
    /// </summary>
    /// <param name="utc">Thời điểm (UTC).</param>
    public void SetLastRunUtc(DateTime utc)
    {
        lock (_gate)
        {
            _lastRunUtc = utc;
        }
    }
}