using System.Text.Json.Serialization;
using TanTienQR.Services.Config;

namespace TanTienQR.Services;

// ---------------------------------------------------------------- Model
// Cấu trúc dữ liệu tương ứng với file appsettings.json.
//
// Dùng class với property cụ thể (không dùng Dictionary) để
// System.Text.Json sinh code AOT-safe từ JsonSerializerContext.
//
// Mỗi section gắn [ConfigSection] và mỗi property gắn [ConfigField] để
// form Cấu hình được SINH ĐỘNG (xem ConfigSchemaProvider). Thêm setting mới
// chỉ cần: thêm property + [ConfigField] (+ default trong appsettings.json).
/// <summary>Cấu hình đầu đọc mã (mục "Camera").</summary>
[ConfigSection(
    Key = "Camera",
    Title = "CAMERA · ĐẦU ĐỌC MÃ",
    Order = 10,
    Icon = "Camera",
    Description = "Kết nối TCP tới đầu đọc mã. Xác nhận khi lưu để áp dụng ngay, không cần khởi động lại.")]
public sealed class CameraConfig
{
    [ConfigField("IP đầu đọc", Order = 1, Placeholder = "VD: 192.168.1.50", RequiresRestart = false, Validation = "IpOrHost", HelpText = "Địa chỉ IPv4 hoặc hostname của đầu đọc mã.")]
    [JsonPropertyName("Ip")] public string Ip { get; set; } = "127.0.0.1";

    [ConfigField("Port TCP", Order = 2, Min = 1, Max = 65535, RequiresRestart = false)]
    [JsonPropertyName("Port")] public int Port { get; set; } = 9000;

}

/// <summary>Cấu hình PLC Omron (mục "Plc").</summary>
[ConfigSection(
    Key = "Plc",
    Title = "PLC · OMERON FINS/UDP",
    Order = 20,
    Icon = "CpuChip",
    Description = "Kết nối FINS/UDP tới PLC. Xác nhận khi lưu để áp dụng ngay, không cần khởi động lại.")]
public sealed class PlcConfig
{
    [ConfigField("Bật PLC", Order = 0, RequiresRestart = false)]
    [JsonPropertyName("Enabled")] public bool Enabled { get; set; } = true;

    [ConfigField("IP PLC", Order = 1, Placeholder = "VD: 192.168.1.10", RequiresRestart = false, Validation = "IpOrHost")]
    [JsonPropertyName("Ip")] public string Ip { get; set; } = "127.0.0.1";

    [ConfigField("Port FINS", Order = 2, Min = 1, Max = 65535, RequiresRestart = false)]
    [JsonPropertyName("Port")] public int Port { get; set; } = 9600;

    [ConfigField("Remote node (DA1)", Order = 3, Min = 0, Max = 255, RequiresRestart = false)]
    [JsonPropertyName("RemoteNode")] public int RemoteNode { get; set; }

    [ConfigField("Local node (SA1)", Order = 4, Min = 0, Max = 255, RequiresRestart = false)]
    [JsonPropertyName("LocalNode")] public int LocalNode { get; set; } = 1;

    [ConfigField("Timeout (ms)", Order = 5, Min = 1, Max = 60000, RequiresRestart = false)]
    [JsonPropertyName("TimeoutMs")] public int TimeoutMs { get; set; } = 1000;

    [ConfigField("Số lần thử lại", Order = 6, Min = 0, Max = 10, RequiresRestart = false)]
    [JsonPropertyName("Retries")] public int Retries { get; set; }

    [ConfigField("Kết quả (1/0)", Order = 7, Placeholder = "VD: D12", RequiresRestart = false, HelpText = "D12 = 1 khi đọc mã hợp lệ, D12 = 0 khi NoRead / FAIL / sai cấu trúc / không tồn tại trong PO / trùng mã.")]
    [JsonPropertyName("ResultAddress")] public string ResultAddress { get; set; } = "D12";

    [ConfigField("Ready (1/0)", Order = 8, Placeholder = "VD: D16", RequiresRestart = false, HelpText = "D16 = 1 mỗi chu kỳ khi camera + PLC + license đều sẵn sàng.")]
    [JsonPropertyName("ReadyAddress")] public string ReadyAddress { get; set; } = "D16";

    [ConfigField("Chu kỳ heartbeat (ms)", Order = 9, Min = 100, Max = 60000, RequiresRestart = false)]
    [JsonPropertyName("RefreshIntervalMs")] public int RefreshIntervalMs { get; set; } = 1000;

    [ConfigField("Bộ đếm PLC (Int32 × 5)", Order = 10, Validation = "PlcCounterAddress", RequiresRestart = false, HelpText = "Word bắt đầu khối Total, ReadFail, Pass, dự phòng, Timeout. Mỗi số Int32 chiếm 2 word; Fail = ReadFail + Timeout.")]
    [JsonPropertyName("CounterAddress")] public string CounterAddress { get; set; } = "D100";

