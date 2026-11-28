namespace ClassLibraryMT.MultiConnect;

/// <summary>
/// Cấu hình gateway MultiConnect. Mỗi <see cref="MultiConnectChannelOptions"/> là một
/// cầu nối độc lập: đọc từ một nguồn (Serial / TCP client / UDP) và phát lại ra
/// một TCP server riêng để app (hoặc thiết bị khác) kết nối vào.
/// </summary>
public sealed class MultiConnectOptions
{
    /// <summary>Bật/tắt toàn bộ gateway. Mặc định tắt để không chiếm cổng khi chưa cấu hình.</summary>
    public bool Enabled { get; set; }

    /// <summary>Độ trễ trước khi thử lại sau khi một kênh bị lỗi/mất kết nối.</summary>
    public int ReconnectDelayMs { get; set; } = 3000;

    /// <summary>Độ dài tối đa của một frame text (ký tự). Vượt quá sẽ bị cắt bỏ.</summary>
    public int MaxFrameLength { get; set; } = 512;

    /// <summary>Danh sách kênh. Được bind thủ công từ <c>MultiConnect:Channels</c>.</summary>
    public List<MultiConnectChannelOptions> Channels { get; } = [];
}

/// <summary>Cấu hình một kênh chuyển tiếp.</summary>
public sealed class MultiConnectChannelOptions
{
    /// <summary>Tên kênh (chỉ để hiển thị/log).</summary>
    public string Name { get; set; } = "";

    /// <summary>Loại nguồn: <c>Serial</c>, <c>TcpClient</c> hoặc <c>Udp</c>.</summary>
    public string Type { get; set; } = "Serial";

    /// <summary>Bật/tắt riêng kênh này.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Địa chỉ IP để TCP server của kênh lắng nghe.</summary>
    public string ListenIp { get; set; } = "127.0.0.1";

    /// <summary>Port TCP server của kênh (app sẽ trỏ Camera:Port tới port này).</summary>
    public int ListenPort { get; set; }

    // ---- Serial ----
    public string PortName { get; set; } = "COM3";
    public int BaudRate { get; set; } = 9600;
    public int DataBits { get; set; } = 8;
    public string Parity { get; set; } = "None";
    public int StopBits { get; set; } = 1;

    // ---- TcpClient (ví dụ chuyển tiếp từ camera thật sang gateway) ----
    public string Host { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 9000;

    // ---- Udp ----
    public string UdpListenIp { get; set; } = "127.0.0.1";
    public int UdpListenPort { get; set; } = 9100;
}

/// <summary>Trạng thái runtime của một kênh (dùng để hiển thị/log).</summary>
public sealed class MultiConnectChannelStatus
{
    private long _framesReceived;
    private long _framesDropped;
    private int _clientsConnected;

    public string Name { get; init; } = "";
    public string Type { get; init; } = "";
    public int ListenPort { get; init; }

    public string State { get; set; } = "Stopped";
    public string? LastError { get; set; }

    public long FramesReceived => Interlocked.Read(ref _framesReceived);
    public long FramesDropped => Interlocked.Read(ref _framesDropped);
    public int ClientsConnected => Volatile.Read(ref _clientsConnected);

    public void AddFrame() => Interlocked.Increment(ref _framesReceived);
    public void AddDropped() => Interlocked.Increment(ref _framesDropped);
    public void SetClients(int count) => Volatile.Write(ref _clientsConnected, count);
}
