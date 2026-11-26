using System.Threading.Channels;
using ClassLibraryMT.Po;
using ClassLibraryMT.Scans;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace TanTienQR.Services;

/// <summary>
/// The application's single scan persistence clock. A reservation is queued before
/// RAM acceptance/PLC; a FIFO barrier also waits for reservations not yet published.
/// History and outbox commit together, then PO files are updated idempotently, then
/// records become eligible for Checkee. The two SQLite files are not one transaction.
/// </summary>
internal sealed class ScanPersistenceWorker : IScanRecordWriter, IHostedService, IDisposable
{
    private readonly IScanPersistenceStore _store;
    private readonly PoStore _poStore;
    private readonly ProductionOrderService _po;
    private readonly ScanIntakeGate _intake;
    private readonly ILogger<ScanPersistenceWorker> _logger;
    private readonly ScanStoreOptions _options;
    private readonly Channel<object> _queue = Channel.CreateUnbounded<object>(new() { SingleReader = true });
    private readonly SemaphoreSlim _slots;
    private readonly CancellationTokenSource _abort = new();
    private readonly CancellationToken _workerToken;
    private Task? _worker;
    private Exception? _failure;
    private TaskCompletionSource<Exception> _faultSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private IDisposable? _faultLease;
    private IDisposable? _pressureLease;
    private readonly object _state = new();
    private int _pending;
    private bool _stopped;
    private int _disposed;

    public ScanPersistenceWorker(IScanPersistenceStore store, PoStore poStore,
        ProductionOrderService po, ScanIntakeGate intake, IOptions<ScanStoreOptions> options,
        ILogger<ScanPersistenceWorker> logger)
    {
        _store = store; _poStore = poStore; _po = po; _intake = intake;
        _options = options.Value; _logger = logger;
        _workerToken = _abort.Token;
        _slots = new SemaphoreSlim(Math.Max(1, _options.QueueCapacity));
        _po.PersistenceBarrier = FlushAsync;
    }

    internal bool IsHealthy => Volatile.Read(ref _disposed) == 0 && Volatile.Read(ref _failure) is null && !_stopped && _worker is { IsCompleted: false };
    internal int PendingCount => Volatile.Read(ref _pending);
    private int PressureThreshold => Math.Min(Math.Max(1, _options.QueueCapacity / 20), Math.Max(0, _options.QueueCapacity - 1));

    internal Reservation? TryReserve()
    {
        Reservation reservation;
        lock (_state)
        {
            if (!IsHealthy || !_slots.Wait(0)) return null;
            reservation = new Reservation();
            Interlocked.Increment(ref _pending);
            if (!_queue.Writer.TryWrite(reservation))
            {
                Interlocked.Decrement(ref _pending); _slots.Release(); return null;
            }
        }
        // Never acquire intake's lock while holding _state: camera Dispatch
        // takes intake first. Preserve that lock order on worker threads too.
        if (_slots.CurrentCount <= PressureThreshold)
        {
            var lease = _intake.Suspend();
            if (Interlocked.CompareExchange(ref _pressureLease, lease, null) is not null) lease.Dispose();
            ReleasePressureIfRecovered();
        }
        return reservation;
    }

    private void ReleasePressureIfRecovered()
    {
        if (_slots.CurrentCount > PressureThreshold)
            Interlocked.Exchange(ref _pressureLease, null)?.Dispose();
    }

    internal sealed class Reservation
    {
        internal readonly TaskCompletionSource<ScanPersistenceEnvelope> Completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal string EventId { get; } = Guid.NewGuid().ToString("N");
        internal void Publish(ScanRecordEntity record, PoScanMutation? mutation = null)
        {
            // Copy the mutable transport model at the publication boundary.
            // Neither caller/UI mutation nor later PO switching can alter this event.
            var snapshot = new ScanRecordEntity
            {
                ReceivedUtc = record.ReceivedUtc, CodeContent = record.CodeContent,
                Gtin = record.Gtin, Serial = record.Serial, Status = record.Status,
                ProcessingTimeMs = record.ProcessingTimeMs, PlcAckTimeMs = record.PlcAckTimeMs,
                PlcResult = record.PlcResult, BatchNo = record.BatchNo, Raw = record.Raw,
                OrderNo = mutation?.OrderNo ?? record.OrderNo,
            };
            if (!Completion.TrySetResult(new(EventId, snapshot, mutation?.OrderCreatedUtc, mutation is not null)))
                throw new InvalidOperationException("Scan reservation was already published.");
        }
        internal bool IsPublished => Completion.Task.IsCompleted;
    }