    [ConfigField("Lệnh xóa số PLC", Order = 11, Validation = "PlcWordAddress", Required = false, RequiresRestart = false, HelpText = "Word reset thực tế của máy: ghi Int16 = 1 theo TApp. Để trống để khóa nút xóa; không tự xóa số trên ứng dụng.")]
    [JsonPropertyName("ResetCounterAddress")] public string ResetCounterAddress { get; set; } = string.Empty;

}

/// <summary>Cấu hình trang web nhúng (mục "Web").</summary>
[ConfigSection(
    Key = "Web",
    Title = "WEB · CHECKEE",
    Order = 50,
    Icon = "Link",
    Description = "Trang web nhúng trong tab Sản xuất.")]
public sealed class WebConfig
{
    [ConfigField("URL trang", Order = 1, Placeholder = "https://estrace.checkee.vn", RequiresRestart = true, Validation = "Url", SkipWhenDisabledBy = "Web:Enabled")]
    [JsonPropertyName("Url")] public string Url { get; set; } = "https://estrace.checkee.vn";

    [ConfigField("Cho phép mở web", Order = 2, RequiresRestart = true, HelpText = "Bật tab Sản xuất.")]
    [JsonPropertyName("Enabled")] public bool Enabled { get; set; } = true;
}

/// <summary>
/// Cấu hình máy quét cầm tay (mục "Scanner").
///
/// Khớp với <c>HandheldOptions</c> — chế độ TCP server/client, host/port,
/// cổng COM và baud rate. Cấu hình này được bind một lần khi app khởi động
/// nên thay đổi cần restart ứng dụng.
/// </summary>
[ConfigSection(
    Key = "Scanner",
    Title = "MÁY QUÉT CẦM TAY · SCANNER",
    Order = 30,
    Icon = "Scan",
    Description = "Máy quét cầm tay kết nối TCP hoặc COM. Được nạp một lần khi app khởi động.")]
public sealed class ScannerConfig
{
    [ConfigField("Bật máy quét", Order = 1, RequiresRestart = true, HelpText = "Lắng nghe / kết nối thiết bị quét.")]
    [JsonPropertyName("Enabled")] public bool Enabled { get; set; } = true;

    [ConfigField("Chế độ", Order = 2, Options = new[] { "TcpServer", "TcpClient" }, RequiresRestart = true)]
    [JsonPropertyName("Mode")] public string Mode { get; set; } = "TcpServer";

    [ConfigField("Host TCP", Order = 3, Placeholder = "VD: 127.0.0.1", RequiresRestart = true, Validation = "IpOrHost", SkipWhenDisabledBy = "Scanner:Enabled")]
    [JsonPropertyName("TcpHost")] public string TcpHost { get; set; } = "127.0.0.1";

    [ConfigField("Port TCP", Order = 4, Min = 1, Max = 65535, RequiresRestart = true, SkipWhenDisabledBy = "Scanner:Enabled")]
    [JsonPropertyName("TcpPort")] public int TcpPort { get; set; } = 6999;

    [ConfigField("Cổng COM", Order = 5, Placeholder = "VD: COM3", RequiresRestart = true, SkipWhenDisabledBy = "Scanner:Enabled")]
    [JsonPropertyName("ComPort")] public string ComPort { get; set; } = "COM3";

    [ConfigField("Baud rate", Order = 6, Min = 1200, Max = 115200, Increment = 1200, RequiresRestart = true, SkipWhenDisabledBy = "Scanner:Enabled")]
    [JsonPropertyName("BaudRate")] public int BaudRate { get; set; } = 9600;
}

/// <summary>
/// Cấu hình gateway MultiConnect (mục "MultiConnect").
/// Danh sách <c>Channels</c> là mảng object nên không hiển thị trên form Cấu hình;
/// ConfigStore vẫn giữ nguyên key này khi lưu (unknown-key merge).
/// </summary>
[ConfigSection(
    Key = "MultiConnect",
    Title = "MULTI CONNECT · CHUYỂN GIAO THỨC SANG TCP",
    Order = 35,
    Icon = "Link",
    Description = "Serial / TCP client / UDP sang TCP server. Được nạp một lần khi app khởi động.")]
public sealed class MultiConnectConfig
{
    [ConfigField("Bật gateway", Order = 1, RequiresRestart = true,
        HelpText = "Bật các kênh chuyển tiếp đã khai báo trong MultiConnect:Channels.")]
    [JsonPropertyName("Enabled")] public bool Enabled { get; set; }

    [ConfigField("Trễ kết nối lại (ms)", Order = 2, Min = 200, Max = 60000, Increment = 100, RequiresRestart = true, SkipWhenDisabledBy = "MultiConnect:Enabled")]
    [JsonPropertyName("ReconnectDelayMs")] public int ReconnectDelayMs { get; set; } = 3000;

    [ConfigField("Dài frame tối đa", Order = 3, Min = 32, Max = 8192, RequiresRestart = true, SkipWhenDisabledBy = "MultiConnect:Enabled")]
    [JsonPropertyName("MaxFrameLength")] public int MaxFrameLength { get; set; } = 512;
}

