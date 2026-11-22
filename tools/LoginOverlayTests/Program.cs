using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TanTienQR;
using TanTienQR.Controls;
using TanTienQR.Services;
using TanTienQR.ViewModels;
using TanTienQR.Views;

// ===================================================================
// KIEM THU OFF-SCREEN cho luong dang nhap kieu OVERLAY (1 cua so).
//
// Chay: dotnet run --project tools\LoginOverlayTests
//
// KHONG mo cua so that tren desktop, KHONG chup man hinh.
//
// ViewModel trong TanTienQR la `internal` -> harness dung reflection de
// truy cap (giong cac harness khac trong tools/).
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
Console.WriteLine("  KIEM THU OFF-SCREEN - OVERLAY DANG NHAP");
Console.WriteLine("========================================");

// ---------------------------------------------------------------
// [0] Chu bi moi (headless + Skia)
// ---------------------------------------------------------------
AppBuilder.Configure<App>()
    .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
    .UseSkia()
    .WithInterFont()
    .SetupWithoutStarting();

Console.WriteLine("[Harness] Avalonia headless (Skia) khoi dong.");

// ---------------------------------------------------------------
// Sandbox rieng
// ---------------------------------------------------------------
var root = Path.Combine(AppContext.BaseDirectory, "login-overlay-test");
if (Directory.Exists(root))
{
    Directory.Delete(root, true);
}

Directory.CreateDirectory(root);
var logs = Path.Combine(root, "logs");
Directory.CreateDirectory(logs);
Directory.CreateDirectory(Path.Combine(logs, "po"));

// Auth/Scans/PlcSettings dung duong dan TUONG DOI so voi AppContext.BaseDirectory
// -> de chay lai nhieu lan thi DB se ton tai (va mat khau admin da doi) .
// Dung duong dan TUYET DOI tro vao sandbox -> moi lan chay deu co DB sach.
string AbsDb(string relative) => Path.Combine(root, relative).Replace("\\", "/");

File.WriteAllText(Path.Combine(root, "appsettings.json"), $$"""
{
  "Camera": { "Ip": "127.0.0.1", "Port": 9000, "DuplicateWindowSeconds": 30 },
  "Plc": { "Ip": "127.0.0.1", "Port": 9600, "RemoteNode": 0, "LocalNode": 1, "TimeoutMs": 1000, "Retries": 0, "ResultAddress": "D12", "ReadyAddress": "D16", "RefreshIntervalMs": 1000 },
  "Web": { "Url": "https://estrace.checkee.vn", "Enabled": false },
  "Scanner": { "Enabled": false, "Mode": "TcpServer", "TcpHost": "127.0.0.1", "TcpPort": 6999, "ComPort": "COM3", "BaudRate": 9600 },
  "Po": { "BaseUrl": "http://127.0.0.1:5080", "DbRoot": "{{AbsDb("logs/po")}}", "ActivationIntervalMs": 2500, "AutoActivate": false, "CodeSampleSize": 100 },
  "Auth": { "DbPath": "{{AbsDb("logs/users.db")}}", "MaxFailedAttempts": 5, "LockoutMinutes": 5, "Pbkdf2Iterations": 210000, "SeedAdminUser": "admin", "SeedAdminPassword": "admin" },
  "Logging": { "Sqlite": { "Path": "{{AbsDb("logs/app.db")}}", "MinLevel": "Warning", "RetentionDays": 30, "EnableUserActions": true, "BatchSize": 100, "FlushIntervalMs": 1000, "PruneIntervalMs": 3600000 }, "History": { "Enabled": true, "PageSize": 200 } },
  "PlcSettings": { "DbPath": "{{AbsDb("logs/plc.db")}}" },
  "Scans": { "DbPath": "{{AbsDb("logs/scans.db")}}", "BatchSize": 100, "FlushIntervalMs": 1000, "QueueCapacity": 20000, "RetentionDays": 0, "PruneIntervalMs": 3600000 }
}
""");

// ---------------------------------------------------------------
// DI container (mirror App) — KHONG host.Start() de khong chay worker
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
using var host = builder.Build();

var services = host.Services;

