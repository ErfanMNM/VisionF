using Microsoft.Extensions.Logging;
using System.Net.Sockets;

namespace ClassLibraryMT.MultiConnect;

/// <summary>
/// Nguồn TCP client: kết nối tới một TCP server có sẵn (camera thật, đầu đọc mã,
/// hoặc thiết bị khác) rồi chuyển tiếp frame sang TCP server của gateway.
/// </summary>
internal sealed class TcpClientInputSource : IInputSource
{
    private readonly MultiConnectChannelOptions _options;
    private readonly int _reconnectDelayMs;
    private readonly int _maxFrameLength;
    private readonly ILogger _logger;

    public TcpClientInputSource(
        MultiConnectChannelOptions options,
        int reconnectDelayMs,
        int maxFrameLength,
        ILogger logger)
    {
        _options = options;
        _reconnectDelayMs = reconnectDelayMs;
        _maxFrameLength = maxFrameLength;
        _logger = logger;
    }

    public async Task RunAsync(Func<string, Task> onFrame, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var client = new TcpClient();
                await client.ConnectAsync(_options.Host, _options.Port, ct).ConfigureAwait(false);
                _logger.LogInformation("[{Channel}] Da ket noi TCP {Host}:{Port}",
                    _options.Name, _options.Host, _options.Port);

                await using var stream = client.GetStream();
                var decoder = new FrameDecoder(_maxFrameLength);
                var buffer = new byte[1024];

                while (!ct.IsCancellationRequested)
                {
                    var read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false);
                    if (read == 0)
                    {
                        break;
                    }

                    foreach (var frame in decoder.Push(buffer.AsSpan(0, read)))
                    {
                        await onFrame(frame).ConfigureAwait(false);
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex) when (ex is SocketException or IOException)
            {
                _logger.LogWarning(ex, "[{Channel}] Mat ket noi TCP {Host}:{Port}, se thu lai: {Message}",
                    _options.Name, _options.Host, _options.Port, ex.Message);
            }

            if (ct.IsCancellationRequested)
            {
                break;
            }

            try
            {
                await Task.Delay(_reconnectDelayMs, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
}
