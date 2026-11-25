using System.Globalization;
using System.Net.Sockets;
using System.Text;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace ClassLibraryMT.Print;

public sealed class G10PrinterClient : IPrinterClient
{
    private const int FilePollIntervalMs = 15_000;
    private readonly PrinterOptions _options;
    private readonly int _timeoutMs;
    private readonly ILogger<G10PrinterClient>? _logger;
    private readonly object _disposeGate = new();
    private readonly SemaphoreSlim _io = new(1, 1);
    private TcpClient? _tcp;
    private NetworkStream? _stream;
    private CancellationTokenSource? _readerCts;
    private Task? _readerTask;
    private Channel<string> _responses = NewResponseChannel();
    private PrinterStatusSnapshot _status;
    private PrinterFilesSnapshot _files = new("", []);
    private long _nextFilePollTick;
    private Task? _disposeTask;

    public G10PrinterClient(PrinterOptions options, int timeoutMs = 1500, ILogger<G10PrinterClient>? logger = null)
    {
        _options = options; _timeoutMs = Math.Clamp(timeoutMs, 250, 30_000); _logger = logger;
        _status = new(PrinterConnectionState.Disconnected, false, "", null, null, null, "Chưa kết nối", DateTime.UtcNow);
    }

    public string PrinterId => _options.Id;
    public PrinterStatusSnapshot Status => _status;
    public PrinterFilesSnapshot Files => _files;
    public event Func<string, Task>? PrintConfirmed;
    public event Action<PrinterCommandLog>? CommandObserved;

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        await _io.WaitAsync(ct).ConfigureAwait(false);
        try { await EnsureConnectedCoreAsync(ct).ConfigureAwait(false); }
        finally { _io.Release(); }
    }

    private async Task EnsureConnectedCoreAsync(CancellationToken ct)
    {
        if (_status.Connection == PrinterConnectionState.Connected && _tcp?.Connected == true && _stream is not null)
            return;

        _status = _status with { Connection = PrinterConnectionState.Connecting, Message = "Đang kết nối…", ObservedUtc = DateTime.UtcNow };
        await DisconnectCoreAsync(ct).ConfigureAwait(false);
        _status = _status with { Connection = PrinterConnectionState.Connecting, Message = "Đang kết nối…", ObservedUtc = DateTime.UtcNow };
        var tcp = new TcpClient();
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(_timeoutMs);
            try { await tcp.ConnectAsync(_options.Ip, _options.Port, timeout.Token).ConfigureAwait(false); }
            catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
            {
                throw new TimeoutException($"Hết thời gian kết nối máy in {_options.Ip}:{_options.Port} sau {_timeoutMs} ms.", ex);
            }
            _tcp = tcp; _stream = tcp.GetStream();
            _readerCts = new CancellationTokenSource();
            _responses = NewResponseChannel();
            _readerTask = Task.Run(() => ReadLoopAsync(_stream, _readerCts.Token));
            _nextFilePollTick = 0;
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
        await _io.WaitAsync(ct).ConfigureAwait(false);
        try { await DisconnectCoreAsync(ct).ConfigureAwait(false); }
        finally { _io.Release(); }
    }

    private async Task DisconnectCoreAsync(CancellationToken ct)
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
            await EnsureConnectedCoreAsync(ct).ConfigureAwait(false);
            var status = await SendCommandAsync("STATUS", "STATUS", ct).ConfigureAwait(false);
            var ink = await SendCommandAsync("INKV", "INKV", ct).ConfigureAwait(false);
            var buffer = await SendCommandAsync("MFDLEN", "MFDLEN", ct).ConfigureAwait(false);
            var (ink1, ink2) = ParseInk(ink);
            _status = _status with { Connection = PrinterConnectionState.Connected, IsPrinting = ParseInt(status) == 1,
                Ink1Percent = ink1, Ink2Percent = ink2, BufferLength = ParseInt(buffer), Message = "Đã cập nhật trạng thái", ObservedUtc = DateTime.UtcNow };

            // Bản tin có thể đổi trực tiếp trên HMI. Đọc chậm hơn các trạng thái nhanh,
            // cùng khóa I/O để phản hồi GFILE không tranh luồng gửi mã PO.
            if (Environment.TickCount64 >= _nextFilePollTick)
            {
                _nextFilePollTick = Environment.TickCount64 + FilePollIntervalMs;
                try { await SendCommandAsync("GFILE", "GFILE", ct).ConfigureAwait(false); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (OperationCanceledException ex)
                {
                    _logger?.LogDebug(ex, "Chưa đọc được bản tin máy in {PrinterId}; giữ trạng thái nhanh vừa nhận.", PrinterId);
                }
            }
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
            await EnsureConnectedCoreAsync(ct).ConfigureAwait(false);
            // MFD|MFD_NAME:data — ':' ',' '|' là ký tự điều khiển của frame, phải escape.
            var frame = $"MFD|{_options.MfdName}:{EscapeMfdData(code)}";
            var response = await SendCommandAsync(frame, "MFD", ct).ConfigureAwait(false);
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
            await EnsureConnectedCoreAsync(ct).ConfigureAwait(false);
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
        // Máy G10 nhận lệnh thô, không có CRLF; chỉ phản hồi mới kết thúc bằng CRLF.
        await _stream.WriteAsync(Encoding.ASCII.GetBytes(command), ct).ConfigureAwait(false);
        await _stream.FlushAsync(ct).ConfigureAwait(false);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(timeoutMs ?? _timeoutMs);
        while (true)
        {
            var line = await _responses.Reader.ReadAsync(timeout.Token).ConfigureAwait(false);
            if (!line.StartsWith(expectedPrefix + "|", StringComparison.OrdinalIgnoreCase)) continue;

            var accepted = IsAcceptedResponse(command, expectedPrefix, line);
            if (command.Equals("GFILE", StringComparison.OrdinalIgnoreCase)
                && TryParseFilesResponse(line, out var files))
            {
                _files = files;
                _status = _status with { Job = files.CurrentJob, ObservedUtc = DateTime.UtcNow };
                _nextFilePollTick = Environment.TickCount64 + FilePollIntervalMs;
            }
            var observers = CommandObserved;
            if (observers is not null)
            {
                try { observers(new PrinterCommandLog(DateTimeOffset.Now, command, line, accepted)); }
                catch { /* Lịch sử UI không được làm lỗi luồng gửi lệnh. */ }
            }
            return line;
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
                    // GFILE kết thúc bằng chuỗi literal "\r\n", các lệnh khác dùng CRLF thật.
                    var realEnd = text.IndexOf("\r\n", StringComparison.Ordinal);
                    var literalEnd = text.IndexOf("\\r\\n", StringComparison.Ordinal);
                    int end, skip;
                    if (realEnd >= 0 && (literalEnd < 0 || realEnd <= literalEnd)) { end = realEnd; skip = 2; }
                    else if (literalEnd >= 0) { end = literalEnd; skip = 4; }
                    else break;

                    var line = text[..end].Trim();
                    buffer.Remove(0, end + skip);
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

    private static bool IsAcceptedResponse(string command, string prefix, string response)
    {
        // Truy vấn trả dữ liệu, không trả ACK "|OK" như các lệnh điều khiển.
        if (command.Equals("GFILE", StringComparison.OrdinalIgnoreCase))
            return TryParseFilesResponse(response, out _);
        if (command.Equals("STATUS", StringComparison.OrdinalIgnoreCase))
            return ParseInt(response) is 0 or 1;
        if (command.Equals("MFDLEN", StringComparison.OrdinalIgnoreCase))
            return ParseInt(response) is >= 0;
        if (command.Equals("INKV", StringComparison.OrdinalIgnoreCase))
        {
            var values = response[(response.IndexOf('|') + 1)..].Split(',', StringSplitOptions.TrimEntries);
            return values.Length is 1 or 2 && values.All(value =>
                double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var percent)
                && percent is >= 0 and <= 100);
        }
        return response.StartsWith(prefix + "|OK", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Đọc GFILE|CU:id,id:tên,...; tên bản tin có thể chứa dấu ':'. Phản hồi lỗi,
    /// mục thiếu tên/Id hoặc Id trùng không được ghi đè danh sách tốt gần nhất.
    /// </summary>
    public static bool TryParseFilesResponse(string response, out PrinterFilesSnapshot files)
    {
        files = new("", []);
        if (string.IsNullOrWhiteSpace(response)) return false;
        response = response.Trim();
        if (response.EndsWith("\\r\\n", StringComparison.Ordinal)) response = response[..^4].TrimEnd();
        const string prefix = "GFILE|";
        if (!response.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;

        string? currentId = null;
        var parsed = new List<PrinterFileSnapshot>();
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in response[prefix.Length..].Split(',', StringSplitOptions.TrimEntries))
        {
            // Một số firmware thêm dấu phẩy cuối danh sách.
            if (part.Length == 0) continue;
            var separator = part.IndexOf(':');
            if (separator <= 0) return false;
            var id = part[..separator].Trim();
            var name = part[(separator + 1)..].Trim();
            if (id.Equals("CU", StringComparison.OrdinalIgnoreCase))
            {
                if (currentId is not null) return false;
                currentId = name;
            }
            else
            {
                if (id.Length == 0 || name.Length == 0 || !ids.Add(id)) return false;
                parsed.Add(new PrinterFileSnapshot(id, name));
            }
        }
        if (currentId is null) return false;
        files = new(currentId, parsed.AsReadOnly());
        return true;
    }

    /// <summary>
    /// Escape dữ liệu theo tài liệu Network port communication protocol.
    /// PDF in dạng \\\\ do bị escape thêm khi dịch; trên dây máy nhận 2 dấu '\'
    /// trước ký tự đặc biệt. Ví dụ ':' → '\\:' (hai backslash + colon).
    /// </summary>
    private static string EscapeMfdData(string value) => value
        .Replace("\\", "\\\\\\\\", StringComparison.Ordinal)
        .Replace(":", "\\\\:", StringComparison.Ordinal)
        .Replace(",", "\\\\,", StringComparison.Ordinal)
        .Replace("|", "\\\\|", StringComparison.Ordinal);

    private static int? ParseInt(string raw)
    {
        var value = raw.Contains('|') ? raw[(raw.IndexOf('|') + 1)..] : raw;
        value = value.Trim();
        // STATUS/CSTATUS trả về "0,1" / "0,3131": legacy chỉ đọc số trước dấu phẩy.
        var comma = value.IndexOf(',');
        if (comma >= 0) value = value[..comma].Trim();
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result) ? result : null;
    }
    private static (double?, double?) ParseInk(string raw)
    {
        var value = raw.Contains('|') ? raw[(raw.IndexOf('|') + 1)..] : raw;
        var parts = value.Split(',', StringSplitOptions.TrimEntries);
        double? Parse(string x) => double.TryParse(x, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;
        return (parts.Length > 0 ? Parse(parts[0]) : null, parts.Length > 1 ? Parse(parts[1]) : null);
    }

    public ValueTask DisposeAsync()
    {
        lock (_disposeGate)
            return new(_disposeTask ??= DisposeCoreAsync());
    }

    private async Task DisposeCoreAsync()
    {
        await DisconnectAsync().ConfigureAwait(false);
        _io.Dispose();
    }
}
