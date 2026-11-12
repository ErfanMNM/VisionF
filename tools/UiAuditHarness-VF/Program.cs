using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
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
using TanTienQR.Views;

namespace UiAuditHarness;

/// <summary>
/// Helper truy cập các ViewModel <c>internal</c> của TanTienQR qua reflection.
///
/// KHÔNG sửa file nguồn ứng dụng -> không thêm InternalsVisibleTo.
/// </summary>
internal static class Reflect
{
    private static readonly Assembly AppAsm = typeof(TanTienQR.App).Assembly;

    public static Type Type(string fullName)
    {
        var t = AppAsm.GetType(fullName, throwOnError: false);

        if (t is null && !fullName.Contains('.'))
        {
            t = AppAsm.GetType("TanTienQR.ViewModels." + fullName, throwOnError: false);
        }

        if (t is null)
        {
            throw new InvalidOperationException("Khong tim thay type: " + fullName);
        }

        return t;
    }

    /// <summary>Lấy instance đã đăng ký trong DI theo tên type đầy đủ.</summary>
    public static object FromDi(IServiceProvider sp, string vmTypeName)
    {
        var t = Type(vmTypeName);
        return sp.GetRequiredService(t);
    }

    /// <summary>
    /// Tạo ViewModel bằng reflection: các tham số lấy từ DI nếu có,
    /// các tham số còn lại dùng giá trị mặc định (null / 0 / chuỗi rỗng).
    /// </summary>
    public static object Create(IServiceProvider sp, string vmTypeName, params (string ParamType, object? Value)[] extra)
    {
        var t = Type(vmTypeName);
        var ctor = t.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .OrderByDescending(c => c.GetParameters().Length)
            .First();

        var ps = ctor.GetParameters();
        var args = new object?[ps.Length];

        for (var i = 0; i < ps.Length; i++)
        {
            var p = ps[i];
            var pt = p.ParameterType;

            // Ưu tiên giá trị truyền tường minh.
            var explicitVal = extra.FirstOrDefault(e => e.ParamType == p.Name).Value;
            bool matched = extra.Any(e => e.ParamType == p.Name);
            if (matched)
            {
                args[i] = explicitVal;
                continue;
            }

            // Tham số kiểu DI -> lấy từ container.
            if (sp.GetService(pt) is { } fromDi)
            {
                args[i] = fromDi;
                continue;
            }

            // Tham số optional -> dùng default.
            if (p.HasDefaultValue)
            {
                args[i] = p.DefaultValue;
                continue;
            }

            // Tham số kiểu string -> chuỗi rỗng.
            if (pt == typeof(string))
            {
                args[i] = string.Empty;
                continue;
            }

            // Tham số kiểu value type -> default.
            if (pt.IsValueType)
            {
                args[i] = Activator.CreateInstance(pt);
                continue;
            }

            args[i] = null;
        }

        return ctor.Invoke(args);
    }

    /// <summary>Gọi một public property (kể cả internal type).</summary>
    public static object? Get(object? obj, string propName)
    {
        if (obj is null)
        {
            return null;
        }

        var p = obj.GetType().GetProperty(propName,
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        return p?.GetValue(obj);
    }

    /// <summary>Gọi một method.</summary>
    public static object? Invoke(object? obj, string methodName, params object?[] args)
    {
        if (obj is null)
        {
            return null;
        }

        var m = obj.GetType().GetMethod(methodName,
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        return m?.Invoke(obj, args);
    }

    /// <summary>Lấy RelayCommand từ property và Execute nó.</summary>
    public static void ExecuteCommand(object? vm, string commandPropName)
    {
        var cmd = Get(vm, commandPropName);
        if (cmd is null)
        {
            throw new InvalidOperationException("Khong tim thay command: " + commandPropName);
        }

        var exec = cmd.GetType().GetMethod("Execute",
            BindingFlags.Public | BindingFlags.Instance);
        exec?.Invoke(cmd, new object?[] { null });
    }

