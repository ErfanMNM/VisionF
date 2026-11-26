using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TanTienQR;
using TanTienQR.Services;
using TanTienQR.ViewModels;
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
//   [4] Camera / PLC xác nhận rồi áp dụng nóng; huỷ giữ nguyên file và runtime
//   [5] Các mục khác vẫn chờ restart; nhiều lần lưu không xoá mất banner
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

var confirmationOutputIndex = Array.IndexOf(args, "--confirmation-artifacts");
var confirmationOutput = confirmationOutputIndex >= 0 && confirmationOutputIndex + 1 < args.Length
    ? Path.GetFullPath(args[confirmationOutputIndex + 1])
    : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../artifacts/connection-confirmation"));
ConnectionConfirmationChecks.Run(Check, confirmationOutput);
if (args.Contains("--confirmation-only"))
{
    Console.WriteLine($"KẾT QUẢ HỘP THOẠI: {passed} pass / {failed} fail");
    return failed == 0 ? 0 : 1;
}

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
  "Camera": { "Ip": "127.0.0.1", "Port": 9000 },
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
    });

builder.Logging.AddConsole();
builder.Logging.SetMinimumLevel(LogLevel.Error);
builder.Services.AddTanTienServices(builder.Configuration);
// Thay coordinator thật để test không đổi kết nối camera/PLC trên máy.
var deviceApplier = new FakeDeviceConfigurationApplier();
builder.Services.AddSingleton<IDeviceConfigurationApplier>(deviceApplier);
using var host = builder.Build();

var services = host.Services;
var vmType = typeof(TanTienQR.App).Assembly.GetType("TanTienQR.ViewModels.ConfigEditorViewModel")!;
var vm = (object)services.GetRequiredService(vmType);

