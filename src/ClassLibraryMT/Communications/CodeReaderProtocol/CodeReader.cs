using System.Net;
using System.Net.Sockets;
using System.Text;
namespace ClassLibraryMT.Communications.CodeReaderProtocol;

public enum eReaderState
{
    Connected,
    Disconnected,
    Received,
    Reconnecting
}

public sealed class Reader
{
    private readonly object _sync = new();
    private readonly TimeSpan _reconnectDelay;
    private readonly SemaphoreSlim _configurationGate = new(1, 1);
    private Socket? _client;
    private CancellationTokenSource? _runCancellation;
    private CancellationTokenSource? _connectionCancellation;
    private TaskCompletionSource? _connectionEnded;
    private bool _isRunning;
    private bool _connected;
    private string _ip;
    private int _port;

    public string IP
    {
        get { lock (_sync) return _ip; }
        set { lock (_sync) _ip = value; }
    }
    public int Port
    {
        get { lock (_sync) return _port; }
        set { lock (_sync) _port = value; }
    }
    public bool Connected => Volatile.Read(ref _connected);

    public delegate void ClientEventHandler(eReaderState state, string data);
    public event ClientEventHandler? ClientCallback;

    public Reader(string ip, int port, TimeSpan? reconnectDelay = null)
    {
        _ip = ip;
        _port = port;
        _reconnectDelay = reconnectDelay ?? TimeSpan.FromSeconds(3);
    }

    /// <summary>
    /// Thay đích TCP trong cùng vòng chạy. Đợi phiên cũ kết thúc để không còn
    /// frame từ socket cũ sau khi trả về; kết nối mới tiếp tục tự retry ở nền.
    /// </summary>
    public async Task ReconfigureAsync(string ip, int port, CancellationToken cancellationToken = default)
    {
        // Resolve trước khi đổi cấu hình: lỗi nhập liệu/DNS không cắt kết nối cũ.
        await ResolveEndpointAsync(ip, port, cancellationToken).ConfigureAwait(false);
        await _configurationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Task? previousConnection;
            Socket? previousSocket;
            lock (_sync)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (string.Equals(_ip, ip, StringComparison.OrdinalIgnoreCase) && _port == port)
                    return;

                _ip = ip;
                _port = port;
                previousConnection = _connectionEnded?.Task;
                previousSocket = _client;
                Volatile.Write(ref _connected, false);
                // CTS luôn được xóa/dispose trong cùng lock, tránh đua với teardown.
                _connectionCancellation?.Cancel();
            }

