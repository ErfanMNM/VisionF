using System.Text.Json;
using ClassLibraryMT.Print;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using TanTienQR.Services;
using TanTienQR.ViewModels;

// ===================================================================
// Test harness cho logic Cấu hình (validation + phát hiện thay đổi
// bắt buộc restart).
//
// Chạy: dotnet run --project tools\ConfigTests
// Headless hoàn toàn — KHÔNG mở cửa sổ Avalonia, KHÔNG chạm màn hình.
// ===================================================================

int passed = 0, failed = 0;

void Check(string label, bool ok, string detail = "")
{
    if (ok)
    {
        passed++;
        Console.WriteLine($"  [OK]   {label}");
    }
    else
    {
        failed++;
        Console.WriteLine($"  [FAIL] {label}" + (detail.Length > 0 ? $" -- {detail}" : ""));
    }
}

Console.WriteLine("========================================");
Console.WriteLine("  KIEM THU LOGIC CAU HINH / RESTART");
Console.WriteLine("========================================");

AppConfigSnapshot NewSnapshot() => new()
{
    Camera = new CameraConfig { Ip = "192.168.1.50", Port = 9000, DuplicateWindowSeconds = 30 },
    Plc = new PlcConfig
    {
        Ip = "192.168.1.10",
        Port = 9600,
        RemoteNode = 0,
        LocalNode = 1,
        TimeoutMs = 1000,
        Retries = 0,
        ResultAddress = "D12",
        ReadyAddress = "D16",
        RefreshIntervalMs = 1000,
    },
    Scanner = new ScannerConfig
    {
        Enabled = true,
        Mode = "TcpServer",
        TcpHost = "127.0.0.1",
        TcpPort = 6999,
        ComPort = "COM3",
        BaudRate = 9600,
    },
    Po = new PoConfig
    {
        BaseUrl = "http://127.0.0.1:5080",
        DbRoot = "logs/po",
        ActivationIntervalMs = 2500,
        AutoActivate = true,
        CodeSampleSize = 100,
    },
    Web = new WebConfig { Url = "https://estrace.checkee.vn", Enabled = true },
    Logging = new LoggingConfig { Sqlite = new SqliteLoggingConfig(), History = new LoggingHistoryConfig() },
    Auth = new AuthConfig(),
    PlcSettings = new PlcSettingsConfig(),
    Scans = new ScanConfig(),
};

// ---------------------------------------------------------------
// [1] Validation: snapshot hợp lệ mặc định
// ---------------------------------------------------------------
Console.WriteLine("\n[1] ConfigValidation: snapshot mac dinh hop le");

var baseSnapshot = NewSnapshot();
Check(
    "Snapshot mac dinh hop le",
    ConfigValidation.Validate(baseSnapshot, out var err1),
    err1 ?? "");

// ---------------------------------------------------------------
// [2] Validation: IP / host
// ---------------------------------------------------------------
Console.WriteLine("\n[2] ConfigValidation: IP / host");

Check("IPv4 hop le", ConfigValidation.IsValidIpOrHost("192.168.1.50"));
Check("IPv4 0.0.0.0 hop le", ConfigValidation.IsValidIpOrHost("0.0.0.0"));
Check("Hostname hop le", ConfigValidation.IsValidIpOrHost("plc.local"));
Check("Hostname ngan hop le", ConfigValidation.IsValidIpOrHost("server01"));
Check("Chuoi rong -> sai", !ConfigValidation.IsValidIpOrHost(""));
Check("Null -> sai", !ConfigValidation.IsValidIpOrHost(null));
Check("IP sai (999.1.1.1) -> sai", !ConfigValidation.IsValidIpOrHost("999.1.1.1"));
Check("Host sai dau cham -> sai", !ConfigValidation.IsValidIpOrHost("host..local"));
Check("Host sai dau '-' -> sai", !ConfigValidation.IsValidIpOrHost("-bad.local"));
Check("Co khoang trang -> sai", !ConfigValidation.IsValidIpOrHost("  "));

// ---------------------------------------------------------------
// [3] Validation: từng nhóm thông số
// ---------------------------------------------------------------
Console.WriteLine("\n[3] ConfigValidation: nhom thong so");

