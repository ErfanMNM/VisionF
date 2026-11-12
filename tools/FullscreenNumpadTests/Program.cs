using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ClassLibraryMT.Plc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TanTienQR.Services;

// ===================================================================
// KIEM THU OFF-SCREEN: fullscreen + bug bàn phím số tab "Chỉnh PLC".
//
// Chạy: dotnet run --project tools\FullscreenNumpadTests
//
// Dùng Avalonia.Headless + Skia: KHÔNG mở cửa sổ thật, KHÔNG chụp màn
// hình desktop, KHÔNG ảnh hưởng phiên làm việc của người dùng.
//
// Kiểm tra:
//   [1] MainWindow tự FULLSCREEN khi mở
//   [2] F11 toggle fullscreen, Esc thoát fullscreen
//   [3] Bàn phím số: nhấn OK -> panel bàn phím ĐÓNG (IsKeypadOpen = false)
//   [4] Bàn phím số: nhấn OK -> giá trị được áp dụng vào dòng
//   [5] Bàn phím số: giá trị KHÔNG hợp lệ -> panel GIỮ MỞ + báo lỗi
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

Console.WriteLine("==========================================");
Console.WriteLine("  KIEM THU OFF-SCREEN - FULLSCREEN + NUMPAD");
Console.WriteLine("==========================================");

// ---------------------------------------------------------------
// [0] Chu bi moi (headless, Skia -> bitmap, khong cua so that)
// ---------------------------------------------------------------
AppBuilder.Configure<TanTienQR.App>()
    .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
    .UseSkia()
    .WithInterFont()
    .SetupWithoutStarting();

Console.WriteLine("[Harness] Avalonia headless (Skia) khoi dong.");

// ---------------------------------------------------------------
// Sandbox rieng cho test: config goc + thu muc ghi rieng
// ---------------------------------------------------------------
var root = Path.Combine(AppContext.BaseDirectory, "fullscreen-numpad-test");
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
  "Web": { "Url": "https://estrace.checkee.vn", "Enabled": false },
  "Scanner": { "Enabled": false, "Mode": "TcpServer", "TcpHost": "127.0.0.1", "TcpPort": 6999, "ComPort": "COM3", "BaudRate": 9600 },
  "Po": { "BaseUrl": "http://127.0.0.1:5080", "DbRoot": "logs/po", "ActivationIntervalMs": 2500, "AutoActivate": false, "CodeSampleSize": 100 },
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
var asm = typeof(TanTienQR.App).Assembly;

// ---------------------------------------------------------------
// Reflection helpers: ViewModel/control la "internal" nen phai
// dung reflection de goi (khong mo duoc trong project test).
// ---------------------------------------------------------------
object? GetProp(object target, string name) =>
    target.GetType()
        .GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
        ?.GetValue(target);

object? GetField(object target, string name) =>
    target.GetType()
        .GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
        ?.GetValue(target);

void InvokeMethod(object target, string name, params object?[] args) =>
    target.GetType()
        .GetMethod(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)!
        .Invoke(target, args);

/// <summary>Gọi method trả về giá trị (bool) qua reflection.</summary>
bool InvokeBool(object target, string name, params object?[] args) =>
    (bool)(target.GetType()
        .GetMethod(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)!
        .Invoke(target, args) ?? false);

object? ExecCommand(object target, string commandProp, object? param = null)
{
    var cmd = target.GetType()
        .GetProperty(commandProp, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)!
        .GetValue(target)!;

    // RelayCommand<T> co ca hai overload Execute(T) va Execute(object) ->
    // GetMethod("Execute") se "AmbiguousMatch". Chon dung overload theo
    // kieu tham so truyen vao.
    var exec = cmd.GetType()
        .GetMethods(BindingFlags.Public | BindingFlags.Instance)
        .First(m =>
            m.Name == "Execute"
            && m.GetParameters().Length == 1
            && m.GetParameters()[0].ParameterType.IsInstanceOfType(param));

    exec.Invoke(cmd, new[] { param });
    return null;
}

