using System.IO.Ports;
using Microsoft.Extensions.Logging;

namespace ClassLibraryMT.MultiConnect;

/// <summary>Nguồn Serial/RS232: đọc COM port và tách frame theo CRLF.</summary>
internal sealed class SerialInputSource : IInputSource
{
    private readonly MultiConnectChannelOptions _options;
    private readonly int _reconnectDelayMs;
    private readonly int _maxFrameLength;
    private readonly ILogger _logger;

    public SerialInputSource(
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
            SerialPort? port = null;
            try
            {
                var parity = Enum.TryParse<Parity>(_options.Parity, ignoreCase: true, out var p)
                    ? p : Parity.None;
                var stopBits = _options.StopBits == 2 ? StopBits.Two : StopBits.One;

                port = new SerialPort(_options.PortName, _options.BaudRate, parity,
                    _options.DataBits, stopBits)
                {
                    ReadTimeout = 1000,
                    WriteTimeout = 1000,
                };
                port.Open();
                _logger.LogInformation("[{Channel}] Da mo COM {Port} ({Baud},{Data},{Parity},{Stop})",
                    _options.Name, _options.PortName, _options.BaudRate, _options.DataBits,
                    parity, stopBits);

                var decoder = new FrameDecoder(_maxFrameLength);
                var buffer = new byte[1024];
                var stream = port.BaseStream;

                while (!ct.IsCancellationRequested)
                {
                    int read;
                    try
                    {
                        read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false);
                    }
                    catch (TimeoutException)
                    {
                        continue;
                    }

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
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                          or ArgumentException or InvalidOperationException
                                          or NotSupportedException)
            {
                _logger.LogWarning(ex, "[{Channel}] Loi COM {Port}, se thu lai: {Message}",
                    _options.Name, _options.PortName, ex.Message);
            }
            finally
            {
                port?.Dispose();
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