/// <summary>
/// Cấu hình server quản lý Production Order (mục "Po").
///
/// Khớp với <c>PoOptions</c>: địa chỉ server, thư mục DB, chu kỳ gửi mã
/// kích hoạt và số mã mẫu sinh cho mỗi PO.
/// </summary>
[ConfigSection(
    Key = "Po",
    Title = "SERVER PRODUCTION ORDER · PO",
    Order = 40,
    Icon = "Clipboard",
    Description = "Kết nối server quản lý Production Order. Được nạp một lần khi app khởi động.")]
public sealed class PoConfig
{
    [ConfigField("Địa chỉ server", Order = 1, Placeholder = "VD: http://127.0.0.1:5080", RequiresRestart = true, Validation = "Url")]
    [JsonPropertyName("BaseUrl")] public string BaseUrl { get; set; } = "http://127.0.0.1:5080";

    [ConfigField("Thư mục lưu PO", Order = 2, Placeholder = "VD: logs/po", RequiresRestart = true)]
    [JsonPropertyName("DbRoot")] public string DbRoot { get; set; } = "logs/po";

    [ConfigField("Chu kỳ kích hoạt (ms)", Order = 3, Min = 200, Max = 600000, Increment = 100, RequiresRestart = true)]
    [JsonPropertyName("ActivationIntervalMs")] public int ActivationIntervalMs { get; set; } = 2500;

    [ConfigField("Tự động kích hoạt", Order = 4, RequiresRestart = true, HelpText = "Gửi mã kích hoạt định kỳ.")]
    [JsonPropertyName("AutoActivate")] public bool AutoActivate { get; set; } = true;

    [ConfigField("Số mã mẫu / PO", Order = 5, Min = 0, Max = 10000, RequiresRestart = true)]
    [JsonPropertyName("CodeSampleSize")] public int CodeSampleSize { get; set; } = 100;
}

/// <summary>Cấu hình máy in G10 mặc định.</summary>
[ConfigSection(Key = "Print", Title = "MÁY IN · G10", Order = 48, Icon = "Printer",
    Description = "Cấu hình máy in và lô gửi. Thay đổi kết nối cần khởi động lại.")]
public sealed class PrintConfig
{
    [ConfigField("Bật module máy in", Order = 1, RequiresRestart = true)]
    [JsonPropertyName("Enabled")] public bool Enabled { get; set; }

    [ConfigField("MFD_NAME trên máy in", Order = 2, Required = true, RequiresRestart = true)]
    [JsonPropertyName("MfdName")] public string MfdName { get; set; } = "POD01";

    [ConfigField("Mã máy in mặc định", Order = 3, Required = true, RequiresRestart = true)]
    [JsonPropertyName("DefaultPrinterId")] public string DefaultPrinterId { get; set; } = "g10-1";

    [ConfigField("Tên máy in", Order = 4, Required = true, RequiresRestart = true)]
    [JsonPropertyName("PrinterName")] public string PrinterName { get; set; } = "Máy in G10";

    [ConfigField("IP máy in", Order = 5, Validation = "IpOrHost", RequiresRestart = true)]
    [JsonPropertyName("Ip")] public string Ip { get; set; } = "192.168.10.2";

    [ConfigField("Port TCP", Order = 6, Min = 1, Max = 65535, RequiresRestart = true)]
    [JsonPropertyName("Port")] public int Port { get; set; } = 6007;

    [ConfigField("Số mã tối đa / lần gửi", Order = 7, Min = 1, Max = 1000, RequiresRestart = true)]
    [JsonPropertyName("BatchSize")] public int BatchSize { get; set; } = 100;

    [ConfigField("Dung lượng buffer máy in", Order = 8, Min = 1, Max = 1000, RequiresRestart = true)]
    [JsonPropertyName("BufferCapacity")] public int BufferCapacity { get; set; } = 100;

    [ConfigField("Timeout phản hồi (ms)", Order = 9, Min = 250, Max = 30000, RequiresRestart = true)]
    [JsonPropertyName("ResponseTimeoutMs")] public int ResponseTimeoutMs { get; set; } = 5000;

    [ConfigField("Chu kỳ đối soát (ms)", Order = 10, Min = 1000, Max = 60000, RequiresRestart = true)]
    [JsonPropertyName("ReconcileIntervalMs")] public int ReconcileIntervalMs { get; set; } = 2500;

    [JsonPropertyName("Remote")] public PrinterRemoteConfig Remote { get; set; } = new();
}

/// <summary>Cấu hình màn hình máy in; mật khẩu được lưu riêng trong kho Windows.</summary>
[ConfigSection(Key = "PrinterRemote", Title = "MÁY IN · ĐIỀU KHIỂN TỪ XA", Order = 49, Icon = "Printer",
    Description = "Nhúng màn hình RDP trong tab Máy in trên Windows. Lưu thay đổi rồi khởi động lại ứng dụng.")]
public sealed class PrinterRemoteConfig
{
    [ConfigField("Bật màn hình máy in", Order = 1, RequiresRestart = true)]
    [JsonPropertyName("Enabled")] public bool Enabled { get; set; }