// Bàn phím ảo là singleton toàn cục (giống App).
var keyboardService = services.GetRequiredService<KeyboardService>();
VirtualKeyboardHost.Register(keyboardService);

// Seed tài khoản quản trị (giống App).
var userService = services.GetRequiredService<ClassLibraryMT.Auth.IUserService>();
await userService.EnsureSeedAdminAsync();

// ---------------------------------------------------------------
// Helper reflection (VM la internal)
// ---------------------------------------------------------------
const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

object? GetProp(object target, string name) =>
    target.GetType().GetProperty(name, All)?.GetValue(target);

void SetProp(object target, string name, object? value)
{
    var prop = target.GetType().GetProperty(name, All)
        ?? throw new MissingMemberException(target.GetType().FullName, name);
    prop.SetValue(target, value);
}

bool GetBool(object target, string name) => (bool)(GetProp(target, name) ?? false);

double GetDouble(object target, string name) => (double)(GetProp(target, name) ?? 0d);

string GetStr(object target, string name) => (string)(GetProp(target, name) ?? string.Empty);

async Task ExecAsync(object target, string commandPropertyName)
{
    var prop = target.GetType().GetProperty(commandPropertyName, All)
        ?? throw new MissingMemberException(target.GetType().FullName, commandPropertyName);

    var cmd = prop.GetValue(target)
        ?? throw new InvalidOperationException($"Command {commandPropertyName} = null.");

    // CommunityToolkit: dung `Execute` roi lay `ExecutionTask` (giong UiAuditHarness).
    // `ExecuteAsync` co the deadlock trong headless vi no yeu cau dispatcher.
    var executionTaskProp = cmd.GetType().GetProperty("ExecutionTask", All);
    var hasExecuteAsync = cmd.GetType().GetMethod("ExecuteAsync", All) is not null;

    if (hasExecuteAsync && executionTaskProp is not null)
    {
        cmd.GetType().GetMethod("Execute", All)?.Invoke(cmd, new object?[] { null });

        var task = executionTaskProp.GetValue(cmd) as Task;
        if (task is not null)
        {
            await task;
        }

        return;
    }

    cmd.GetType().GetMethod("Execute", All)?.Invoke(cmd, new object?[] { null });
}

int CountWindows()
{
    return Avalonia.Application.Current?.ApplicationLifetime
        is IClassicDesktopStyleApplicationLifetime desktop
            ? desktop.Windows.Count
            : 0;
}

// ---------------------------------------------------------------
// Tao MainWindow 1 lan duy nhat (dung luong cua App)
// ---------------------------------------------------------------
// MainViewModel la `internal` -> lay qua reflection.
var mainVmType = typeof(App).Assembly.GetType("TanTienQR.ViewModels.MainViewModel")!;
var mainVm = services.GetRequiredService(mainVmType);
var mainWindow = new MainWindow { DataContext = mainVm };
keyboardService.Attach(mainWindow);

mainWindow.Show();
Dispatcher.UIThread.RunJobs();

Console.WriteLine("\n[1] MainWindow hien ngay + overlay phu len");

Check("MainWindow da hien", mainWindow.IsVisible);
Check("Overlay dang nhap bat dau HIEN",
    GetBool(mainVm, "IsLoginOverlayVisible"),
    "IsLoginOverlayVisible=" + GetBool(mainVm, "IsLoginOverlayVisible"));

// Overlay la LoginView (UserControl) nam trong MainWindow.
var loginView = mainWindow.GetVisualDescendants().OfType<LoginView>().FirstOrDefault();
Check("Co LoginView trong cay hinh anh cua MainWindow", loginView is not null);

var loginVm = GetProp(mainVm, "Login");
Check("LoginView dung chung VM voi MainViewModel",
    loginView is not null && ReferenceEquals(loginView.DataContext, loginVm),
    loginView?.DataContext?.GetType().Name ?? "null");
Check("LoginViewModel la singleton (cung instance qua lai)",
    ReferenceEquals(loginVm, GetProp(mainVm, "Login")));

// Overlay nam o ZIndex cao -> ve tren tat ca noi dung khac.
var overlayBorder = mainWindow.GetVisualDescendants()
    .OfType<Border>()
    .FirstOrDefault(b => b.Child is LoginView);
