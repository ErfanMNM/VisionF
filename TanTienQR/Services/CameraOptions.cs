namespace TanTienQR.Services;

// Cấu hình đầu đọc mã (đọc từ appsettings.json, mục "Camera").
internal sealed class CameraOptions
{
    private int _duplicateWindowSeconds = 30;
    public string Ip { get; set; } = "127.0.0.1"; // IP đầu đọc
    public int Port { get; set; } = 9000;         // Port đầu đọc

    /// <summary>
    /// Cửa sổ (giây) để phát hiện mã trùng. Mã quét trong khoảng thời gian này
    /// sẽ được coi là trùng và gửi Fail xuống PLC.
    /// </summary>
    public int DuplicateWindowSeconds
    {
        get => Volatile.Read(ref _duplicateWindowSeconds);
        set => Volatile.Write(ref _duplicateWindowSeconds, value);
    }
}
