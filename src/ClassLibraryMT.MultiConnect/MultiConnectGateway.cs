using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ClassLibraryMT.MultiConnect;

/// <summary>
/// BackgroundService chạy tất cả kênh MultiConnect. Mỗi kênh là một task độc lập:
/// lỗi của kênh này không làm kênh khác ngừng, và tự khởi động lại sau một khoảng trễ.
/// </summary>
public sealed class MultiConnectGateway : BackgroundService
{
    private readonly MultiConnectOptions _options;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<MultiConnectGateway> _logger;
    private readonly Dictionary<string, MultiConnectChannelStatus> _statuses = new(StringComparer.Ordinal);

    // Vòng đệm traffic cho tab "MultiConnect": giữ 500 dòng gần nhất để UI
    // đọc định kỳ theo Id. Không đẩy event sang UI trực tiếp (tránh cross-thread).
    private const int TrafficCapacity = 500;
    private readonly object _trafficLock = new();
    private readonly Queue<MultiConnectTrafficItem> _traffic = new();
    private long _lastTrafficId;

    public MultiConnectGateway(
        MultiConnectOptions options,
        ILoggerFactory loggerFactory,
        ILogger<MultiConnectGateway> logger)
    {
        _options = options;
        _loggerFactory = loggerFactory;
        _logger = logger;
    }

    /// <summary>Trạng thái runtime của các kênh (chỉ đọc).</summary>
    public IReadOnlyCollection<MultiConnectChannelStatus> Statuses =>
        _statuses.Values.ToArray();

    /// <summary>Lấy các dòng traffic có Id lớn hơn <paramref name="afterId"/>.</summary>
    public IReadOnlyList<MultiConnectTrafficItem> GetTrafficAfter(long afterId)
    {
        lock (_trafficLock)
        {
            return _traffic.Where(item => item.Id > afterId).ToArray();
        }
    }

    private void RecordTraffic(string channel, string payload, bool isOutput, int clients)
    {
        var item = new MultiConnectTrafficItem
        {
            Id = Interlocked.Increment(ref _lastTrafficId),
            Time = DateTimeOffset.Now,
            Channel = channel,
            IsOutput = isOutput,
            Payload = payload,
            Clients = clients,
        };

        lock (_trafficLock)
        {
            _traffic.Enqueue(item);
            while (_traffic.Count > TrafficCapacity)
            {
                _traffic.Dequeue();
            }
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            _logger.LogInformation("MultiConnect dang tat (MultiConnect:Enabled = false).");
            return;
        }

        var channels = _options.Channels.Where(c => c.Enabled).ToArray();
        if (channels.Length == 0)
        {
            _logger.LogWarning("MultiConnect bat nhung khong co kenh nao duoc cau hinh.");
            return;
        }

        _logger.LogInformation("MultiConnect khoi dong voi {Count} kenh.", channels.Length);

        var runners = channels
            .Select(channel => Task.Run(() => RunChannelAsync(channel, stoppingToken), stoppingToken))
            .ToArray();

        await Task.WhenAll(runners).ConfigureAwait(false);
    }

    private async Task RunChannelAsync(MultiConnectChannelOptions channel, CancellationToken ct)
    {
        var status = new MultiConnectChannelStatus
        {
            Name = channel.Name,
            Type = channel.Type,
            ListenPort = channel.ListenPort,
            State = "Starting",
        };
        lock (_statuses)
        {
            _statuses[channel.Name] = status;
        }

        var logger = _loggerFactory.CreateLogger($"{nameof(MultiConnectGateway)}.{channel.Name}");

        while (!ct.IsCancellationRequested)
        {
            status.State = "Starting";
            status.LastError = null;

            IInputSource source;
            try
            {
                source = CreateSource(channel, logger);
            }
            catch (Exception ex)
            {
                status.State = "Error";
                status.LastError = ex.Message;
                _logger.LogError(ex, "[{Channel}] Khong tao duoc nguon: {Message}", channel.Name, ex.Message);
                if (!await DelayAsync(ct)) break;
                continue;
            }

            var server = new TcpFanoutServer(
                channel.ListenIp, channel.ListenPort, channel.Name, logger);

            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var sourceTask = source.RunAsync(
                frame => ForwardAsync(status, server, frame, linked.Token),
                linked.Token);
            var serverTask = server.RunAsync(linked.Token);
            status.State = "Running";

            try
            {
                var finished = await Task.WhenAny(sourceTask, serverTask).ConfigureAwait(false);
                if (!ct.IsCancellationRequested)
                {
                    status.LastError = finished.IsFaulted
                        ? finished.Exception?.Flatten().InnerException?.Message ?? "Loi khong xac dinh"
                        : "Nguon hoac server ket thuc";
                    _logger.LogWarning("[{Channel}] Kenh dung bat thuong: {Error}",
                        channel.Name, status.LastError);
                }
            }
            finally
            {
                linked.Cancel();
                try
                {
                    await Task.WhenAll(sourceTask, serverTask).ConfigureAwait(false);
                }
                catch
                {
                    // Da log o tren; khong de loi lan ra ngoai vong lap kenh.
                }
            }

            status.State = "Stopped";
            status.SetClients(0);
            if (!await DelayAsync(ct)) break;
        }

        status.State = "Stopped";
        _logger.LogInformation("[{Channel}] Kenh da dung", channel.Name);
    }

    private IInputSource CreateSource(MultiConnectChannelOptions channel, ILogger logger) =>
        channel.Type.Trim().ToLowerInvariant() switch
        {
            "serial" or "com" => new SerialInputSource(
                channel, _options.ReconnectDelayMs, _options.MaxFrameLength, logger),
            "tcpclient" or "tcp" or "forward" => new TcpClientInputSource(
                channel, _options.ReconnectDelayMs, _options.MaxFrameLength, logger),
            "udp" => new UdpInputSource(channel, _options.MaxFrameLength, logger),
            _ => throw new ArgumentException(
                $"Loai nguon '{channel.Type}' khong hop le (Serial | TcpClient | Udp).",
                nameof(channel)),
        };

    private async Task ForwardAsync(
        MultiConnectChannelStatus status,
        TcpFanoutServer server,
        string frame,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(frame))
        {
            return;
        }

        status.AddFrame();
        RecordTraffic(status.Name, frame, isOutput: false, server.ClientCount);
        await server.BroadcastAsync(frame, ct).ConfigureAwait(false);
        status.SetClients(server.ClientCount);
        RecordTraffic(status.Name, frame, isOutput: true, server.ClientCount);
    }

    private async Task<bool> DelayAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(_options.ReconnectDelayMs, ct).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}
