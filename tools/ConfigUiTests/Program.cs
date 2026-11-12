using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TanTienQR;
using TanTienQR.Services;
using TanTienQR.Views;

// ===================================================================
// KIEM THU UI OFF-SCREEN cho trang Cấu hình.
//
// Chạy: dotnet run --project tools\ConfigUiTests
//
// Dùng Avalonia.Headless + Skia: KHÔNG mở cửa sổ thật, KHÔNG chụp màn
// hình desktop, KHÔNG ảnh hưởng phiên làm việc của người dùng.
//
// Kiểm tra:
//   [1] Form nạp đủ các thông số camera / PLC / scanner / PO / web
//   [2] Sửa 1 ô -> badge "Chưa lưu" hiện, nút Lưu được bật
//   [3] Hoàn tác -> badge tắt
//   [4] Lưu thông số quan trọng -> banner "Cần khởi động lại" hiện
//   [5] Nút "Khởi động lại ngay" / "Để sau" tồn tại và bấm được
//   [6] Lưu giá trị không đổi -> KHÔNG yêu cầu restart
//   [7] Validation: IP sai -> không lưu được, hiện thông báo lỗi
//   [8] File appsettings.json trong sandbox được ghi đúng
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
Console.WriteLine("  KIEM THU UI OFF-SCREEN - TRANG CAU HINH");
Console.WriteLine("========================================");

// ---------------------------------------------------------------
// [0] Chu bi moi (headless, Skia -> bitmap, khong cua so that)
// ---------------------------------------------------------------
AppBuilder.Configure<App>()
    .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
    .UseSkia()
    .WithInterFont()
    .SetupWithoutStarting();

Console.WriteLine("[Harness] Avalonia headless (Skia) khoi dong.");

// ---------------------------------------------------------------
// Sandbox rieng cho test: config goc + thu muc ghi rieng
// ---------------------------------------------------------------
var root = Path.Combine(AppContext.BaseDirectory, "config-ui-test");
if (Directory.Exists(root))
{
    Directory.Delete(root, true);
}

Directory.CreateDirectory(root);
var logs = Path.Combine(root, "logs");
Directory.CreateDirectory(logs);
Directory.CreateDirectory(Path.Combine(logs, "po"));

File.WriteAllText(Path.Combine(root, "appsettings.json"), """
{
  "Camera": { "Ip": "127.0.0.1", "Port": 9000, "DuplicateWindowSeconds": 30 },
  "Plc": { "Ip": "127.0.0.1", "Port": 9600, "RemoteNode": 0, "LocalNode": 1, "TimeoutMs": 1000, "Retries": 0, "ResultAddress": "D12", "ReadyAddress": "D16", "RefreshIntervalMs": 1000 },
  "Web": { "Url": "https://estrace.checkee.vn", "Enabled": true },
  "Scanner": { "Enabled": true, "Mode": "TcpServer", "TcpHost": "127.0.0.1", "TcpPort": 6999, "ComPort": "COM3", "BaudRate": 9600 },
  "Po": { "BaseUrl": "http://127.0.0.1:5080", "DbRoot": "logs/po", "ActivationIntervalMs": 2500, "AutoActivate": true, "CodeSampleSize": 100 },
  "Auth": { "DbPath": "logs/users.db", "MaxFailedAttempts": 5, "LockoutMinutes": 5, "Pbkdf2Iterations": 210000, "SeedAdminUser": "admin", "SeedAdminPassword": "admin" },
  "Logging": { "Sqlite": { "Path": "logs/app.db", "MinLevel": "Warning", "RetentionDays": 30, "EnableUserActions": true, "BatchSize": 100, "FlushIntervalMs": 1000, "PruneIntervalMs": 3600000 }, "History": { "Enabled": true, "PageSize": 200 } },
  "PlcSettings": { "DbPath": "logs/plc.db" },
  "Scans": { "DbPath": "logs/scans.db", "BatchSize": 100, "FlushIntervalMs": 1000, "QueueCapacity": 20000, "RetentionDays": 0, "PruneIntervalMs": 3600000 }
}
""");