    /// <summary>Đọc collection (IEnumerable) từ property.</summary>
    public static IEnumerable? GetItems(object? vm, string propName)
    {
        var v = Get(vm, propName);
        return v as IEnumerable;
    }

    /// <summary>
    /// Tạo một instance MỚI của view trong namespace TanTienQR.Views.
    ///
    /// Lý do: MainWindow chứa cả 9 view dưới cùng một ô Grid, chỉ bật/tắt bằng
    /// <c>IsVisible</c>. Avalonia 12 cache render data theo visual trong
    /// <c>CompositingRenderer</c>, nên render lại MainWindow sau khi đổi tab vẫn
    /// ra khung của view trước. Đặt một instance view MỚI làm <c>Content</c> của
    /// một Window MỚI đảm bảo composition tree hoàn toàn mới → không dùng cache cũ.
    /// </summary>
    public static Avalonia.Controls.Control CreateView(string simpleTypeName)
    {
        var type = AppAsm.GetType("TanTienQR.Views." + simpleTypeName, throwOnError: false);

        if (type is null)
        {
            throw new InvalidOperationException("Khong tim thay view type: " + simpleTypeName);
        }

        if (!typeof(Avalonia.Controls.Control).IsAssignableFrom(type))
        {
            throw new InvalidOperationException("View khong phai Control: " + type.FullName);
        }

        return (Avalonia.Controls.Control)Activator.CreateInstance(type)!;
    }
}

internal static class Program
{
    private static readonly List<Finding> AllFindings = new();
    private static readonly List<string> Notes = new();
    private static readonly List<string> CapturedViews = new();
    private static readonly List<string> FailedViews = new();

    private static IHost? _host;

    [STAThread]
    public static int Main(string[] args)
    {
        var sw = Stopwatch.StartNew();

        // Watchdog: nếu toàn bộ audit treo (ví dụ WebView2 chặn), buộc thoát
        // sau 12 phút để không bị kẹt vô hạn.
        var watchdog = new Thread(() =>
        {
            Thread.Sleep(TimeSpan.FromMinutes(12));
            Console.Error.WriteLine("[Harness] WATCHDOG: audit treo > 12 phut -> thoat ep.");
            Environment.Exit(3);
        })
        {
            IsBackground = true,
            Name = "harness-watchdog",
        };
        watchdog.Start();

        if (!Sandbox.Prepare())
        {
            Console.Error.WriteLine("[FATAL] Khong tim thay DB nguon de tao sandbox.");
            return 2;
        }

        Sandbox.WriteSandboxConfig();

        // ---- Chế độ headless: KHÔNG cửa sổ nào hiện trên màn hình thật ----
        AppBuilder.Configure<App>()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions
            {
                UseHeadlessDrawing = false,
            })
            .UseSkia()
            .SetupWithoutStarting();

        Console.WriteLine("[Harness] Avalonia headless (Skia) khoi dong.");

        try
        {
            RunAudit();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("[FATAL] " + ex);
            AllFindings.Add(new Finding
            {
                View = "(harness)",
                Kind = "harness-crash",
                Severity = "Blocker",
                Element = "-",
                Detail = ex.ToString(),
            });
        }
        finally
        {
            try
            {
                _host?.StopAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
                _host?.Dispose();
            }
            catch
            {
                // ignore
            }
        }

        WriteOutputs();
        Console.WriteLine($"[Harness] xong sau {sw.Elapsed.TotalSeconds:F1}s. Findings={AllFindings.Count}");
        return 0;
    }

    // ------------------------------------------------------------------

    private static void RunAudit()
    {
        // ============ 1. DI container (mirror App) ============
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration
            .SetBasePath(Sandbox.SandboxRoot)
            .AddJsonFile("appsettings.json", optional: false, reloadOnChange: false)
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                // ConfigStore đọc/ghi file cùng thư mục với cấu hình đang nạp.
                ["Config:BasePath"] = Sandbox.SandboxRoot,
            })
            .AddEnvironmentVariables("TANTIEN_");

