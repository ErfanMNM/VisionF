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

/// <summary>
/// Nhận nguyên CodeContent từ camera, đối chiếu PO và gửi kết quả xuống PLC.
///
/// Không phân tích GTIN/serial: mã tồn tại và chưa kích hoạt trong PO được Pass;
/// mã trùng, không tồn tại hoặc NoRead/FAIL được Fail (D12 = 0).
///
/// Ghi PLC được thực hiện trong <see cref="ProcessQueueAsync"/> (luồng riêng qua
/// <see cref="Channel{T}"/>) chứ không phải trong callback của camera — vì ghi
/// PLC là I/O đồng bộ có timeout, làm trong callback sẽ chặn socket loop và
/// làm mất frame.
///
/// <para>
/// <b>Ưu tiên mã đang xử lý:</b> hệ thống chỉ xử lý <b>một mã tại một thời
/// điểm</b>. Nếu camera gửi mã mới trong lúc mã cũ đang được xử lý thì mã mới
/// bị từ chối ngay với trạng thái <see cref="ScanOutcome.Overload"/> và
/// <b>không</b> ghi xuống PLC (xem <see cref="OnCameraEvent"/>).
/// </para>
///
/// <para>
/// <b>Lưu lịch sử:</b> mọi mã (kể cả mã bị từ chối) đều được ghi vào SQLite
/// qua <see cref="IScanRecordWriter"/> — hàng đợi bất đồng bộ nên không ảnh
/// hưởng tới luồng xử lý.
/// </para>
///
/// <para>
/// <b>Gate Production Order:</b> trước khi quyết định Pass/Fail, mỗi mã
/// được kiểm tra với PO đang chạy qua <see cref="ProductionOrderService"/>:
/// chưa bắt đầu sản xuất → loại mã (không ghi PLC); mã ngoài PO hoặc trùng
/// PO → Fail (gửi D12 = 0); mã hợp lệ → Pass và được đánh dấu đã quét.
/// </para>
/// </summary>
internal sealed class CameraSignalWorker : BackgroundService
{
    /// <summary>
    /// Sức chứa tối đa của hàng đợi mã quét.
    ///
    /// Hàng đợi có giới hạn để không rò bộ nhớ: nếu camera đẩy mã nhanh hơn
    /// lúc worker xử lý (mỗi mã lại ghi PLC — I/O đồng bộ có timeout), hàng
    /// đợi unbounded sẽ phình vô hạn và cuối cùng làm app bị OOM.
    ///
    /// Khi đầy thì bỏ mã cũ nhất (<c>DropOldest</c>) — chấp nhận được vì UI
    /// cũng chỉ giữ 200 dòng gần nhất.
    /// </summary>
    private const int CameraQueueCapacity = 512;

    private readonly Reader _camera;
    private readonly PlcWriter _plc;
    private readonly ScanFeed _feed;
    private readonly IScanRecordWriter _scanWriter;
    private readonly CameraOptions _opt;
    private readonly RuntimeCounters _counters; 
    private readonly ProductionOrderService _po;
    private readonly ILogger<CameraSignalWorker> _logger;

    // Hàng đợi để callback của camera không bị chặn bởi I/O.
    private readonly Channel<ScanRequest> _scanQueue = Channel.CreateBounded<ScanRequest>(
        new BoundedChannelOptions(CameraQueueCapacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
        });

    // Số mã đã nhận nhưng chưa xử lý xong (đang chờ trong hàng đợi + đang xử lý).
    //
    // Dùng Interlocked vì được đọc/ghi từ 2 luồng: luồng callback của camera
    // (kiểm tra) và luồng worker (giảm). Khi giá trị > 1 nghĩa là đã có mã
    // đang được xử lý -> mã mới bị từ chối ngay (ưu tiên mã đang xử lý).
    //
    // Vì chỉ nhận 1 mã tại một thời điểm nên hàng đợi luôn có tối đa 1 phần
    // tử chờ -> không bao giờ chạm giới hạn 512, không lo mất mã do
    // DropOldest.
    private int _inFlight;
    private IDisposable? _scanFaultSuspension;
    private readonly ScanIntakeGate _intake;

    public CameraSignalWorker(
        Reader camera,
        PlcWriter plc,
        ScanFeed feed,
        IScanRecordWriter scanWriter,
        IOptions<CameraOptions> options,
        RuntimeCounters counters,
        ProductionOrderService po,
        ScanIntakeGate intake,
        ILogger<CameraSignalWorker> logger)
    {
        _camera = camera;
        _plc = plc;
        _feed = feed;
        _scanWriter = scanWriter;
        _opt = options.Value;
        _counters = counters;
        _po = po;
        _intake = intake;
        _logger = logger;
    }