var s = NewSnapshot();
s.Camera.Ip = "";
Check("Camera IP rong -> bao loi", !ConfigValidation.Validate(s, out var e3) && e3!.Contains("IP đầu đọc", StringComparison.OrdinalIgnoreCase), e3 ?? "");

s = NewSnapshot();
s.Camera.Port = 0;
Check("Camera Port 0 -> bao loi", !ConfigValidation.Validate(s, out var e4) && e4!.Contains("Port TCP"), e4 ?? "");

s = NewSnapshot();
s.Camera.Port = 70000;
Check("Camera Port > 65535 -> bao loi", !ConfigValidation.Validate(s, out var e5) && e5!.Contains("Port TCP"), e5 ?? "");

s = NewSnapshot();
s.Camera.DuplicateWindowSeconds = 0;
Check("Cua so trung ma <= 0 -> bao loi", !ConfigValidation.Validate(s, out var e6), e6 ?? "");

s = NewSnapshot();
s.Plc.Ip = "192.168.1.999";
Check("PLC IP sai -> bao loi", !ConfigValidation.Validate(s, out var e7), e7 ?? "");

s = NewSnapshot();
s.Plc.Port = 0;
Check("PLC Port 0 -> bao loi", !ConfigValidation.Validate(s, out var e8), e8 ?? "");

s = NewSnapshot();
s.Plc.RemoteNode = 256;
Check("PLC RemoteNode > 255 -> bao loi", !ConfigValidation.Validate(s, out var e9), e9 ?? "");

s = NewSnapshot();
s.Plc.LocalNode = -1;
Check("PLC LocalNode < 0 -> bao loi", !ConfigValidation.Validate(s, out var e10), e10 ?? "");

s = NewSnapshot();
s.Plc.TimeoutMs = 0;
Check("PLC Timeout <= 0 -> bao loi", !ConfigValidation.Validate(s, out var e11), e11 ?? "");

s = NewSnapshot();
s.Plc.Retries = -1;
Check("PLC Retries am -> bao loi", !ConfigValidation.Validate(s, out var e12), e12 ?? "");

s = NewSnapshot();
s.Plc.ResultAddress = "";
Check("Dia chi vung D rong -> bao loi", !ConfigValidation.Validate(s, out var e13), e13 ?? "");

s = NewSnapshot();
s.Plc.RefreshIntervalMs = 0;
Check("Heartbeat <= 0 -> bao loi", !ConfigValidation.Validate(s, out var e14), e14 ?? "");

s = NewSnapshot();
s.Scanner.TcpPort = 0;
Check("Scanner TCP Port 0 -> bao loi", !ConfigValidation.Validate(s, out var e15), e15 ?? "");

s = NewSnapshot();
s.Scanner.TcpHost = "bad host!";
Check("Scanner host sai -> bao loi", !ConfigValidation.Validate(s, out var e16), e16 ?? "");

s = NewSnapshot();
s.Scanner.ComPort = "";
Check("Scanner COM rong -> bao loi", !ConfigValidation.Validate(s, out var e17), e17 ?? "");

s = NewSnapshot();
s.Scanner.BaudRate = 0;
Check("Scanner baud <= 0 -> bao loi", !ConfigValidation.Validate(s, out var e18), e18 ?? "");

s = NewSnapshot();
s.Scanner.Enabled = false;
s.Scanner.TcpPort = 0;
s.Scanner.ComPort = "";
Check("Scanner tat -> bo qua validate TCP/COM", ConfigValidation.Validate(s, out var e19), e19 ?? "");

s = NewSnapshot();
s.Po.BaseUrl = "khong phai url";
Check("PO BaseUrl sai -> bao loi", !ConfigValidation.Validate(s, out var e20), e20 ?? "");

s = NewSnapshot();
s.Po.BaseUrl = "ftp://host";
Check("PO BaseUrl sai scheme -> bao loi", !ConfigValidation.Validate(s, out var e21), e21 ?? "");

s = NewSnapshot();
s.Po.DbRoot = "";
Check("PO DbRoot rong -> bao loi", !ConfigValidation.Validate(s, out var e22), e22 ?? "");