Check("Overlay la Border phu len (khong phai view inline)", overlayBorder is not null);
Check("Overlay co ZIndex cao",
    overlayBorder is not null && overlayBorder.ZIndex >= 1000,
    "ZIndex=" + (overlayBorder?.ZIndex.ToString() ?? "null"));

Console.WriteLine("\n[2] Form dang nhap render dung");

var loginTextBoxes = loginView?.GetVisualDescendants().OfType<TextBox>().ToList() ?? new List<TextBox>();
Check("Co TextBox ten dang nhap", loginTextBoxes.Any(t => t.Name == "UsernameBox"));
Check("Co TextBox mat khau", loginTextBoxes.Any(t => t.Name == "LoginPasswordBox"));
Check("Co nut Dang nhap",
    loginView?.GetVisualDescendants().OfType<Button>().Any(b => b.Content as string == "Đăng nhập") == true);
Check("Co nut Thoat ung dung",
    loginView?.GetVisualDescendants().OfType<Button>().Any(b => b.Content as string == "Thoát ứng dụng") == true);
Check("Bam phim co ban phim ao",
    loginView?.GetVisualDescendants().OfType<VirtualKeyboard>().Any() == true);

// Ban phim ao trong overlay VA trong cua so chinh dung CHUNG 1 ViewModel
// (singleton) -> chi mot ban phim hien, dung cho ca hai noi dung.
var overlayKeyboard = loginView?.GetVisualDescendants().OfType<VirtualKeyboard>().FirstOrDefault();
var mainKeyboard = mainWindow.GetVisualDescendants()
    .OfType<VirtualKeyboard>()
    .FirstOrDefault(k => !ReferenceEquals(k, overlayKeyboard));
Check("Cua so chinh cung co ban phim ao", mainKeyboard is not null);
Check("Hai ban phim ao dung CHUNG 1 ViewModel",
    overlayKeyboard is not null && mainKeyboard is not null
        && ReferenceEquals(overlayKeyboard.DataContext, mainKeyboard.DataContext),
    "overlay=" + (overlayKeyboard?.DataContext?.GetType().Name ?? "null")
        + " main=" + (mainKeyboard?.DataContext?.GetType().Name ?? "null"));

// Overlay phai focus o "Ten dang nhap" khi mo (buoc 1).
var focusedName = mainWindow.FocusManager?.GetFocusedElement() is TextBox focused
    ? focused.Name
    : null;
Check("Overlay focus o 'Ten dang nhap' khi mo", focusedName == "UsernameBox",
    "focused=" + (focusedName ?? "null"));

Console.WriteLine("\n[3] FullScreen ngay tu dau (khong chuyen state 2 lan)");

Check("WindowState = FullScreen", mainWindow.WindowState == WindowState.FullScreen,
    "WindowState=" + mainWindow.WindowState);

Console.WriteLine("\n[4] Tab lazy: chi InfoView dung san");

var tabHosts = mainWindow.GetVisualDescendants().OfType<LazyTabHost>().ToList();
Check("Co 11 LazyTabHost", tabHosts.Count == 11, "count=" + tabHosts.Count);

var createdHosts = tabHosts.Where(h => h.IsCreated).ToList();
Check("Chi 1 tab duoc dung san (Info)",
    createdHosts.Count == 1,
    "created=" + string.Join(",", createdHosts.Select(h => h.Name)));

// Cho dispatcher chay (headless: phai pump co tay) trong khi delay.
// KHONG dung `await Task.Delay` o day: trong headless, await se resume tren
// Avalonia SynchronizationContext, ma context chi duoc pump khi RunJobs() chay
// -> deadlock. Thay vao do: vong lap trong khi sleep ngan tren luong hien tai.
void PumpFor(int ms)
{
    var deadline = Environment.TickCount64 + ms;
    while (Environment.TickCount64 < deadline)
    {
        Dispatcher.UIThread.RunJobs();
        Thread.Sleep(10);
    }

    Dispatcher.UIThread.RunJobs();
    Dispatcher.UIThread.RunJobs();
}
Console.WriteLine("\n[5] Dang nhap thanh cong -> overlay AN, khong tao cua so moi");

var windowCountBefore = CountWindows();