    // Non-camera legacy producers retain the durable ACK contract.
    public void Enqueue(ScanRecordEntity record)
    {
        var reservation = TryReserve() ?? throw new IOException("Scan persistence is unavailable or full.");
        reservation.Publish(record);
        FlushAsync().GetAwaiter().GetResult();
    }

    public async Task FlushAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        var barrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<Exception> fault;
        Task? worker;
        lock (_state)
        {
            if (_failure is { } failure) throw new IOException("Scan persistence failed; intake is stopped.", failure);
            if (_stopped) return; // StopAsync already verified its final barrier.
            if (_worker is null)
            {
                if (PendingCount == 0) return; // No producers exist before hosted startup.
                throw new IOException("Scan persistence has not started.");
            }
            fault = _faultSignal.Task;
            worker = _worker;
            if (!_queue.Writer.TryWrite(barrier)) throw new IOException("Scan persistence queue is closed.");
        }
        // A fault can happen after admission but before the worker reaches this
        // barrier. Never strand its waiter behind a failed/retrying database.
        var completed = await Task.WhenAny(barrier.Task, fault, worker ?? Task.Delay(Timeout.Infinite, cancellationToken))
            .WaitAsync(cancellationToken).ConfigureAwait(false);
        if (completed == fault) throw new IOException("Scan persistence failed; batch retained for retry.", await fault.ConfigureAwait(false));
        if (completed == worker && !barrier.Task.IsCompleted)
        {
            await worker!.ConfigureAwait(false);
            throw new IOException("Scan persistence worker stopped before its barrier.");
        }
        await barrier.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task VerifyHealthyAsync(CancellationToken cancellationToken = default)
    {
        await FlushAsync(cancellationToken).ConfigureAwait(false);
        await _store.VerifyHealthyAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        // Hosted services start in registration order: replay -> restore -> camera/Ready.
        using var starting = _intake.Suspend();
        try
        {
            await _store.VerifyHealthyAsync(cancellationToken).ConfigureAwait(false);
            await ApplyPendingAsync(cancellationToken).ConfigureAwait(false);
            await _po.RestoreRunningOrderAsync(cancellationToken).ConfigureAwait(false);
            _worker = Task.Run(RunAsync);
        }
        catch (Exception ex) { Fault(ex); throw; }
    }

    private async Task ApplyPendingAsync(CancellationToken ct)
    {
        var pending = await _store.GetPendingAsync(ct).ConfigureAwait(false);
        if (pending.Count == 0) return;
        var mutations = pending.Select(e => new PoScanMutation(e.EventId, e.Record.OrderNo,
            e.OrderCreatedUtc ?? throw new IOException("Outbox PO identity is missing."),
            e.Record.CodeContent, e.Record.ReceivedUtc)).ToArray();
        await _poStore.MarkScannedBatchAsync(mutations, ct).ConfigureAwait(false);
        await _store.CompleteAsync(pending.Select(e => e.EventId).ToArray(), ct).ConfigureAwait(false);
    }

