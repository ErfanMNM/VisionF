using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ClassLibraryMT.MesSimulator;

/// <summary>Worker upload một file CodeContent đã cung cấp khi khởi động.</summary>
public sealed class MesSimulatorWorker : BackgroundService
{
    private readonly MesSimulatorClient _client;
    private readonly MesSimulatorOptions _options;
    private readonly ILogger<MesSimulatorWorker> _logger;
    private readonly MesSimulatorState _state = new();

    public MesSimulatorWorker(MesSimulatorClient client, IOptions<MesSimulatorOptions> options,
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
            return;
        }
        try
        {
            await SendOneAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        catch (Exception ex)
        {
            _state.SetStatus("Upload file CodeContent thất bại: " + ex.Message, true);
            _logger.LogError(ex, "MES simulator không upload được file {FilePath}.", _options.CustomFilePath);
        }
    }

    /// <summary>Gửi file đã cấu hình một lần; không tạo mã thay thế khi thiếu file.</summary>
    public async Task<MesSimulatorResult> SendOneAsync(CancellationToken ct = default)
    {
        var filePath = _options.ResolveCustomFilePath();
        if (filePath.Length == 0)
            throw new InvalidOperationException("Cần cung cấp CustomFilePath trỏ tới file một cột CodeContent; simulator không tự sinh mã.");
        var poName = (_options.CustomOrderNo ?? string.Empty).Trim();
        if (poName.Length == 0) poName = (_options.CustomPoName ?? string.Empty).Trim();
        if (poName.Length == 0) poName = Path.GetFileNameWithoutExtension(filePath);
        _state.SetStatus($"Đang upload file {Path.GetFileName(filePath)}...", false);
        var result = await _client.UploadExistingFileAsync(poName, _options.OrderQty, filePath, ct, _options.Gtin)
            .ConfigureAwait(false);
        _state.Record(result);
        _logger.LogInformation("MES simulator gửi PO {PoName}: success={Success}, HTTP {StatusCode}.",
            result.PoName, result.Success, result.StatusCode);
        return result;
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
        if (result.Success) Interlocked.Increment(ref _sentCount);
        SetStatus(result.Success
            ? $"Đã gửi PO {result.PoName} thành công (HTTP {result.StatusCode})."
            : $"Gửi PO {result.PoName} thất bại: {result.ResponseBody}", !result.Success);
        lock (_gate) _lastSentUtc = DateTime.UtcNow;
    }
}