SetProp(loginVm!, "Username", "admin");
SetProp(loginVm!, "Password", "admin");
Dispatcher.UIThread.RunJobs();

await ExecAsync(loginVm!, "LoginCommand");
Dispatcher.UIThread.RunJobs();
Dispatcher.UIThread.RunJobs();

// Tai khoan seed (admin/admin) BANG BUOC doi mat khau lan dau -> buoc 2.
// Neu vay thi hoan tat buoc 2 truoc khi kiem tra overlay.
if (GetBool(loginVm!, "IsChangePasswordStep"))
{
    Console.WriteLine("  (buoc 2: doi mat khau lan dau)");

    SetProp(loginVm!, "CurrentPassword", "admin");
    SetProp(loginVm!, "NewPassword", "Overlay123!");
    SetProp(loginVm!, "ConfirmPassword", "Overlay123!");
    Dispatcher.UIThread.RunJobs();

    await ExecAsync(loginVm!, "ConfirmPasswordChangeCommand");
    Dispatcher.UIThread.RunJobs();
    Dispatcher.UIThread.RunJobs();
}

Check("Dang nhap thanh cong (IsAuthenticated)", GetBool(loginVm!, "IsAuthenticated"),
    "Error=" + GetStr(loginVm!, "ErrorMessage"));

// Biet moi co RequestAuthenticated -> MainViewModel -> overlay fade di.
// Overlay dung IS_VISIBLE = false sau khi het hieu ung mo (200ms) ->
// phai cho dispatcher chay de thay timer cua DispatcherTimer.
PumpFor(350);
Dispatcher.UIThread.RunJobs();
Dispatcher.UIThread.RunJobs();

Check("Overlay da AN sau khi dang nhap",
    !GetBool(mainVm, "IsLoginOverlayVisible"),
    "IsLoginOverlayVisible=" + GetBool(mainVm, "IsLoginOverlayVisible"));
Check("Overlay da fade ra (Opacity = 0)",
    Math.Abs(GetDouble(mainVm, "LoginOverlayOpacity")) < 0.001,
    "opacity=" + GetDouble(mainVm, "LoginOverlayOpacity"));
Check("KHONG tao them cua so nao",
    CountWindows() == windowCountBefore,
    "before=" + windowCountBefore + " after=" + CountWindows());
Check("Cua so chinh van con hien", mainWindow.IsVisible);

Console.WriteLine("\n[6] Thong tin user + quyen cap nhat sau dang nhap");

Check("Co ten hien thi user", GetStr(mainVm, "CurrentUserDisplayName") != "—",
    "user=" + GetStr(mainVm, "CurrentUserDisplayName"));
Check("Co vai tro hien thi", GetStr(mainVm, "CurrentRoleText") != "—",
    "role=" + GetStr(mainVm, "CurrentRoleText"));
Check("Quan tri co quyen quan ly tai khoan", GetBool(mainVm, "CanManageUsers"));
Check("Quan tri co quyen xem lich su", GetBool(mainVm, "CanViewHistory"));
Check("Quan tri co quyen mo tab Chinh PLC", GetBool(mainVm, "CanEditPlc"));

Console.WriteLine("\n[7] Mo tab nang -> dung view lazy dung luc");

// KHONG mo tab San xuat / Chinh PLC trong headless: chung chua WebView2
// (WebView2 can COM apartment -> se nem RPC_E_CHANGED_MODE o che do headless).
// Dung cac tab nhe nhung van "nang" (bang du lieu, timer, danh sach service).

// Mo tab Lich su (bang du lieu lon).
await ExecAsync(mainVm, "ShowHistoryTabCommand");
Dispatcher.UIThread.RunJobs();

var historyHost = tabHosts.FirstOrDefault(h => h.Name == "HistoryTab");
Check("Tab Lich su da dung view lazy", historyHost?.IsCreated == true);
Check("Noi dung tab la HistoryView",
    historyHost?.Content is HistoryView,
    historyHost?.Content?.GetType().Name ?? "null");