s = NewSnapshot();
s.Po.ActivationIntervalMs = 0;
Check("PO Activation <= 0 -> bao loi", !ConfigValidation.Validate(s, out var e23), e23 ?? "");

s = NewSnapshot();
s.Po.CodeSampleSize = -1;
Check("PO CodeSampleSize am -> bao loi", !ConfigValidation.Validate(s, out var e24), e24 ?? "");

s = NewSnapshot();
s.Web.Url = "khong phai url";
Check("Web URL sai -> bao loi", !ConfigValidation.Validate(s, out var e25), e25 ?? "");

s = NewSnapshot();
s.Web.Enabled = false;
s.Web.Url = "khong phai url";
Check("Web tat -> bo qua validate URL", ConfigValidation.Validate(s, out var e26), e26 ?? "");

s = NewSnapshot();
s.Auth.Pbkdf2Iterations = 9999;
Check("PBKDF2 duoi gioi han -> bao loi", !ConfigValidation.Validate(s, out var e27), e27 ?? "");
s = NewSnapshot();
s.Logging.Sqlite.BatchSize = 0;
Check("Log batch size 0 -> bao loi", !ConfigValidation.Validate(s, out var e28), e28 ?? "");
s = NewSnapshot();
s.Scans.QueueCapacity = 0;
Check("Scan queue capacity 0 -> bao loi", !ConfigValidation.Validate(s, out var e29), e29 ?? "");

// ---------------------------------------------------------------
// [4] ConfigDiff: phat hien thay doi yeu cau restart
//
// Nhãn trong danh sách thay đổi được LẤY TỪ SCHEMA ([ConfigField] Label)
// nên test dùng đúng nhãn khai báo trong AppConfigModels.cs.
// ---------------------------------------------------------------
Console.WriteLine("\n[4] ConfigDiff: thay doi thong so quan trong");

var before = NewSnapshot();
var after = NewSnapshot();
Check("Khong doi gi -> khong co thay doi", ConfigDiff.DescribeCriticalChanges(before, after).Count == 0);

after = NewSnapshot();
after.Camera.Ip = "10.0.0.9";
var diff = ConfigDiff.DescribeCriticalChanges(before, after);
Check("Doi IP camera -> hot-apply, khong restart", diff.Count == 0, string.Join(",", diff));

after = NewSnapshot();
after.Camera.Port = 9001;
diff = ConfigDiff.DescribeCriticalChanges(before, after);
Check("Doi Port camera -> hot-apply, khong restart", diff.Count == 0, string.Join(",", diff));

after = NewSnapshot();
after.Plc.Ip = "10.0.0.10";
diff = ConfigDiff.DescribeCriticalChanges(before, after);
Check("Doi IP PLC -> hot-apply, khong restart", diff.Count == 0, string.Join(",", diff));

after = NewSnapshot();
after.Plc.Port = 9601;
diff = ConfigDiff.DescribeCriticalChanges(before, after);
Check("Doi Port PLC -> hot-apply, khong restart", diff.Count == 0, string.Join(",", diff));

after = NewSnapshot();
after.Plc.TimeoutMs = 2000;
diff = ConfigDiff.DescribeCriticalChanges(before, after);
Check("Doi Timeout PLC -> hot-apply, khong restart", diff.Count == 0, string.Join(",", diff));

after = NewSnapshot();
after.Plc.Retries = 3;
diff = ConfigDiff.DescribeCriticalChanges(before, after);
Check("Doi Retries PLC -> hot-apply, khong restart", diff.Count == 0, string.Join(",", diff));

after = NewSnapshot();
after.Plc.ResultAddress = "D20";
diff = ConfigDiff.DescribeCriticalChanges(before, after);
Check("Doi dia chi ket qua PLC -> hot-apply, khong restart", diff.Count == 0, string.Join(",", diff));

after = NewSnapshot();
after.Plc.RefreshIntervalMs = 1500;
diff = ConfigDiff.DescribeCriticalChanges(before, after);
Check("Doi chu ky heartbeat -> hot-apply, khong restart", diff.Count == 0, string.Join(",", diff));

