using System.IO.Ports;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace TanTienQR.Services;

/// <summary>A scanner transport opened once for a single active-tab visit.</summary>
public interface IHandheldScannerTransport : IDisposable
{
    Task OpenAsync(CancellationToken cancellationToken);
    ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken);
}

/// <summary>
/// Receives complete CR/LF scanner frames only while a scanner tab is active.
/// Each activation makes one connection attempt; reopening the tab permits retry.
/// </summary>
public sealed class HandheldScannerService : IDisposable
{
    private readonly HandheldOptions _options;
    private readonly ILogger<HandheldScannerService> _logger;
    private readonly ScanIntakeGate _intake;
    private readonly Func<HandheldOptions, IHandheldScannerTransport> _transportFactory;
    private readonly object _gate = new();
    private Session? _session;
    private bool _disposed;
    private string _statusText = "Chưa bật";

    public event Action<string>? CodeReceived;
    public event Action<string>? StatusChanged;
    public string StatusText => Volatile.Read(ref _statusText);
    public bool IsActive { get { lock (_gate) return _session is not null; } }
    internal bool IsIntakeSuspended => _intake.IsSuspended;

    public HandheldScannerService(IOptions<HandheldOptions> options,
        ILogger<HandheldScannerService> logger, ScanIntakeGate intake)
        : this(options, logger, intake, static o => new ScannerTransport(o)) { }

    // Allows deterministic offline transport/lifecycle tests without a COM device.
    internal HandheldScannerService(IOptions<HandheldOptions> options,
        ILogger<HandheldScannerService> logger, ScanIntakeGate intake,
        Func<HandheldOptions, IHandheldScannerTransport> transportFactory)
    {
        _options = options.Value;
        _logger = logger;
        _intake = intake;
        _transportFactory = transportFactory;
    }

    public void Activate()
    {
        Session session;
        lock (_gate)
        {
            if (_disposed || _session is not null) return;
            session = new Session();
            _session = session;
        }
        UpdateStatus(session, _options.Enabled ? "Đang kết nối tay quét..." : "Không có tay quét");
        _ = Task.Run(() => RunSessionAsync(session));
    }

    public void Deactivate()
    {
        Session? session;
        lock (_gate)
        {
            session = _session;
            _session = null;
            Volatile.Write(ref _statusText, "Chưa bật");
        }
        session?.Close();
        PublishStatus();
    }

    /// <summary>Compatibility alias; application startup must not call this.</summary>
    public void Start() => Activate();
    public void Stop() => Deactivate();