// ---------------------------------------------------------------
// DI container (mirror App)
// ---------------------------------------------------------------
var builder = Host.CreateApplicationBuilder();
builder.Configuration
    .SetBasePath(root)
    .AddJsonFile("appsettings.json", optional: false, reloadOnChange: false)
    .AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["Config:BasePath"] = root,
        ["Logging:Sqlite:Path"] = Path.Combine(logs, "app.db"),
        ["Auth:DbPath"] = Path.Combine(logs, "users.db"),
    });

builder.Logging.AddConsole();
builder.Logging.SetMinimumLevel(LogLevel.Error);
builder.Services.AddTanTienServices(builder.Configuration);
using var host = builder.Build();

var services = host.Services;
var vmType = typeof(TanTienQR.App).Assembly.GetType("TanTienQR.ViewModels.ConfigEditorViewModel")!;
var vm = (object)services.GetRequiredService(vmType);

// Helper reflection
object? Get(string prop) => vmType.GetProperty(prop,
    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(vm);

void Set(string prop, object? value) => vmType.GetProperty(prop,
    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)!
    .SetValue(vm, value);

void Invoke(string method)
{
    vmType.GetMethod(method, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)!
        .Invoke(vm, null);
}

void Exec(string commandProp)
{
    var cmd = Get(commandProp);
    cmd!.GetType().GetMethod("Execute", BindingFlags.Public | BindingFlags.Instance)!
        .Invoke(cmd, new object?[] { null });
}

Console.WriteLine($"[Harness] Config:BasePath = {builder.Configuration["Config:BasePath"]}");

// Mo phong app khoi dong lai: dung host moi, doc config tu file da luu.
IHost RestartHost(string basePath)
{
    var b = Host.CreateApplicationBuilder();
    b.Configuration
        .SetBasePath(basePath)
        .AddJsonFile("appsettings.json", optional: false, reloadOnChange: false)
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Config:BasePath"] = basePath,
            ["Logging:Sqlite:Path"] = Path.Combine(basePath, "logs", "app.db"),
            ["Auth:DbPath"] = Path.Combine(basePath, "logs", "users.db"),
        });

    b.Logging.AddConsole();
    b.Logging.SetMinimumLevel(LogLevel.Error);
    b.Services.AddTanTienServices(b.Configuration);
    return b.Build();
}

// ---------------------------------------------------------------
// [1] Form nap day du thong so
// ---------------------------------------------------------------
Console.WriteLine("\n[1] Form nap thong so");

Invoke("Load");

Check("Camera IP nap dung", (string?)Get("CameraIp") == "127.0.0.1", (string?)Get("CameraIp") ?? "");
Check("Camera Port nap dung", (int?)Get("CameraPort") == 9000);
Check("PLC IP nap dung", (string?)Get("PlcIp") == "127.0.0.1");
Check("PLC Port nap dung", (int?)Get("PlcPort") == 9600);
Check("PLC Timeout nap dung", (int?)Get("PlcTimeoutMs") == 1000);
Check("Scanner host nap dung", (string?)Get("ScannerTcpHost") == "127.0.0.1");
Check("Scanner port nap dung", (int?)Get("ScannerTcpPort") == 6999);
Check("Scanner COM nap dung", (string?)Get("ScannerComPort") == "COM3");
Check("Scanner baud nap dung", (int?)Get("ScannerBaudRate") == 9600);
Check("PO BaseUrl nap dung", (string?)Get("PoBaseUrl") == "http://127.0.0.1:5080");
Check("Web URL nap dung", (string?)Get("WebUrl") == "https://estrace.checkee.vn");
Check("Khong co banner restart luc dau", (bool?)Get("RestartRequired") == false);
Check("Khong co thay doi chua luu luc dau", (bool?)Get("HasUnsavedChanges") == false);
Check("Nut Luu bi khoa khi khong co thay doi", (bool?)Get("CanSave") == false);

// ---------------------------------------------------------------
// [2] Sua 1 o -> dirty
// ---------------------------------------------------------------
Console.WriteLine("\n[2] Sua o -> danh dau chua luu");

Set("PlcTimeoutMs", 2500);

Check("Co thay doi chua luu", (bool?)Get("HasUnsavedChanges") == true);
Check("IsDirty = true", (bool?)Get("IsDirty") == true);
Check("Nut Luu duoc bat", (bool?)Get("CanSave") == true);