    internal async Task DrainAsync(CancellationToken ct)
    {
        while (Volatile.Read(ref _inFlight) != 0)
            await Task.Delay(25, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Áp dụng camera trong phiên hiện tại. Coordinator phải chặn nhận mã mới
    /// trước khi gọi; worker/queue được giữ nguyên.
    /// </summary>
    internal async Task ApplyConfigurationAsync(CameraConfig target, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (!_intake.IsSuspended)
            throw new InvalidOperationException("Cần tạm dừng nhận mã trước khi đổi cấu hình camera.");

        await DrainAsync(ct).ConfigureAwait(false);
        await _camera.ReconfigureAsync(target.Ip, target.Port, ct).ConfigureAwait(false);

        // Giữ cùng options instance cho các consumer như camera web/PLC settings.
        _opt.Ip = target.Ip;
        _opt.Port = target.Port;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _camera.ClientCallback += OnCameraEvent;
        try
        {
            await Task.WhenAll(_camera.RunAsync(stoppingToken), ProcessQueueAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutdown bình thường.
        }
        finally
        {
            _camera.ClientCallback -= OnCameraEvent;
            _scanQueue.Writer.TryComplete();
            _logger.LogInformation("Camera signal worker đã dừng.");
        }
    }

    private async Task ProcessQueueAsync(CancellationToken ct)
    {
        await foreach (var request in _scanQueue.Reader.ReadAllAsync(ct))
        {
            try
            {
                await HandleAsync(request, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi xử lý mã quét: {Data}", request.Raw);
            }
            finally
            {
                // Giảm bộ đếm "đang xử lý" — mã tiếp theo được nhận bình thường.
                Interlocked.Decrement(ref _inFlight);
            }
        }
    }

    private async Task HandleAsync(ScanRequest request, CancellationToken ct)
    {
        var raw = request.Raw;
        var orderNo = _po.CurrentOrder?.OrderNo ?? string.Empty;
        if (_po.IsHalted)
        {
            _counters.AddScan(pass: false);
            Publish(raw, ScanOutcome.Halted, request.ReceivedUtc, false, MeasureProcessingMs(request), orderNo);
            return;
        }

        if (QrParser.IsNoRead(raw))
        {
            var processingMs = MeasureProcessingMs(request);
            var written = await _plc.WriteResultAsync(pass: false, ct);
            _counters.AddScan(pass: false);
            _logger.LogWarning("Camera không đọc được mã, gửi Fail xuống PLC: CodeContent={CodeContent}", raw);
            Publish(raw, ScanOutcome.NoRead, request.ReceivedUtc, written, processingMs, orderNo);
            return;
        }

        var passAttempted = false;
        var passWritten = false;
        var passProcessingMs = 0.0;
        PoCodeValidation validation;
        try
        {
            // Backend giữ khóa vòng đời PO xuyên suốt validate → PLC ACK → commit DB.
            // PLC từ chối chắc chắn (false) không tiêu thụ mã hay tăng số đếm PO.
            // Pass mất ACK hoặc phản hồi không xác định phải throw để giữ quarantine.
            var result = await _po.ProcessCodeAsync(raw, async token =>
            {
                orderNo = _po.CurrentOrder?.OrderNo ?? orderNo;
                passProcessingMs = MeasureProcessingMs(request);
                passAttempted = true;
                passWritten = await _plc.WriteResultAsync(pass: true, token).ConfigureAwait(false);
                return passWritten;
            }, ct).ConfigureAwait(false);
            validation = result.Validation;
            orderNo = result.OrderNo;
            if (validation == PoCodeValidation.Valid)
            {
                var accepted = result.PlcWritten == true && result.Committed;
                _counters.AddScan(pass: accepted);
                _logger.LogInformation("CodeContent={CodeContent}: PLC Pass={PlcWritten}, PO committed={Committed}",
                    raw, result.PlcWritten, result.Committed);
                if (result.ProductionHalted)
                {
                    // This Pass is already committed. Keep that outcome truthful,
                    // but stop admission while the separate completion fault is reconciled.
                    _scanFaultSuspension ??= _intake.Suspend();
                    _logger.LogCritical(
                        "DỪNG NHẬN MÃ: Pass đã ghi PLC và commit PO nhưng hoàn tất PO gặp lỗi. PO={OrderNo}, CodeContent={CodeContent}. {Message}",
                        orderNo, raw, result.Message);
                    try { await _plc.WriteReadyAsync(0, CancellationToken.None).ConfigureAwait(false); }
                    catch (Exception readyError) { _logger.LogError(readyError, "Không ghi được Ready=0 sau lỗi hoàn tất PO."); }
                }
                Publish(raw, accepted ? ScanOutcome.Pass : ScanOutcome.PlcError,
                    request.ReceivedUtc, result.PlcWritten == true, passProcessingMs, orderNo);
                return;
            }
        }
        catch (Exception ex) when (passAttempted || _po.IsHalted)
        {
            // PLC có thể đã nhận Pass nhưng trạng thái bền vững không xác nhận được.
            // Giữ lease đến hết tiến trình: không tự mở lại intake bằng hotapply/resume.
            _scanFaultSuspension ??= _intake.Suspend();
            _logger.LogCritical(ex,
                "DỪNG NHẬN MÃ: không xác nhận được commit sau PLC Pass. PO={OrderNo}, CodeContent={CodeContent}. Cần đối soát PLC và DB trước khi chạy lại.",
                orderNo, raw);
            _counters.AddScan(pass: false);
            Publish(raw, ScanOutcome.Halted, request.ReceivedUtc, passWritten,
                passProcessingMs > 0 ? passProcessingMs : MeasureProcessingMs(request), orderNo);
            try { await _plc.WriteReadyAsync(0, ct).ConfigureAwait(false); }
            catch (Exception readyError) { _logger.LogError(readyError, "Không ghi được Ready=0 sau lỗi commit mã."); }
            return;
        }

        var outcome = validation switch
        {
            PoCodeValidation.Duplicate => ScanOutcome.Duplicate,
            PoCodeValidation.InvalidForOrder => ScanOutcome.InvalidForOrder,
            PoCodeValidation.Halted => ScanOutcome.Halted,
            _ => ScanOutcome.NoProductionOrder,
        };
        var processing = MeasureProcessingMs(request);
        var writesFailure = outcome is ScanOutcome.Duplicate or ScanOutcome.InvalidForOrder;
        var plcWritten = writesFailure && await _plc.WriteResultAsync(pass: false, ct).ConfigureAwait(false);
        _counters.AddScan(pass: false);
        _logger.LogWarning("CodeContent={CodeContent}: {Outcome}; PLC={PlcWritten}", raw, outcome, plcWritten);
        Publish(raw, outcome, request.ReceivedUtc, plcWritten, processing, orderNo);
    }

    /// <summary>
    /// Đo thời gian xử lý của một mã: từ lúc camera nhận frame đến trước khi
    /// gửi xuống PLC.
    ///
    /// Phải gọi <b>trước</b> lệnh ghi PLC, nếu gọi sau thì thời gian chờ
    /// timeout của PLC sẽ bị tính nhầm vào "thời gian xử lý".
    /// </summary>
    /// <param name="request">Yêu cầu quét, chứa mốc nhận từ camera.</param>
    /// <returns>Thời gian xử lý (ms) — số thực, giữ được phần thập phân.</returns>
    private static double MeasureProcessingMs(ScanRequest request)
    {
        var elapsedTicks = Stopwatch.GetTimestamp() - request.ReceivedTimestamp;
        if (elapsedTicks <= 0)
        {
            return 0;
        }

        // Stopwatch.Frequency là số tick trên 1 giây (thường là 10_000_000).
        return elapsedTicks * 1000.0 / Stopwatch.Frequency;
    }

    /// <summary>
    /// Đẩy kết quả ra UI (kênh <see cref="ScanFeed"/>) và ghi vào SQLite.
    ///
    /// Ghi DB là bất đồng bộ (chỉ enqueue) nên KHÔNG ảnh hưởng tới luồng xử lý.
    /// </summary>
    /// <param name="processingMs">
    /// Thời gian xử lý (ms) — đã đo trước khi gửi xuống PLC.
    /// </param>
    private void Publish(
        string raw,
        ScanOutcome outcome,
        DateTime receivedUtc,
        bool plcWritten,
        double processingMs,
        string? orderNo = null)
    {
        _feed.Publish(new ScanRecord(
            raw,
            outcome,
            receivedUtc,
            plcWritten,
            processingMs));

        _scanWriter.Enqueue(new ScanRecordEntity
        {
            ReceivedUtc = receivedUtc,
            CodeContent = raw,
            Status = outcome switch
            {
                ScanOutcome.Pass => ScanStatuses.Pass,
                ScanOutcome.Overload => ScanStatuses.Overload,
                ScanOutcome.Duplicate => ScanStatuses.Duplicate,
                ScanOutcome.InvalidForOrder => ScanStatuses.InvalidForOrder,
                ScanOutcome.NoRead => ScanStatuses.NoRead,
                _ => ScanStatuses.Fail,
            },
            ProcessingTimeMs = processingMs,
            PlcResult = plcWritten
                ? ScanPlcResults.Ok
                : outcome switch
                {
                    ScanOutcome.Overload => ScanPlcResults.Skipped,
                    ScanOutcome.NoProductionOrder => ScanPlcResults.Skipped,
                    ScanOutcome.Halted => ScanPlcResults.Skipped,
                    _ => ScanPlcResults.Failed,
                },
            BatchNo = ScanBatchDefaults.NoBatch,
            Raw = raw,

            // Lưu PO của mã để cơ chế chống trùng lịch sử toàn cục biết mã
            // này từng thuộc PO nào.
            OrderNo = orderNo ?? _po.CurrentOrder?.OrderNo ?? string.Empty,
        });
    }

    private void OnCameraEvent(eReaderState state, string data)
    {
        switch (state)
        {
            case eReaderState.Connected:
                _logger.LogInformation("Camera RD đã kết nối: {Message}", data);
                AppStateManager.SetCameraStatus(CameraStatus.Connected);
                break;
            case eReaderState.Disconnected:
                _logger.LogWarning("Camera RD mất kết nối: {Message}", data);
                AppStateManager.SetCameraStatus(CameraStatus.Disconnected);
                break;
            case eReaderState.Reconnecting:
                _logger.LogInformation("Camera RD đang kết nối lại: {Message}", data);
                AppStateManager.SetCameraStatus(CameraStatus.Reconnecting);
                break;
            case eReaderState.Received:
                _intake.Dispatch(() => HandleReceived(data));
                break;
        }
    }

    /// <summary>
    /// Xử lý mã vừa nhận từ camera.
    ///
    /// Nếu hệ thống đang xử lý mã trước đó thì mã mới bị TỪ CHỐI NGAY:
    /// ghi trạng thái <see cref="ScanOutcome.Overload"/>, không đưa vào hàng đợi
    /// và không ghi xuống PLC.
    /// </summary>
    /// <param name="data">Nội dung mã camera gửi lên.</param>
    private void HandleReceived(string data)
    {
        // Mốc nhận: DateTime để hiển thị/lưu DB, Stopwatch để đo (độ phân giải cao).
        var receivedUtc = DateTime.UtcNow;
        var receivedTimestamp = Stopwatch.GetTimestamp();

        // Ưu tiên mã đang xử lý: nếu đã có mã đang chờ/đang xử lý thì mã mới
        // bị TỪ CHỐI NGAY.
        //
        // Interlocked.Increment trả về giá trị SAU khi tăng: nếu > 1 nghĩa là
        // đã có 1 mã khác đang chiếm "chỗ" -> mã này là mã thứ 2 -> từ chối.
        if (Interlocked.Increment(ref _inFlight) > 1)
        {
            // Đã tăng lên 2 -> phải trả lại về 1 (giữ mã đang xử lý).
            Interlocked.Decrement(ref _inFlight);

            _logger.LogWarning(
                "Hệ thống đang xử lý mã trước, bỏ qua mã mới (quá tải): {Data}", data);

            _counters.AddScan(pass: false);

            // Mã bị từ chối: vẫn ghi vào DB + hiện ra UI, nhưng KHÔNG ghi PLC.
            Publish(
                data,
                ScanOutcome.Overload,
                receivedUtc,
                plcWritten: false,
                MeasureProcessingMs(new ScanRequest(data, receivedUtc, receivedTimestamp)));
            return;
        }

        _logger.LogInformation("Camera RD nhận QR: {Data}", data);

        if (!_scanQueue.Writer.TryWrite(new ScanRequest(data, receivedUtc, receivedTimestamp)))
        {
            // Hàng đợi đã đóng (app đang tắt) -> trả lại bộ đếm để không kẹt.
            Interlocked.Decrement(ref _inFlight);
        }
    }
}

/// <summary>
/// Một mã cần xử lý, kèm mốc thời gian nhận từ camera.
///
/// <para>
/// <b>ReceivedUtc</b> dùng để hiển thị và lưu DB (định dạng "O" — độ phân
/// giải 100ns).
/// </para>
///
/// <para>
/// <b>ReceivedTimestamp</b> là mốc đo bằng <see cref="Stopwatch"/> — độ phân
/// giải ~1ns, dùng để tính thời gian xử lý. Không dùng <see cref="DateTime"/>
/// để đo vì trên Windows độ phân giải chỉ ~15ms (thực tế ~1.16ms), không
/// đủ để hiển thị mức <c>0.333 ms</c>.
/// </para>
/// </summary>
/// <param name="Raw">Nội dung gốc camera gửi lên.</param>
/// <param name="ReceivedUtc">Thời điểm nhận mã (UTC) — để hiển thị và lưu DB.</param>
/// <param name="ReceivedTimestamp">Mốc thời gian từ <see cref="Stopwatch"/> — để đo.</param>
internal readonly record struct ScanRequest(
    string Raw,
    DateTime ReceivedUtc,
    long ReceivedTimestamp);
