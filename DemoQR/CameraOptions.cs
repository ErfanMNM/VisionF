namespace QRDemoCodeBackend;

// Cấu hình đầu đọc mã (đọc từ appsettings.json, mục "Camera").
internal sealed class CameraOptions
{
    public string Ip { get; set; } = "127.0.0.1"; // IP đầu đọc
    public int Port { get; set; } = 9000;         // Port đầu đọc
}
