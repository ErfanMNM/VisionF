using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace TanTienQR.Services;

/// <summary>
/// Lắng nghe mã quét từ máy quét cầm tay (handheld scanner).
///
/// <para>
/// Hiện tại mô phỏng handheld bằng TCP: app mở TCP server trên
/// <c>127.0.0.1:6999</c> (hoặc chủ động kết nối ra nếu
/// <see cref="HandheldOptions.Mode"/> = <see cref="HandheldMode.TcpClient"/>).
/// Mỗi khung (kết thúc bằng <c>\r</c> hoặc <c>\n</c>) được coi là một mã
/// và phát qua event <see cref="CodeReceived"/>.
/// </para>
///
/// <para>
/// Giao diện COM (cổng cắm của handheld) sẽ là một transport khác cùng
/// interface — hiện chưa triển khai, chỉ để sẵn cấu hình trong
/// <see cref="HandheldOptions"/>.
/// </para>
///
/// <para>
/// Mọi I/O chạy trên luồng nền; event được phát từ luồng I/O nên ViewModel
/// phải dispatch sang luồng UI trước khi cập nhật binding.
/// </para>
/// </summary>
public sealed class HandheldScannerService : IDisposable
{
    /// <summary>Bộ đệm đọc từ socket.</summary>
    private const int ReadBufferSize = 1024;

    /// <summary>
    /// Số ký tự tối đa cho một mã. Mã quét thường ngắn; giới hạn để tránh
    /// một client gửi dữ liệu vô hạn làm phình bộ đệm.
    /// </summary>
    private const int MaxCodeLength = 512;

    private readonly HandheldOptions _opt;
    private readonly ILogger<HandheldScannerService> _logger;
    private readonly CancellationTokenSource _cts = new();

    /// <summary>Các task đang chạy (listener / client loop).</summary>
    private readonly List<Task> _tasks = [];

    /// <summary>Cờ chống dispose 2 lần.</summary>
    private bool _disposed;

    /// <summary>Cờ đã bắt đầu chưa.</summary>
    private bool _started;

    /// <summary>Nội dung trạng thái (xem <see cref="StatusText"/>).</summary>
    private string? _statusText;

    /// <summary>
    /// Sự kiện phát ra khi nhận được một mã từ handheld.
    ///
    /// Tham số là nội dung mã đã trim.
    /// </summary>
    public event Action<string>? CodeReceived;

    /// <summary>
    /// Trạng thái hiện tại của kết nối (để hiển thị trên UI).
    ///
    /// Được ghi từ luồng I/O và đọc từ luồng UI -> dùng
    /// <see cref="Volatile"/> để đảm bảo giá trị mới nhất được thấy.
    /// </summary>
    public string StatusText => Volatile.Read(ref _statusText) ?? "Chưa bật";

    /// <summary>
    /// Khởi tạo service lắng nghe handheld.
    /// </summary>
    /// <param name="options">Cấu hình kết nối.</param>
    /// <param name="logger">Logger kỹ thuật.</param>
    public HandheldScannerService(
        IOptions<HandheldOptions> options,
        ILogger<HandheldScannerService> logger)
    {
        _opt = options.Value;
        _logger = logger;
    }

    /// <summary>
    /// Bắt đầu lắng nghe. Gọi 1 lần khi app khởi động.
    ///
    /// Không chặn: mọi thao tác I/O chạy nền.
    /// </summary>
    public void Start()
    {
        if (_disposed || _started)
        {
            return;
        }

        if (!_opt.Enabled)
        {
            SetStatus("Đã tắt");
            _logger.LogInformation("Lắng nghe handheld đang tắt theo cấu hình.");
            return;
        }

        _started = true;

        var task = _opt.Mode == HandheldMode.TcpClient
            ? RunClientAsync(_cts.Token)
            : RunServerAsync(_cts.Token);

        _tasks.Add(task);
    }

    /// <summary>Dừng lắng nghe và giải phóng tài nguyên.</summary>
    public void Stop()
    {
        if (!_started)
        {
            return;
        }

        _started = false;

        try
        {
            _cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Đã dispose rồi.
        }

        // Không chờ lâu — app đang tắt, không cần chờ hết vòng lặp socket.
        try
        {
            Task.WaitAll(_tasks.ToArray(), TimeSpan.FromSeconds(2));
        }
        catch (Exception ex) when (ex is AggregateException or OperationCanceledException)
        {
            // Bỏ qua: đây là shutdown bình thường.
        }

        _tasks.Clear();
    }