        builder.Logging.AddConsole();
        builder.Logging.SetMinimumLevel(LogLevel.Warning);

        builder.Services.AddTanTienServices(builder.Configuration);
        _host = builder.Build();

        Console.WriteLine(
            "[Harness] Config:BasePath = " + (builder.Configuration["Config:BasePath"] ?? "<null>"));

        var services = _host.Services;

        try
        {
            // LicenseManager là internal -> gọi qua reflection.
            var licenseMgr = Reflect.FromDi(services, "TanTienQR.Services.LicenseManager");
            Reflect.Invoke(licenseMgr, "Load");
            Console.WriteLine("[Harness] LicenseManager.Load() xong (khong chan app).");
        }
        catch (Exception ex)
        {
            Notes.Add("LicenseManager.Load() nem loi (khong chan): " + ex.Message);
        }

        try
        {
            var cfgVm = Reflect.FromDi(services, "ConfigEditorViewModel");
            Reflect.Invoke(cfgVm, "Load");
        }
        catch (Exception ex)
        {
            Notes.Add("ConfigEditorViewModel.Load() nem loi: " + ex.Message);
        }

        // KHONG host.Start() -> khong bat BackgroundService, khong bind port.
        // Camera/PLC/heartbeat/PO worker khong chay; scanner Disabled trong config sandbox.

        // ============ 2. Đăng nhập (qua service, không cần UI) ============
        var session = services.GetRequiredService<ClassLibraryMT.Auth.ISessionContext>();
        var users = services.GetRequiredService<ClassLibraryMT.Auth.IUserService>();

        var loginOk = false;
        var loginError = string.Empty;

        try
        {
            var result = users.AuthenticateAsync("admin", "tantien512").GetAwaiter().GetResult();
            if (result.Ok && result.User is not null)
            {
                session.SignIn(result.User);
                loginOk = true;
                Console.WriteLine($"[Harness] Dang nhap OK: {result.User.Username} / role={result.User.RoleName}");
            }
            else
            {
                loginError = result.Message;
                Console.Error.WriteLine("[Harness] Dang nhap that bai: " + result.Message);
            }
        }
        catch (Exception ex)
        {
            loginError = ex.Message;
            Console.Error.WriteLine("[Harness] Login exception: " + ex.Message);
        }

        if (!loginOk)
        {
            Notes.Add(
                "Khong dang nhap duoc bang admin/tantien512 (" + loginError +
                "). Harness van chay de audit giao dien; quyen hien thi theo mac dinh.");

            try
            {
                var seed = users.AuthenticateAsync("admin", "admin").GetAwaiter().GetResult();
                if (seed.Ok && seed.User is not null)
                {
                    session.SignIn(seed.User);
                    loginOk = true;
                    Console.WriteLine("[Harness] Fallback admin/admin OK, role=" + seed.User.RoleName);
                }
            }
            catch
            {
                // ignore
            }
        }

        // ============ 3. Cửa sổ chính ============
        var mainVm = Reflect.FromDi(services, "MainViewModel");
        var mainWindow = new MainWindow { DataContext = mainVm };

        mainWindow.Width = 1360;
        mainWindow.Height = 820;
        mainWindow.WindowStartupLocation = WindowStartupLocation.Manual;
        mainWindow.Position = new PixelPoint(-4000, -4000);

        ShowWindow(mainWindow);
        Pump(900);

        if (loginOk)
        {
            Reflect.Invoke(mainVm, "RefreshPermissionFlags");
        }

        Pump(400);

