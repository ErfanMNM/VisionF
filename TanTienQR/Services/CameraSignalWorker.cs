using System.Diagnostics;
using System.Threading.Channels;
using ClassLibraryMT.Communications.CodeReaderProtocol;
using ClassLibraryMT.DeviceStatus;
using ClassLibraryMT.Po;
using ClassLibraryMT.Scans;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TanTienQR.Models;

namespace TanTienQR.Services;

/// <summary>One PLC result at a time; reserve history capacity before RAM acceptance.
/// Camera-to-PLC never opens SQLite. Accepted codes remain consumed on PLC timeout.
/// Durable history/outbox/PO synchronization belongs to ScanPersistenceWorker.</summary>
internal sealed class CameraSignalWorker : BackgroundService
{
    private readonly Reader _camera;
    private readonly PlcWriter _plc;
    private readonly ScanFeed _feed;
    private readonly IScanRecordWriter _scanWriter;
    private readonly ScanPersistenceWorker? _persistence;
    private readonly CameraOptions _opt;
    private readonly RuntimeCounters _counters;
    private readonly ProductionOrderService _po;
    private readonly ScanIntakeGate _intake;
    private readonly ILogger<CameraSignalWorker> _logger;
    private readonly Channel<ScanRequest> _scanQueue = Channel.CreateBounded<ScanRequest>(
        new BoundedChannelOptions(1) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
    private int _inFlight;

    public CameraSignalWorker(Reader camera, PlcWriter plc, ScanFeed feed, IScanRecordWriter scanWriter,
        IOptions<CameraOptions> options, RuntimeCounters counters, ProductionOrderService po,
        ScanIntakeGate intake, ILogger<CameraSignalWorker> logger)
    {
        _camera = camera; _plc = plc; _feed = feed; _scanWriter = scanWriter;
        _persistence = scanWriter as ScanPersistenceWorker;
        _opt = options.Value; _counters = counters; _po = po; _intake = intake; _logger = logger;
    }

    internal async Task DrainAsync(CancellationToken ct)
    {
        while (Volatile.Read(ref _inFlight) != 0) await Task.Delay(10, ct).ConfigureAwait(false);
        if (_scanWriter is not null) await _scanWriter.FlushAsync(ct).ConfigureAwait(false);
    }

    internal async Task ApplyConfigurationAsync(CameraConfig target, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (!_intake.IsSuspended) throw new InvalidOperationException("Cần tạm dừng nhận mã trước khi đổi cấu hình camera.");
        await DrainAsync(ct).ConfigureAwait(false);
        await _camera.ReconfigureAsync(target.Ip, target.Port, ct).ConfigureAwait(false);
        _opt.Ip = target.Ip; _opt.Port = target.Port;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _camera.ClientCallback += OnCameraEvent;
        var queueTask = ProcessQueueAsync(CancellationToken.None);
        try { await _camera.RunAsync(stoppingToken).ConfigureAwait(false); }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally { _camera.ClientCallback -= OnCameraEvent; _scanQueue.Writer.TryComplete(); }
        await queueTask.ConfigureAwait(false);
    }

    private async Task ProcessQueueAsync(CancellationToken ct)
    {
        await foreach (var request in _scanQueue.Reader.ReadAllAsync(ct))
        {
            try { await HandleAsync(request, ct).ConfigureAwait(false); }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi xử lý mã {CodeContent}", request.Raw);
                if (request.Reservation?.IsPublished != true)
                {
                    _counters.AddScan(false);
                    Publish(request, ScanOutcome.Halted, false, MeasureProcessingMs(request), plcSkipped: true);
                }
            }
            finally { Interlocked.Decrement(ref _inFlight); }
        }
    }

    private async Task HandleAsync(ScanRequest request, CancellationToken ct)
    {
        var raw = request.Raw;
        if (GlobalVariables.PLCStatus != PLCStatus.Connected)
        {
            _counters.AddScan(false);
            Publish(request, ScanOutcome.PlcError, false, MeasureProcessingMs(request), plcSkipped: true); return;
        }
        if (_po.IsHalted || _persistence?.IsHealthy == false)
        {
            _counters.AddScan(false);
            Publish(request, ScanOutcome.Halted, false, MeasureProcessingMs(request), plcSkipped: true); return;
        }
        if (QrParser.IsNoRead(raw) || !QrParser.HasGs1Structure(raw))
        {
            var noRead = QrParser.IsNoRead(raw);
            var processing = MeasureProcessingMs(request);
            var sendFail = noRead || _po.IsProductionRunning;
            var written = sendFail && await _plc.WriteResultAsync(false, ct).ConfigureAwait(false);
            var ackMs = sendFail ? MeasureProcessingMs(request) : 0;
            _counters.AddScan(false);
            Publish(request, noRead ? ScanOutcome.NoRead : ScanOutcome.InvalidStructure, written, processing, plcSkipped: !sendFail, plcAckMs: ackMs);
            return;
        }
        var processingMs = MeasureProcessingMs(request);
        var passAckMs = 0.0;
        var result = await _po.ProcessCodeAsync(raw, async token =>
        {
            processingMs = MeasureProcessingMs(request);
            try { return await _plc.WriteResultAsync(true, token).ConfigureAwait(false); }
            finally { passAckMs = MeasureProcessingMs(request); }
        }, ct, request.ReceivedUtc, request.Reservation?.EventId).ConfigureAwait(false);
        if (result.Validation == PoCodeValidation.Valid)
        {
            _counters.AddScan(true);
            Publish(request, ScanOutcome.Pass, result.PlcWritten == true, processingMs, result.Mutation, result.OrderNo, plcAckMs: passAckMs);
            if (result.PlcWritten != true) _logger.LogWarning("Pass đã tiêu thụ trong RAM, PLC chưa ACK: {CodeContent}", raw);
            return;
        }
        var outcome = result.Validation switch
        {
            PoCodeValidation.Duplicate => ScanOutcome.Duplicate,
            PoCodeValidation.InvalidForOrder => ScanOutcome.InvalidForOrder,
            PoCodeValidation.Halted => ScanOutcome.Halted,
            _ => ScanOutcome.NoProductionOrder,
        };
        processingMs = MeasureProcessingMs(request);
        var sendsFail = outcome is ScanOutcome.Duplicate or ScanOutcome.InvalidForOrder;
        var plcWritten = sendsFail && await _plc.WriteResultAsync(false, ct).ConfigureAwait(false);
        var failAckMs = sendsFail ? MeasureProcessingMs(request) : 0;
        _counters.AddScan(false);
        Publish(request, outcome, plcWritten, processingMs, orderNo: result.OrderNo, plcSkipped: !sendsFail, plcAckMs: failAckMs);
    }