    private async Task RunAsync()
    {
        var batch = new List<ScanPersistenceEnvelope>();
        var reservations = new List<Reservation>();
        TaskCompletionSource? activeBarrier = null;
        var lastFlush = Environment.TickCount64;
        var lastPrune = lastFlush;
        try
        {
            while (await WaitForWorkAsync(_workerToken).ConfigureAwait(false))
            {
                while (_queue.Reader.TryRead(out var item))
                {
                    if (item is Reservation reservation)
                    {
                        batch.Add(await reservation.Completion.Task.WaitAsync(_workerToken).ConfigureAwait(false));
                        reservations.Add(reservation);
                    }
                    else if (item is TaskCompletionSource barrier)
                    {
                        activeBarrier = barrier;
                        await PersistAsync(batch, reservations, _workerToken).ConfigureAwait(false);
                        barrier.TrySetResult(); activeBarrier = null;
                    }
                    if (batch.Count >= Math.Max(1, _options.BatchSize))
                        await PersistAsync(batch, reservations, _workerToken).ConfigureAwait(false);
                }
                if (Environment.TickCount64 - lastFlush >= Math.Max(1, _options.FlushIntervalMs))
                {
                    await PersistAsync(batch, reservations, _workerToken).ConfigureAwait(false);
                    lastFlush = Environment.TickCount64;
                    try { await _po.CompletePendingAsync(_workerToken).ConfigureAwait(false); }
                    catch (OperationCanceledException) when (_workerToken.IsCancellationRequested) { throw; }
                    catch (Exception ex)
                    {
                        // PO completion persists its halt marker. Preserve the
                        // writer so outcomes already reserved during completion
                        // still reach history, while intake remains stopped.
                        Fault(ex);
                    }
                }
                if (_options.RetentionDays > 0 && Environment.TickCount64 - lastPrune >= _options.PruneIntervalMs)
                {
                    try
                    {
                        await _store.PruneAsync(DateTime.UtcNow.AddDays(-_options.RetentionDays), _workerToken).ConfigureAwait(false);
                        lastPrune = Environment.TickCount64;
                    }
                    catch (OperationCanceledException) when (_workerToken.IsCancellationRequested) { throw; }
                    catch (Exception ex)
                    {
                        // Housekeeping must never terminate the scan writer or
                        // discard a reserved Pass admitted while prune awaited I/O.
                        // Retain detail and retry later; normal inserts still use
                        // their separate fail-closed, batch-preserving retry path.
                        lastPrune = Environment.TickCount64 + 500;
                        _logger.LogWarning(ex, "Không dọn được lịch sử hết hạn; giữ dữ liệu và thử lại sau.");
                    }
                }
                if (_queue.Reader.Completion.IsCompleted) break;
            }
            await PersistAsync(batch, reservations, _workerToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Fault(ex);
            _queue.Writer.TryComplete(ex);
            activeBarrier?.TrySetException(ex);
            while (_queue.Reader.TryRead(out var item))
                if (item is TaskCompletionSource barrier) barrier.TrySetException(ex);
            throw;
        }
    }

    private async Task<bool> WaitForWorkAsync(CancellationToken ct)
    {
        using var tick = CancellationTokenSource.CreateLinkedTokenSource(ct);
        tick.CancelAfter(Math.Max(1, _options.FlushIntervalMs));
        try { return await _queue.Reader.WaitToReadAsync(tick.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return true; }
    }

    private async Task PersistAsync(List<ScanPersistenceEnvelope> batch, List<Reservation> reservations, CancellationToken ct)
    {
        if (batch.Count == 0) return;
        // Keep this exact batch until both stages succeed. A transient failure
        // stops intake and wakes barrier callers, while retry preserves RAM tail.
        while (true)
        {
            try
            {
                await _store.PersistBatchAsync(batch, ct).ConfigureAwait(false);
                await ApplyPendingAsync(ct).ConfigureAwait(false);
                Recover();
                break;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                Fault(ex);
                await Task.Delay(500, ct).ConfigureAwait(false);
            }
        }
        var count = reservations.Count;
        batch.Clear(); reservations.Clear();
        Interlocked.Add(ref _pending, -count); _slots.Release(count);
        ReleasePressureIfRecovered();
    }

    private void Fault(Exception ex)
    {
        var first = false;
        lock (_state)
        {
            if (_failure is null) { _failure = ex; first = true; }
            _faultSignal.TrySetResult(ex);
        }
        var lease = _intake.Suspend();
        if (Interlocked.CompareExchange(ref _faultLease, lease, null) is not null) lease.Dispose();
        if (first) _logger.LogCritical(ex, "DỪNG NHẬN MÃ: ghi PO/lịch sử scan thất bại; giữ batch và thử lại.");
    }

    private void Recover()
    {
        lock (_state)
        {
            if (_failure is null) return;
            if (_po.IsHalted) return; // A lifecycle halt requires reconciliation.
            _failure = null;
            _faultSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        Interlocked.Exchange(ref _faultLease, null)?.Dispose();
        _logger.LogInformation("PO/lịch sử scan đã đồng bộ sau retry; mở lại intake khi các gate khác cho phép.");
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        using var stopping = _intake.Suspend();
        await FlushAsync(cancellationToken).ConfigureAwait(false);
        _queue.Writer.TryComplete();
        if (_worker is not null) await _worker.WaitAsync(cancellationToken).ConfigureAwait(false);
        await _po.CompletePendingAsync(cancellationToken).ConfigureAwait(false);
        _stopped = true;
    }

    public void Dispose()
    {
        // The same singleton is registered as hosted service and writer alias;
        // the DI container may dispose more than one registration.
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _abort.Cancel();
        _abort.Dispose();
    }
}