    [ConfigField("Host RDP", Order = 2, Required = false, Validation = "IpOrHost", RequiresRestart = true,
        SkipWhenDisabledBy = "Print:Remote:Enabled", Placeholder = "Để trống để dùng IP máy in đang chọn")]
    [JsonPropertyName("Host")] public string Host { get; set; } = "";

    [ConfigField("Cổng RDP", Order = 3, Min = 1, Max = 65535, RequiresRestart = true, SkipWhenDisabledBy = "Print:Remote:Enabled")]
    [JsonPropertyName("Port")] public int Port { get; set; } = 3389;

    [ConfigField("Tài khoản máy in", Order = 4, Required = true, RequiresRestart = true, SkipWhenDisabledBy = "Print:Remote:Enabled",
        HelpText = "Tài khoản RDP của máy in. Nhập và lưu mật khẩu tại tab Máy in.")]
    [JsonPropertyName("Username")] public string Username { get; set; } = "";

    [ConfigField("Tên lưu thông tin đăng nhập", Order = 5, Required = false, RequiresRestart = true,
        SkipWhenDisabledBy = "Print:Remote:Enabled", HelpText = "Để trống để dùng tên theo mã máy in. Không nhập mật khẩu vào đây.")]
    [JsonPropertyName("CredentialTarget")] public string CredentialTarget { get; set; } = "";

    [ConfigField("Tự kết nối khi mở tab", Order = 6, RequiresRestart = true)]
    [JsonPropertyName("AutoConnect")] public bool AutoConnect { get; set; } = true;

    [ConfigField("Timeout kết nối (giây)", Order = 7, Min = 3, Max = 120, RequiresRestart = true, SkipWhenDisabledBy = "Print:Remote:Enabled")]
    [JsonPropertyName("ConnectTimeoutSeconds")] public int ConnectTimeoutSeconds { get; set; } = 15;
}

/// <summary>
/// Cấu hình kết nối Checkee Middleware API (mục "Checkee").
///
/// Khớp với <c>ClassLibraryMT.Checkee.CheckeeOptions</c> — địa chỉ host,
/// API key / client id, chu kỳ gửi mã (ms), số mã mỗi batch và các thông số
/// phụ (GTIN / loại mã / mã lô).
/// </summary>
[ConfigSection(
    Key = "Checkee",
    Title = "CHECKEE · GỬI MÃ KÍCH HOẠT",
    Order = 45,
    Icon = "Upload",
    Description = "Kết nối Checkee Middleware API để gửi mã đã quét. Được nạp một lần khi app khởi động.")]
public sealed class CheckeeConfig
{
    [ConfigField("Địa chỉ host", Order = 1, Placeholder = "VD: https://beta-api-eco.checkee.vn", RequiresRestart = true, Validation = "Url")]
    [JsonPropertyName("BaseUrl")] public string BaseUrl { get; set; } = "https://beta-api-eco.checkee.vn";

    [ConfigField("API key", Order = 2, Control = ConfigControlKind.Password, Required = false, RequiresRestart = true, HelpText = "API Key do Checkee cấp cho doanh nghiệp (header x-api-key).")]
    [JsonPropertyName("ApiKey")] public string ApiKey { get; set; } = string.Empty;

    [ConfigField("Client ID", Order = 3, Control = ConfigControlKind.Password, Required = false, RequiresRestart = true, HelpText = "Client Key / Client ID do Checkee cấp (header x-client-id).")]
    [JsonPropertyName("ClientId")] public string ClientId { get; set; } = string.Empty;

    [ConfigField("Bật gửi mã", Order = 4, RequiresRestart = true, HelpText = "Bật worker gửi mã đã quét lên Checkee theo chu kỳ.")]
    [JsonPropertyName("Enabled")] public bool Enabled { get; set; } = true;

    [ConfigField("Chu kỳ gửi (ms)", Order = 5, Min = 100, Max = 86400000, RequiresRestart = true, HelpText = "Số mili-giây giữa hai lần gửi mã lên Checkee (mặc định 1000 ms).")]
    [JsonPropertyName("SendIntervalMs")] public int SendIntervalMs { get; set; } = 1000;

    [ConfigField("Số mã / lần gửi", Order = 6, Min = 1, Max = 500, RequiresRestart = true, HelpText = "Số mã tối đa gửi trong một chu kỳ.")]
    [JsonPropertyName("BatchSize")] public int BatchSize { get; set; } = 100;

    [ConfigField("Timeout (giây)", Order = 7, Min = 5, Max = 600, RequiresRestart = true)]
    [JsonPropertyName("TimeoutSeconds")] public int TimeoutSeconds { get; set; } = 30;

    [ConfigField("Chỉ gửi mã Pass", Order = 8, RequiresRestart = true, HelpText = "Chỉ gửi những mã quét có kết quả hợp lệ.")]
    [JsonPropertyName("OnlyPassCodes")] public bool OnlyPassCodes { get; set; } = true;

    [ConfigField("Mã lô (batch code)", Order = 9, Placeholder = "VD: LOT-2026-01", Required = false, RequiresRestart = true, HelpText = "Mã lô mặc định gửi kèm khi kích hoạt mã; có thể để trống để dùng thông tin từ PO.")]
    [JsonPropertyName("BatchCode")] public string BatchCode { get; set; } = string.Empty;

