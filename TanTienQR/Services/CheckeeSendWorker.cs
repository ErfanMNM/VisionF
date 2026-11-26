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
/// Mỗi chu kỳ (<see cref="CheckeeOptions.SendIntervalMs"/>, mặc định 1000 ms),
/// worker gom các mã CHƯA gửi (<c>SendStatus = Pending</c>) rồi gửi
/// lên <c>POST /middleware-api/CTEs/active</c> theo batch, nhóm theo GTIN +
/// batch code vì API Checkee yêu cầu một GTIN / batchCode cho mỗi request.
/// </para>
///
/// <para>
/// Quy tắc xử lý kết quả:
/// <list type="bullet">
/// <item>HTTP thành công và Checkee chấp nhận -> đánh dấu <c>Sent</c> (kèm thời điểm gửi).</item>
/// <item>HTTP lỗi hoặc Checkee từ chối nghiệp vụ -> đánh dấu <c>Failed</c> kèm mã HTTP + thông điệp,
/// KHÔNG tự thử lại. Người dùng xem trong bảng "Mã gửi lỗi" và bấm "Gửi lại".</item>
/// <item>Không có phản hồi (timeout / lỗi mạng) -> giữ mã ở trạng thái
/// <c>Pending</c> để thử lại ở chu kỳ sau (KHÔNG bỏ mã).</item>
/// <item>Thiếu metadata GTIN sản phẩm / mã lô -> giữ <c>Pending</c>,
/// giải thích thông tin cần bổ sung; không thay đổi kết quả quét Pass.</item>
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
    private readonly CheckeeOutboundOptions _outbound;
    private readonly ILogger<CheckeeSendWorker> _logger;
    private readonly ScanIntakeGate _intake;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private int _inFlight;

    /// <summary>Trạng thái gần nhất (để UI hiển thị, đọc an toàn từ nhiều luồng).</summary>
    private readonly CheckeeSendState _state = new();

    public CheckeeSendWorker(
        CheckeeClient client,
        IScanSendStatusStore sendStore,
        IScanRecordWriter scanWriter,
        IOptions<CheckeeOptions> options,
        IOptions<CheckeeOutboundOptions> outbound,
        ScanIntakeGate intake,
        ILogger<CheckeeSendWorker> logger)
    {
        _client = client;
        _sendStore = sendStore;
        _scanWriter = scanWriter;
        _opt = options.Value;
        _outbound = outbound.Value;
        _intake = intake;
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
            "CheckeeSendWorker bắt đầu, chu kỳ {IntervalMs} ms, batch tối đa {Batch} mã.",
            interval.TotalMilliseconds,
            _opt.BatchSize);

        _state.SetStatus(
            $"Đang chờ chu kỳ gửi mã ({interval.TotalMilliseconds:0} ms).",
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
    public Task<int> SendPendingAsync(CancellationToken ct = default) =>
        SendWithGateAsync(() => SendPendingCoreAsync(ct), 0, ct);

    /// <summary>Đưa mã lỗi về Pending rồi gửi trong cùng lượt được theo dõi khi bàn giao.</summary>
    public Task<(int Reset, int Sent)> ResendFailedAsync(IReadOnlyList<long> ids, CancellationToken ct = default) =>
        SendWithGateAsync(async () =>
        {
            var reset = await _sendStore.ResetToPendingAsync(ids, ct).ConfigureAwait(false);
            var sent = await SendPendingCoreAsync(ct).ConfigureAwait(false);
            return (reset, sent);
        }, (0, 0), ct);

    private async Task<T> SendWithGateAsync<T>(Func<Task<T>> action, T suspendedResult, CancellationToken ct)
    {
        // Admission dùng cùng khóa với bật bảo trì: Drain không bỏ sót một
        // yêu cầu manual/timer vừa đi qua cổng nhưng chưa bắt đầu HTTP.
        var admitted = false;
        _intake.Dispatch(() =>
        {
            Interlocked.Increment(ref _inFlight);
            admitted = true;
        });
        if (!admitted)
        {
            _state.SetStatus("Tạm dừng gửi mã trong lúc cập nhật phần mềm.", isError: false);
            return suspendedResult;
        }

        var acquired = false;
        try
        {
            // Một lượt manual và một tick timer không được gửi trùng tập Pending.
            await _sendLock.WaitAsync(ct).ConfigureAwait(false);
            acquired = true;
            if (_intake.IsSuspended)
                return suspendedResult;
            return await action().ConfigureAwait(false);
        }
        finally
        {
            if (acquired) _sendLock.Release();
            Interlocked.Decrement(ref _inFlight);
        }
    }

    internal async Task DrainAsync(CancellationToken ct)
    {
        if (!_intake.IsSuspended)
            throw new InvalidOperationException("Phải khóa nhận lượt gửi mới trước khi chờ bàn giao cập nhật.");
        // Bao gồm cả ghi trạng thái trả về vào SQLite sau khi HTTP đã hoàn tất.
        while (Volatile.Read(ref _inFlight) != 0)
            await Task.Delay(25, ct).ConfigureAwait(false);
    }

    private async Task<int> SendPendingCoreAsync(CancellationToken ct)
    {
        // Đẩy bản ghi đang chờ ghi xuống DB để không bỏ sót mã vừa quét.
        try
        {
            await _scanWriter.FlushAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
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

        // GTIN là metadata của giao thức Checkee. Worker giữ nguyên CodeContent
        // để đối chiếu bản ghi; client tách serial sau /21/ khi tạo payload gửi.
        var groups = pending
            .GroupBy(r => (Gtin: ResolveProductGtin(r), Batch: NormalizeBatch(r)))
            .ToList();

        var sentCount = 0;
        var failedCount = 0;
        var blockedCount = 0;
        var transientCount = 0;
        var blockedReasons = new HashSet<string>(StringComparer.Ordinal);

        foreach (var group in groups)
        {
            var gtin = group.Key.Gtin;
            var batchCode = group.Key.Batch;

            // Thiếu metadata cấu hình không phải server từ chối mã. Giữ Pending
            // để gửi lại sau khi bổ sung mà không mất bản ghi Pass.
            if (string.IsNullOrWhiteSpace(gtin)
                || string.IsNullOrWhiteSpace(batchCode))
            {
                var missing = new List<string>();
                if (string.IsNullOrWhiteSpace(gtin)) missing.Add("Checkee:ProductGtin (GTIN sản phẩm)");
                if (string.IsNullOrWhiteSpace(batchCode)) missing.Add("mã lô Checkee:BatchCode hoặc mã PO");
                var message = "Đang chờ bổ sung " + string.Join(" và ", missing) + ". Mã quét vẫn được giữ lại.";
                blockedReasons.Add(message);
                foreach (var record in group)
                {
                    await _sendStore.MarkPendingAsync(record.Id, message, ct).ConfigureAwait(false);
                    blockedCount++;
                }

                continue;
            }

            // Không được đánh dấu Sent cho bản ghi không có nội dung chỉ vì
            // cùng nhóm metadata với một bản ghi gửi được.
            var recordsToSend = new List<ScanRecordEntity>();
            foreach (var record in group)
            {
                if (string.IsNullOrWhiteSpace(record.CodeContent))
                {
                    const string message = "Đang chờ đối soát: bản ghi không có CodeContent. Mã quét vẫn được giữ lại.";
                    await _sendStore.MarkPendingAsync(record.Id, message, ct).ConfigureAwait(false);
                    blockedReasons.Add(message);
                    blockedCount++;
                }
                else recordsToSend.Add(record);
            }
            if (recordsToSend.Count == 0) continue;

            var codeContents = recordsToSend.Select(record => record.CodeContent)
                .Distinct(StringComparer.Ordinal).ToList();

            var attemptId = await _sendStore.BeginSendAttemptAsync(
                recordsToSend, gtin, batchCode, _opt.SerialType, ct).ConfigureAwait(false);
            CheckeeSendResult result;
            try
            {
                result = await _client
                    .ActivateAsync(gtin, batchCode, _opt.SerialType, codeContents, ct)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // HTTP có thể đã đến máy chủ. Lưu trạng thái chưa xác định,
                // không đánh dấu Sent và không tự gửi lại khi khởi động tiếp.
                await _sendStore.CompleteSendAttemptAsync(attemptId, CheckeeSendAttemptStatuses.Unknown, 0,
                    "Lượt gửi bị hủy trước khi xác nhận kết quả; cần đối soát trước khi gửi lại.", CancellationToken.None)
                    .ConfigureAwait(false);
                throw;
            }
            catch (Exception ex)
            {
                // Lỗi không lường trước -> coi như lỗi tạm thời, giữ mã chờ gửi.
                _logger.LogError(ex, "Lỗi khi gọi API kích hoạt mã trên Checkee.");
                result = new CheckeeSendResult { Message = ex.Message, IsTransient = true };
            }

            var attemptStatus = result.IsUnknown ? CheckeeSendAttemptStatuses.Unknown
                : result.Ok ? CheckeeSendAttemptStatuses.Sent
                : result.IsTransient ? CheckeeSendAttemptStatuses.RetryPending : CheckeeSendAttemptStatuses.Failed;
            // Sau khi server trả lời, hoàn tất SQLite độc lập token của caller.
            // History và trạng thái từng mã được commit chung một transaction.
            await _sendStore.CompleteSendAttemptAsync(attemptId, attemptStatus,
                result.StatusCode, result.Message, CancellationToken.None).ConfigureAwait(false);

            if (result.Ok)
            {
                sentCount += recordsToSend.Count;

                _logger.LogInformation(
                    "Đã gửi {Count} mã (GTIN {Gtin}, lô {Batch}) lên Checkee.",
                    recordsToSend.Count,
                    gtin,
                    batchCode);
            }
            else if (result.IsTransient)
            {
                // Server không phản hồi -> giữ mã ở trạng thái chờ gửi.
                transientCount += recordsToSend.Count;

                _logger.LogWarning(
                    "Checkee không phản hồi, giữ {Count} mã để thử lại ở chu kỳ sau: {Message}",
                    recordsToSend.Count,
                    result.Message);
            }
            else
            {
                // Server đã trả lỗi -> đánh dấu Failed, KHÔNG tự thử lại.
                failedCount += recordsToSend.Count;

                _logger.LogWarning(
                    "Checkee từ chối {Count} mã (HTTP {Code}): {Message}",
                    recordsToSend.Count,
                    result.StatusCode,
                    result.Message);
            }
        }

        var status = $"Đã gửi {sentCount} mã thành công, {failedCount} mã lỗi.";
        if (transientCount > 0) status += $" {transientCount} mã chờ thử lại khi Checkee phản hồi.";
        if (blockedCount > 0) status += $" {blockedCount} mã đang chờ xử lý. " + string.Join(" ", blockedReasons);
        _state.SetStatus(status, isError: failedCount > 0 || blockedCount > 0 || transientCount > 0);

        _state.SetLastRunUtc(DateTime.UtcNow);

        return sentCount;
    }

    /// <summary>
    /// Metadata GTIN của request: ưu tiên cấu hình sản phẩm, chỉ fallback
    /// metadata đã lưu trong bản ghi cũ. Tuyệt đối không phân tích CodeContent.
    /// </summary>
    private string ResolveProductGtin(ScanRecordEntity record) =>
        !string.IsNullOrWhiteSpace(_outbound.ProductGtin)
            ? _outbound.ProductGtin.Trim()
            : record.Gtin?.Trim() ?? string.Empty;

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
