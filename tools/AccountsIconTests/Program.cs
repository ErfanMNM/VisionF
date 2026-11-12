using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TanTienQR.Services;
using IOPath = System.IO.Path;
using AvaloniaPath = Avalonia.Controls.Shapes.Path;

// ===================================================================
// KIEM THU OFF-SCREEN: trang "Tai khoan nguoi dung" (AccountsView).
//
// Chay: dotnet run --project tools\AccountsIconTests
//
// Dung Avalonia.Headless + Skia: KHONG mo cua so that tren desktop,
// KHONG chup man hinh, KHONG anh huong phien lam viec cua nguoi dung.
//
// Kiem tra:
//   [1] Icon cua 4 nut thao tac (Sua / Mat khau / Khoa / Xoa) duoc TO MAU,
//       khac nhau, va khac mau chu mac dinh (TextPrimary).
//   [2] Nut trong hang khong bi cat chu: Bounds.Width >= noi dung can cho,
//       va chieu cao bang 32px (khong tran doc nhu truoc khi Height=44).
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
Console.WriteLine("  KIEM THU OFF-SCREEN - ICON MAU COT THAO TAC");
Console.WriteLine("==========================================");

// Mau chu mac dinh cua app - icon nen PHAI khac mau nay de "co mau sac".
var textPrimaryColor = Color.Parse("#FF1F2933");

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
var root = IOPath.Combine(AppContext.BaseDirectory, "accounts-icon-test");
if (Directory.Exists(root))
{
    Directory.Delete(root, recursive: true);
}

Directory.CreateDirectory(root);

var builder = Host.CreateApplicationBuilder();
builder.Configuration
    .SetBasePath(root)
    .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false)
    .AddJsonFile("appsettings.Development.json", optional: true, reloadOnChange: false)
    .AddEnvironmentVariables("TANTIEN_");

builder.Logging.AddConsole();
builder.Logging.SetMinimumLevel(LogLevel.Warning);
builder.Services.AddTanTienServices(builder.Configuration);
builder.Configuration["Config:BasePath"] = root;

using var host = builder.Build();
host.Start();

var sp = host.Services;
var appAsm = typeof(TanTienQR.App).Assembly;

// ---------------------------------------------------------------
// [1] Tao du lieu 1 tai khoan de render 1 dong bang
// ---------------------------------------------------------------
Console.WriteLine("\n[1] Tao du lieu tai khoan de render dong bang");

object row;
try
{
    var rowType = appAsm.GetType("TanTienQR.ViewModels.UserRowViewModel", throwOnError: true)!;

    // UserAccount nam o assembly khac (ClassLibraryMT.Auth) -> dung type truc tiep.
    var accountType = typeof(ClassLibraryMT.Auth.UserAccount);
    {
        var ctor = accountType.GetConstructors(BindingFlags.Public | BindingFlags.Instance)
            .OrderByDescending(c => c.GetParameters().Length)
            .First();

        var ps = ctor.GetParameters();
        var ctorArgs = new object?[ps.Length];
        for (var i = 0; i < ps.Length; i++)
        {
            var pt = ps[i].ParameterType;
            if (pt == typeof(string))
            {
                ctorArgs[i] = ps[i].Name?.Contains("User", StringComparison.OrdinalIgnoreCase) == true
                    ? "admin"
                    : ps[i].Name?.Contains("Display", StringComparison.OrdinalIgnoreCase) == true
                        ? "Quản trị viên"
                        : "Quản trị";
            }
            else if (pt == typeof(bool))
            {
                ctorArgs[i] = true;
            }
            else if (pt.IsValueType)
            {
                ctorArgs[i] = Activator.CreateInstance(pt);
            }
            else
            {
                ctorArgs[i] = null;
            }
        }

        var account = ctor.Invoke(ctorArgs);
        row = Activator.CreateInstance(rowType, account)!;
    }
}
catch (Exception ex)
{
    Check("Tao du lieu tai khoan", false, ex.Message);
    row = null!;
}

