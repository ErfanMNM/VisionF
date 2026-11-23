using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging;

namespace ClassLibraryMT.MultiConnect;

/// <summary>
/// TCP server của một kênh: chấp nhận NHIỀU client cùng lúc và phát (fan-out)
/// mỗi frame text tới tất cả client. Frame được đóng gói UTF-8 + CRLF để khớp
/// protocol mà <c>CameraSignalWorker</c> đang đọc.
/// </summary>
internal sealed class TcpFanoutServer
{
    private sealed class Client
    {
        public required TcpClient Tcp { get; init; }
        public required NetworkStream Stream { get; init; }
        public SemaphoreSlim WriteLock { get; } = new(1, 1);
    }

    private readonly TcpListener _listener;
    private readonly string _name;
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<long, Client> _clients = new();
    private long _nextId;

    public TcpFanoutServer(string listenIp, int listenPort, string name, ILogger logger)
    {
        _name = name;
        _logger = logger;
        var ip = IPAddress.TryParse(listenIp, out var parsed) ? parsed : IPAddress.Loopback;
        _listener = new TcpListener(ip, listenPort);
    }

    /// <summary>Số client đang kết nối.</summary>
    public int ClientCount => _clients.Count;

    /// <summary>
    /// Chạy vòng lặp accept cho tới khi <paramref name="ct"/> bị hủy.
    /// </summary>
    public async Task RunAsync(CancellationToken ct)
    {
        _listener.Start();
        _logger.LogInformation("[{Channel}] TCP server dang lang nghe {Endpoint}",
            _name, _listener.LocalEndpoint);

        try
        {
            while (!ct.IsCancellationRequested)
            {
                TcpClient tcp;
                try
                {
                    tcp = await _listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }

                var id = Interlocked.Increment(ref _nextId);
                var client = new Client
                {
                    Tcp = tcp,
                    Stream = tcp.GetStream(),
                };
                _clients[id] = client;
                _logger.LogInformation("[{Channel}] Client {Id} ket noi ({Count} client)",
                    _name, id, _clients.Count);

                _ = Task.Run(() => ClientLoopAsync(id, client, ct), ct);
            }
        }
        finally
        {
            foreach (var pair in _clients)
            {
                DisposeClient(pair.Value);
            }
            _clients.Clear();
            _listener.Stop();
            _logger.LogInformation("[{Channel}] TCP server da dung", _name);
        }
    }

    /// <summary>Phát một frame tới mọi client. Không ném lỗi ra ngoài.</summary>
    public async Task BroadcastAsync(string frame, CancellationToken ct)
    {
        if (_clients.IsEmpty)
        {
            return;
        }

        var payload = Encoding.UTF8.GetBytes(frame + "\r\n");
        foreach (var pair in _clients)
        {
            if (ct.IsCancellationRequested)
            {
                return;
            }

            var client = pair.Value;
            try
            {
                await client.WriteLock.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    await client.Stream.WriteAsync(payload, ct).ConfigureAwait(false);
                    await client.Stream.FlushAsync(ct).ConfigureAwait(false);
                }
                finally
                {
                    client.WriteLock.Release();
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or SocketException)
            {
                _logger.LogWarning("[{Channel}] Gui frame cho client {Id} that bai: {Message}",
                    _name, pair.Key, ex.Message);
                if (_clients.TryRemove(pair.Key, out var dead))
                {
                    DisposeClient(dead);
                }
            }
        }
    }

    private async Task ClientLoopAsync(long id, Client client, CancellationToken ct)
    {
        var buffer = new byte[256];
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var read = await client.Stream.ReadAsync(buffer, ct).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Bình thường khi gateway tắt.
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or SocketException)
        {
            _logger.LogDebug("[{Channel}] Client {Id} mat ket noi: {Message}", _name, id, ex.Message);
        }
        finally
        {
            if (_clients.TryRemove(id, out var removed))
            {
                DisposeClient(removed);
            }
            _logger.LogInformation("[{Channel}] Client {Id} ngat ({Count} client)",
                _name, id, _clients.Count);
        }
    }

    private static void DisposeClient(Client client)
    {
        try
        {
            client.Tcp.Dispose();
        }
        catch
        {
            // Bỏ qua lỗi khi đóng socket đã hỏng.
        }
        finally
        {
            client.WriteLock.Dispose();
        }
    }
}