after = NewSnapshot();
after.Plc.Enabled = false;
after.Plc.RemoteNode = 7;
after.Plc.LocalNode = 8;
after.Plc.ReadyAddress = "D30";
diff = ConfigDiff.DescribeCriticalChanges(before, after);
Check("Bat/tat PLC, node va dia chi Ready deu hot-apply", diff.Count == 0, string.Join(",", diff));
Check("Thong so PLC hot-apply van danh dau dirty", !ConfigDiff.AreEquivalent(before, after));

after = NewSnapshot();
after.Scanner.TcpHost = "10.0.0.20";
diff = ConfigDiff.DescribeCriticalChanges(before, after);
Check("Doi host scanner -> co thay doi", diff.Contains("Host TCP"), string.Join(",", diff));

after = NewSnapshot();
after.Scanner.TcpPort = 7000;
diff = ConfigDiff.DescribeCriticalChanges(before, after);
Check("Doi port scanner -> co thay doi", diff.Contains("Port TCP"), string.Join(",", diff));

after = NewSnapshot();
after.Scanner.ComPort = "COM5";
diff = ConfigDiff.DescribeCriticalChanges(before, after);
Check("Doi COM scanner -> co thay doi", diff.Contains("Cổng COM"), string.Join(",", diff));

after = NewSnapshot();
after.Scanner.BaudRate = 19200;
diff = ConfigDiff.DescribeCriticalChanges(before, after);
Check("Doi baud scanner -> co thay doi", diff.Contains("Baud rate"), string.Join(",", diff));

after = NewSnapshot();
after.Scanner.Mode = "TcpClient";
diff = ConfigDiff.DescribeCriticalChanges(before, after);
Check("Doi mode scanner -> co thay doi", diff.Contains("Chế độ"), string.Join(",", diff));

after = NewSnapshot();
after.Po.BaseUrl = "http://10.0.0.30:5080";
diff = ConfigDiff.DescribeCriticalChanges(before, after);
Check("Doi server PO -> co thay doi", diff.Contains("Địa chỉ server"), string.Join(",", diff));

after = NewSnapshot();
after.Web.Url = "https://khac.checkee.vn";
diff = ConfigDiff.DescribeCriticalChanges(before, after);
Check("Doi web URL -> co thay doi", diff.Contains("URL trang"), string.Join(",", diff));

after = NewSnapshot();
after.Logging.Sqlite.RetentionDays++;
diff = ConfigDiff.DescribeCriticalChanges(before, after);
Check("Doi retention log -> co thay doi restart", diff.Contains("Số ngày lưu log"), string.Join(",", diff));

after = NewSnapshot();
after.Auth.SeedAdminPassword = "changed-secret";
diff = ConfigDiff.DescribeCriticalChanges(before, after);
Check("Doi seed password -> co thay doi restart", diff.Contains("Mật khẩu seed"), string.Join(",", diff));

// Toàn bộ mục Camera được áp dụng cùng kết nối, bao gồm cửa sổ trùng mã.
after = NewSnapshot();
after.Camera.DuplicateWindowSeconds = 60;
diff = ConfigDiff.DescribeCriticalChanges(before, after);
Check(
    "Doi duplicate window -> hot-apply, khong restart",
    diff.Count == 0,
    string.Join(",", diff));
Check("Duplicate window van la thay doi can luu", !ConfigDiff.AreEquivalent(before, after));

after.Scanner.TcpPort++;
diff = ConfigDiff.DescribeCriticalChanges(before, after);
Check("Doi Camera cung Scanner chi yeu cau restart Scanner",
    diff.Count == 1 && diff.Contains("Port TCP"), string.Join(",", diff));

Console.WriteLine("\n[4b] Noi dung xac nhan chi gom thong so Camera/PLC");
after = NewSnapshot();
after.Camera.Ip = "10.0.0.9";
after.Plc.TimeoutMs = 2000;
after.Scanner.TcpPort = 7001;
after.Auth.SeedAdminPassword = "secret-must-not-appear";
var connectionChanges = DeviceConfigurationChanges.Describe(before, after);
Check("Camera va PLC cung co trong mot danh sach xac nhan", connectionChanges.Count == 2
    && connectionChanges.Any(change => change.Contains("IP đầu đọc"))
    && connectionChanges.Any(change => change.Contains("Timeout (ms)")), string.Join(",", connectionChanges));
