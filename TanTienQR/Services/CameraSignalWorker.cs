using System.Threading.Channels;
using ClassLibraryMT.Communications.CodeReaderProtocol;
using ClassLibraryMT.DeviceStatus;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TanTienQR.Models;

namespace TanTienQR.Services;

/// <summary>
/// Nhận tín hiệu từ camera, phân tích mã và gửi kết quả xuống PLC.
///
/// Không gọi API Checkee: mã đọc được coi là Pass (D12 = 1), mã lỗi hoặc
/// sai định dạng coi là Fail (D12 = 0).
///
/// Ghi PLC được thực hiện trong <see cref="ProcessQueueAsync"/> (luồng riêng qua
/// <see cref="Channel{T}"/>) chứ không phải trong callback của camera — vì ghi
/// PLC là I/O đồng bộ có timeout, làm trong callback sẽ chặn socket loop và
/// làm mất frame.
/// </summary>
internal sealed class CameraSignalWorker : BackgroundService
{
    private readonly Reader _camera;
    private readonly PlcWriter _plc;
    private readonly ScanFeed _feed;
    private readonly CameraOptions _opt;
    private readonly ILogger<CameraSignalWorker> _logger;

    // Hàng đợi để callback của camera không bị chặn bởi I/O.
    private readonly Channel<string> _queue = Channel.CreateUnbounded<string>(
        new UnboundedChannelOptions { SingleReader = true });

    // Mã đã quét gần đây, dùng để phát hiện mã trùng.
    private readonly Dictionary<string, DateTime> _recent = new();

    public CameraSignalWorker(
        Reader camera,
        PlcWriter plc,
        ScanFeed feed,
        IOptions<CameraOptions> options,
        ILogger<CameraSignalWorker> logger)
    {
        _camera = camera;
        _plc = plc;
        _feed = feed;
        _opt = options.Value;
        _logger = logger;
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
            _queue.Writer.TryComplete();
            _logger.LogInformation("Camera signal worker đã dừng.");
        }
    }

    private async Task ProcessQueueAsync(CancellationToken ct)
    {
        await foreach (var raw in _queue.Reader.ReadAllAsync(ct))
        {
            try
            {
                await HandleAsync(raw, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi xử lý mã quét: {Data}", raw);
            }
        }
    }

    private async Task HandleAsync(string raw, CancellationToken ct)
    {
        // Camera báo lỗi đọc mã ("NoRead" hoặc "FAIL") → Fail.
        if (QrParser.IsNoRead(raw))
        {
            _logger.LogWarning("Camera không đọc được mã, gửi Fail xuống PLC: {Data}", raw);
            var written = await _plc.WriteResultAsync(pass: false, ct);
            _feed.Publish(new ScanRecord(raw, null, null, ScanOutcome.NoRead, DateTime.UtcNow, written));
            return;
        }

        // Mã không khớp pattern TXNG/GS1 → Fail.
        if (!QrParser.TryParse(raw, out var gtin, out var serial))
        {
            _logger.LogWarning("QR không đúng định dạng TXNG, gửi Fail xuống PLC: {Data}", raw);
            var written = await _plc.WriteResultAsync(pass: false, ct);
            _feed.Publish(new ScanRecord(raw, null, null, ScanOutcome.InvalidFormat, DateTime.UtcNow, written));
            return;
        }

        // Mã trùng (quét lại trong cửa sổ thời gian) → Fail.
        if (IsDuplicate($"{gtin}|{serial}"))
        {
            _logger.LogInformation("Mã vừa quét gần đây, gửi Fail xuống PLC: {Serial}", serial);
            var dupWritten = await _plc.WriteResultAsync(pass: false, ct);
            _feed.Publish(new ScanRecord(raw, gtin, serial, ScanOutcome.Duplicate, DateTime.UtcNow, dupWritten));
            return;
        }

        // Đọc được mã hợp lệ → Pass (không cần gọi API để quyết định).
        var ok = await _plc.WriteResultAsync(pass: true, ct);
        _logger.LogInformation(
            ok
                ? "Mã hợp lệ, gửi Pass xuống PLC: GTIN={Gtin} Serial={Serial}"
                : "Mã hợp lệ nhưng ghi PLC thất bại: GTIN={Gtin} Serial={Serial}",
            gtin, serial);

        _feed.Publish(new ScanRecord(raw, gtin, serial, ScanOutcome.Pass, DateTime.UtcNow, ok));
    }

    /// <summary>
    /// Kiểm tra mã vừa quét có trùng với mã đã quét gần đây không.
    ///
    /// Mã trùng được tính là Fail (D12 = 0). Cửa sổ trùng lấy từ
    /// <see cref="CameraOptions.DuplicateWindowSeconds"/>.
    /// </summary>
    private bool IsDuplicate(string key)
    {
        var now = DateTime.UtcNow;
        var window = TimeSpan.FromSeconds(Math.Max(1, _opt.DuplicateWindowSeconds));

        // Dọn các mã đã cũ hơn cửa sổ trùng.
        foreach (var k in _recent.Where(kv => now - kv.Value > window).Select(kv => kv.Key).ToList())
            _recent.Remove(k);

        if (_recent.ContainsKey(key)) return true;

        _recent[key] = now;
        return false;
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
                _logger.LogInformation("Camera RD nhận QR: {Data}", data);
                _queue.Writer.TryWrite(data);
                break;
        }
    }
}