    [ConfigField("Loại mã TXNG", Order = 10, Options = new[] { "DECLARE", "RANDOM", "INCREASING" }, RequiresRestart = true)]
    [JsonPropertyName("SerialType")] public string SerialType { get; set; } = "DECLARE";

    [ConfigField("GTIN gửi Checkee", Order = 11, Required = false, RequiresRestart = true, HelpText = "Metadata riêng cho API ngoài; không dùng nhận diện mã sản xuất. CodeContent được gửi nguyên văn trong serialNumbers. Thiếu GTIN giữ mã Pending.")]
    [JsonPropertyName("ProductGtin")] public string ProductGtin { get; set; } = string.Empty;

}

/// <summary>
/// Cấu hình HTTP server nhận Production Order từ MES (mục "PoServer").
///
/// Khớp với <c>ClassLibraryMT.PoServer.PoServerOptions</c> — bật/tắt, cổng,
/// địa chỉ lắng nghe, API key và dung lượng body tối đa.
/// </summary>
[ConfigSection(
    Key = "PoServer",
    Title = "NHẬN PO TỪ MES · HTTP SERVER",
    Order = 42,
    Icon = "Download",
    Description = "Cho phép MES đẩy lệnh sản xuất xuống máy qua HTTP POST /api/po. Được nạp một lần khi app khởi động.")]
public sealed class PoServerConfig
{
    [ConfigField("Bật server nhận PO", Order = 1, RequiresRestart = true, HelpText = "Bật để MES gửi PO trực tiếp xuống máy (POST /api/po).")]
    [JsonPropertyName("Enabled")] public bool Enabled { get; set; }

    [ConfigField("Cổng HTTP", Order = 2, Min = 1, Max = 65535, RequiresRestart = true, SkipWhenDisabledBy = "PoServer:Enabled", HelpText = "Cổng mà MES sẽ gọi, ví dụ 8085.")]
    [JsonPropertyName("Port")] public int Port { get; set; } = 8085;

    [ConfigField("Địa chỉ lắng nghe", Order = 3, Placeholder = "VD: 127.0.0.1 hoặc 0.0.0.0", RequiresRestart = true, SkipWhenDisabledBy = "PoServer:Enabled", HelpText = "127.0.0.1 = chỉ máy cục bộ. 0.0.0.0 = mọi máy trong mạng (cần quyền quản trị + mở firewall).")]
    [JsonPropertyName("BindAddress")] public string BindAddress { get; set; } = "127.0.0.1";

    [ConfigField("API key", Order = 4, Control = ConfigControlKind.Password, Required = false, RequiresRestart = true, SkipWhenDisabledBy = "PoServer:Enabled", HelpText = "MES gửi kèm header x-api-key. Để trống = không kiểm tra.")]
    [JsonPropertyName("ApiKey")] public string ApiKey { get; set; } = string.Empty;

    [ConfigField("Body tối đa (MB)", Order = 5, Min = 1, Max = 1024, RequiresRestart = true, SkipWhenDisabledBy = "PoServer:Enabled", HelpText = "Giới hạn dung lượng body của một lần gửi PO.")]
    [JsonPropertyName("MaxBodyMB")] public int MaxBodyMB { get; set; } = 64;

    [ConfigField("Timeout (giây)", Order = 6, Min = 5, Max = 3600, RequiresRestart = true, SkipWhenDisabledBy = "PoServer:Enabled", HelpText = "Thời gian chờ tối đa cho một lần gửi PO.")]
    [JsonPropertyName("RequestTimeoutSeconds")] public int RequestTimeoutSeconds { get; set; } = 120;

    [ConfigField("Số request đồng thời", Order = 7, Min = 1, Max = 64, RequiresRestart = true, SkipWhenDisabledBy = "PoServer:Enabled")]
    [JsonPropertyName("MaxConcurrentRequests")] public int MaxConcurrentRequests { get; set; } = 4;
}

/// <summary>Cấu hình plugin giả lập MES gửi PO để test tích hợp.</summary>
[ConfigSection(
    Key = "MesSimulator",
    Title = "GIẢ LẬP MES · GỬI PO TEST",
    Order = 43,
    Icon = "Upload",
    Description = "Upload một file CodeContent qua HTTP POST /api/po khi khởi động; không tự sinh mã.")]
public sealed class MesSimulatorConfig
{
    [ConfigField("Bật giả lập MES", Order = 1, RequiresRestart = true, HelpText = "Gửi file PO một lần khi khởi động. Cần chọn file CodeContent và cấu hình đúng TargetUrl/API key.")]
    [JsonPropertyName("Enabled")] public bool Enabled { get; set; }

    [ConfigField("Địa chỉ HTTP đích", Order = 2, Placeholder = "VD: http://127.0.0.1:8085", RequiresRestart = true, Validation = "Url", SkipWhenDisabledBy = "MesSimulator:Enabled")]
    [JsonPropertyName("TargetUrl")] public string TargetUrl { get; set; } = "http://127.0.0.1:8085";

