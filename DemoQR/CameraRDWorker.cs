using System.Threading.Channels;
using ClassLibraryMT.Communications.Checkee;
using ClassLibraryMT.Communications.CodeReaderProtocol;
using ClassLibraryMT.DeviceStatus;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace QRDemoCodeBackend;

// ---------------------------------------------------------------- Worker
internal sealed class CameraRDWorker : BackgroundService
{
    private readonly Reader _camera;
    private readonly CheckeeClient _checkee;
    private readonly CheckeeOptions _opt;
    private readonly PlcWriter _plc;
    private readonly ILogger<CameraRDWorker> _logger;

    // Hàng đợi để callback của camera không bị chặn bởi HTTP
    private readonly Channel<string> _queue = Channel.CreateUnbounded<string>(
        new UnboundedChannelOptions { SingleReader = true });

    private readonly Dictionary<string, DateTime> _recent = new();

    public CameraRDWorker(
        Reader camera,
        CheckeeClient checkee,
        IOptions<CheckeeOptions> opt,
        PlcWriter plc,
        ILogger<CameraRDWorker> logger)
    {
        _camera = camera;
        _checkee = checkee;
        _opt = opt.Value;
        _plc = plc;
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
        }
        finally
        {
            _camera.ClientCallback -= OnCameraEvent;
            _queue.Writer.TryComplete();
            _logger.LogInformation("Camera RD worker đã dừng.");
        }
    }

    private async Task ProcessQueueAsync(CancellationToken ct)
    {
        await foreach (var raw in _queue.Reader.ReadAllAsync(ct))
        {
            try
            {
                // Camera báo lỗi đọc mã ("NoRead" hoặc "FAIL") -> gửi Fail xuống PLC.
                if (QrParser.IsNoRead(raw))
                {
                    _logger.LogWarning("Camera không đọc được mã, gửi Fail xuống PLC: {Data}", raw);
                    await _plc.WriteResultAsync(pass: false, ct);
                    continue;
                }

                if (!QrParser.TryParse(raw, out var gtin, out var serial))
                {
                    // Mã sai định dạng: gửi Fail xuống PLC (giống TApp: FormatError -> Fail).
                    // Đây là lỗi của code xử lý nên chỉ log, không đưa vào AppState.
                    _logger.LogWarning("QR không đúng định dạng TXNG, gửi Fail xuống PLC: {Data}", raw);
                    await _plc.WriteResultAsync(pass: false, ct);
                    continue;
                }

                if (IsDuplicate($"{gtin}|{serial}"))
                {
                    _logger.LogInformation("Mã vừa quét gần đây, gửi Fail xuống PLC: {Serial}", serial);
                    await _plc.WriteResultAsync(pass: false, ct);
                    continue;
                }

                var result = await _checkee.ActivateAsync(gtin, serial, ct);
                if (result.Success)
                {
                    _logger.LogInformation(
                        "Kích hoạt OK: GTIN={Gtin} Serial={Serial} | Lô {Batch}: {Active}/{Total}",
                        gtin, serial, result.Data?.Batch, result.Data?.ActiveSerials, result.Data?.QuantityInBatch);
                    await _plc.WriteResultAsync(pass: true, ct);
                }
                else
                {
                    _logger.LogError("Kích hoạt THẤT BẠI: GTIN={Gtin} Serial={Serial} | {Message}",
                        gtin, serial, result.Message);
                    await _plc.WriteResultAsync(pass: false, ct);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Lỗi khi gọi API (mạng, token, server...) vẫn phải báo Fail
                // xuống PLC, nếu không PLC sẽ chờ kết quả mãi.
                _logger.LogError(ex, "Lỗi khi gửi kích hoạt mã: {Data}", raw);
                await _plc.WriteResultAsync(pass: false, ct);
            }
        }
    }

    private bool IsDuplicate(string key)
    {
        var now = DateTime.UtcNow;
        var window = TimeSpan.FromSeconds(_opt.DuplicateWindowSeconds);

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
