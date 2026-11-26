using System.Text.Json;
using System.Text.RegularExpressions;
using ClassLibraryMT.Checkee;
using ClassLibraryMT.Po;
using ClassLibraryMT.Scans;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace TanTienQR.Services;

/// <summary>
/// Gửi Pass của sending-session PO đã quan sát Running. Sau auto-complete chỉ drain
/// đúng OrderNo + CreatedUtc đã nhận; PO mới thay session, startup không chọn lịch sử.
/// </summary>
internal sealed class CheckeeSendWorker : BackgroundService
{
    private readonly CheckeeClient _client;
    private readonly IScanSendStatusStore _sendStore;
    private readonly IScanRecordWriter _scanWriter;
    private readonly CheckeeOptions _opt;
    private readonly CheckeeOutboundOptions _outbound;
    private readonly ProductionOrderService _po;
    private readonly ILogger<CheckeeSendWorker> _logger;
    private readonly ScanIntakeGate _intake;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly SemaphoreSlim _wake = new(0, 1);
    private readonly object _sessionGate = new();
    private ProductionOrder? _sendingSession;
    private int _sendingEnabled;
    private int _inFlight;
    private readonly CheckeeSendState _state = new();

    public CheckeeSendWorker(CheckeeClient client, IScanSendStatusStore sendStore,
        IScanRecordWriter scanWriter, IOptions<CheckeeOptions> options,
        IOptions<CheckeeOutboundOptions> outbound, ScanIntakeGate intake,
        ProductionOrderService po, ILogger<CheckeeSendWorker> logger)
    {
        _client=client; _sendStore=sendStore; _scanWriter=scanWriter;
        _opt=options.Value; _outbound=outbound.Value; _intake=intake; _po=po; _logger=logger;
        _sendingEnabled=_opt.Enabled ? 1 : 0;
        _po.Changed += OnPoChanged;
        ObserveRunningOrder();
        _state.SetStatus(SendingEnabled ? "Đang chờ mã Pass của PO đang chạy." : "Đã tắt gửi Checkee.", false);
    }

    public CheckeeSendState State => _state;
    public bool SendingEnabled
    {
        get => Volatile.Read(ref _sendingEnabled) == 1;
        set
        {
            Interlocked.Exchange(ref _sendingEnabled, value ? 1 : 0);
            _state.SetStatus(value ? "Đã bật gửi Checkee; đang chờ xử lý." : "Đã tắt gửi Checkee; lượt đã gửi đang hoàn tất.", false);
            if (value && _wake.CurrentCount == 0)
                try { _wake.Release(); } catch (SemaphoreFullException) { }
        }
    }

    private static bool SameOrder(ProductionOrder? a, ProductionOrder? b) => a is not null && b is not null
        && a.OrderNo == b.OrderNo && a.CreatedUtc == b.CreatedUtc;