    /// <summary>Chạy TCP server: nhận nhiều client, mỗi client một task riêng.</summary>
    private async Task RunServerAsync(CancellationToken ct)
    {
        IPAddress address;

        try
        {
            if (!IPAddress.TryParse(_opt.TcpHost, out var parsed))
            {
                SetStatus($"Địa chỉ không hợp lệ: {_opt.TcpHost}");
                _logger.LogError(
                    "Handheld: địa chỉ bind không hợp lệ {Host}", _opt.TcpHost);
                return;
            }

            address = parsed;
        }
        catch (Exception ex)
        {
            SetStatus("Lỗi địa chỉ");
            _logger.LogError(ex, "Handheld: lỗi phân tích địa chỉ {Host}", _opt.TcpHost);
            return;
        }

        TcpListener? listener = null;

        try
        {
            listener = new TcpListener(address, _opt.TcpPort);
            listener.Start();

            SetStatus($"Đang lắng nghe {address}:{_opt.TcpPort}");
            _logger.LogInformation(
                "Handheld: đang lắng nghe TCP {Host}:{Port}", address, _opt.TcpPort);

            while (!ct.IsCancellationRequested)
            {
                TcpClient client;

                try
                {
                    client = await listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                catch (SocketException ex)
                {
                    _logger.LogWarning(ex, "Handheld: lỗi khi chấp nhận kết nối.");
                    continue;
                }

                // Mỗi client một task — không chặn vòng lặp accept.
                _tasks.Add(HandleClientAsync(client, ct));
            }
        }
        catch (OperationCanceledException)
        {
            // Shutdown bình thường.
        }
        catch (Exception ex)
        {
            SetStatus("Lỗi listener");
            _logger.LogError(ex, "Handheld: lỗi TCP server.");
        }
        finally
        {
            listener?.Stop();
        }
    }

    /// <summary>Chạy TCP client: kết nối ra handheld rồi đọc mã liên tục.</summary>
    private async Task RunClientAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (!IPAddress.TryParse(_opt.TcpHost, out var address))
                {
                    SetStatus($"Địa chỉ không hợp lệ: {_opt.TcpHost}");
                    _logger.LogError("Handheld: địa chỉ không hợp lệ {Host}", _opt.TcpHost);
                    return;
                }

                using var client = new TcpClient();

                await client.ConnectAsync(address, _opt.TcpPort, ct).ConfigureAwait(false);

                SetStatus($"Đã kết nối {address}:{_opt.TcpPort}");
                _logger.LogInformation(
                    "Handheld: đã kết nối tới {Host}:{Port}", address, _opt.TcpPort);

                await ReadLoopAsync(client, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                SetStatus("Mất kết nối");
                _logger.LogWarning(ex, "Handheld: lỗi kết nối client.");
            }

            if (ct.IsCancellationRequested)
            {
                break;
            }

            // Thử lại sau 3 giây.
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(3), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>Xử lý một client kết nối (chỉ dùng cho chế độ server).</summary>
    private async Task HandleClientAsync(TcpClient client, CancellationToken ct)
    {
        try
        {
            using (client)
            {
                await ReadLoopAsync(client, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Shutdown bình thường.
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Handheld: lỗi khi đọc dữ liệu từ handheld.");
        }
    }

    /// <summary>
    /// Đọc dữ liệu từ client và tách thành từng mã (kết thúc bằng CR/LF).
    /// </summary>
    private async Task ReadLoopAsync(TcpClient client, CancellationToken ct)
    {
        var stream = client.GetStream();
        var buffer = new byte[ReadBufferSize];
        var decoder = Encoding.UTF8.GetDecoder();
        var chars = new char[Encoding.UTF8.GetMaxCharCount(buffer.Length)];
        var frame = new StringBuilder();

        while (!ct.IsCancellationRequested)
        {
            int received;

            try
            {
                received = await stream.ReadAsync(buffer, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or SocketException)
            {
                break;
            }

            if (received <= 0)
            {
                // Client đóng kết nối.
                break;
            }

            var charCount = decoder.GetChars(buffer.AsSpan(0, received), chars, false);

            for (var i = 0; i < charCount; i++)
            {
                var c = chars[i];

                if (c is '\r' or '\n')
                {
                    EmitFrame(frame);
                    continue;
                }

                // Bỏ ký tự điều khiển (ASCII < 0x20 trừ CR/LF) — handheld có thể
                // gửi ký tự NUL/BEL kèm theo.
                if (c < ' ')
                {
                    continue;
                }

                frame.Append(c);

                // Chặn độ dài quá lớn -> coi như mã hỏng, bỏ khung.
                if (frame.Length > MaxCodeLength)
                {
                    frame.Clear();
                }
            }
        }

        // Client đóng kết nối giữa chừng -> vẫn phát nốt khung dở nếu có.
        EmitFrame(frame);
    }

    /// <summary>Phát một khung mã hoàn chỉnh (nếu không rỗng).</summary>
    private void EmitFrame(StringBuilder frame)
    {
        if (frame.Length == 0)
        {
            return;
        }

        var code = frame.ToString().Trim();
        frame.Clear();

        if (code.Length == 0)
        {
            return;
        }

        _logger.LogInformation("Handheld nhận mã: {Code}", code);

        try
        {
            CodeReceived?.Invoke(code);
        }
        catch (Exception ex)
        {
            // Handler lỗi không được làm chết vòng đọc socket.
            _logger.LogError(ex, "Handheld: lỗi trong handler nhận mã.");
        }
    }

    /// <summary>Ghi trạng thái (an toàn khi gọi từ luồng I/O).</summary>
    private void SetStatus(string text) => Volatile.Write(ref _statusText, text);

    /// <summary>Giải phóng tài nguyên.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        Stop();

        try
        {
            _cts.Dispose();
        }
        catch (ObjectDisposedException)
        {
            // Đã dispose rồi.
        }

        GC.SuppressFinalize(this);
    }
}