// ===================================================================
// [1] + [2] FULLSCREEN
// ===================================================================
Console.WriteLine("\n[1] MainWindow tu FULLSCREEN khi mo");

var mainWindowType = asm.GetType("TanTienQR.Views.MainWindow")!;
var mainWindow = (Window)Activator.CreateInstance(mainWindowType)!;

// LƯU Ý: MainWindow chứa PlcSettingsView -> NativeWebView (WebView2).
// WebView2 không chạy được trong Avalonia.Headless (lỗi COM
// "Cannot change thread mode after it is set") -> tao KHÔNG Show() cửa sổ
// thật ở đây. Thay vào đó ta gọi trực tiếp logic fullscreen + OnKeyDown,
// đúng những gì app làm khi mở (OnOpened -> SetFullScreen(true)).
InvokeMethod(mainWindow, "SetFullScreen", true);

Check(
    "SetFullScreen(true) -> WindowState = FullScreen",
    mainWindow.WindowState == WindowState.FullScreen,
    "WindowState = " + mainWindow.WindowState);

Check(
    "Co co fullscreen bat (khong phai maximize)",
    (bool?)GetField(mainWindow, "_isFullScreen") == true);

// Kiem tra OnOpened that su gọi SetFullScreen(true) (app tu fullscreen khi mo).
var openedType = mainWindowType.GetMethod(
    "OnOpened",
    BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public);

Check("Co the goi OnOpened (logic auto-fullscreen)", openedType is not null);

Console.WriteLine("\n[2] Phim tat F11 / Esc");

// F11 -> toggle
mainWindow.RaiseEvent(new KeyEventArgs
{
    RoutedEvent = InputElement.KeyDownEvent,
    Key = Key.F11,
    Source = mainWindow,
});

Dispatcher.UIThread.RunJobs();

Check(
    "F11 thoat fullscreen -> ve maximized",
    mainWindow.WindowState == WindowState.Maximized,
    "WindowState = " + mainWindow.WindowState);

Check(
    "F11 thoat fullscreen -> co flag false",
    (bool?)GetField(mainWindow, "_isFullScreen") == false);

// F11 lan nua -> lai fullscreen
mainWindow.RaiseEvent(new KeyEventArgs
{
    RoutedEvent = InputElement.KeyDownEvent,
    Key = Key.F11,
    Source = mainWindow,
});

Dispatcher.UIThread.RunJobs();

Check(
    "F11 lan 2 -> lai fullscreen",
    mainWindow.WindowState == WindowState.FullScreen,
    "WindowState = " + mainWindow.WindowState);

// Esc -> thoat fullscreen
mainWindow.RaiseEvent(new KeyEventArgs
{
    RoutedEvent = InputElement.KeyDownEvent,
    Key = Key.Escape,
    Source = mainWindow,
});

Dispatcher.UIThread.RunJobs();

Check(
    "Esc thoat fullscreen -> ve maximized",
    mainWindow.WindowState == WindowState.Maximized,
    "WindowState = " + mainWindow.WindowState);

Check(
    "Esc thoat fullscreen -> co flag false",
    (bool?)GetField(mainWindow, "_isFullScreen") == false);

// Esc khi KHONG fullscreen -> phai giu nguyen hành vi mac dinh
// (khong thoat khoi trang thai Maximized hien tai).
InvokeMethod(mainWindow, "SetFullScreen", true);
mainWindow.RaiseEvent(new KeyEventArgs
{
    RoutedEvent = InputElement.KeyDownEvent,
    Key = Key.F11,
    Source = mainWindow,
});

Dispatcher.UIThread.RunJobs();

mainWindow.RaiseEvent(new KeyEventArgs
{
    RoutedEvent = InputElement.KeyDownEvent,
    Key = Key.Escape,
    Source = mainWindow,
});

Dispatcher.UIThread.RunJobs();

Check(
    "Esc khi khong fullscreen -> giu nguyen maximized",
    mainWindow.WindowState == WindowState.Maximized,
    "WindowState = " + mainWindow.WindowState);

// ===================================================================
// [3] + [4] + [5] BAN PHIM SO
// ===================================================================
Console.WriteLine("\n[3] Ban phim so: OK -> panel phai DONG");