// ---------------------------------------------------------------
// [2] Render AccountsView off-screen
// ---------------------------------------------------------------
Console.WriteLine("\n[2] Render AccountsView off-screen");

Control? view = null;
try
{
    // AccountsViewModel that (co Items) + MainViewModel that (la DataContext).
    // Ca hai deu la `internal` -> dung reflection (khong them InternalsVisibleTo
    // vao source app).
    var accountsVmType = appAsm.GetType("TanTienQR.ViewModels.AccountsViewModel", throwOnError: true)!;
    var mainVmType = appAsm.GetType("TanTienQR.ViewModels.MainViewModel", throwOnError: true)!;

    var accountsVm = sp.GetRequiredService(accountsVmType);
    var mainVm = sp.GetRequiredService(mainVmType);

    // Them 1 dong tai khoan vao Items de render that DataTemplate.
    var itemsProp = accountsVmType.GetProperty("Items", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)!;
    var items = itemsProp.GetValue(accountsVm)!;
    var itemsType = items.GetType();
    itemsType.GetMethod("Add")!.Invoke(items, [row]);
    Console.WriteLine($"  [Harness] Items.Count = {itemsType.GetProperty("Count")!.GetValue(items)}");

    var viewType = appAsm.GetType("TanTienQR.Views.AccountsView", throwOnError: true)!;
    view = (Control)Activator.CreateInstance(viewType)!;
    view.DataContext = mainVm;

    var window = new Window
    {
        Width = 1400,
        Height = 800,
        Content = view,
    };

    window.Show();
    Dispatcher.UIThread.RunJobs();

    var visualCount = CountVisuals(view);
    Check("AccountsView tao duoc va render", visualCount > 10, "visuals=" + visualCount);

    // ---------------------------------------------------------------
    // [3] Tim 4 nut thao tac va do mau icon
    // ---------------------------------------------------------------
    Console.WriteLine("\n[3] Kiem tra mau icon 4 nut thao tac");

    var buttons = FindRowButtons(view);
    Check("Tim thay 4 nut thao tac trong dong bang", buttons.Count == 4, "so nut = " + buttons.Count);

    // Chuan doan: in ra tat ca Button tim duoc de biet vi sao khong match.
    if (buttons.Count != 4 && view is not null)
    {
        var allButtons = new List<string>();
        var q = new Queue<Control>();
        q.Enqueue(view);
        while (q.Count > 0)
        {
            var cur = q.Dequeue();
            if (cur is Button b)
            {
                var cls = string.Join(",", b.Classes);
                var lbl = FindLabelText(b);
                allButtons.Add($"[cls={cls}] label='{lbl}' w={b.Bounds.Width:F1} h={b.Bounds.Height:F1} HeightProp={b.Height:F1} MinH={b.MinHeight:F1} Pad={b.Padding}");
            }

            foreach (var child in cur.GetVisualChildren().OfType<Control>())
            {
                q.Enqueue(child);
            }
        }

        Console.WriteLine($"  [DIAG] Tong so Button trong cay = {allButtons.Count}");
        foreach (var line in allButtons.Take(25))
        {
            Console.WriteLine("         " + line);
        }
    }

    if (buttons.Count == 4)
    {
        var expected = new (string Label, string Color)[]
        {
            ("Sua", "Accent"),
            ("Mat khau", "Purple"),
            ("Khoa", "Warn"),
            ("Xoa", "Danger"),
        };

        var seen = new List<string>();

        foreach (var (btn, label) in buttons)
        {
            var path = FindFirstPath(btn);
            var stroke = path?.Stroke;

            var strokeColor = (stroke as ISolidColorBrush)?.Color;
            var strokeHex = strokeColor?.ToString() ?? "<null>";

            seen.Add(strokeHex);

            Check(
                $"Nut '{label}' co icon mau (khong phai mau chu mac dinh)",
                strokeColor is not null && strokeColor.Value != textPrimaryColor,
                $"stroke = {strokeHex}");

            Check(
                $"Nut '{label}' icon stroke duoc dat (khong null)",
                stroke is not null,
                "stroke = " + strokeHex);
        }

        Check(
            "4 nut co 4 mau icon KHAC NHAU",
            seen.Distinct().Count() == 4,
            "mau = " + string.Join(", ", seen));

        // Kiem tra nhanh: nut Xoa phai dung mau Danger (#D93025)
        var deleteBtn = buttons.Last();
        var deletePath = FindFirstPath(deleteBtn.Button);
        var deleteColor = (deletePath?.Stroke as ISolidColorBrush)?.Color;

        Check(
            "Nut 'Xoa' dung mau Danger (#D93025)",
            deleteColor?.ToString()?.StartsWith("#FFD93025", StringComparison.OrdinalIgnoreCase) == true,
            "stroke = " + (deleteColor?.ToString() ?? "<null>"));
    }

    // ---------------------------------------------------------------
    // [4] Nut khong bi cat chu + khong tran doc
    // ---------------------------------------------------------------
    Console.WriteLine("\n[4] Kiem tra kich thuoc nut thao tac (khong cat chu, khong tran doc)");

    if (view is not null)
    {
        foreach (var (btn, label) in FindRowButtons(view))
        {
            var contentWidth = btn.Content is Control content
                ? content.Bounds.Width
                : 0;

            Check(
                $"Nut '{label}' chieu rong >= noi dung (khong cat chu)",
                btn.Bounds.Width >= contentWidth - 0.5,
                $"button.Width = {btn.Bounds.Width:F1}, content.Width = {contentWidth:F1}");

            Check(
                $"Nut '{label}' chieu cao 32px (khong tran doc nhu Height=44)",
                Math.Abs(btn.Bounds.Height - 32) < 1.5,
                $"height = {btn.Bounds.Height:F1}");
        }
    }
    window.Close();
    Dispatcher.UIThread.RunJobs();
}
catch (Exception ex)
{
    Check("Render AccountsView off-screen", false, ex.Message);
}