            previousSocket?.Dispose();
            if (previousConnection is not null)
                await previousConnection.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _configurationGate.Release();
        }
    }

    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        CancellationTokenSource runCancellation;
        lock (_sync)
        {
            if (_isRunning)
            {
                throw new InvalidOperationException("Reader is already running.");
            }

            _isRunning = true;
            runCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _runCancellation = runCancellation;
        }

        try
        {
            await RunConnectionLoopAsync(runCancellation.Token).ConfigureAwait(false);
        }
        finally
        {
            CleanupSocket();
            lock (_sync)
            {
                _runCancellation = null;
                _isRunning = false;
            }
            runCancellation.Dispose();
        }
    }

    public void Disconnect()
    {
        Socket? socket;
        lock (_sync)
        {
            _runCancellation?.Cancel();
            socket = _client;
        }

        socket?.Dispose();
    }

    public async Task<bool> SendAsync(string data, CancellationToken cancellationToken = default)
    {
        Socket? socket;
        lock (_sync)
        {
            socket = _client;
        }

        if (string.IsNullOrEmpty(data) || socket == null || !Connected)
        {
            return false;
        }

        try
        {
            var bytes = Encoding.UTF8.GetBytes(data);
            var sent = 0;
            while (sent < bytes.Length)
            {
                var bytesSent = await socket.SendAsync(
                    bytes.AsMemory(sent), SocketFlags.None, cancellationToken);
                if (bytesSent == 0)
                {
                    return false;
                }

                sent += bytesSent;
            }
            return true;
        }
        catch (Exception exception) when (exception is SocketException or ObjectDisposedException)
        {
            socket.Dispose();
            return false;
        }
    }

    private async Task RunConnectionLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            string ip;
            int port;
            CancellationTokenSource connectionCancellation;
            TaskCompletionSource connectionEnded;
            lock (_sync)
            {
                ip = _ip;
                port = _port;
                connectionCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                connectionEnded = new(TaskCreationOptions.RunContinuationsAsynchronously);
                _connectionCancellation = connectionCancellation;
                _connectionEnded = connectionEnded;
            }

            try
            {
                var token = connectionCancellation.Token;
                token.ThrowIfCancellationRequested();
                var reason = await RunConnectionAsync(ip, port, token).ConfigureAwait(false);
                if (!token.IsCancellationRequested)
                {
                    OnClientCallback(eReaderState.Disconnected, reason);
                    await Task.Delay(_reconnectDelay, token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (connectionCancellation.IsCancellationRequested)
            {
                // Reconfigure hủy chỉ phiên này; vòng chính mở ngay đích mới.
            }
            finally
            {
                lock (_sync)
                {
                    _connectionCancellation = null;
                    _connectionEnded = null;
                    connectionCancellation.Dispose();
                }
                connectionEnded.TrySetResult();
            }
        }
    }

    private async Task<string> RunConnectionAsync(string ip, int port, CancellationToken cancellationToken)
    {
        Socket? socket = null;
        OnClientCallback(eReaderState.Reconnecting, "Attempting to connect...");
        try
        {
            var endPoint = await ResolveEndpointAsync(ip, port, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            SetSocket(socket);
            await socket.ConnectAsync(endPoint, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            Volatile.Write(ref _connected, true);
            OnClientCallback(eReaderState.Connected, "Connected successfully");
            return await ReceiveFramesAsync(socket, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is SocketException or ObjectDisposedException or ArgumentException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return $"Connection error: {exception.Message}";
        }
        finally
        {
            Volatile.Write(ref _connected, false);
            if (socket is not null) CleanupSocket(socket);
        }
    }

    private static async Task<IPEndPoint> ResolveEndpointAsync(string ip, int port, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ip);
        if (port is <= 0 or > 65535) throw new ArgumentOutOfRangeException(nameof(port));
        if (IPAddress.TryParse(ip, out var address))
        {
            if (address.AddressFamily != AddressFamily.InterNetwork)
                throw new ArgumentException("Camera chỉ hỗ trợ địa chỉ IPv4.", nameof(ip));
            cancellationToken.ThrowIfCancellationRequested();
            return new IPEndPoint(address, port);
        }

        var addresses = await Dns.GetHostAddressesAsync(ip, cancellationToken).ConfigureAwait(false);
        address = addresses.FirstOrDefault(candidate => candidate.AddressFamily == AddressFamily.InterNetwork)
            ?? throw new ArgumentException("Không tìm thấy địa chỉ IPv4 của đầu đọc mã.", nameof(ip));
        return new IPEndPoint(address, port);
    }

    private async Task<string> ReceiveFramesAsync(Socket socket, CancellationToken cancellationToken)
    {
        var bytes = new byte[1024];
        var chars = new char[Encoding.UTF8.GetMaxCharCount(bytes.Length)];
        var decoder = Encoding.UTF8.GetDecoder();
        var frame = new StringBuilder();

        while (true)
        {
            var received = await socket.ReceiveAsync(bytes, SocketFlags.None, cancellationToken);
            if (received == 0)
            {
                return "Connection closed by remote host.";
            }

            var charCount = decoder.GetChars(bytes.AsSpan(0, received), chars, false);
            for (var index = 0; index < charCount; index++)
            {
                if (chars[index] is '\r' or '\n')
                {
                    if (frame.Length > 0)
                    {
                        OnClientCallback(eReaderState.Received, frame.ToString());
                        frame.Clear();
                    }
                }
                else
                {
                    frame.Append(chars[index]);
                }
            }
        }
    }

    private void SetSocket(Socket socket)
    {
        lock (_sync)
        {
            _client = socket;
        }
    }

    private void CleanupSocket(Socket? expectedSocket = null)
    {
        Socket? socket;
        lock (_sync)
        {
            if (expectedSocket != null && !ReferenceEquals(_client, expectedSocket)) return;
            socket = _client;
            _client = null;
        }
        socket?.Dispose();
    }

    private void OnClientCallback(eReaderState state, string data) => ClientCallback?.Invoke(state, data);
}