// ---------------------------------------------------------------
// [3] Hoan tac -> dirty tat
// ---------------------------------------------------------------
Console.WriteLine("\n[3] Hoan tac -> het thay doi chua luu");

Exec("ResetCommand");

Check("Sau Hoan tac: khong co thay doi chua luu", (bool?)Get("HasUnsavedChanges") == false);
Check("Sau Hoan tac: nut Luu bi khoa", (bool?)Get("CanSave") == false);
Check("Sau Hoan tac: Timeout ve lai gia tri cu", (int?)Get("PlcTimeoutMs") == 1000);

// ---------------------------------------------------------------
// [4] Luu thong so quan trong -> banner restart
// ---------------------------------------------------------------
Console.WriteLine("\n[4] Luu thong so quan trong -> yeu cau restart");

Set("PlcIp", "192.168.9.9");
Exec("SaveCommand");

Check("RestartRequired = true", (bool?)Get("RestartRequired") == true);
Check("CanRestartNow = true", (bool?)Get("CanRestartNow") == true);
Check("Banner co noi dung", !string.IsNullOrWhiteSpace((string?)Get("RestartBannerText")));
Check(
    "Banner nhac dung thong so da doi",
    ((string?)Get("RestartBannerText"))?.Contains("IP PLC") == true,
    (string?)Get("RestartBannerText") ?? "");
Check("Thong bao bao da luu", ((string?)Get("ConfigMessageText"))?.Contains("Đã lưu") == true);
Check("Nut Luu bi khoa sau khi luu", (bool?)Get("CanSave") == false);
Check("Khong co thay doi chua luu sau khi luu", (bool?)Get("HasUnsavedChanges") == false);

// File da duoc ghi?
var savedText = File.ReadAllText(Path.Combine(root, "appsettings.json"));
Check("File appsettings da ghi IP PLC moi", savedText.Contains("192.168.9.9"));
Check("File appsettings giu section Auth", savedText.Contains("\"Auth\""));
Check("File appsettings giu section Logging", savedText.Contains("\"Logging\""));
Check("File appsettings giu section Scanner", savedText.Contains("\"Scanner\""));
Check("File appsettings giu section Po", savedText.Contains("\"Po\""));

// Da luu xong: sua them roi Hoan tac -> banner restart phai VAN con,
// vi cau hinh da luu van chua duoc ap dung cho app dang chay.
Set("PlcTimeoutMs", 3333);
Exec("ResetCommand");
Check("Luu xong roi Hoan tac: banner restart van con", (bool?)Get("RestartRequired") == true);
Check("Luu xong roi Hoan tac: khong co thay doi chua luu", (bool?)Get("HasUnsavedChanges") == false);

// ---------------------------------------------------------------
// [5] Nut restart
// ---------------------------------------------------------------
Console.WriteLine("\n[5] Nut khoi dong lai");

Exec("RestartLaterCommand");
Check("RestartLater khong lam tat banner", (bool?)Get("RestartRequired") == true);
Check("RestartLater co thong bao", !string.IsNullOrWhiteSpace((string?)Get("ConfigMessageText")));

// ---------------------------------------------------------------
// [6] Luu gia tri KHONG doi -> khong yeu cau restart
// ---------------------------------------------------------------
Console.WriteLine("\n[6] Luu gia tri khong doi -> khong yeu cau restart");

// Mo phong vong doi dang chay -> luu -> app KHOI DONG LAI (doc lai config
// tu file vua luu) -> luu lai khong doi gi -> KHONG yeu cau restart.
Invoke("Load");
Set("PlcIp", "192.168.9.10");   // config dang chay van la 192.168.9.9 (da luu o [4])
Exec("SaveCommand");
Check("Doi IP -> van yeu cau restart", (bool?)Get("RestartRequired") == true);

// Tao host moi tu file da luu = mo phong app khoi dong lai.
var restarted = RestartHost(root);
var restartedVmType = typeof(TanTienQR.App).Assembly.GetType("TanTienQR.ViewModels.ConfigEditorViewModel")!;
var restartedVm = restarted.Services.GetRequiredService(restartedVmType);
restartedVmType.GetMethod("Load", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)!
    .Invoke(restartedVm, null);