// ---------------------------------------------------------------
// [5] Mau chu mac dinh de so sanh
// ---------------------------------------------------------------
Console.WriteLine("\n[5] Mau chu mac dinh de so sanh");
Console.WriteLine($"  TextPrimary = {textPrimaryColor}");

// ---------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------

/// <summary>Tim cac nut thao tac (rowbtn) trong cay visual, theo thu tu.</summary>
static List<(Button Button, string Label)> FindRowButtons(Control root)
{
    var result = new List<(Button, string)>();
    var queue = new Queue<Control>();
    queue.Enqueue(root);

    while (queue.Count > 0)
    {
        var cur = queue.Dequeue();

        if (cur is Button btn && btn.Classes.Contains("rowbtn"))
        {
            var label = FindLabelText(btn);
            result.Add((btn, label));
        }

        foreach (var child in cur.GetVisualChildren().OfType<Control>())
        {
            queue.Enqueue(child);
        }
    }

    return result;
}

/// <summary>Tim chu trong nut (TextBlock con).</summary>
static string FindLabelText(Control root)
{
    var queue = new Queue<Control>();
    queue.Enqueue(root);

    while (queue.Count > 0)
    {
        var cur = queue.Dequeue();

        if (cur is TextBlock tb && !string.IsNullOrWhiteSpace(tb.Text))
        {
            return tb.Text;
        }

        foreach (var child in cur.GetVisualChildren().OfType<Control>())
        {
            queue.Enqueue(child);
        }
    }

    return "";
}

/// <summary>Tim Path dau tien trong control (icon).</summary>
static AvaloniaPath? FindFirstPath(Control root)
{
    var queue = new Queue<Control>();
    queue.Enqueue(root);

    while (queue.Count > 0)
    {
        var cur = queue.Dequeue();

        if (cur is AvaloniaPath p)
        {
            return p;
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