    [ConfigField("API key", Order = 3, Control = ConfigControlKind.Password, Required = false, RequiresRestart = true, SkipWhenDisabledBy = "MesSimulator:Enabled")]
    [JsonPropertyName("ApiKey")] public string ApiKey { get; set; } = string.Empty;

    [ConfigField("Chu kỳ gửi cũ (giây)", Order = 4, Min = 1, Max = 86400, RequiresRestart = true, SkipWhenDisabledBy = "MesSimulator:Enabled", HelpText = "Giữ để đọc cấu hình cũ; file hiện chỉ được upload một lần khi khởi động.")]
    [JsonPropertyName("IntervalSeconds")] public int IntervalSeconds { get; set; } = 30;

    [ConfigField("Số dòng mã trong file", Order = 5, Min = 1, Max = 100000, RequiresRestart = true, SkipWhenDisabledBy = "MesSimulator:Enabled", HelpText = "orderQty phải bằng số dòng CodeContent trước lọc trùng.")]
    [JsonPropertyName("OrderQty")] public int OrderQty { get; set; } = 10;

    [ConfigField("Tiền tố PO cũ", Order = 7, RequiresRestart = true, SkipWhenDisabledBy = "MesSimulator:Enabled", HelpText = "Giữ để đọc cấu hình cũ; không tự sinh tên PO hoặc mã.")]
    [JsonPropertyName("PoPrefix")] public string PoPrefix { get; set; } = "MES-TEST";

    [ConfigField("File PO có sẵn", Order = 8, Control = ConfigControlKind.Path, Required = true, Placeholder = "Bắt buộc khi bật; CSV/JSON/XLSX một cột CodeContent", RequiresRestart = true, SkipWhenDisabledBy = "MesSimulator:Enabled", HelpText = "Upload file CodeContent một lần lúc khởi động. Không tự sinh GTIN/serial. Đường dẫn tương đối tính từ thư mục ứng dụng.")]
    [JsonPropertyName("CustomFilePath")] public string CustomFilePath { get; set; } = string.Empty;

    [ConfigField("Tên PO file custom", Order = 9, Required = false, Placeholder = "Mặc định lấy tên file", RequiresRestart = true, SkipWhenDisabledBy = "MesSimulator:Enabled")]
    [JsonPropertyName("CustomPoName")] public string CustomPoName { get; set; } = string.Empty;

    [ConfigField("Timeout (giây)", Order = 10, Min = 5, Max = 3600, RequiresRestart = true, SkipWhenDisabledBy = "MesSimulator:Enabled")]
    [JsonPropertyName("TimeoutSeconds")] public int TimeoutSeconds { get; set; } = 30;
}

/// <summary>Cấu hình ghi log SQLite (mục "Logging:Sqlite").</summary>
[ConfigSection(
    Key = "Sqlite",
    Title = "LỊCH SỬ · GHI LOG",
    Order = 60,
    Icon = "File",
    Description = "Nơi lưu log kỹ thuật và thao tác người dùng.")]
public sealed class SqliteLoggingConfig
{
    [ConfigField("File log SQLite", Order = 1, Placeholder = "VD: logs/app.db", Control = ConfigControlKind.Path, RequiresRestart = true)]
    [JsonPropertyName("Path")] public string Path { get; set; } = "logs/app.db";

    [ConfigField("Mức log", Order = 2, Options = new[] { "Trace", "Debug", "Information", "Warning", "Error", "Critical", "None" }, RequiresRestart = true)]
    [JsonPropertyName("MinLevel")] public string MinLevel { get; set; } = "Information";

    [ConfigField("Số ngày lưu log", Order = 3, Min = 0, Max = 3650, RequiresRestart = true)]
    [JsonPropertyName("RetentionDays")] public int RetentionDays { get; set; } = 30;

    [ConfigField("Ghi thao tác người dùng", Order = 4, HelpText = "Bật audit thao tác.", RequiresRestart = true)]
    [JsonPropertyName("EnableUserActions")] public bool EnableUserActions { get; set; } = true;

    [ConfigField("Batch ghi log", Order = 5, Min = 1, Max = 100000, RequiresRestart = true)]
    [JsonPropertyName("BatchSize")] public int BatchSize { get; set; } = 100;

    [ConfigField("Chu kỳ flush log (ms)", Order = 6, Min = 1, Max = 600000, RequiresRestart = true)]
    [JsonPropertyName("FlushIntervalMs")] public int FlushIntervalMs { get; set; } = 1000;

    [ConfigField("Chu kỳ dọn log (ms)", Order = 7, Min = 1, Max = 31536000000, RequiresRestart = true)]
    [JsonPropertyName("PruneIntervalMs")] public long PruneIntervalMs { get; set; } = 3_600_000;
}

/// <summary>Cấu hình trang Lịch sử (mục "Logging:History").</summary>
[ConfigSection(
    Key = "History",
    Title = "LỊCH SỬ · TRA CỨU",
    Order = 70,
    Icon = "Clock",
    Description = "Tuỳ chọn trang tra cứu lịch sử quét mã.")]
