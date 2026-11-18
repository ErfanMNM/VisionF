using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ClassLibraryMT.MesSimulator;

/// <summary>Worker nền sinh PO và gửi tới HTTP server nhận PO.</summary>
public sealed class MesSimulatorWorker : BackgroundService
{
    private readonly MesSimulatorClient _client;
    private readonly MesSimulatorOptions _options;
    private readonly ILogger<MesSimulatorWorker> _logger;
    private long _sequence;
    private readonly MesSimulatorState _state = new();

    public MesSimulatorWorker(
        MesSimulatorClient client,
        IOptions<MesSimulatorOptions> options,
        ILogger<MesSimulatorWorker> logger)
    {
        _client = client;
        _options = options.Value;
        _logger = logger;
    }

    public MesSimulatorState State => _state;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            _state.SetStatus("Đã tắt (MesSimulator:Enabled = false).", false);
            _logger.LogInformation("MES simulator không chạy vì đang tắt.");
            return;
        }

        _logger.LogInformation(
            "MES simulator bật, gửi tới {Endpoint}.",
            _options.ResolveEndpoint());

        if (!string.IsNullOrWhiteSpace(_options.CustomFilePath))
        {
            await SendCustomFileOnceAsync(stoppingToken).ConfigureAwait(false);
            return;
        }

        _logger.LogInformation(
            "Chế độ tự sinh PO: {Qty} mã mỗi {Interval} giây.",
            _options.OrderQty, _options.ResolveInterval().TotalSeconds);

        try
        {
            await SendOneAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            _state.SetStatus("Lỗi simulator: " + ex.Message, true);
            _logger.LogError(ex, "Lỗi khi gửi PO đầu tiên từ MES simulator.");
        }

        using var timer = new PeriodicTimer(_options.ResolveInterval());
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                try
                {
                    await SendOneAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _state.SetStatus("Lỗi simulator: " + ex.Message, true);
                    _logger.LogError(ex, "Lỗi trong chu kỳ MES simulator.");
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    /// <summary>Sinh và gửi ngay một PO test; hữu ích cho smoke test.</summary>
    public async Task<MesSimulatorResult> SendOneAsync(CancellationToken ct = default)
    {
        var sequence = Interlocked.Increment(ref _sequence);
        var stamp = DateTime.UtcNow.ToString("yyyyMMddHHmmss");
        var uniqueId = Guid.NewGuid().ToString("N")[..8];
        var poName = $"{_options.PoPrefix.Trim()}-{stamp}-{sequence:0000}-{uniqueId}";
        var qty = Math.Clamp(_options.OrderQty, 1, 100_000);
        var serials = Enumerable.Range(1, qty)
            .Select(i => $"{_options.PoPrefix.Trim()}-{stamp}-{sequence:0000}-{uniqueId}-{i:000000}")
            .ToArray();

        _state.SetStatus($"Đang gửi PO {poName}...", false);
        var result = await _client.UploadTestOrderAsync(poName, serials, ct).ConfigureAwait(false);
        _state.Record(result);
        _logger.LogInformation(
            "MES simulator gửi PO {PoName}: success={Success}, HTTP {StatusCode}.",
            result.PoName, result.Success, result.StatusCode);
        return result;
    }

    private async Task SendCustomFileOnceAsync(CancellationToken ct)
    {
        try
        {
            var filePath = _options.ResolveCustomFilePath();
            var poName = _options.CustomPoName.Trim();
            if (poName.Length == 0)
                poName = Path.GetFileNameWithoutExtension(filePath);

            _state.SetStatus($"Đang upload file {Path.GetFileName(filePath)}...", false);
            var result = await _client.UploadExistingFileAsync(
                poName, Math.Clamp(_options.OrderQty, 1, 100_000), filePath, _options.Gtin, ct)
                .ConfigureAwait(false);
            _state.Record(result);
            _logger.LogInformation(
                "MES simulator upload file {FilePath} thành {PoName}: success={Success}, HTTP {StatusCode}.",
                filePath, result.PoName, result.Success, result.StatusCode);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _state.SetStatus("Upload file custom thất bại: " + ex.Message, true);
            _logger.LogError(ex, "MES simulator không upload được file {FilePath}.", _options.CustomFilePath);
        }
    }
}

/// <summary>Trạng thái runtime của simulator để UI hoặc test đọc.</summary>
public sealed class MesSimulatorState
{
    private readonly Lock _gate = new();
    private string _status = "Chưa chạy.";
    private bool _isError;
    private int _sentCount;
    private DateTime? _lastSentUtc;

    public string Status => Volatile.Read(ref _status);
    public bool IsError => Volatile.Read(ref _isError);
    public int SentCount => Volatile.Read(ref _sentCount);
    public DateTime? LastSentUtc { get { lock (_gate) return _lastSentUtc; } }

    internal void SetStatus(string status, bool isError)
    {
        Volatile.Write(ref _status, status);
        Volatile.Write(ref _isError, isError);
    }

    internal void Record(MesSimulatorResult result)
    {
        if (result.Success)
            Interlocked.Increment(ref _sentCount);
        SetStatus(
            result.Success
                ? $"Đã gửi PO {result.PoName} thành công (HTTP {result.StatusCode})."
                : $"Gửi PO {result.PoName} thất bại: {result.ResponseBody}",
            !result.Success);
        lock (_gate) _lastSentUtc = DateTime.UtcNow;
    }
}