object? RGet(string prop) => restartedVmType.GetProperty(prop,
    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(restartedVm);

Check("Sau restart: config moi da duoc nap", (string?)RGet("PlcIp") == "192.168.9.10", (string?)RGet("PlcIp") ?? "");
Check("Sau restart: banner restart tat", (bool?)RGet("RestartRequired") == false);
Check("Sau restart: khong co thay doi chua luu", (bool?)RGet("HasUnsavedChanges") == false);

// Luu lai khong doi gi -> khong yeu cau restart.
restartedVmType.GetProperty("SaveCommand",
    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)!
    .GetValue(restartedVm)!
    .GetType().GetMethod("Execute", BindingFlags.Public | BindingFlags.Instance)!
    .Invoke(
        restartedVmType.GetProperty("SaveCommand",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(restartedVm),
        new object?[] { null });

Check("Luu khong doi gi -> khong yeu cau restart", (bool?)RGet("RestartRequired") == false);
Check(
    "Luu khong doi gi -> thong bao thanh cong",
    ((string?)RGet("ConfigMessageText"))?.Contains("Đã lưu") == true,
    (string?)RGet("ConfigMessageText") ?? "");

restarted.Dispose();

// ---------------------------------------------------------------
// [7] Validation: IP sai -> khong luu
// ---------------------------------------------------------------
Console.WriteLine("\n[7] Validation: gia tri sai bi tu choi");

Invoke("Load");
var fileBefore = File.ReadAllText(Path.Combine(root, "appsettings.json"));

Set("PlcIp", "192.168.1.999");
Exec("SaveCommand");

Check("IP sai -> khong ghi file", File.ReadAllText(Path.Combine(root, "appsettings.json")) == fileBefore);
Check("IP sai -> co thong bao loi", (bool?)Get("HasErrorMessage") == true, (string?)Get("ConfigMessageText") ?? "");
Check(
    "IP sai -> thong bao nhac IP",
    ((string?)Get("ConfigMessageText"))?.Contains("IP PLC") == true,
    (string?)Get("ConfigMessageText") ?? "");

Invoke("Load");
Set("CameraPort", 0);
Exec("SaveCommand");
Check("Port 0 -> khong ghi file", File.ReadAllText(Path.Combine(root, "appsettings.json")) == fileBefore);
Check("Port 0 -> co thong bao loi", (bool?)Get("HasErrorMessage") == true);

Invoke("Load");
Set("PoBaseUrl", "khong phai url");
Exec("SaveCommand");
Check("PO URL sai -> khong ghi file", File.ReadAllText(Path.Combine(root, "appsettings.json")) == fileBefore);

Invoke("Load");
Set("ScannerTcpPort", 99999);
Exec("SaveCommand");
Check("Scanner port sai -> khong ghi file", File.ReadAllText(Path.Combine(root, "appsettings.json")) == fileBefore);

// ---------------------------------------------------------------
// [8] View thuc te render duoc (compiled binding khong loi)
// ---------------------------------------------------------------
Console.WriteLine("\n[8] View that render off-screen");

try
{
    var viewType = typeof(TanTienQR.App).Assembly.GetType("TanTienQR.Views.ConfigView")!;
    var view = (Control)Activator.CreateInstance(viewType)!;
    var window = new Window
    {
        Width = 1360,
        Height = 820,
        Content = view,
    };

    window.Show();
    Dispatcher.UIThread.RunJobs();

    var viewCount = CountVisuals(view);
    Check("ConfigView tao duoc va render", viewCount > 50, "visuals=" + viewCount);

    window.Close();
    Dispatcher.UIThread.RunJobs();
}
catch (Exception ex)
{
    Check("ConfigView tao duoc va render", false, ex.Message);
}

static int CountVisuals(Control root)
{
    var count = 1;
    var queue = new Queue<Control>();
    queue.Enqueue(root);

    while (queue.Count > 0)
    {
        var cur = queue.Dequeue();
        foreach (var child in cur.GetVisualChildren().OfType<Control>())
        {
            count++;
            queue.Enqueue(child);
        }
    }

    return count;
}

// ---------------------------------------------------------------
Console.WriteLine("\n========================================");
Console.WriteLine($"  KET QUA: {passed} pass / {failed} fail");
Console.WriteLine("========================================");

return failed == 0 ? 0 : 1;