var plcSettingsType = asm.GetType("TanTienQR.ViewModels.PlcSettingsViewModel")!;
var plcSettings = services.GetRequiredService(plcSettingsType);

var param1 = new ClassLibraryMT.Plc.PlcParameter
{
    Id = 1,
    Name = "Delay Camera - Short",
    Address = "D12",
    ValueType = ClassLibraryMT.Plc.PlcValueType.Int16,
    AccessMode = ClassLibraryMT.Plc.PlcAccessMode.ReadWrite,
    Description = "Test row",
};

var rowType = asm.GetType("TanTienQR.ViewModels.PlcParamRowViewModel")!;

// PlcParamRowViewModel la `internal` nen constructor khong public ->
// phai tao qua BindingFlags (NonPublic) thay vi Activator.CreateInstance mac dinh.
var row = Activator.CreateInstance(
    rowType,
    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
    binder: null,
    args: new object[] { param1 },
    culture: null)!;

// Dong vao bang tham so (UI dung ItemsControl tren ParameterRows).
var rows = (System.Collections.IEnumerable)GetProp(plcSettings, "ParameterRows")!;
rows.GetType().GetMethod("Add")!.Invoke(rows, new[] { row });

Check(
    "Bang tham so co 1 dong",
    ((System.Collections.ICollection)rows).Count == 1);

// Mo ban phim so cho dong nay (tuong duong bam nut "mo ban phim so").
ExecCommand(plcSettings, "OpenKeypadCommand", row);

Check(
    "Ban phim so da mo (IsKeypadOpen = true)",
    (bool?)GetProp(plcSettings, "IsKeypadOpen") == true);

Check(
    "Ban phim so gan dung dong (KeypadRow)",
    ReferenceEquals(GetProp(plcSettings, "KeypadRow"), row));

// Ghi vao gia tri qua ViewModel cua ban phim so.
var keypad = GetProp(plcSettings, "Keypad")!;
InvokeMethod(keypad, "InputDigit", "1");
InvokeMethod(keypad, "InputDigit", "2");
InvokeMethod(keypad, "InputDigit", "3");

Check(
    "Gia tri dang nhap = 123",
    (string?)GetProp(row, "InputValue") == "123",
    "InputValue = " + (string?)GetProp(row, "InputValue"));

// Bam OK -> Commit()
var committed = InvokeBool(keypad, "Commit");
Dispatcher.UIThread.RunJobs();

Check("Commit() tra true", committed);

Check(
    "OK -> panel ban phim da DONG (IsKeypadOpen = false)",
    (bool?)GetProp(plcSettings, "IsKeypadOpen") == false,
    "IsKeypadOpen = " + (bool?)GetProp(plcSettings, "IsKeypadOpen"));

Check(
    "OK -> KeypadRow da duoc xoa",
    GetProp(plcSettings, "KeypadRow") is null);

Check(
    "OK -> ban phim khong con gan dong nao",
    GetProp(keypad, "Row") is null);

Console.WriteLine("\n[4] Ban phim so: OK -> ap dung gia tri vao dong");

Check(
    "Gia tri da duoc ap dung vao dong (123)",
    (string?)GetProp(row, "InputValue") == "123",
    "InputValue = " + (string?)GetProp(row, "InputValue"));

Check(
    "Dong khong bi danh dau loi",
    (bool?)GetProp(row, "IsError") == false);

Console.WriteLine("\n[5] Ban phim so: gia tri SAI -> phai GIU MO + bao loi");

ExecCommand(plcSettings, "OpenKeypadCommand", row);

Check(
    "Mo lai ban phim so",
    (bool?)GetProp(plcSettings, "IsKeypadOpen") == true);

// Ghi mot gia tri khong hop le: vượt quá Int16 (32767).
InvokeMethod(keypad, "Clear");
InvokeMethod(keypad, "InputDigit", "9");
InvokeMethod(keypad, "InputDigit", "9");
InvokeMethod(keypad, "InputDigit", "9");
InvokeMethod(keypad, "InputDigit", "9");
InvokeMethod(keypad, "InputDigit", "9");

