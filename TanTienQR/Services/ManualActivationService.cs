using System.Diagnostics;
using System.Threading.Channels;
using ClassLibraryMT.Po;
using ClassLibraryMT.Scans;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace TanTienQR.Services;

/// <summary>Bounded manual queue. Only CameraSignalWorker consumes it; no polling worker.</summary>
internal sealed class ManualActivationService
{
    private readonly ManualActivationHistoryStore _history;
    private readonly ScanPersistenceWorker _persistence;
    private readonly ProductionOrderService _po;
    private readonly ScanIntakeGate _intake;
    private readonly ManualActivationOptions _options;
    private readonly ILogger<ManualActivationService> _logger;
    private readonly Channel<Work> _queue;
    private readonly object _gate = new();
    private TaskCompletionSource _idle = CompletedSignal();
    private int _pending;
    private bool _stopping;

    public ManualActivationService(ManualActivationHistoryStore history, ScanPersistenceWorker persistence,
        ProductionOrderService po, ScanIntakeGate intake, IOptions<ManualActivationOptions> options,
        ILogger<ManualActivationService> logger)
    {
        _history = history; _persistence = persistence; _po = po; _intake = intake;
        _options = options.Value; _logger = logger;
        _queue = Channel.CreateBounded<Work>(new BoundedChannelOptions(Math.Max(1, _options.QueueCapacity))
        { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
    }

    public event Action<ManualActivationResult>? Completed;
    internal event Action? WorkAvailable;
    internal int PendingCount { get { lock (_gate) return _pending; } }
    internal Task DrainAsync(CancellationToken ct) { lock (_gate) return _idle.Task.WaitAsync(ct); }
    private static TaskCompletionSource CompletedSignal()
    {
        var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        signal.SetResult(); return signal;
    }

    public Task<IReadOnlyList<ManualActivationResult>> GetRecentAsync(int limit = 10, CancellationToken ct = default)
        => Task.Run(() => _history.GetRecentAsync(limit, ct), ct);

    public async Task<ManualActivationResult> SubmitAsync(string codeContent, string source, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var result = new ManualActivationResult { CodeContent = codeContent ?? "", Source = source ?? "" };
        // Record the attempt before reserving production persistence. From here, client cancellation
        // cannot abandon a reservation or an accepted RAM mutation (e.g. PDA disconnect/tab exit).
        try { await Task.Run(() => _history.SaveAsync(result, CancellationToken.None)).ConfigureAwait(false); }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Manual audit unavailable; code has not been accepted.");
            return Notify(result with { Status = "AuditError", Message = "Không ghi được nhật ký; chưa nhận mã.", AuditPersisted = false });
        }
        if (result.CodeContent.Length == 0 || result.CodeContent.Length > Math.Clamp(_options.MaxCodeLength, 1, 65536)
            || result.Source.Length > 128)
            return await FinishAsync(result with { Status = "InvalidStructure", Message = "Nội dung mã trống hoặc vượt giới hạn." }).ConfigureAwait(false);

        Work? work = null;
        var rejection = "Paused";
        _intake.Dispatch(() =>
        {
            lock (_gate)
            {
                if (_stopping) { rejection = "Stopped"; return; }
                if (_pending >= Math.Max(1, _options.QueueCapacity)) { rejection = "Overload"; return; }
                var reservation = _persistence.TryReserve();
                if (reservation is null) { rejection = "Overload"; return; }
                work = new Work(result, reservation);
                if (!_queue.Writer.TryWrite(work))
                {
                    // Stop/admission use this same lock; defensive publication still closes
                    // the pre-enqueued reservation before returning an overload outcome.
                    reservation.Publish(Record(result with { Status = "Overload" }, 0));
                    work = null; rejection = "Overload"; return;
                }
                if (_pending++ == 0) _idle = new(TaskCreationOptions.RunContinuationsAsynchronously);
            }
        });
        if (work is null)
        {
            lock (_gate) if (_stopping) rejection = "Stopped";
            return await FinishAsync(result with { Status = rejection, Message = Message(rejection) }).ConfigureAwait(false);
        }
        SignalWork();
        return await work.Completion.Task.ConfigureAwait(false);
    }

    private void SignalWork()
    {
        if (WorkAvailable is not { } available) return;
        foreach (Action observer in available.GetInvocationList())
            try { observer(); } catch (Exception ex) { _logger.LogError(ex, "Manual work wake observer failed."); }
    }

    internal void StopAccepting()
    {
        lock (_gate) { _stopping = true; _queue.Writer.TryComplete(); }
        SignalWork();
    }

    /// <summary>One item per turn so camera and manual intake share a fair event-driven clock.</summary>
    internal Task<bool> ProcessNextAsync(RuntimeCounters counters)
    {
        if (!_queue.Reader.TryRead(out var work)) return Task.FromResult(false);
        var result = work.Result;
        PoScanMutation? mutation = null;
        try
        {
            // Admission already synchronized with intake suspension. A reservation may
            // itself trigger the persistence pressure lease; do not reject its own work.
            // PO lifecycle/update gates are checked atomically by AcceptManualCode.
            if (_po.IsHalted || !_persistence.IsHealthy)
                result = result with { Status = "Halted", Message = Message("Halted") };
            else if (QrParser.IsNoRead(result.CodeContent) || !QrParser.HasGs1Structure(result.CodeContent)
                || !PoCodeParser.TryGetGtin(result.CodeContent, out _))
                result = result with { Status = "InvalidStructure", Message = Message("InvalidStructure") };
            else
            {
                var accepted = _po.AcceptManualCode(result.CodeContent, result.ReceivedUtc, work.Reservation.EventId);
                mutation = accepted.Mutation;
                var status = accepted.Validation switch
                {
                    PoCodeValidation.Valid => "Pass", PoCodeValidation.Duplicate => "Duplicate",
                    PoCodeValidation.InvalidForOrder => "InvalidForOrder", PoCodeValidation.Halted => "Halted",
                    _ => "NoProductionOrder",
                };
                result = result with { Status = status, Message = Message(status), OrderNo = accepted.OrderNo,
                    Success = accepted.Validation == PoCodeValidation.Valid };
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Manual validation failed for audit {Id}.", result.Id);
            result = result with { Status = "Error", Message = "Lỗi xử lý mã; xem nhật ký kỹ thuật." };
        }
        // Never await audit, completion observers, lifecycle mutex, or SQL before this publication.
        // The mutation identity is exactly the reservation identity, including on shutdown/pause.
        try
        {
            work.Reservation.Publish(Record(result, Stopwatch.GetElapsedTime(work.Started).TotalMilliseconds), mutation);
            counters.AddScan(result.Success);
            // Sqlite async APIs may execute synchronously while busy. Explicit dispatch
            // keeps even that synchronous prefix off the single camera consumer clock.
            // The bounded pending slot is retained until final audit/observers finish.
            _ = Task.Run(() => CompleteWorkAsync(work, result));
        }
        catch (Exception ex)
        {
            _logger.LogCritical(ex, "Manual reservation publication failed for {Id}.", result.Id);
            work.Completion.TrySetException(ex);
            ReleasePending();
        }
        return Task.FromResult(true);
    }

    private async Task CompleteWorkAsync(Work work, ManualActivationResult result)
    {
        try { work.Completion.TrySetResult(await FinishAsync(result).ConfigureAwait(false)); }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Manual completion failed for {Id}.", result.Id);
            work.Completion.TrySetException(ex);
        }
        finally { ReleasePending(); }
    }

    private void ReleasePending()
    {
        lock (_gate) if (--_pending == 0) _idle.TrySetResult();
    }

    private static ScanRecordEntity Record(ManualActivationResult result, double elapsed) => new()
    {
        ReceivedUtc = result.ReceivedUtc, CodeContent = result.CodeContent, Raw = result.CodeContent,
        Gtin = PoCodeParser.TryGetGtin(result.CodeContent, out var gtin) ? gtin : "",
        OrderNo = result.OrderNo, Status = result.Status is "Pass" or "Duplicate" or "InvalidForOrder" or "InvalidStructure"
            ? result.Status : result.Status == "Overload" ? ScanStatuses.Overload : ScanStatuses.Fail,
        BatchNo = ScanBatchDefaults.NoBatch, PlcResult = ScanPlcResults.Skipped, ProcessingTimeMs = elapsed,
    };

    private async Task<ManualActivationResult> FinishAsync(ManualActivationResult result)
    {
        try { await _history.SaveAsync(result, CancellationToken.None).ConfigureAwait(false); }
        catch (Exception ex)
        {
            // A successful RAM/outbox acceptance must never be reported as retryable failure.
            // Its durable Pending audit row remains available for reconciliation on restart.
            _logger.LogError(ex, "Manual audit finalization failed for {Id} (accepted={Accepted}).", result.Id, result.Success);
            result = result with { AuditPersisted = false, Message = result.Message + " Chưa cập nhật được nhật ký riêng." };
        }
        return Notify(result);
    }

    private ManualActivationResult Notify(ManualActivationResult result)
    {
        if (Completed is { } completed)
            foreach (Action<ManualActivationResult> observer in completed.GetInvocationList())
                try { observer(result); } catch (Exception ex) { _logger.LogError(ex, "Manual result observer failed."); }
        return result;
    }

    private static string Message(string status) => status switch
    {
        "Pass" => "Đã chấp nhận mã; đang lưu lịch sử và đồng bộ PO.",
        "Duplicate" => "Mã đã quét hoặc kích hoạt.", "InvalidStructure" => "Mã không đúng cấu trúc GS1/GTIN.",
        "InvalidForOrder" => "Mã hoặc GTIN không thuộc PO đang chạy.",
        "NoProductionOrder" => "Chưa có PO đang chạy.", "Halted" => "Sản xuất đang dừng hoặc chuyển trạng thái.",
        "Paused" => "Đang tạm dừng nhận mã.", "Stopped" => "Ứng dụng đang dừng nhận mã.",
        "Overload" => "Hàng đợi đầy hoặc lưu dữ liệu chưa sẵn sàng; chưa nhận mã.", _ => "Lỗi xử lý mã.",
    };

    private sealed record Work(ManualActivationResult Result, ScanPersistenceWorker.Reservation Reservation)
    {
        internal long Started { get; } = Stopwatch.GetTimestamp();
        internal TaskCompletionSource<ManualActivationResult> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
