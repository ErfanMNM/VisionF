namespace TanTienQR.Services;

// Cấu hình kết nối đầu đọc; mã trùng được xác định bằng trạng thái bền vững của PO.
internal sealed class CameraOptions
{
    public string Ip { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 9000;
}