public sealed class LoggingHistoryConfig
{
    [ConfigField("Bật lịch sử", Order = 1, HelpText = "Cho phép tra cứu lịch sử.", RequiresRestart = true)]
    [JsonPropertyName("Enabled")] public bool Enabled { get; set; } = true;

    [ConfigField("Số dòng / trang", Order = 2, Min = 1, Max = 10000, RequiresRestart = true)]
    [JsonPropertyName("PageSize")] public int PageSize { get; set; } = 200;
}

/// <summary>
/// Cấu hình ghi log (mục "Logging").
///
/// Đây là section CHỈA chứa: các section con (Sqlite / History) được khai báo
/// ở property bên trong, mỗi property lại mang <c>[ConfigSection]</c> riêng.
/// </summary>
[ConfigSection(
    Key = "Logging",
    Title = "GHI LOG",
    Order = 55,
    Icon = "File",
    Description = "Cấu hình ghi log kỹ thuật và thao tác người dùng.")]
public sealed class LoggingConfig
{
    [JsonPropertyName("Sqlite")] public SqliteLoggingConfig Sqlite { get; set; } = new();
    [JsonPropertyName("History")] public LoggingHistoryConfig History { get; set; } = new();
}

/// <summary>
/// Cấu hình xác thực (mục "Auth").
///
/// Khớp với <see cref="ClassLibraryMT.Auth.AuthOptions"/> — chỉ dùng để đọc
/// và giữ lại khi lưu cấu hình từ UI, phần bind thực tế do lib Auth đảm nhiệm.
/// </summary>
[ConfigSection(
    Key = "Auth",
    Title = "TÀI KHOẢN · BẢO MẬT",
    Order = 80,
    Icon = "Lock",
    Description = "Cơ sở dữ liệu tài khoản và chính sách đăng nhập.")]
public sealed class AuthConfig
{
    [ConfigField("Database tài khoản", Order = 1, Placeholder = "VD: logs/users.db", Control = ConfigControlKind.Path, RequiresRestart = true)]
    [JsonPropertyName("DbPath")] public string DbPath { get; set; } = "logs/users.db";

    [ConfigField("Số lần đăng nhập sai", Order = 2, Min = 1, Max = 100, RequiresRestart = true)]
    [JsonPropertyName("MaxFailedAttempts")] public int MaxFailedAttempts { get; set; } = 5;

    [ConfigField("Khóa tài khoản (phút)", Order = 3, Min = 1, Max = 10080, RequiresRestart = true)]
    [JsonPropertyName("LockoutMinutes")] public int LockoutMinutes { get; set; } = 5;

    [ConfigField("PBKDF2 iterations", Order = 4, Min = 10000, Max = 10000000, Increment = 10000, RequiresRestart = true)]
    [JsonPropertyName("Pbkdf2Iterations")] public int Pbkdf2Iterations { get; set; } = 210_000;

    [ConfigField("Admin mặc định", Order = 5, RequiresRestart = true)]
    [JsonPropertyName("SeedAdminUser")] public string SeedAdminUser { get; set; } = "admin";

    [ConfigField("Mật khẩu seed", Order = 6, Control = ConfigControlKind.Password, RequiresRestart = true)]
    [JsonPropertyName("SeedAdminPassword")] public string SeedAdminPassword { get; set; } = "admin";
}

/// <summary>
/// Cấu hình quản lý tham số PLC (mục "PlcSettings").
///
/// Khớp với <see cref="ClassLibraryMT.Plc.PlcSettingsOptions"/> — chỉ dùng để đọc
/// và giữ lại khi lưu cấu hình từ UI, phần bind thực tế do lib Plc đảm nhiệm.
/// </summary>
[ConfigSection(
    Key = "PlcSettings",
    Title = "THAM SỐ PLC",
    Order = 90,
    Icon = "Table",
    Description = "Nơi lưu tham số / recipe của PLC.")]
public sealed class PlcSettingsConfig
{
    [ConfigField("Database tham số PLC", Order = 1, Placeholder = "VD: logs/plc.db", Control = ConfigControlKind.Path, RequiresRestart = true)]
    [JsonPropertyName("DbPath")] public string DbPath { get; set; } = "logs/plc.db";
}

/// <summary>
/// Cấu hình lưu bản ghi quét mã (mục "Scans").
///
/// Khớp với <c>ClassLibraryMT.Scans.ScanStoreOptions</c> — chỉ dùng để đọc
/// và giữ lại khi lưu cấu hình từ UI, phần bind thực tế do lib Scans đảm nhiệm.
/// </summary>
[ConfigSection(
    Key = "Scans",
    Title = "LƯU BẢN GHI QUÉT",
    Order = 100,
    Icon = "Database",
    Description = "Nơi lưu bản ghi quét mã và chính sách dọn dữ liệu.")]
public sealed class ScanConfig
{
    [ConfigField("Database bản ghi", Order = 1, Placeholder = "VD: logs/scans.db", Control = ConfigControlKind.Path, RequiresRestart = true)]
    [JsonPropertyName("DbPath")] public string DbPath { get; set; } = "logs/scans.db";