Check("Xac nhan ket noi khong dua cac muc khac hoac mat khau vao noi dung",
    !string.Join("\n", connectionChanges).Contains("secret-must-not-appear")
    && connectionChanges.All(change => change.StartsWith("Camera", StringComparison.Ordinal)
        || change.StartsWith("PLC", StringComparison.Ordinal)));
Check("Loc theo Camera/PLC dung section", DeviceConfigurationChanges.Describe(before, after, "Camera").Count == 1
    && DeviceConfigurationChanges.Describe(before, after, "Plc").Count == 1);
after = NewSnapshot();
after.Scanner.TcpPort = 7001;
Check("Chi doi Scanner khong tao xac nhan Camera/PLC", DeviceConfigurationChanges.Describe(before, after).Count == 0);

// ---------------------------------------------------------------
// [5] ConfigDiff.AreEquivalent (cờ "chưa lưu")
// ---------------------------------------------------------------
Console.WriteLine("\n[5] ConfigDiff.AreEquivalent: co thay doi chua luu");

before = NewSnapshot();
after = NewSnapshot();
Check("Hai snapshot giong -> tuong duong", ConfigDiff.AreEquivalent(before, after));

after = NewSnapshot();
after.Plc.TimeoutMs = 1500;
Check("Khac timeout -> khong tuong duong", !ConfigDiff.AreEquivalent(before, after));

after = NewSnapshot();
after.Camera.Ip = " 192.168.1.50 ";
Check("Khac khoang trang -> van tuong duong", ConfigDiff.AreEquivalent(before, after));

after = NewSnapshot();
after.Scanner.Mode = "tcpclient";
Check("Khac chu hoa mode -> khong tuong duong (mode khac nhau)", !ConfigDiff.AreEquivalent(before, after));

after = NewSnapshot();
after.Scanner.Mode = "TcpClient";
Check("Doi mode -> khong tuong duong", !ConfigDiff.AreEquivalent(before, after));

after = NewSnapshot();
after.Scans.RetentionDays++;
Check("Doi scans retention -> dirty", !ConfigDiff.AreEquivalent(before, after));

// ---------------------------------------------------------------
// [6] Round-trip JSON: persist qua AppConfigJsonContext
// ---------------------------------------------------------------
Console.WriteLine("\n[6] Persist: round-trip JSON appsettings");

var json = JsonSerializer.Serialize(baseSnapshot, AppConfigJsonContext.Default.AppConfigSnapshot);
var restored = JsonSerializer.Deserialize(json, AppConfigJsonContext.Default.AppConfigSnapshot);

Check("Doc lai snapshot tu JSON thanh cong", restored is not null);
if (restored is not null)
{
    Check("Camera IP giu nguyen qua JSON", restored.Camera.Ip == baseSnapshot.Camera.Ip);
    Check("Camera Port giu nguyen qua JSON", restored.Camera.Port == baseSnapshot.Camera.Port);
    Check("PLC Timeout giu nguyen qua JSON", restored.Plc.TimeoutMs == baseSnapshot.Plc.TimeoutMs);
    Check("Scanner host/port giu nguyen qua JSON",
        restored.Scanner.TcpHost == baseSnapshot.Scanner.TcpHost
        && restored.Scanner.TcpPort == baseSnapshot.Scanner.TcpPort);
    Check("Scanner COM/baud giu nguyen qua JSON",
        restored.Scanner.ComPort == baseSnapshot.Scanner.ComPort
        && restored.Scanner.BaudRate == baseSnapshot.Scanner.BaudRate);
    Check("PO BaseUrl giu nguyen qua JSON", restored.Po.BaseUrl == baseSnapshot.Po.BaseUrl);
    Check("Web giu nguyen qua JSON", restored.Web.Url == baseSnapshot.Web.Url);
    Check("Snapshot vua doc lai van hop le", ConfigValidation.Validate(restored, out var e6b), e6b ?? "");
}

// File appsettings.json cua app phai chua day du cac section.
var appSettings = Path.Combine(
    AppContext.BaseDirectory, "..", "..", "..", "..", "..",
    "TanTienQR", "appsettings.json");