        // ============ 4. Đi qua từng tab ============
        // Mỗi tab: (1) chạy command điều hướng thật trên mainVm để side-effect
        // của ViewModel xảy ra y như app thật (load lịch sử, danh sách PO, refresh
        // PLC...), (2) render view trong MỘT cửa sổ MỚI để tránh composition cache
        // của Avalonia 12 (xem CaptureView).
        var tabs = new (string Name, string Cmd, string ViewType)[]
        {
            ("Info", "ShowInfoTabCommand", "InfoView"),
            ("Production", "ShowProductionTabCommand", "ProductionView"),
            ("Config", "ShowConfigTabCommand", "ConfigView"),
            ("PlcSettings", "ShowPlcSettingsTabCommand", "PlcSettingsView"),
            ("History", "ShowHistoryTabCommand", "HistoryView"),
            ("ProductionOrders", "ShowProductionOrdersTabCommand", "ProductionOrdersView"),
            ("CodeLookup", "ShowCodeLookupTabCommand", "CodeLookupView"),
            ("Accounts", "ShowAccountsTabCommand", "AccountsView"),
            ("Dev", "ShowDevTabCommand", "DevView"),
        };

        foreach (var tab in tabs)
        {
            try
            {
                RunWithTimeout($"tab:{tab.Name}", 15000, () =>
                {
                    Reflect.ExecuteCommand(mainVm, tab.Cmd);
                    Pump(1000);
                });

                CaptureView(mainVm, tab.ViewType, $"MainWindow__{tab.Name}", tab.Name);

                Notes.Add(
                    $"Tab '{tab.Name}': section={Reflect.Get(mainVm, "CurrentSection")}, " +
                    $"title='{Reflect.Get(mainVm, "PageTitle")}', " +
                    $"dev={Reflect.Get(mainVm, "CanViewDev")}, " +
                    $"users={Reflect.Get(mainVm, "CanManageUsers")}, " +
                    $"config={Reflect.Get(mainVm, "CanEditConfig")}, " +
                    $"history={Reflect.Get(mainVm, "CanViewHistory")}, " +
                    $"production={Reflect.Get(mainVm, "CanViewProduction")}.");
            }
            catch (Exception ex)
            {
                FailedViews.Add(tab.Name);
                Console.Error.WriteLine($"[Harness] tab {tab.Name} loi: {ex.Message}");
                AllFindings.Add(new Finding
                {
                    View = tab.Name,
                    Kind = "navigation-failed",
                    Severity = "Major",
                    Element = "-",
                    Detail = "Khong chuyen duoc sang tab: " + ex.Message,
                });
            }
        }

        // ============ 5. Cửa sổ con ============
        AuditChildWindows(services, mainVm);

        // ============ 6. Cửa sổ đăng nhập ============
        TryAuditWindow("LoginShellWindow", () =>
        {
            var vm = Reflect.Create(services, "LoginViewModel");
            var win = new LoginShellWindow { DataContext = vm };
            win.Width = 1360;
            win.Height = 820;
            return win;
        });

        // ============ 7. Overlay license panel ============
        AuditOverlays(mainWindow, mainVm);