    [ConfigField("Batch ghi bản ghi", Order = 2, Min = 1, Max = 100000, RequiresRestart = true)]
    [JsonPropertyName("BatchSize")] public int BatchSize { get; set; } = 100;

    [ConfigField("Chu kỳ flush (ms)", Order = 3, Min = 1, Max = 600000, RequiresRestart = true)]
    [JsonPropertyName("FlushIntervalMs")] public int FlushIntervalMs { get; set; } = 100;

    [ConfigField("Dung lượng hàng đợi", Order = 4, Min = 1, Max = 10000000, RequiresRestart = true)]
    [JsonPropertyName("QueueCapacity")] public int QueueCapacity { get; set; } = 20_000;

    [ConfigField("Số ngày lưu", Order = 5, Min = 0, Max = 3650, RequiresRestart = true)]
    [JsonPropertyName("RetentionDays")] public int RetentionDays { get; set; }

    [ConfigField("Chu kỳ dọn dữ liệu (ms)", Order = 6, Min = 1, Max = 31536000000, RequiresRestart = true)]
    [JsonPropertyName("PruneIntervalMs")] public long PruneIntervalMs { get; set; } = 3_600_000;
}

/// <summary>Địa chỉ portal nhận yêu cầu cấp license, không chứa tài khoản quản trị.</summary>
[ConfigSection(Key = "LicensePortal", Title = "LICENSE · CỔNG CẤP PHÉP", Order = 95, Icon = "Link",
    Description = "Địa chỉ cổng nhận yêu cầu cấp license từ máy này.")]
public sealed class LicensePortalConfig
{
    [ConfigField("URL License Portal", Required = false, Validation = "Url", RequiresRestart = true,
        Placeholder = "https://portal-cua-ban.workers.dev", HelpText = "Điền URL của Worker đã triển khai. Không cần mật khẩu quản trị.")]
    [JsonPropertyName("BaseUrl")] public string BaseUrl { get; set; } = "";
}

/// <summary>
/// Toàn bộ nội dung file appsettings.json. Giữ đúng tên section và giữ cả các
/// thiết lập không có form nhập khi lưu cấu hình từ giao diện.
/// </summary>
public sealed class AppConfigSnapshot
{
    [JsonPropertyName("Camera")] public CameraConfig Camera { get; set; } = new();
    [JsonPropertyName("Plc")] public PlcConfig Plc { get; set; } = new();
    [JsonPropertyName("Web")] public WebConfig Web { get; set; } = new();
    [JsonPropertyName("Scanner")] public ScannerConfig Scanner { get; set; } = new();
    [JsonPropertyName("MultiConnect")] public MultiConnectConfig MultiConnect { get; set; } = new();
    [JsonPropertyName("Po")] public PoConfig Po { get; set; } = new();
    [JsonPropertyName("Print")] public PrintConfig Print { get; set; } = new();
    [JsonPropertyName("PoServer")] public PoServerConfig PoServer { get; set; } = new();
    [JsonPropertyName("MesSimulator")] public MesSimulatorConfig MesSimulator { get; set; } = new();
    [JsonPropertyName("Checkee")] public CheckeeConfig Checkee { get; set; } = new();
    [JsonPropertyName("Logging")] public LoggingConfig Logging { get; set; } = new();
    [JsonPropertyName("Auth")] public AuthConfig Auth { get; set; } = new();
    [JsonPropertyName("PlcSettings")] public PlcSettingsConfig PlcSettings { get; set; } = new();
    [JsonPropertyName("Scans")] public ScanConfig Scans { get; set; } = new();
    [JsonPropertyName("LicensePortal")] public LicensePortalConfig LicensePortal { get; set; } = new();
}

// ---------------------------------------------------------------- JsonContext
// Source-generated context: không dùng reflection nên an toàn với Native AOT.

[JsonSourceGenerationOptions(
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(AppConfigSnapshot))]
[JsonSerializable(typeof(LicensePortalConfig))]
[JsonSerializable(typeof(LoggingConfig))]
[JsonSerializable(typeof(SqliteLoggingConfig))]
[JsonSerializable(typeof(LoggingHistoryConfig))]
[JsonSerializable(typeof(AuthConfig))]
[JsonSerializable(typeof(PlcSettingsConfig))]
[JsonSerializable(typeof(ScanConfig))]
[JsonSerializable(typeof(ScannerConfig))]
[JsonSerializable(typeof(MultiConnectConfig))]
[JsonSerializable(typeof(PoConfig))]
[JsonSerializable(typeof(PrintConfig))]
[JsonSerializable(typeof(PrinterRemoteConfig))]
[JsonSerializable(typeof(PoServerConfig))]
[JsonSerializable(typeof(MesSimulatorConfig))]
[JsonSerializable(typeof(CheckeeConfig))]
[JsonSerializable(typeof(CameraConfig))]
[JsonSerializable(typeof(PlcConfig))]
[JsonSerializable(typeof(WebConfig))]
public sealed partial class AppConfigJsonContext : JsonSerializerContext
{
}
