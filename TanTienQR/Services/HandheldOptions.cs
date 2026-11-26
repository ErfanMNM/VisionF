namespace TanTienQR.Services;

/// <summary>
/// Cách kết nối với máy quét cầm tay (handheld scanner).
/// </summary>
public enum HandheldMode
{
    /// <summary>
    /// App chạy TCP server, chờ handheld/emulator kết nối vào
    /// (giữ tương thích công cụ mô phỏng cũ).
    /// </summary>
    TcpServer = 0,

    /// <summary>
    /// App chủ động kết nối ra ngoài tới handheld/emulator
    /// (dùng khi handheld ở chế độ client, ví dụ qua WiFi).
    /// </summary>
    TcpClient = 1,

    /// <summary>Máy quét thật qua COM, 8 data bits, no parity, 1 stop bit.</summary>
    Serial = 2,
}

/// <summary>
/// Cấu hình kết nối máy quét cầm tay.
///
/// Bind thủ công trong <c>TanTienAppServices</c> (không dùng
/// <c>ConfigurationBinder</c>) để tương thích Native AOT.
/// </summary>
public sealed class HandheldOptions
{
    /// <summary>Tên section cấu hình trong appsettings.json.</summary>
    public const string SectionName = "Scanner";

    /// <summary>
    /// Bật/tắt việc lắng nghe handheld.
    ///
    /// Tắt mặc định khi cấu hình sai để app không báo lỗi khởi động.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Chế độ kết nối (COM mặc định, TCP tương thích).</summary>
    public HandheldMode Mode { get; set; } = HandheldMode.Serial;

    /// <summary>Địa chỉ IP dùng cho chế độ client (hoặc địa chỉ bind của server).</summary>
    public string TcpHost { get; set; } = "127.0.0.1";

    /// <summary>Cổng TCP (mặc định 6999).</summary>
    public int TcpPort { get; set; } = 6999;

    /// <summary>
    /// Cổng COM của handheld, chỉ mở khi tab nhận mã đang hoạt động.
    /// </summary>
    public string ComPort { get; set; } = "COM3";

    /// <summary>Tốc độ baudrate cho cổng COM.</summary>
    public int BaudRate { get; set; } = 9600;
}