// Ve lai tab Thong tin -> view cu khong bi dung lai (cache).
var infoHost = tabHosts.FirstOrDefault(h => h.Name == "InfoTab");
var infoContentBefore = infoHost?.Content;
await ExecAsync(mainVm, "ShowInfoTabCommand");
Dispatcher.UIThread.RunJobs();
Check("Tab Thong tin giu nguyen view da cache (khong dung lai)",
    ReferenceEquals(infoHost?.Content, infoContentBefore));

// Tab chua mo -> van chua dung (lazy that su).
var codeLookupHost = tabHosts.FirstOrDefault(h => h.Name == "CodeLookupTab");
Check("Tab chua mo -> chua dung view", codeLookupHost?.IsCreated == false);

// Mo tab Tra cuu ma (mo cho moi vai tro).
await ExecAsync(mainVm, "ShowCodeLookupTabCommand");
Dispatcher.UIThread.RunJobs();
Check("Tab Tra cuu ma da dung view lazy", codeLookupHost?.IsCreated == true);
Check("Noi dung tab la CodeLookupView",
    codeLookupHost?.Content is CodeLookupView,
    codeLookupHost?.Content?.GetType().Name ?? "null");

// Tab Dev: chi Lap trinh vien duoc mo -> voi vai tri Quan tri thi KHONG mo,
// va view khong duoc dung (lazy).
await ExecAsync(mainVm, "ShowDevTabCommand");
Dispatcher.UIThread.RunJobs();

var devHost = tabHosts.FirstOrDefault(h => h.Name == "DevTab");
var devAllowed = GetBool(mainVm, "CanViewDev");
Check("Tab Dev: dung view chi khi vai tro cho phep",
    devHost?.IsCreated == devAllowed,
    "allowed=" + devAllowed + " created=" + devHost?.IsCreated);

Console.WriteLine("\n[8] Dang xuat -> overlay HIEN LAI + form reset sach");

await ExecAsync(mainVm, "LogoutCommand");
Dispatcher.UIThread.RunJobs();

Check("Overlay hien lai sau dang xuat", GetBool(mainVm, "IsLoginOverlayVisible"),
    "IsLoginOverlayVisible=" + GetBool(mainVm, "IsLoginOverlayVisible"));
Check("Overlay hien lai day du (Opacity = 1)",
    Math.Abs(GetDouble(mainVm, "LoginOverlayOpacity") - 1d) < 0.001,
    "opacity=" + GetDouble(mainVm, "LoginOverlayOpacity"));
Check("Form reset: Username rong", GetStr(loginVm!, "Username").Length == 0,
    "u=" + GetStr(loginVm!, "Username"));
Check("Form reset: Password rong", GetStr(loginVm!, "Password").Length == 0);
Check("Form reset: Error rong", GetStr(loginVm!, "ErrorMessage").Length == 0);
Check("Form reset: ve buoc 1 (SignIn)", GetBool(loginVm!, "IsSignInStep"));
Check("Form reset: IsAuthenticated = false", !GetBool(loginVm!, "IsAuthenticated"));
Check("KHONG tao them cua so nao khi dang xuat", CountWindows() == windowCountBefore,
    "before=" + windowCountBefore + " after=" + CountWindows());
Check("Cua so chinh van con hien", mainWindow.IsVisible);

Console.WriteLine("\n[9] Dang nhap lai van duoc");

// Mat khau admin da doi o [5] -> dung mat khau moi.
SetProp(loginVm!, "Username", "admin");
SetProp(loginVm!, "Password", "Overlay123!");
await ExecAsync(loginVm!, "LoginCommand");
Dispatcher.UIThread.RunJobs();
Dispatcher.UIThread.RunJobs();
PumpFor(350);
Dispatcher.UIThread.RunJobs();
Dispatcher.UIThread.RunJobs();

Check("Dang nhap lai thanh cong", GetBool(loginVm!, "IsAuthenticated"),
    "Error=" + GetStr(loginVm!, "ErrorMessage"));
Check("Overlay lai an", !GetBool(mainVm, "IsLoginOverlayVisible"));
Check("Van chi co 1 cua so", CountWindows() == windowCountBefore);

mainWindow.Close();
Dispatcher.UIThread.RunJobs();

Console.WriteLine("\n========================================");
Console.WriteLine($"  KET QUA: {passed} pass / {failed} fail");
Console.WriteLine("========================================");

return failed == 0 ? 0 : 1;