var committedBad = InvokeBool(keypad, "Commit");
Dispatcher.UIThread.RunJobs();

Check("Commit() tra false khi gia tri sai", !committedBad);

Check(
    "Gia tri sai -> ban phim van MO (khong tu dong)",
    (bool?)GetProp(plcSettings, "IsKeypadOpen") == true,
    "IsKeypadOpen = " + (bool?)GetProp(plcSettings, "IsKeypadOpen"));

Check(
    "Gia tri sai -> dong duoc danh dau loi",
    (bool?)GetProp(row, "IsError") == true);

Check(
    "Gia tri sai -> co thong bao loi",
    !string.IsNullOrWhiteSpace((string?)GetProp(row, "StatusText")),
    (string?)GetProp(row, "StatusText") ?? "");

// ---------------------------------------------------------------
// [6] UI that render off-screen (compiled binding khong loi)
// ---------------------------------------------------------------
// LƯU Ý: PlcSettingsView chứa NativeWebView (WebView2) nên KHÔNG render
// được trong Avalonia.Headless (lỗi COM "Cannot change thread mode").
// Thay vào đó ta render control bàn phím số (PlcValueKeypad) — đúng control
// mà người dùng bấm nút OK — để chắc chắn nút OK được nối đúng handler.
Console.WriteLine("\n[6] Control ban phim so render off-screen");

try
{
    var keypadType = asm.GetType("TanTienQR.Controls.PlcValueKeypad")!;
    var keypadControl = (Control)Activator.CreateInstance(keypadType)!;
    keypadControl.DataContext = keypad;

    var window = new Window
    {
        Width = 320,
        Height = 320,
        Content = keypadControl,
    };

    window.Show();
    Dispatcher.UIThread.RunJobs();

    var viewCount = CountVisuals(keypadControl);
    Check("PlcValueKeypad tao duoc va render", viewCount > 10, "visuals=" + viewCount);

    // Tim nut OK trong cay visual va bam that (kiem tra Click -> Commit).
    var okButton = FindByText(keypadControl, "OK");
    Check("Tim thay nut OK tren ban phim so", okButton is not null);

    if (okButton is not null)
    {
        // Mo lai ban phim so cho dong (gia tri hien tai la 99999 -> sai).
        ExecCommand(plcSettings, "OpenKeypadCommand", row);
        InvokeMethod(keypad, "Clear");
        InvokeMethod(keypad, "InputDigit", "5");

        // Bam nut OK that.
        okButton.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs
        {
            RoutedEvent = Button.ClickEvent,
            Source = okButton,
        });
        Dispatcher.UIThread.RunJobs();

        Check(
            "Bam nut OK that -> panel ban phim DONG",
            (bool?)GetProp(plcSettings, "IsKeypadOpen") == false,
            "IsKeypadOpen = " + (bool?)GetProp(plcSettings, "IsKeypadOpen"));

        Check(
            "Bam nut OK that -> gia tri 5 da duoc ap dung",
            (string?)GetProp(row, "InputValue") == "5",
            "InputValue = " + (string?)GetProp(row, "InputValue"));
    }

    window.Close();
    Dispatcher.UIThread.RunJobs();
}
catch (Exception ex)
{
    Check("PlcValueKeypad tao duoc va render", false, ex.Message);
}

/// <summary>Tim control co Text/Content chua duong chon (de tim nut OK).</summary>
static Control? FindByText(Control root, string text)
{
    var queue = new Queue<Control>();
    queue.Enqueue(root);

    while (queue.Count > 0)
    {
        var cur = queue.Dequeue();

        if (cur is Button btn
            && string.Equals(btn.Content?.ToString(), text, StringComparison.Ordinal))
        {
            return btn;
        }

        foreach (var child in cur.GetVisualChildren().OfType<Control>())
        {
            queue.Enqueue(child);
        }
    }

    return null;
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
Console.WriteLine("\n==========================================");
Console.WriteLine($"  KET QUA: {passed} pass / {failed} fail");
Console.WriteLine("==========================================");

return failed == 0 ? 0 : 1;