    private async Task RunSessionAsync(Session session)
    {
        var ct = session.Cancellation.Token;
        try
        {
            if (!_options.Enabled) return;
            ct.ThrowIfCancellationRequested();
            var transport = _transportFactory(_options);
            if (!session.SetTransport(transport)) return;
            await transport.OpenAsync(ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            UpdateStatus(session, _options.Mode == HandheldMode.Serial
                ? $"Đã kết nối {_options.ComPort}" : $"Đã kết nối {_options.TcpHost}:{_options.TcpPort}");

            var frames = new HandheldFrameDecoder();
            var buffer = new byte[1024];
            while (!ct.IsCancellationRequested)
            {
                var count = await transport.ReadAsync(buffer, ct).ConfigureAwait(false);
                if (count == 0) break;
                foreach (var code in frames.Push(buffer.AsSpan(0, count)))
                {
                    _intake.Dispatch(() =>
                    {
                        lock (_gate)
                        {
                            if (!ReferenceEquals(_session, session) || ct.IsCancellationRequested) return;
                            try { CodeReceived?.Invoke(code); }
                            catch (Exception ex) { _logger.LogError(ex, "Handheld frame handler failed."); }
                        }
                    });
                }
            }
            UpdateStatus(session, "Không có tay quét");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (!ct.IsCancellationRequested)
            {
                UpdateStatus(session, "Không có tay quét");
                _logger.LogWarning(ex, "Handheld connection failed; retry only after reopening the tab.");
            }
        }
        finally
        {
            // A failed session remains selected, preventing a retry in this visit.
            session.Complete();
        }
    }

    private void UpdateStatus(Session session, string status)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(_session, session)) return;
            Volatile.Write(ref _statusText, status);
        }
        PublishStatus();
    }

    private void PublishStatus()
    {
        try { StatusChanged?.Invoke(StatusText); }
        catch (Exception ex) { _logger.LogError(ex, "Handheld status handler failed."); }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
        }
        Deactivate();
        GC.SuppressFinalize(this);
    }

    private sealed class Session
    {
        private readonly object _gate = new();
        private IHandheldScannerTransport? _transport;
        private bool _closed;
        public CancellationTokenSource Cancellation { get; } = new();
        public bool SetTransport(IHandheldScannerTransport transport)
        {
            lock (_gate)
            {
                if (!_closed) { _transport = transport; return true; }
            }
            transport.Dispose();
            return false;
        }
        public void Close()
        {
            IHandheldScannerTransport? transport;
            lock (_gate)
            {
                if (_closed) return;
                _closed = true;
                Cancellation.Cancel();
                transport = _transport;
                _transport = null;
            }
            try { transport?.Dispose(); }
            catch (Exception) { /* A driver close failure must not break navigation. */ }
        }
        public void Complete()
        {
            Close();
            lock (_gate) Cancellation.Dispose();
        }
    }

    private sealed class ScannerTransport : IHandheldScannerTransport
    {
        private readonly HandheldOptions _options;
        private readonly object _gate = new();
        private SerialPort? _serial;
        private TcpClient? _client;
        private TcpListener? _listener;
        private Stream? _stream;
        private bool _disposed;

        public ScannerTransport(HandheldOptions options) => _options = options;

        public async Task OpenAsync(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (_options.Mode == HandheldMode.Serial)
            {
                lock (_gate)
                {
                    ObjectDisposedException.ThrowIf(_disposed, this);
                    ct.ThrowIfCancellationRequested();
                    _serial = new SerialPort(_options.ComPort, _options.BaudRate, Parity.None, 8, StopBits.One);
                    _serial.Open();
                    _stream = _serial.BaseStream;
                }
                return;
            }
            if (!IPAddress.TryParse(_options.TcpHost, out var address))
                throw new ArgumentException("Scanner TCP host must be an IP address.");

            // TCP compatibility has one connection per visit and no retry loop.
            if (_options.Mode == HandheldMode.TcpServer)
            {
                var listener = new TcpListener(address, _options.TcpPort);
                lock (_gate)
                {
                    ObjectDisposedException.ThrowIf(_disposed, this);
                    _listener = listener;
                    listener.Start();
                }
                var client = await listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
                lock (_gate)
                {
                    if (_disposed) { client.Dispose(); throw new OperationCanceledException(ct); }
                    _client = client;
                }
                listener.Stop();
            }
            else if (_options.Mode == HandheldMode.TcpClient)
            {
                var client = new TcpClient();
                lock (_gate)
                {
                    if (_disposed) { client.Dispose(); throw new OperationCanceledException(ct); }
                    _client = client;
                }
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(3));
                await client.ConnectAsync(address, _options.TcpPort, timeout.Token).ConfigureAwait(false);
            }
            else throw new ArgumentOutOfRangeException(nameof(_options.Mode));

            lock (_gate)
            {
                if (_disposed) { _client?.Dispose(); throw new OperationCanceledException(ct); }
                _stream = _client!.GetStream();
            }
        }

        public ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct) =>
            (_stream ?? throw new InvalidOperationException("Scanner is not open.")).ReadAsync(buffer, ct);

        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed) return;
                _disposed = true;
                _listener?.Stop();
                _client?.Dispose();
                _serial?.Dispose();
                _stream = null;
            }
        }
    }
}

/// <summary>Preserves GS1 GS and discards an overlong frame up to its terminator.</summary>
internal sealed class HandheldFrameDecoder
{
    private const int MaxCodeLength = 512;
    private readonly Decoder _decoder = Encoding.UTF8.GetDecoder();
    private readonly StringBuilder _frame = new();
    private bool _discard;

    public IReadOnlyList<string> Push(ReadOnlySpan<byte> bytes)
    {
        var chars = new char[Encoding.UTF8.GetMaxCharCount(bytes.Length)];
        var count = _decoder.GetChars(bytes, chars, false);
        var result = new List<string>();
        for (var i = 0; i < count; i++)
        {
            var c = chars[i];
            if (c is '\r' or '\n')
            {
                if (!_discard && _frame.Length > 0)
                {
                    var code = _frame.ToString();
                    if (!string.IsNullOrWhiteSpace(code)) result.Add(code);
                }
                _frame.Clear();
                _discard = false;
            }
            else if (!_discard && (c >= ' ' || c == '\u001d'))
            {
                _frame.Append(c);
                if (_frame.Length > MaxCodeLength) { _frame.Clear(); _discard = true; }
            }
        }
        return result;
    }
}