        try
        {
            mainWindow.Close();
        }
        catch
        {
            // ignore
        }
    }

    // ------------------------------------------------------------------

    private static void AuditChildWindows(IServiceProvider services, object mainVm)
    {
        // --- LicenseWindow ---
        TryAuditWindow("LicenseWindow", () =>
        {
            var vm = Reflect.FromDi(services, "TanTienQR.Services.LicenseManager");
            var win = new LicenseWindow { DataContext = vm };
            win.Width = 520;
            win.Height = 460;
            return win;
        });

        // --- PasswordChangeWindow (can user account hien tai) ---
        TryAuditWindow("PasswordChangeWindow", () =>
        {
            var session = services.GetRequiredService<ClassLibraryMT.Auth.ISessionContext>();
            var current = Reflect.Get(session, "CurrentUser");
            var vm = Reflect.Create(services, "PasswordChangeViewModel", ("user", current));
            var win = new PasswordChangeWindow { DataContext = vm };
            win.Width = 520;
            win.Height = 560;
            return win;
        });

        // --- UserEditWindow (tao moi) ---
        TryAuditWindow("UserEditWindow", () =>
        {
            var vm = Reflect.Create(services, "UserEditViewModel");
            var win = new UserEditWindow { DataContext = vm };
            win.Width = 520;
            win.Height = 560;
            return win;
        });

        // --- RecipeDeleteConfirmWindow ---
        TryAuditWindow("RecipeDeleteConfirmWindow", () =>
        {
            var vm = Reflect.Create(services, "RecipeDeleteConfirmViewModel", ("recipeName", "Recipe_A"));
            var win = new RecipeDeleteConfirmWindow { DataContext = vm };
            win.Width = 520;
            win.Height = 340;
            return win;
        });

        // --- PlcParamEditWindow ---
        TryAuditWindow("PlcParamEditWindow", () =>
        {
            var vm = Reflect.Create(services, "PlcParamEditViewModel");
            var win = new PlcParamEditWindow { DataContext = vm };
            win.Width = 620;
            win.Height = 520;
            return win;
        });
    }

    private static void AuditOverlays(Window mainWindow, object mainVm)
    {
        try
        {
            RunWithTimeout("overlay:license", 8000, () =>
            {
                Reflect.ExecuteCommand(mainVm, "ToggleLicensePanelCommand");
                Pump(700);
            });

            CaptureWindow(mainWindow, "MainWindow__LicensePanel", "LicensePanel");
        }
        catch (Exception ex)
        {
            Notes.Add("Overlay license panel: " + ex.Message);
        }

        // Menu cửa sổ (popup hamburger)
        try
        {
            var popup = mainWindow.FindControl<Avalonia.Controls.Primitives.Popup>("WindowMenuPopup");
            if (popup is not null)
            {
                popup.IsOpen = true;
                Pump(600);
                CaptureWindow(mainWindow, "MainWindow__WindowMenuPopup", "WindowMenuPopup");
                popup.IsOpen = false;
            }
            else
            {
                Notes.Add("Khong tim thay Popup 'WindowMenuPopup' trong MainWindow.");
            }
        }
        catch (Exception ex)
        {
            Notes.Add("WindowMenuPopup: " + ex.Message);
        }
    }

    private static void TryAuditWindow(string name, Func<Window> factory)
    {
        Window? win = null;
        try
        {
            win = RunWithTimeout($"open:{name}", 12000, () =>
            {
                var w = factory();
                w.Position = new PixelPoint(-4000, -4000);
                ShowWindow(w);
                Pump(800);
                return w;
            });

            if (win is null)
            {
                FailedViews.Add(name);
                return;
            }

            CaptureWindow(win, name, name);
        }
        catch (Exception ex)
        {
            FailedViews.Add(name);
            Console.Error.WriteLine($"[Harness] window {name} loi: {ex.Message}");
            AllFindings.Add(new Finding
            {
                View = name,
                Kind = "window-open-failed",
                Severity = "Major",
                Element = "-",
                Detail = ex.Message,
            });
        }
        finally
        {
            try
            {
                win?.Close();
                Pump(200);
            }
            catch
            {
                // ignore
            }
        }
    }

    // ------------------------------------------------------------------

    /// <summary>
    /// Chụp một cửa sổ độc lập (dialog, overlay). Không dùng chung composition
    /// với MainWindow nên không gặp vấn đề cache.
    /// </summary>
    private static void CaptureWindow(Window win, string stem, string viewName)
    {
        try
        {
            RunWithTimeout($"render:{stem}", 15000, () =>
            {
                // Nhiều vòng layout/paint để nội dung ScrollViewer được realize
                // và toạ độ ổn định trước khi render.
                for (var pass = 0; pass < 3; pass++)
                {
                    (win as Avalonia.Controls.TopLevel)?.UpdateLayout();
                    PumpRender(200);
                }

                var size = win.ClientSize;
                var w = (int)Math.Max(1, Math.Ceiling(size.Width));
                var h = (int)Math.Max(1, Math.Ceiling(size.Height));

                var nodes = VisualAudit.Dump(win, stem);

                using var bmp = new RenderTargetBitmap(new PixelSize(w, h), new Vector(96, 96));
                bmp.Render(win);

                var path = Path.Combine(Sandbox.ShotsRoot, stem + ".png");
                bmp.Save(path, new Avalonia.Media.Imaging.PngBitmapEncoderOptions());

                var findings = VisualAudit.Check(viewName, nodes, size.Width, size.Height);

                foreach (var f in findings)
                {
                    f.Screenshot = stem + ".png";
                }

                AllFindings.AddRange(findings);
                CapturedViews.Add(viewName);

                Console.WriteLine(
                    $"[Shot] {stem}.png  {w}x{h}  window={win.GetType().Name}  " +
                    $"nodes={nodes.Count}  findings={findings.Count}");
            });
        }
        catch (Exception ex)
        {
            FailedViews.Add(viewName);
            Console.Error.WriteLine($"[Harness] capture {stem} loi: {ex.Message}");
            AllFindings.Add(new Finding
            {
                View = viewName,
                Kind = "capture-failed",
                Severity = "Minor",
                Element = stem,
                Detail = "Khong render duoc: " + ex.Message,
            });
        }
    }

    /// <summary>
    /// Chụp MỘT view trong MỘT cửa sổ MỚI.
    ///
    /// Vấn đề gốc: MainWindow chứa cả 9 view dưới cùng một ô Grid, chỉ bật/tắt
    /// bằng IsVisible. Avalonia 12 cache render data theo visual trong
    /// CompositingRenderer, nên RenderTargetBitmap.Render(mainWindow) sau khi
    /// đổi tab vẫn trả về khung của view TRƯỚC đó.
    ///
    /// Cách sửa: tạo instance MỚI của view + Window MỚI → composition tree
    /// hoàn toàn mới, không có cache cũ. Đồng thời cây trực quan được dump từ
    /// CHÍNH cửa sổ này, nên PNG và .tree.json luôn mô tả cùng một view.
    /// </summary>
    private static void CaptureView(
        object mainVm,
        string viewTypeName,
        string stem,
        string viewName,
        int width = 1360,
        int height = 820)
    {
        Window? scratch = null;

        try
        {
            RunWithTimeout($"render:{stem}", 20000, () =>
            {
                var view = Reflect.CreateView(viewTypeName);

                // DataContext phải là MainViewModel để binding trong view hoạt động.
                view.DataContext = mainVm;

                scratch = new Window
                {
                    Width = width,
                    Height = height,
                    WindowStartupLocation = WindowStartupLocation.Manual,
                    Position = new PixelPoint(-4000, -4000),
                    Background = new Avalonia.Media.SolidColorBrush(
                        Avalonia.Media.Color.Parse("#F6F9FB")),
                    Content = view,
                };

                scratch.DataContext = mainVm;
                scratch.Show();
                Pump(700);

                // Layout + paint pass trước khi render.
                // Nhiều vòng vì view có ScrollViewer + ItemsControl: vòng đầu
                // thực hiện Measure/Arrange, vòng sau mới dựng được nội dung
                // đã "realize" (hàng bảng, nút trong footer) và gán lại
                // toạ độ chính xác.
                for (var pass = 0; pass < 3; pass++)
                {
                    (scratch as Avalonia.Controls.TopLevel)?.UpdateLayout();
                    PumpRender(200);
                }

                var size = scratch.ClientSize;
                var w = (int)Math.Max(1, Math.Ceiling(size.Width));
                var h = (int)Math.Max(1, Math.Ceiling(size.Height));

                // Dump cây trực quan TRƯỚC khi render, không có bước pump nào
                // ở giữa => PNG và .tree.json mô tả CÙNG một trạng thái layout.
                var nodes = VisualAudit.Dump(scratch, stem);

                using var bmp = new RenderTargetBitmap(new PixelSize(w, h), new Vector(96, 96));
                bmp.Render(scratch);

                var path = Path.Combine(Sandbox.ShotsRoot, stem + ".png");
                bmp.Save(path, new Avalonia.Media.Imaging.PngBitmapEncoderOptions());

                var findings = VisualAudit.Check(viewName, nodes, size.Width, size.Height);

                foreach (var f in findings)
                {
                    f.Screenshot = stem + ".png";
                }

                AllFindings.AddRange(findings);
                CapturedViews.Add(viewName);

                Console.WriteLine(
                    $"[Shot] {stem}.png  {w}x{h}  view={viewTypeName}  " +
                    $"nodes={nodes.Count}  findings={findings.Count}");
            });
        }
        catch (Exception ex)
        {
            FailedViews.Add(viewName);
            Console.Error.WriteLine($"[Harness] capture {stem} loi: {ex.Message}");
            AllFindings.Add(new Finding
            {
                View = viewName,
                Kind = "capture-failed",
                Severity = "Minor",
                Element = stem,
                Detail = "Khong render duoc: " + ex.Message,
            });
        }
        finally
        {
            try
            {
                scratch?.Close();
                Pump(200);
            }
            catch
            {
                // ignore
            }
        }
    }

    // ------------------------------------------------------------------

    private static void ShowWindow(Window w)
    {
        w.Show();
        Pump(200);
    }

    /// <summary>
    /// Đẩy dispatcher theo thứ tự ưu tiên Render (cao nhất) để buộc Avalonia
    /// dựng lại composition tree sau khi nội dung vừa được realize.
    /// </summary>
    private static void PumpRender(int ms)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < deadline)
        {
            Dispatcher.UIThread.RunJobs(Avalonia.Threading.DispatcherPriority.Render);
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(5);
        }

        Dispatcher.UIThread.RunJobs(Avalonia.Threading.DispatcherPriority.Render);
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>
    /// Đẩy hàng đợi dispatcher + chờ để layout ổn định.
    ///
    /// PHẢI chạy trên thread sở hữu Dispatcher.UIThread (main thread),
    /// nếu không Avalonia sẽ ném "different thread owns it".
    /// </summary>
    private static void Pump(int ms)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < deadline)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(10);
        }

        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>
    /// Chạy một hàm TRÊN thread chủ sở hữu dispatcher (main thread).
    ///
    /// Không dùng thread riêng vì Avalonia gắn quyền sở hữu visual tree
    /// với thread đã gọi SetupWithoutStarting().
    /// Timeout chỉ dùng để ghi nhận (không cắt ngang) — chống treo thật sự
    /// do watchdog bên ngoài (Program.Main) đảm nhiệm.
    /// </summary>
    private static T RunWithTimeout<T>(string label, int timeoutMs, Func<T> action)
    {
        var sw = Stopwatch.StartNew();
        var result = action();

        if (sw.ElapsedMilliseconds > timeoutMs)
        {
            Console.Error.WriteLine(
                $"[Harness] CANH BAO: buoc '{label}' mat {sw.ElapsedMilliseconds}ms " +
                $"(vuot timeout {timeoutMs}ms).");
        }

        return result;
    }

    private static void RunWithTimeout(string label, int timeoutMs, Action action)
    {
        RunWithTimeout<object?>(label, timeoutMs, () =>
        {
            action();
            return null;
        });
    }

    private static void WriteOutputs()
    {
        Directory.CreateDirectory(Sandbox.ShotsRoot);

        var payload = new
        {
            generatedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            viewsCaptured = CapturedViews.Distinct().ToList(),
            viewsFailed = FailedViews.Distinct().ToList(),
            notes = Notes,
            findings = AllFindings,
        };

        var path = Path.Combine(Sandbox.ShotsRoot, "findings.json");
        File.WriteAllText(
            path,
            JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));

        Console.WriteLine($"[Harness] findings -> {path}");
    }
}