    private void OnPoChanged(object? sender, EventArgs e) => ObserveRunningOrder();
    private ProductionOrder? ObserveRunningOrder()
    {
        // Serialize observation so a stale observer cannot overwrite a newer session.
        lock (_sessionGate)
        {
            var running = _po.GetRunningOrderSnapshot();
            if (running is not null) _sendingSession = running;
            else if (_po.LastFinishedOrder is { } finished
                && (_po.LastFinishedStatus == ProductionOrderStatus.Cancelled
                    || !SameOrder(_sendingSession, finished))) _sendingSession = null;
            return _sendingSession;
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            // Keep the loop alive when disabled; runtime enable wakes it without a restart.
            while (!stoppingToken.IsCancellationRequested)
            {
                try { await SendPendingAsync(stoppingToken).ConfigureAwait(false); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                catch (Exception ex)
                {
                    _logger.LogError("Chu kỳ Checkee thất bại ({ErrorType}).", ex.GetType().Name);
                    _state.SetStatus("Không gửi được Checkee; xem audit và lịch sử lần gửi.", true);
                }
                await _wake.WaitAsync(_opt.ResolveSendInterval(), stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }

    public Task<int> SendPendingAsync(CancellationToken ct = default) =>
        SendWithGateAsync(() => SendPendingCoreAsync(ct), 0, ct);

    public Task<(int Reset, int Sent)> ResendFailedAsync(IReadOnlyList<long> ids, CancellationToken ct = default) =>
        SendWithGateAsync(async () =>
        {
            var session=ObserveRunningOrder();
            if (session is null) return (0, 0);
            var failed=await _sendStore.GetBySendStatusAsync(new ScanSendFilter
            {
                SendStatus=ScanSendStatuses.Failed, OnlyPass=true, OnlyAccepted=true,
                OrderNo=session.OrderNo, OrderCreatedUtc=session.CreatedUtc, Gtin=session.Gtin, Limit=5000,
            }, ct).ConfigureAwait(false);
            var requested=ids.ToHashSet();
            var eligible=failed.Where(r => requested.Contains(r.Id) && MatchesCode(r, session.Gtin)).Select(r => r.Id).ToArray();
            if (!SendingEnabled) return (0, 0);
            var reset=await _sendStore.ResetToPendingAsync(eligible, ct).ConfigureAwait(false);
            var sent=await SendPendingCoreAsync(ct).ConfigureAwait(false);
            return (reset, sent);
        }, (0, 0), ct);

    private async Task<T> SendWithGateAsync<T>(Func<Task<T>> action, T suspendedResult, CancellationToken ct)
    {
        if (!SendingEnabled) { _state.SetStatus("Đã tắt gửi Checkee.", false); return suspendedResult; }
        var admitted=false;
        _intake.Dispatch(() => { Interlocked.Increment(ref _inFlight); admitted=true; });
        if (!admitted) { _state.SetStatus("Tạm dừng gửi mã trong lúc cập nhật phần mềm.", false); return suspendedResult; }
        var acquired=false;
        try
        {
            await _sendLock.WaitAsync(ct).ConfigureAwait(false); acquired=true;
            if (_intake.IsSuspended || !SendingEnabled) return suspendedResult;
            return await action().ConfigureAwait(false);
        }
        finally { if (acquired) _sendLock.Release(); Interlocked.Decrement(ref _inFlight); }
    }

    internal async Task DrainAsync(CancellationToken ct)
    {
        if (!_intake.IsSuspended) throw new InvalidOperationException("Phải khóa nhận lượt gửi mới trước khi chờ bàn giao cập nhật.");
        while (Volatile.Read(ref _inFlight) != 0) await Task.Delay(25, ct).ConfigureAwait(false);
    }

    private static bool MatchesCode(ScanRecordEntity record, string gtin) =>
        PoCodeParser.TryGetGtin(record.CodeContent, out var parsed) && parsed == gtin;

    private async Task<int> SendPendingCoreAsync(CancellationToken ct)
    {
        if (!SendingEnabled) return 0;
        if (!_opt.IsConfigured) { _state.SetStatus("Chưa cấu hình kết nối Checkee (BaseUrl / ApiKey / ClientId).", true); return 0; }
        var session=ObserveRunningOrder(); // before Flush can auto-complete final PO
        if (session is null) { _state.SetStatus("Chưa có PO đang chạy được nhận vào phiên gửi; không gửi lịch sử cũ.", false); return 0; }
        if (!PoCodeParser.TryNormalizeGtin(session.Gtin, out var gtin))
        { _state.SetStatus("PO thiếu GTIN hợp lệ; giữ nguyên các mã chờ gửi.", true); return 0; }
        await _scanWriter.FlushAsync(ct).ConfigureAwait(false);
        if (!SameOrder(session, ObserveRunningOrder()) || !SendingEnabled) return 0;
        var pending=await _sendStore.GetPendingForOrderAsync(session.OrderNo, session.CreatedUtc,
            gtin, _opt.BatchSize, ct).ConfigureAwait(false);
        var records=new List<ScanRecordEntity>();
        foreach (var record in pending)
        {
            if (record.Status == ScanStatuses.Pass && record.PoAccepted && record.OrderNo == session.OrderNo
                && record.OrderCreatedUtc == session.CreatedUtc && MatchesCode(record, gtin)) records.Add(record);
            else await _sendStore.MarkPendingAsync(record.Id, "Mã không cùng GTIN/identity PO; chưa gửi Checkee.", ct).ConfigureAwait(false);
        }
        if (records.Count == 0)
        {
            _state.SetStatus(pending.Count == 0 ? "Không có mã Pass cùng GTIN của phiên PO chờ gửi." : "Có mã sai GTIN/identity; giữ chờ đối soát.", pending.Count > 0);
            return 0;
        }
        if (!SameOrder(session, ObserveRunningOrder()) || !SendingEnabled) return 0;
        var batch=ResolveBatch(session);
        var attempt=await _sendStore.BeginSendAttemptAsync(records, gtin, batch, _opt.SerialType, ct).ConfigureAwait(false);
        CheckeeSendResult result;
        try
        {
            // Fail closed if durable file audit cannot be written before network dispatch.
            WriteAudit(attempt, "Started", session, gtin, batch, records, 0, "");
            if (!SendingEnabled || !SameOrder(session, ObserveRunningOrder()))
                result=new() { IsTransient=true, Message="Đã tắt gửi hoặc thay PO trước HTTP; giữ hàng đợi." };
            else result=await _client.ActivateAsync(gtin, batch, _opt.SerialType,
                records.Select(r => r.CodeContent).Distinct(StringComparer.Ordinal).ToArray(), ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            const string message="Lượt gửi bị hủy trước xác nhận; cần đối soát trước khi gửi lại.";
            await _sendStore.CompleteSendAttemptAsync(attempt, CheckeeSendAttemptStatuses.Unknown, 0, message, CancellationToken.None).ConfigureAwait(false);
            TryWriteAudit(attempt, CheckeeSendAttemptStatuses.Unknown, session, gtin, batch, records, 0, message);
            throw;
        }
        catch (Exception ex)
        {
            // Unexpected/audit errors are not proof the request was rejected.
            result=new() { IsUnknown=true, Message="Không xác nhận được lượt gửi: " + ex.GetType().Name };
        }
        var status=result.IsUnknown ? CheckeeSendAttemptStatuses.Unknown : result.Ok ? CheckeeSendAttemptStatuses.Sent
            : result.IsTransient ? CheckeeSendAttemptStatuses.RetryPending : CheckeeSendAttemptStatuses.Failed;
        var safeMessage=Redact(result.Message);
        // Write network result first: survives a subsequent SQLite failure.
        var audited=TryWriteAudit(attempt, status, session, gtin, batch, records, result.StatusCode, safeMessage, result.RawMessage);
        await _sendStore.CompleteSendAttemptAsync(attempt, status, result.StatusCode, safeMessage, CancellationToken.None).ConfigureAwait(false);
        _state.SetLastRunUtc(DateTime.UtcNow);
        _state.SetStatus(audited ? $"PO {session.OrderNo}: {records.Count} mã · {status}. {safeMessage}"
            : "Đã lưu SQLite nhưng không ghi được audit kết quả; cần kiểm tra ổ đĩa.", !result.Ok || !audited);
        return result.Ok ? records.Count : 0;
    }

    private static string ResolveBatch(ProductionOrder order)
    {
        foreach (var key in new[] { "lot", "lotNo", "batchCode", "batchNo" })
            foreach (var item in order.Additional)
                if (string.Equals(key, item.Key, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(item.Value)) return item.Value.Trim();
        return order.OrderNo;
    }

    private string Redact(string message, int maxLength = 1000)
    {
        foreach (var secret in new[] { _opt.ApiKey, _opt.ClientId })
            if (!string.IsNullOrEmpty(secret)) message=message.Replace(secret, "[REDACTED]", StringComparison.Ordinal);
        message=Regex.Replace(message, @"(?i)(Bearer\s+|accessToken[=:]\s*|(?:api[-_]?key|client[-_]?id)[\""']?\s*[:=]\s*[\""']?)[^\s,;\""'}]+", "$1[REDACTED]");
        return message.Length > maxLength ? message[..maxLength] : message;
    }

    private bool TryWriteAudit(long attempt, string status, ProductionOrder order, string gtin,
        string batch, IReadOnlyList<ScanRecordEntity> records, int http, string message, string response = "")
    {
        try { WriteAudit(attempt, status, order, gtin, batch, records, http, message, response); return true; }
        catch (Exception ex) { _logger.LogError("Không ghi được audit Checkee ({ErrorType}).", ex.GetType().Name); return false; }
    }

    private void WriteAudit(long attempt, string status, ProductionOrder order, string gtin,
        string batch, IReadOnlyList<ScanRecordEntity> records, int http, string message, string response = "")
    {
        var path=Path.GetFullPath(_outbound.AuditLogPath, AppContext.BaseDirectory);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var json=new MemoryStream();
        using (var writer=new Utf8JsonWriter(json))
        {
            writer.WriteStartObject(); writer.WriteString("utc", DateTime.UtcNow);
            writer.WriteNumber("attemptId", attempt); writer.WriteString("status", status);
            writer.WriteString("orderNo", order.OrderNo); writer.WriteString("orderCreatedUtc", order.CreatedUtc);
            writer.WriteString("gtin", gtin); writer.WriteString("lot", batch);
            writer.WriteNumber("httpStatus", http); writer.WriteString("message", Redact(message));
            writer.WriteString("response", Redact(response));
            writer.WriteStartArray("recordIds"); foreach(var record in records) writer.WriteNumberValue(record.Id); writer.WriteEndArray();
            writer.WriteStartArray("codes");
            foreach (var record in records)
            {
                writer.WriteStartObject(); writer.WriteNumber("recordId", record.Id);
                writer.WriteString("codeContent", Redact(record.CodeContent, int.MaxValue));
                writer.WriteString("serial", Redact(CheckeeClient.ExtractSerialNumber(record.CodeContent), int.MaxValue));
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject(); writer.Flush();
        }
        using var file=new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read, 4096, FileOptions.WriteThrough);
        file.Write(json.GetBuffer().AsSpan(0, (int)json.Length)); file.WriteByte(10); file.Flush(flushToDisk:true);
    }

    public override void Dispose()
    {
        _po.Changed -= OnPoChanged;
        base.Dispose();
    }
}

public sealed class CheckeeSendState
{
    public event Action? Changed;
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
        if (Status == message && IsError == isError) return;
        Volatile.Write(ref _status, message ?? string.Empty);
        Volatile.Write(ref _isError, isError);
        Changed?.Invoke();
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