// Helper reflection
object? Get(string prop) => vmType.GetProperty(prop,
    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(vm);

void Invoke(string method)
{
    vmType.GetMethod(method, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)!
        .Invoke(vm, null);
}

void CompleteUiTask(Task task)
{
    var deadline = Environment.TickCount64 + 20000;
    while (!task.IsCompleted && Environment.TickCount64 < deadline)
    {
        Dispatcher.UIThread.RunJobs();
        Thread.Sleep(5);
    }
    if (!task.IsCompleted) throw new TimeoutException("Lenh UI khong hoan tat trong 20 giay.");
    task.GetAwaiter().GetResult();
    Dispatcher.UIThread.RunJobs();
}

void ExecOn(object target, string commandProp)
{
    var cmd = target.GetType().GetProperty(commandProp,
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(target);
    cmd!.GetType().GetMethod("Execute", BindingFlags.Public | BindingFlags.Instance)!
        .Invoke(cmd, new object?[] { null });
    if (cmd.GetType().GetProperty("ExecutionTask")?.GetValue(cmd) is Task task)
        CompleteUiTask(task);
    else
        Dispatcher.UIThread.RunJobs();
}

void Exec(string commandProp) => ExecOn(vm, commandProp);

void SetConfirmation(object target, Func<string, Task<bool>> callback) =>
    target.GetType().GetProperty("ConfirmConnectionChangesAsync",
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(target, callback);

var confirmationCount = 0;
var acceptConnectionChanges = true;
var confirmationMessages = new List<string>();
SetConfirmation(vm, message =>
{
    confirmationCount++;
    confirmationMessages.Add(message);
    return Task.FromResult(acceptConnectionChanges);
});

// ---------------------------------------------------------------
// Truy cap field theo schema (form nay SINH DONG tu [ConfigField]).
// Moi truong duoc dinh danh bang JsonPath nhu "Camera:Ip", "Plc:TimeoutMs".
// ---------------------------------------------------------------
IEnumerable<object> AllFieldsOf(object target)
{
    var prop = target.GetType().GetProperty("AllFields",
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)!;
    return ((IEnumerable)prop.GetValue(target)!).Cast<object>();
}

object FieldOf(object target, string jsonPath)
{
    var field = AllFieldsOf(target).Cast<object>().FirstOrDefault(f =>
        (string?)f.GetType().GetProperty("JsonPath")!.GetValue(f) == jsonPath);
    if (field is null)
    {
        throw new InvalidOperationException($"Khong tim thay field '{jsonPath}'.");
    }

    return field;
}

object? FieldValue(object target, string jsonPath) =>
    FieldOf(target, jsonPath).GetType().GetProperty("Value")!.GetValue(FieldOf(target, jsonPath));

void SetField(object target, string jsonPath, object? value)
{
    var field = FieldOf(target, jsonPath);
    field.GetType().GetProperty("Value")!.SetValue(field, value);
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
        });

    b.Logging.AddConsole();
    b.Logging.SetMinimumLevel(LogLevel.Error);
    b.Services.AddTanTienServices(b.Configuration);
    b.Services.AddSingleton<IDeviceConfigurationApplier>(new FakeDeviceConfigurationApplier());
    return b.Build();
}

// ---------------------------------------------------------------
// [1] Form nap day du thong so
// ---------------------------------------------------------------
Console.WriteLine("\n[1] Form nap thong so");

Invoke("Load");

Check("Camera IP nap dung", (string?)FieldValue(vm, "Camera:Ip") == "127.0.0.1", (string?)FieldValue(vm, "Camera:Ip") ?? "");
Check("Camera Port nap dung", (int?)FieldValue(vm, "Camera:Port") == 9000);
Check("Form Camera chi co IP va port", AllFieldsOf(vm)
    .Select(field => (string?)field.GetType().GetProperty("JsonPath")!.GetValue(field))
    .Where(path => path?.StartsWith("Camera:", StringComparison.Ordinal) == true)
    .OrderBy(path => path).SequenceEqual(new[] { "Camera:Ip", "Camera:Port" }));
Check("PLC IP nap dung", (string?)FieldValue(vm, "Plc:Ip") == "127.0.0.1");
Check("PLC Port nap dung", (int?)FieldValue(vm, "Plc:Port") == 9600);
Check("PLC Timeout nap dung", (int?)FieldValue(vm, "Plc:TimeoutMs") == 1000);
Check("Scanner host nap dung", (string?)FieldValue(vm, "Scanner:TcpHost") == "127.0.0.1");
Check("Scanner port nap dung", (int?)FieldValue(vm, "Scanner:TcpPort") == 6999);
Check("Scanner COM nap dung", (string?)FieldValue(vm, "Scanner:ComPort") == "COM3");
Check("Scanner baud nap dung", (int?)FieldValue(vm, "Scanner:BaudRate") == 9600);
Check("PO BaseUrl nap dung", (string?)FieldValue(vm, "Po:BaseUrl") == "http://127.0.0.1:5080");
Check("Web URL nap dung", (string?)FieldValue(vm, "Web:Url") == "https://estrace.checkee.vn");
Check("Logging retention nap dung", (int?)FieldValue(vm, "Logging:Sqlite:RetentionDays") == 30);
Check("Auth seed password nap dung", (string?)FieldValue(vm, "Auth:SeedAdminPassword") == "admin");
Check("Scan queue capacity nap dung", (int?)FieldValue(vm, "Scans:QueueCapacity") == 20000);
Check("Khong co banner restart luc dau", (bool?)Get("RestartRequired") == false);
Check("Khong co thay doi chua luu luc dau", (bool?)Get("HasUnsavedChanges") == false);
Check("Nut Luu bi khoa khi khong co thay doi", (bool?)Get("CanSave") == false);

// Form duoc sinh dong tu schema: co day du section + field.
var sectionCount = ((IEnumerable)vmType.GetProperty("Sections",
    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(vm)!).Cast<object>().Count();
Check("Form co nhieu section (sinh dong)", sectionCount >= 9, "sections=" + sectionCount);
Check("Form co day du field (sinh dong)", AllFieldsOf(vm).Cast<object>().Count() >= 40,
    "fields=" + AllFieldsOf(vm).Cast<object>().Count());

// ---------------------------------------------------------------
// [2] Sua 1 o -> dirty
// ---------------------------------------------------------------
Console.WriteLine("\n[2] Sua o -> danh dau chua luu");

SetField(vm, "Plc:TimeoutMs", 2500);

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
Check("Sau Hoan tac: Timeout ve lai gia tri cu", (int?)FieldValue(vm, "Plc:TimeoutMs") == 1000);

// ---------------------------------------------------------------
// [4] Xac nhan Camera/PLC truoc khi luu va ap dung nong
// ---------------------------------------------------------------
Console.WriteLine("\n[4] Camera/PLC: khong doi, huy va xac nhan ap dung nong");

var configPath = Path.Combine(root, "appsettings.json");
var initialFile = File.ReadAllText(configPath);
Exec("SaveCommand");
Check("Khong doi gi -> khong hoi xac nhan", confirmationCount == 0);
Check("Khong doi gi -> khong doi ket noi", deviceApplier.ApplyCount == 0);
Check("Khong doi gi -> file giu nguyen", File.ReadAllText(configPath) == initialFile);

acceptConnectionChanges = false;
SetField(vm, "Camera:Ip", "192.168.9.50");
SetField(vm, "Plc:Ip", "192.168.9.9");
Exec("SaveCommand");
Check("Doi Camera va PLC cung luc chi hoi mot lan", confirmationCount == 1);
Check("Tu choi -> khong doi ket noi", deviceApplier.ApplyCount == 0);
Check("Tu choi -> khong luu file", File.ReadAllText(configPath) == initialFile);
Check("Tu choi -> form van chua luu", (bool?)Get("IsDirty") == true);
Check("Tu choi -> giu gia tri dang soan de sua tiep",
    (string?)FieldValue(vm, "Camera:Ip") == "192.168.9.50"
    && (string?)FieldValue(vm, "Plc:Ip") == "192.168.9.9");
Exec("ResetCommand");
Check("Hoan tac sau khi huy -> van dung ket noi cu",
    (string?)FieldValue(vm, "Camera:Ip") == "127.0.0.1"
    && (string?)FieldValue(vm, "Plc:Ip") == "127.0.0.1");

acceptConnectionChanges = true;
SetField(vm, "Camera:Ip", "192.168.9.50");
SetField(vm, "Camera:Port", 9001);
SetField(vm, "Plc:Ip", "192.168.9.9");
Exec("SaveCommand");
Check("Xac nhan -> mot lan ap dung bat dong bo", confirmationCount == 2 && deviceApplier.ApplyCount == 1);
Check("Coordinator nhan dung target Camera/PLC",
    deviceApplier.LastTarget?.Camera.Ip == "192.168.9.50"
    && deviceApplier.LastTarget.Camera.Port == 9001
    && deviceApplier.LastTarget.Plc.Ip == "192.168.9.9");
Check("Coordinator nhan runtime cu de rollback",
    deviceApplier.LastRunning?.Camera.Ip == "127.0.0.1"
    && deviceApplier.LastRunning.Plc.Ip == "127.0.0.1");
Check("Dialog co noi dung de xem truoc khi ap dung", confirmationMessages.All(message => !string.IsNullOrWhiteSpace(message)));
Check("Chi Camera/PLC -> khong yeu cau restart", (bool?)Get("RestartRequired") == false);
Check("Chi Camera/PLC -> khong bat nut restart", (bool?)Get("CanRestartNow") == false);
Check("Thong bao bao da luu", ((string?)Get("ConfigMessageText"))?.Contains("Đã lưu") == true);
Check("Nut Luu bi khoa sau khi luu", (bool?)Get("CanSave") == false);
Check("Khong co thay doi chua luu sau khi luu", (bool?)Get("HasUnsavedChanges") == false);

// File da duoc ghi?
var savedText = File.ReadAllText(configPath);
Check("File appsettings da ghi IP PLC moi", savedText.Contains("192.168.9.9"));
Check("File appsettings da ghi IP camera moi", savedText.Contains("192.168.9.50"));
Check("File appsettings giu section Auth", savedText.Contains("\"Auth\""));
Check("File appsettings giu section Logging", savedText.Contains("\"Logging\""));
Check("File appsettings giu section Scanner", savedText.Contains("\"Scanner\""));
Check("File appsettings giu section Po", savedText.Contains("\"Po\""));
Check("File appsettings luu Logging retention", savedText.Contains("\"RetentionDays\": 30"));
Check("File appsettings luu seed password da nhap", savedText.Contains("\"SeedAdminPassword\": \"admin\""));
Check("File appsettings luu Scans queue capacity", savedText.Contains("\"QueueCapacity\": 20000"));

// IConfiguration van la snapshot khoi dong: reset khong duoc tra IP ve cu.
SetField(vm, "Plc:TimeoutMs", 3333);
Exec("ResetCommand");
Check("Hoan tac giu Camera/PLC da hot-apply du IConfiguration cu",
    (string?)FieldValue(vm, "Camera:Ip") == "192.168.9.50"
    && (int?)FieldValue(vm, "Camera:Port") == 9001
    && (string?)FieldValue(vm, "Plc:Ip") == "192.168.9.9");
Check("Hoan tac bo thay doi PLC chua luu", (int?)FieldValue(vm, "Plc:TimeoutMs") == 1000);
Check("Hoan tac khong tu tao banner restart Camera/PLC", (bool?)Get("RestartRequired") == false);
Check("Luu xong roi Hoan tac: khong co thay doi chua luu", (bool?)Get("HasUnsavedChanges") == false);

// ---------------------------------------------------------------
// [5] Scanner va cac muc khac van can restart, khong mat qua lan luu sau
// ---------------------------------------------------------------
Console.WriteLine("\n[5] Giu banner pending restart qua nhieu lan luu");

var beforeOtherConfirmation = confirmationCount;
SetField(vm, "Scanner:TcpPort", 7001);
Exec("SaveCommand");
Check("Doi rieng Scanner khong hoi doi ket noi Camera/PLC", confirmationCount == beforeOtherConfirmation);
Check("Scanner van yeu cau restart", (bool?)Get("RestartRequired") == true && (bool?)Get("CanRestartNow") == true);
Check("Banner chi ro Scanner da doi", ((string?)Get("RestartBannerText"))?.Contains("Port TCP") == true);

SetField(vm, "Logging:Sqlite:RetentionDays", 31);
Exec("SaveCommand");
Check("Luu muc khac khong xoa banner Scanner chua ap dung",
    (bool?)Get("RestartRequired") == true
    && ((string?)Get("RestartBannerText"))?.Contains("Port TCP") == true
    && ((string?)Get("RestartBannerText"))?.Contains("Số ngày lưu log") == true);

SetField(vm, "Plc:Ip", "192.168.9.10");
var beforeMixedConfirmation = confirmationCount;
Exec("SaveCommand");
Check("Luu PLC trong khi Scanner pending van xac nhan dung mot lan",
    confirmationCount == beforeMixedConfirmation + 1);
Check("Lan ap dung sau dung runtime PLC da ap dung lan truoc",
    deviceApplier.LastRunning?.Plc.Ip == "192.168.9.9");
Check("Coordinator khong danh dau Scanner da chay theo ban da luu",
    deviceApplier.LastRunning?.Scanner.TcpPort == 6999);
Check("Hot-apply PLC khong xoa banner muc khac",
    (bool?)Get("RestartRequired") == true
    && ((string?)Get("RestartBannerText"))?.Contains("Port TCP") == true
    && ((string?)Get("RestartBannerText"))?.Contains("IP PLC") != true);
SetField(vm, "Plc:TimeoutMs", 3333);
Exec("ResetCommand");
Check("Reset giu runtime PLC moi va Scanner da luu",
    (string?)FieldValue(vm, "Plc:Ip") == "192.168.9.10"
    && (int?)FieldValue(vm, "Scanner:TcpPort") == 7001);
Check("Reset giu banner pending muc chua ap dung", (bool?)Get("RestartRequired") == true);

Exec("RestartLaterCommand");
Check("RestartLater khong lam tat banner", (bool?)Get("RestartRequired") == true);
Check("RestartLater co thong bao", !string.IsNullOrWhiteSpace((string?)Get("ConfigMessageText")));

// ---------------------------------------------------------------
// [6] Luu gia tri KHONG doi -> khong yeu cau restart
// ---------------------------------------------------------------
Console.WriteLine("\n[6] Luu gia tri khong doi -> khong yeu cau restart");

// Tao host moi tu file da luu = mo phong app khoi dong lai.
var restarted = RestartHost(root);
var restartedVmType = typeof(TanTienQR.App).Assembly.GetType("TanTienQR.ViewModels.ConfigEditorViewModel")!;
var restartedVm = restarted.Services.GetRequiredService(restartedVmType);
restartedVmType.GetMethod("Load", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)!
    .Invoke(restartedVm, null);

object? RGet(string prop) => restartedVmType.GetProperty(prop,
    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(restartedVm);

Check("Sau restart: config moi da duoc nap", (string?)FieldValue(restartedVm, "Plc:Ip") == "192.168.9.10",
    (string?)FieldValue(restartedVm, "Plc:Ip") ?? "");
Check("Sau restart: banner restart tat", (bool?)RGet("RestartRequired") == false);
Check("Sau restart: khong co thay doi chua luu", (bool?)RGet("HasUnsavedChanges") == false);

var restartConfirmations = 0;
SetConfirmation(restartedVm, _ =>
{
    restartConfirmations++;
    return Task.FromResult(true);
});
ExecOn(restartedVm, "SaveCommand");

Check("Luu khong doi gi -> khong yeu cau restart", (bool?)RGet("RestartRequired") == false);
Check("Luu khong doi sau restart -> khong xac nhan", restartConfirmations == 0);

restarted.Dispose();

Console.WriteLine("\n[6b] Ap dung ket noi that bai khong commit file/baseline");
var beforeFailureFile = File.ReadAllText(configPath);
var beforeFailurePersist = deviceApplier.PersistCount;
deviceApplier.Failure = new IOException("Mo phong ket noi moi that bai.");
SetField(vm, "Plc:Ip", "192.168.9.11");
Exec("SaveCommand");
Check("Ap dung loi -> file cu duoc giu nguyen", File.ReadAllText(configPath) == beforeFailureFile);
Check("Ap dung loi -> chua goi persist", deviceApplier.PersistCount == beforeFailurePersist);
Check("Ap dung loi -> form van dirty va co thong bao loi",
    (bool?)Get("IsDirty") == true && (bool?)Get("HasErrorMessage") == true);
deviceApplier.Failure = null;
Exec("ResetCommand");
Check("Hoan tac sau apply loi khong nhan target loi lam baseline",
    (string?)FieldValue(vm, "Plc:Ip") == "192.168.9.10");

// ---------------------------------------------------------------
// [7] Validation: IP sai -> khong luu
// ---------------------------------------------------------------
Console.WriteLine("\n[7] Validation: gia tri sai bi tu choi");

Invoke("Load");
var fileBefore = File.ReadAllText(Path.Combine(root, "appsettings.json"));
var beforeInvalidConfirmation = confirmationCount;
var beforeInvalidApply = deviceApplier.ApplyCount;

SetField(vm, "Plc:Ip", "192.168.1.999");
Exec("SaveCommand");

Check("IP sai -> khong ghi file", File.ReadAllText(Path.Combine(root, "appsettings.json")) == fileBefore);
Check("IP sai -> co thong bao loi", (bool?)Get("HasErrorMessage") == true, (string?)Get("ConfigMessageText") ?? "");
Check(
    "IP sai -> thong bao nhac IP",
    ((string?)Get("ConfigMessageText"))?.Contains("IP PLC") == true,
    (string?)Get("ConfigMessageText") ?? "");

Invoke("Load");
SetField(vm, "Camera:Port", 0);
Exec("SaveCommand");
Check("Port 0 -> khong ghi file", File.ReadAllText(Path.Combine(root, "appsettings.json")) == fileBefore);
Check("Port 0 -> co thong bao loi", (bool?)Get("HasErrorMessage") == true);

Invoke("Load");
SetField(vm, "Po:BaseUrl", "khong phai url");
Exec("SaveCommand");
Check("PO URL sai -> khong ghi file", File.ReadAllText(Path.Combine(root, "appsettings.json")) == fileBefore);

Invoke("Load");
SetField(vm, "Scanner:TcpPort", 99999);
Exec("SaveCommand");
Check("Scanner port sai -> khong ghi file", File.ReadAllText(Path.Combine(root, "appsettings.json")) == fileBefore);
Check("Cau hinh sai duoc chan truoc dialog va coordinator",
    confirmationCount == beforeInvalidConfirmation && deviceApplier.ApplyCount == beforeInvalidApply);

// ---------------------------------------------------------------
// [8] View thuc te render duoc (compiled binding khong loi)
// ---------------------------------------------------------------
Console.WriteLine("\n[8] View that render off-screen");

try
{
    Invoke("Load");
    var viewType = typeof(TanTienQR.App).Assembly.GetType("TanTienQR.Views.ConfigView")!;
    var view = (Control)Activator.CreateInstance(viewType)!;

    // Form nay SINH DONG tu schema nen can DataContext that
    // (MainViewModel chua ConfigEditor) de cac field duoc render.
    var mainVmType = typeof(TanTienQR.App).Assembly.GetType("TanTienQR.ViewModels.MainViewModel")!;
    var mainVm = services.GetRequiredService(mainVmType);
    view.DataContext = mainVm;

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

    var allTextBoxes = view.GetVisualDescendants().OfType<TextBox>().ToList();
    Check("Co TextBox trong form", allTextBoxes.Count > 0, "textboxes=" + allTextBoxes.Count);

    var secretBox = allTextBoxes.FirstOrDefault(x => x.PasswordChar != '\0');
    Check("Truong seed password duoc che", secretBox is not null);

    // Cac loai control deu duoc render tu schema.
    Check("Form render ComboBox", view.GetVisualDescendants().OfType<ComboBox>().Any());
    var switches = view.GetVisualDescendants().OfType<ToggleSwitch>().ToList();
    Check("Form render cong tac cho tat ca truong bool", switches.Count > 0
        && switches.Count == AllFieldsOf(vm).OfType<BoolField>().Count(),
        "switches=" + switches.Count);
    Check("Form khong con CheckBox", !view.GetVisualDescendants().OfType<CheckBox>().Any());
    Check("Cong tac co vung cham cao it nhat 44px", switches.All(toggle => toggle.Bounds.Height >= 44));
    Check("Cong tac giu nhan huong dan va ten truy cap", switches.All(toggle =>
        toggle.DataContext is BoolField field && Equals(toggle.Content, field.HelpText)
        && AutomationProperties.GetName(toggle) == field.Label));

    var boundSwitch = switches.First();
    var boundField = (BoolField)boundSwitch.DataContext!;
    var originalValue = (bool)boundField.Value!;
    boundSwitch.SetCurrentValue(ToggleSwitch.IsCheckedProperty, !originalValue);
    Dispatcher.UIThread.RunJobs();
    Check("Bat/tat cong tac cap nhat cau hinh va danh dau chua luu",
        Equals(boundField.Value, !originalValue) && (bool?)Get("IsDirty") == true);
    boundField.Value = originalValue;
    Dispatcher.UIThread.RunJobs();
    Check("Cau hinh cap nhat lai trang thai cong tac", boundSwitch.IsChecked == originalValue);
    Check("Form render NumericUpDown", view.GetVisualDescendants().OfType<NumericUpDown>().Any());

    CaptureWindow(window, "config-switches.png");

    window.Close();
    Dispatcher.UIThread.RunJobs();
}
catch (Exception ex)
{
    Check("ConfigView tao duoc va render", false, ex.Message);
}

// Cong tac van dung input native: cham vao ca nhan, Space va trang thai disabled.
Console.WriteLine("\n[8b] Cong tac cam ung + ban phim + disabled");
try
{
    var theme = (Avalonia.Styling.ControlTheme)Application.Current!.Resources["IosToggleSwitchTheme"]!;
    var offSwitch = new ToggleSwitch { Theme = theme, Content = "Tắt · chạm để bật", IsChecked = false };
    var onSwitch = new ToggleSwitch { Theme = theme, Content = "Bật", IsChecked = true };
    var disabledOff = new ToggleSwitch { Theme = theme, Content = "Tắt · đã khoá", IsChecked = false, IsEnabled = false };
    var disabledOn = new ToggleSwitch { Theme = theme, Content = "Bật · đã khoá", IsChecked = true, IsEnabled = false };
    var window = new Window
    {
        Width = 460,
        Height = 330,
        Content = new StackPanel
        {
            Margin = new Thickness(24),
            Spacing = 8,
            Children =
            {
                new TextBlock { Text = "Công tắc", FontSize = 20, FontWeight = FontWeight.Bold },
                offSwitch, onSwitch, disabledOff, disabledOn,
            },
        },
    };
    window.Show();
    Dispatcher.UIThread.RunJobs();
    CaptureWindow(window, "switch-states.png");

    void Click(ToggleSwitch toggle)
    {
        // Chạm vào nhãn để kiểm tra toàn bộ hàng là vùng thao tác.
        var point = toggle.TranslatePoint(new Point(toggle.Bounds.Width - 6, toggle.Bounds.Height / 2), window)
            ?? throw new InvalidOperationException("Khong doi duoc toa do cong tac.");
        window.MouseDown(point, MouseButton.Left, RawInputModifiers.None);
        window.MouseUp(point, MouseButton.Left, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
    }

    Click(offSwitch);
    Check("Cham vao nhan van bat duoc cong tac", offSwitch.IsChecked == true);
    onSwitch.Focus();
    offSwitch.Focus(NavigationMethod.Tab);
    Dispatcher.UIThread.RunJobs();
    Check("Cong tac nhan focus tu ban phim", ReferenceEquals(window.FocusManager?.GetFocusedElement(), offSwitch));
    Check("Focus ban phim co vien nhin thay", offSwitch.GetVisualDescendants().OfType<Border>()
        .Any(border => border.Name == "SwitchFocusRing"
            && border.BorderBrush is ISolidColorBrush brush && brush.Color.A > 0));
    window.KeyPress(Key.Space, RawInputModifiers.None, PhysicalKey.Space, " ");
    window.KeyRelease(Key.Space, RawInputModifiers.None, PhysicalKey.Space, " ");
    Dispatcher.UIThread.RunJobs();
    Check("Space doi trang thai cong tac", offSwitch.IsChecked == false);
    Click(disabledOff);
    Click(disabledOn);
    Check("Cong tac disabled khong doi trang thai khi cham", disabledOff.IsChecked == false && disabledOn.IsChecked == true);
    Check("Disabled van phan biet duoc trang thai nhung hien mo", disabledOff.Opacity < 1 && disabledOn.Opacity < 1);
    window.Close();
    Dispatcher.UIThread.RunJobs();
}
catch (Exception ex)
{
    Check("Cong tac native hoat dong", false, ex.ToString());
}

Console.WriteLine("\n[8c] Cong tac tu lam moi log giu binding hai chieu");
try
{
    var mainVm = services.GetRequiredService(typeof(App).Assembly.GetType("TanTienQR.ViewModels.MainViewModel")!);
    var devVm = mainVm.GetType().GetProperty("DevView")!.GetValue(mainVm)!;
    var autoRefresh = devVm.GetType().GetProperty("IsLogAutoRefresh")!;
    var originalValue = (bool)autoRefresh.GetValue(devVm)!;
    var view = new DevView { DataContext = mainVm };
    var window = new Window { Width = 1360, Height = 820, Content = view };
    window.Show();
    Dispatcher.UIThread.RunJobs();
    var toggle = view.GetVisualDescendants().OfType<ToggleSwitch>().Single();
    Check("Dev dung cong tac va giu nguyen nhan", Equals(toggle.Content, "Tự làm mới log")
        && !view.GetVisualDescendants().OfType<CheckBox>().Any());
    toggle.SetCurrentValue(ToggleSwitch.IsCheckedProperty, !originalValue);
    Dispatcher.UIThread.RunJobs();
    Check("Cong tac cap nhat co tu lam moi log", Equals(autoRefresh.GetValue(devVm), !originalValue));
    autoRefresh.SetValue(devVm, originalValue);
    Dispatcher.UIThread.RunJobs();
    Check("Co tu lam moi cap nhat lai cong tac", toggle.IsChecked == originalValue);
    CaptureWindow(window, "dev-switch.png");
    window.Close();
    Dispatcher.UIThread.RunJobs();
}
catch (Exception ex)
{
    Check("DevView render va bind cong tac", false, ex.ToString());
}

// [9] Tab Giới thiệu dùng MainViewModel thật, không khởi động host hoặc thiết bị.
Console.WriteLine("\n[9] Tab Giới thiệu render và bind off-screen");
try
{
    var appAssembly = typeof(TanTienQR.App).Assembly;
    var mainVm = services.GetRequiredService(appAssembly.GetType("TanTienQR.ViewModels.MainViewModel")!);
    var updates = mainVm.GetType().GetProperty("Updates")!.GetValue(mainVm)!;
    var updateType = updates.GetType();
    updateType.GetProperty("Status")!.SetValue(updates, "Gói đã được xác minh. Chỉ cài khi dây chuyền đã dừng.");
    updateType.GetProperty("AvailableVersion")!.SetValue(updates, "1.2.3");
    updateType.GetProperty("Notes")!.SetValue(updates, "Bản cập nhật có khôi phục khi khởi động thất bại. " +
        "Giữ nguyên cấu hình, license, người dùng và lịch sử sản xuất.");
    updateType.GetProperty("CanDownload")!.SetValue(updates, true);
    updateType.GetProperty("CanInstall")!.SetValue(updates, true);
    var about = new AboutView { DataContext = mainVm };
    var window = new Window { Width = 1360, Height = 820, Content = about };
    window.Show();
    Dispatcher.UIThread.RunJobs();

    Check("View thật render được (không lỗi XAML)", CountVisuals(about) > 50);

    var text = about.GetVisualDescendants().OfType<TextBlock>().ToList();
    Check("AboutView hiển thị giấy phép", text.Any(block => block.Text == "GIẤY PHÉP SỬ DỤNG"));
    Check("AboutView hiển thị mã máy (HWID)", text.Any(block => block.Text == "MÃ MÁY (HWID)"));
    Check("AboutView hiển thị card cập nhật", text.Any(block => block.Text == "CẬP NHẬT PHẦN MỀM"));
    Check("Card bind phiên bản mới", text.Any(block => block.Text == "BẢN MỚI")
                                     && text.Any(block => block.Text == "1.2.3"));
    Check("Card bind trạng thái tiếng Việt", text.Any(block => block.Text?.StartsWith("Gói đã được xác minh.") == true));

    // Nút giờ có icon + TextBlock bên trong (HorizontalContentAlignment=Center)
    // nên tìm theo nhãn thay vì ép kiểu Content sang string.
    var updateButtons = about.GetVisualDescendants().OfType<Button>().Where(button =>
        button.GetVisualDescendants().OfType<TextBlock>().Any(label =>
            label.Text is "Kiểm tra bản mới" or "Tải bản cập nhật" or "Cài đặt và khởi động lại")).ToList();
    Check("Card có ba nút cập nhật với command thật", updateButtons.Count == 3 && updateButtons.All(button => button.Command is not null));
    Check("Các nút cập nhật có kích thước trong cột bên phải", updateButtons.All(button =>
        button.Bounds.Width is > 0 and <= 640 && button.Bounds.Height > 0),
        string.Join(", ", updateButtons.Select(button => $"{button.GetVisualDescendants().OfType<TextBlock>().First().Text}={button.Bounds.Width:0}x{button.Bounds.Height:0}")));
    var installButton = updateButtons.Single(button =>
        button.GetVisualDescendants().OfType<TextBlock>().Any(label => label.Text == "Cài đặt và khởi động lại"));
    Check("Cài đặt truy cập được qua ScrollViewer", installButton.GetVisualAncestors().OfType<ScrollViewer>().Any());

    window.Width = 1024;
    window.Height = 600;
    Dispatcher.UIThread.RunJobs();
    installButton.BringIntoView();
    Dispatcher.UIThread.RunJobs();
    Check("Card vẫn bố trí được trên cửa sổ nhỏ", installButton.Bounds.Width is > 0 and <= 520);
    updateType.GetProperty("IsBusy")!.SetValue(updates, true);
    Dispatcher.UIThread.RunJobs();
    Check("Đang tải/cài thì khóa các nút cập nhật", updateButtons.All(button => !button.IsEffectivelyEnabled));
    updateType.GetProperty("IsBusy")!.SetValue(updates, false);
    updateType.GetProperty("CanDownload")!.SetValue(updates, false);
    updateType.GetProperty("CanInstall")!.SetValue(updates, false);
    window.Close();
    Dispatcher.UIThread.RunJobs();
}
catch (Exception ex)
{
    Check("AboutView tạo được và render", false, ex.ToString());
}

static void CaptureWindow(Window window, string fileName)
{
    for (var frame = 0; frame < 3; frame++)
    {
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
    }
    var output = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../artifacts/ios-switches"));
    Directory.CreateDirectory(output);
    using var bitmap = window.CaptureRenderedFrame()
        ?? throw new InvalidOperationException("Khong chup duoc anh off-screen.");
    bitmap.Save(Path.Combine(output, fileName), new PngBitmapEncoderOptions());
    Console.WriteLine("  [Shot] " + Path.Combine(output, fileName));
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

// Chỉ kiểm tra contract VM/coordinator; không tạo socket hoặc kết nối thiết bị.
internal sealed class FakeDeviceConfigurationApplier : IDeviceConfigurationApplier
{
    public int ApplyCount { get; private set; }
    public int PersistCount { get; private set; }
    public AppConfigSnapshot? LastTarget { get; private set; }
    public AppConfigSnapshot? LastRunning { get; private set; }
    public Exception? Failure { get; set; }

    public async Task ApplyAndSaveAsync(AppConfigSnapshot target, AppConfigSnapshot running,
        Func<bool> persist, CancellationToken ct)
    {
        ApplyCount++;
        LastTarget = Clone(target);
        LastRunning = Clone(running);
        // Bảo đảm harness thực sự đợi AsyncRelayCommand qua UI dispatcher.
        await Task.Yield();
        ct.ThrowIfCancellationRequested();
        if (Failure is not null) throw Failure;
        PersistCount++;
        if (!persist()) throw new IOException("Mo phong khong luu duoc cau hinh.");
    }

    private static AppConfigSnapshot Clone(AppConfigSnapshot value) =>
        JsonSerializer.Deserialize(
            JsonSerializer.Serialize(value, AppConfigJsonContext.Default.AppConfigSnapshot),
            AppConfigJsonContext.Default.AppConfigSnapshot)!;
}