    private static double MeasureProcessingMs(ScanRequest request) =>
        Math.Max(0, Stopwatch.GetTimestamp() - request.ReceivedTimestamp) * 1000.0 / Stopwatch.Frequency;

    private void Publish(ScanRequest request, ScanOutcome outcome, bool plcWritten, double processingMs,
        PoScanMutation? mutation = null, string? orderNo = null, bool plcSkipped = false, double plcAckMs = 0)
    {
        var record = new ScanRecordEntity
        {
            ReceivedUtc = request.ReceivedUtc, CodeContent = request.Raw, Raw = request.Raw,
            OrderNo = orderNo ?? request.OrderNo, BatchNo = ScanBatchDefaults.NoBatch,
            ProcessingTimeMs = processingMs, PlcAckTimeMs = plcAckMs,
            Status = outcome switch
            {
                ScanOutcome.Pass => ScanStatuses.Pass, ScanOutcome.Overload => ScanStatuses.Overload,
                ScanOutcome.Duplicate => ScanStatuses.Duplicate, ScanOutcome.InvalidForOrder => ScanStatuses.InvalidForOrder,
                ScanOutcome.NoRead => ScanStatuses.NoRead, ScanOutcome.InvalidStructure => ScanStatuses.InvalidStructure,
                _ => ScanStatuses.Fail,
            },
            PlcResult = plcWritten ? ScanPlcResults.Ok : plcSkipped ? ScanPlcResults.Skipped : ScanPlcResults.Failed,
        };
        // Publish the immutable event even when a UI subscriber fails.
        if (request.Reservation is { } reservation) reservation.Publish(record, mutation);
        else _scanWriter.Enqueue(record); // compatibility for explicit test/legacy writers
        _feed.Publish(new ScanRecord(request.Raw, outcome, request.ReceivedUtc, plcWritten, processingMs, plcSkipped));
    }

    private void OnCameraEvent(eReaderState state, string data)
    {
        switch (state)
        {
            case eReaderState.Connected: AppStateManager.SetCameraStatus(CameraStatus.Connected); break;
            case eReaderState.Disconnected: AppStateManager.SetCameraStatus(CameraStatus.Disconnected); break;
            case eReaderState.Reconnecting: AppStateManager.SetCameraStatus(CameraStatus.Reconnecting); break;
            case eReaderState.Received: _intake.Dispatch(() => HandleReceived(data)); break;
        }
    }

    private void HandleReceived(string data)
    {
        var receivedUtc = DateTime.UtcNow;
        var receivedTimestamp = Stopwatch.GetTimestamp();
        var reservation = _persistence?.TryReserve();
        if (_persistence is not null && reservation is null)
        {
            _logger.LogCritical("Từ chối intake: persistence fault/đầy, chưa tiêu thụ mã hay gửi PLC.");
            return;
        }
        var request = new ScanRequest(data, receivedUtc, receivedTimestamp, _po.CurrentOrder?.OrderNo ?? "", reservation);
        if (GlobalVariables.PLCStatus != PLCStatus.Connected)
        {
            _counters.AddScan(false);
            Publish(request, ScanOutcome.PlcError, false, MeasureProcessingMs(request), plcSkipped: true); return;
        }
        if (Interlocked.Increment(ref _inFlight) > 1)
        {
            Interlocked.Decrement(ref _inFlight); _counters.AddScan(false);
            Publish(request, ScanOutcome.Overload, false, MeasureProcessingMs(request), plcSkipped: true); return;
        }
        if (!_scanQueue.Writer.TryWrite(request))
        {
            Interlocked.Decrement(ref _inFlight); _counters.AddScan(false);
            Publish(request, ScanOutcome.Overload, false, MeasureProcessingMs(request), plcSkipped: true);
        }
    }
}

internal readonly record struct ScanRequest(string Raw, DateTime ReceivedUtc, long ReceivedTimestamp,
    string OrderNo = "", ScanPersistenceWorker.Reservation? Reservation = null);
