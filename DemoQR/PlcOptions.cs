namespace QRDemoCodeBackend;

// Cấu hình kết nối PLC Omron (đọc từ appsettings.json, mục "Plc").
internal sealed class PlcOptions
{
    public string Ip { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 9600;
    public byte RemoteNode { get; set; } = 0;   // Node PLC (DA1), thường là octet cuối IP
    public byte LocalNode { get; set; } = 1;    // Node PC (SA1)
    public int TimeoutMs { get; set; } = 1000;
    public int Retries { get; set; } = 0;

    // Địa chỉ vùng D
    public string ResultAddress { get; set; } = "D12";   // 1 = Pass, 0 = Fail
    public string ReadyAddress { get; set; } = "D16";    // 1 = Ready, 0 = chưa sẵn sàng
    public int RefreshIntervalMs { get; set; } = 1000;   // chu kỳ ghi D16
}