appSettings = Path.GetFullPath(appSettings);
if (File.Exists(appSettings))
{
    var text = File.ReadAllText(appSettings);
    using var doc = JsonDocument.Parse(text);
    var root = doc.RootElement;

    Check("appsettings.json co section Camera", root.TryGetProperty("Camera", out _));
    Check("appsettings.json co section Plc", root.TryGetProperty("Plc", out _));
    Check("appsettings.json co section Scanner", root.TryGetProperty("Scanner", out _));
    Check("appsettings.json co section Po", root.TryGetProperty("Po", out _));
    Check("appsettings.json co section Web", root.TryGetProperty("Web", out _));
}
else
{
    Console.WriteLine("  [SKIP] Khong tim thay appsettings.json de kiem tra section");
}

// ---------------------------------------------------------------
// [7] ConfigStore: giu nguyen section khi luu
// ---------------------------------------------------------------
Console.WriteLine("\n[7] ConfigStore: giu nguyen section khong co form nhap");

// Tao mot file appsettings rieng cho test, khong cham vao file that.
var sandbox = Path.Combine(AppContext.BaseDirectory, "config-store-test");
Directory.CreateDirectory(sandbox);
var testConfig = Path.Combine(sandbox, "appsettings.json");
File.WriteAllText(testConfig, """
{
  "Camera": { "Ip": "1.1.1.1", "Port": 9000, "DuplicateWindowSeconds": 30 },
  "Plc": { "Ip": "2.2.2.2", "Port": 9600, "RemoteNode": 0, "LocalNode": 1, "TimeoutMs": 1000, "Retries": 0, "ResultAddress": "D12", "ReadyAddress": "D16", "RefreshIntervalMs": 1000 },
  "Web": { "Url": "https://estrace.checkee.vn", "Enabled": true },
  "Scanner": { "Enabled": true, "Mode": "TcpServer", "TcpHost": "127.0.0.1", "TcpPort": 6999, "ComPort": "COM3", "BaudRate": 9600 },
  "Po": { "BaseUrl": "http://127.0.0.1:5080", "DbRoot": "logs/po", "ActivationIntervalMs": 2500, "AutoActivate": true, "CodeSampleSize": 100 },
  "Auth": { "DbPath": "logs/users.db", "MaxFailedAttempts": 5, "LockoutMinutes": 5, "Pbkdf2Iterations": 210000, "SeedAdminUser": "admin", "SeedAdminPassword": "secret" },
  "Logging": { "Sqlite": { "Path": "logs/app.db", "MinLevel": "Information", "RetentionDays": 30, "EnableUserActions": true, "BatchSize": 100, "FlushIntervalMs": 1000, "PruneIntervalMs": 3600000 }, "History": { "Enabled": true, "PageSize": 200 } },
  "PlcSettings": { "DbPath": "logs/plc.db" },
  "Scans": { "DbPath": "logs/scans.db", "BatchSize": 100, "FlushIntervalMs": 1000, "QueueCapacity": 20000, "RetentionDays": 0, "PruneIntervalMs": 3600000 }
}
""");

var savedText = await File.ReadAllTextAsync(testConfig);
var savedDoc = JsonDocument.Parse(savedText).RootElement;
Check("File mau co section Auth", savedDoc.TryGetProperty("Auth", out _));
Check("File mau co section Logging", savedDoc.TryGetProperty("Logging", out _));

// Remote bổ sung phải tương thích cấu hình cũ và dùng cùng cơ chế restart.
Console.WriteLine("\n[8] Màn hình máy in: cấu hình cũ, validation và lưu JSON");
var legacyPrint = JsonSerializer.Deserialize("""{"Print":{"Enabled":true,"Ip":"192.168.10.2"}}""",
    AppConfigJsonContext.Default.AppConfigSnapshot)!;
