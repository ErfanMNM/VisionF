using System.Globalization;
using System.Net.Sockets;
using System.Text;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace ClassLibraryMT.Print;

public sealed class G10PrinterClient : IPrinterClient
{
    private readonly PrinterOptions _options;
    private readonly int _timeoutMs;
    private readonly ILogger<G10PrinterClient>? _logger;
    private readonly SemaphoreSlim _io = new(1, 1);
    private TcpClient? _tcp;
    private NetworkStream? _stream;
    private CancellationTokenSource? _readerCts;
    private Task? _readerTask;
    private Channel<string> _responses = NewResponseChannel();
    private PrinterStatusSnapshot _status;

    public G10PrinterClient(PrinterOptions options, int timeoutMs = 1500, ILogger<G10PrinterClient>? logger = null)
    {
        _options = options; _timeoutMs = Math.Clamp(timeoutMs, 250, 30_000); _logger = logger;
        _status = new(PrinterConnectionState.Disconnected, false, "", null, null, null, "Chưa kết nối", DateTime.UtcNow);
    }

    public string PrinterId => _options.Id;
    public PrinterStatusSnapshot Status => _status;
    public event Func<string, Task>? PrintConfirmed;

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        if (_tcp?.Connected == true) return;
        _status = _status with { Connection = PrinterConnectionState.Connecting, Message = "Đang kết nối…", ObservedUtc = DateTime.UtcNow };
        await DisconnectAsync(ct).ConfigureAwait(false);
        var tcp = new TcpClient();
        try
        {
            await tcp.ConnectAsync(_options.Ip, _options.Port, ct).ConfigureAwait(false);
            _tcp = tcp; _stream = tcp.GetStream();
            _readerCts = new CancellationTokenSource();
            _responses = NewResponseChannel();
            _readerTask = Task.Run(() => ReadLoopAsync(_stream, _readerCts.Token));
            _status = _status with { Connection = PrinterConnectionState.Connected, Message = "Đã kết nối", ObservedUtc = DateTime.UtcNow };
        }
        catch
        {
            tcp.Dispose();
            _status = _status with { Connection = PrinterConnectionState.Faulted, Message = "Không kết nối được máy in", ObservedUtc = DateTime.UtcNow };
            throw;
        }
    }

    public async Task DisconnectAsync(CancellationToken ct = default)
    {
        _readerCts?.Cancel();
        _stream?.Dispose(); _tcp?.Dispose(); _stream = null; _tcp = null;
        if (_readerTask is not null)
        {
            try { await _readerTask.WaitAsync(TimeSpan.FromSeconds(1), ct).ConfigureAwait(false); }
            catch (Exception) { }
        }
        _readerCts?.Dispose(); _readerCts = null; _readerTask = null;
        _status = _status with { Connection = PrinterConnectionState.Disconnected, Message = "Đã ngắt kết nối", ObservedUtc = DateTime.UtcNow };
    }

    public async Task<PrinterStatusSnapshot> RefreshStatusAsync(CancellationToken ct = default)
    {
        await _io.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await ConnectAsync(ct).ConfigureAwait(false);
            var status = await SendCommandAsync("STATUS", "STATUS", ct).ConfigureAwait(false);
            var ink = await SendCommandAsync("INKV", "INKV", ct).ConfigureAwait(false);
            var buffer = await SendCommandAsync("MFDLEN", "MFDLEN", ct).ConfigureAwait(false);
            var (ink1, ink2) = ParseInk(ink);
            _status = _status with { Connection = PrinterConnectionState.Connected, IsPrinting = ParseInt(status) == 1,
                Ink1Percent = ink1, Ink2Percent = ink2, BufferLength = ParseInt(buffer), Message = "Đã cập nhật trạng thái", ObservedUtc = DateTime.UtcNow };
            return _status;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Không đọc được trạng thái máy in {PrinterId}", PrinterId);
            _status = _status with { Connection = PrinterConnectionState.Faulted, Message = ex.Message, ObservedUtc = DateTime.UtcNow };
            throw;
        }
        finally { _io.Release(); }
    }

    public async Task<PrinterSendResult> SendCodeAsync(string code, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        await _io.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await ConnectAsync(ct).ConfigureAwait(false);
            var response = await SendCommandAsync($"MFD|POD01:{code}", "MFD", ct).ConfigureAwait(false);
            return new PrinterSendResult(code, response.StartsWith("MFD|OK", StringComparison.OrdinalIgnoreCase), response);
        }
        finally { _io.Release(); }
    }

    /// <summary>
    /// Gửi lệnh điều khiển thô theo đúng protocol G10 legacy (xem Print/command/CommandString.cs).
    /// </summary>
    public async Task<string> SendRawCommandAsync(
        string command,
        string expectedPrefix,
        int timeoutMs = 5000,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        await _io.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await ConnectAsync(ct).ConfigureAwait(false);
            return await SendCommandAsync(command, expectedPrefix, ct, timeoutMs).ConfigureAwait(false);
        }
        finally { _io.Release(); }
    }

    private async Task<string> SendCommandAsync(
        string command,
        string expectedPrefix,
        CancellationToken ct,
        int? timeoutMs = null)
    {
        if (_stream is null) throw new IOException("Máy in chưa kết nối.");
        await _stream.WriteAsync(Encoding.ASCII.GetBytes(command + "\r\n"), ct).ConfigureAwait(false);
        await _stream.FlushAsync(ct).ConfigureAwait(false);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(timeoutMs ?? _timeoutMs);
        while (true)
        {
            var line = await _responses.Reader.ReadAsync(timeout.Token).ConfigureAwait(false);
            if (line.StartsWith(expectedPrefix, StringComparison.OrdinalIgnoreCase)) return line;
        }
    }

    private async Task ReadLoopAsync(NetworkStream stream, CancellationToken ct)
    {
        var bytes = new byte[1024];
        var buffer = new StringBuilder();
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var count = await stream.ReadAsync(bytes, ct).ConfigureAwait(false);
                if (count == 0) throw new IOException("Máy in đã đóng kết nối.");
                buffer.Append(Encoding.ASCII.GetString(bytes, 0, count));
                while (true)
                {
                    var text = buffer.ToString();
                    var end = text.IndexOf("\r\n", StringComparison.Ordinal);
                    if (end < 0) break;
                    var line = text[..end].Trim();
                    buffer.Remove(0, end + 2);
                    if (line.StartsWith("BACK", StringComparison.OrdinalIgnoreCase))
                    {
                        var handlers = PrintConfirmed;
                        if (handlers is not null)
                            foreach (Func<string, Task> handler in handlers.GetInvocationList())
                                await handler(line).ConfigureAwait(false);
                    }
                    else
                    {
                        await _responses.Writer.WriteAsync(line, ct).ConfigureAwait(false);
                    }
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Mất luồng nhận dữ liệu máy in {PrinterId}", PrinterId);
            _responses.Writer.TryComplete(ex);
            _status = _status with { Connection = PrinterConnectionState.Faulted, Message = ex.Message, ObservedUtc = DateTime.UtcNow };
        }
    }

    private static Channel<string> NewResponseChannel() => Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
    private static int? ParseInt(string raw)
    {
        var value = raw.Contains('|') ? raw[(raw.IndexOf('|') + 1)..] : raw;
        return int.TryParse(value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var result) ? result : null;
    }
    private static (double?, double?) ParseInk(string raw)
    {
        var value = raw.Contains('|') ? raw[(raw.IndexOf('|') + 1)..] : raw;
        var parts = value.Split(',', StringSplitOptions.TrimEntries);
        double? Parse(string x) => double.TryParse(x, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;
        return (parts.Length > 0 ? Parse(parts[0]) : null, parts.Length > 1 ? Parse(parts[1]) : null);
    }

    public async ValueTask DisposeAsync() { await DisconnectAsync().ConfigureAwait(false); _io.Dispose(); }
}
