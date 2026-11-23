using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;

namespace ClassLibraryMT.MultiConnect;

/// <summary>Nguồn UDP: mỗi datagram là một frame (có thể chứa nhiều dòng CRLF).</summary>
internal sealed class UdpInputSource : IInputSource
{
    private readonly MultiConnectChannelOptions _options;
    private readonly int _maxFrameLength;
    private readonly ILogger _logger;

    public UdpInputSource(
        MultiConnectChannelOptions options,
        int maxFrameLength,
        ILogger logger)
    {
        _options = options;
        _maxFrameLength = maxFrameLength;
        _logger = logger;
    }

    public async Task RunAsync(Func<string, Task> onFrame, CancellationToken ct)
    {
        var ip = ResolveIp(_options.UdpListenIp);
        using var udp = new UdpClient();
        udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        udp.Client.Bind(new IPEndPoint(ip, _options.UdpListenPort));
        _logger.LogInformation("[{Channel}] Dang lang nghe UDP {Ip}:{Port}",
            _options.Name, ip, _options.UdpListenPort);

        var decoder = new FrameDecoder(_maxFrameLength);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var result = await udp.ReceiveAsync(ct).ConfigureAwait(false);
                foreach (var frame in decoder.Push(result.Buffer))
                {
                    await onFrame(frame).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (SocketException ex)
            {
                _logger.LogWarning(ex, "[{Channel}] Loi UDP: {Message}", _options.Name, ex.Message);
            }
        }

        var tail = decoder.Flush();
        if (tail is not null)
        {
            await onFrame(tail).ConfigureAwait(false);
        }
    }

    private static IPAddress ResolveIp(string value) =>
        IPAddress.TryParse(value, out var ip) ? ip : IPAddress.Loopback;
}