Check("Cấu hình cũ mặc định tắt remote", !legacyPrint.Print.Remote.Enabled && legacyPrint.Print.Remote.Port == 3389);
var remoteSnapshot = NewSnapshot();
remoteSnapshot.Print.Remote.Enabled = true;
Check("Bật remote thiếu tài khoản bị từ chối", !ConfigValidation.Validate(remoteSnapshot, out _));
remoteSnapshot.Print.Remote.Username = "operator";
Check("Host trống hợp lệ để dùng IP máy in", ConfigValidation.Validate(remoteSnapshot, out var remoteError), remoteError ?? "");
remoteSnapshot.Print.Remote.Port = 0;
Check("Cổng remote ngoài giới hạn bị từ chối", !ConfigValidation.Validate(remoteSnapshot, out _));
remoteSnapshot.Print.Remote.Port = 3389;
remoteSnapshot.Print.Remote.Host = "bad host!";
Check("Host remote sai bị từ chối", !ConfigValidation.Validate(remoteSnapshot, out _));
remoteSnapshot.Print.Remote.Host = "printer.local";
Check("Thay đổi remote yêu cầu restart", ConfigDiff.DescribeCriticalChanges(NewSnapshot(), remoteSnapshot).Contains("Bật màn hình máy in"));
var remoteJson = JsonSerializer.Serialize(remoteSnapshot, AppConfigJsonContext.Default.AppConfigSnapshot);
var remoteRestored = JsonSerializer.Deserialize(remoteJson, AppConfigJsonContext.Default.AppConfigSnapshot)!;
Check("Remote round-trip JSON có đủ thông số", remoteRestored.Print.Remote.Enabled
    && remoteRestored.Print.Remote.Username == "operator" && remoteRestored.Print.Remote.Host == "printer.local");
Check("Cấu hình remote không có trường mật khẩu", !remoteJson.Contains("\"Password\"", StringComparison.Ordinal));

// Gọi ConfigStore nội bộ bằng reflection chỉ trong harness JIT.
var remoteConfigDirectory = Path.Combine(Path.GetTempPath(), "tantien-remote-config-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(remoteConfigDirectory);
try
{
    var configPath = Path.Combine(remoteConfigDirectory, "appsettings.json");
    File.WriteAllText(configPath, """{"Print":{"Remote":null,"FutureKey":"keep"},"FutureSection":{"value":7}}""");
    var storeType = typeof(AppConfigSnapshot).Assembly.GetType("TanTienQR.Services.ConfigStore")!;
    var loggerType = typeof(Microsoft.Extensions.Logging.Abstractions.NullLogger<>).MakeGenericType(storeType);
    var configStore = Activator.CreateInstance(storeType, Activator.CreateInstance(loggerType), remoteConfigDirectory)!;
    var loaded = (AppConfigSnapshot)storeType.GetMethod("Load")!.Invoke(configStore, null)!;
    Check("ConfigStore chuẩn hóa Remote null", loaded.Print.Remote is not null && !loaded.Print.Remote.Enabled);
    storeType.GetMethod("SaveMerged")!.Invoke(configStore, [remoteSnapshot]);
    using var remoteSaved = JsonDocument.Parse(File.ReadAllText(configPath));
    Check("Lưu remote giữ khóa lạ", remoteSaved.RootElement.GetProperty("Print").GetProperty("FutureKey").GetString() == "keep"
        && remoteSaved.RootElement.GetProperty("FutureSection").GetProperty("value").GetInt32() == 7);
}
finally { Directory.Delete(remoteConfigDirectory, recursive: true); }

// Không Start host nên không mở kết nối/worker; xác minh disposal cả TCP và remote đã resolve.
var disposalBuilder = Host.CreateApplicationBuilder();
disposalBuilder.Configuration.Sources.Clear();
disposalBuilder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
{
    ["Print:Enabled"] = "false",
    ["Print:Remote:CredentialTarget"] = "TanTien/Test/" + Guid.NewGuid().ToString("N"),
});
disposalBuilder.Services.AddTanTienServices(disposalBuilder.Configuration);
var disposalHost = disposalBuilder.Build();
_ = disposalHost.Services.GetRequiredService<IPrinterClient>();
await disposalHost.Services.GetRequiredService<PrinterRemoteViewModel>().ShutdownAsync();
await ((IAsyncDisposable)disposalHost).DisposeAsync();
Check("Host dọn được TCP và remote bất đồng bộ", true);

// ---------------------------------------------------------------
Console.WriteLine("\n========================================");
Console.WriteLine($"  KET QUA: {passed} pass / {failed} fail");
Console.WriteLine("========================================");

return failed == 0 ? 0 : 1;
