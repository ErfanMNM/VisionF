using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ClassLibraryMT.Auth;
using ClassLibraryMT.Logging;
using ClassLibraryMT.Plc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TanTienQR;
using TanTienQR.Controls;
using TanTienQR.Services;
using TanTienQR.Views;

namespace UiAuditHarness.Audit;

/// <summary>
/// Harness kiểm tra giao diện TanTienQR chạy hoàn toàn OFF-SCREEN.
///
/// - Avalonia headless (UseHeadlessDrawing = false) + Skia -> render PNG.
/// - Sandbox riêng: DB / license / config tách biệt, mạng ngoài bị vô hiệu hoá.
/// - Duyệt từng view: chụp PNG + dump cây trực quan + đo lỗi bố cục.
///
/// KHÔNG hiện cửa sổ nào lên màn hình người dùng.
/// </summary>
internal static class AuditRunner
{
    private const string AdminUser = "admin";
    private const string AdminPass = "tantien512";
    private const string SandboxNewPass = "Sandbox123!";

    private const string MainVmTypeName = "TanTienQR.ViewModels.MainViewModel";
    private const string LoginVmTypeName = "TanTienQR.ViewModels.LoginViewModel";

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>Tất cả phát hiện từ mọi view.</summary>
    public static List<AuditViewReport> Reports { get; } = [];

    /// <summary>Các giới hạn gặp phải trong lúc audit.</summary>
    public static List<string> Limitations { get; } = [];

    public static int Run(string[] args)
    {
        var appBinDir = ResolveAppBinDir(args);
        AuditSandbox.Prepare(appBinDir);

        AuditSandbox.Log($"App bin: {appBinDir}");
        AuditSandbox.Log($"Sandbox: {AuditSandbox.Root}");

        try
        {
            AppBuilder
                .Configure<App>()
                .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
                .UseSkia()
                .SetupWithoutStarting();

            RunAuditAsync().GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("[Audit] Loi: " + ex);
            AuditSandbox.Log("FATAL: " + ex);
            WriteSummary();
            return 1;
        }

        WriteSummary();
        return 0;
    }

    /// <summary>
    /// Thử tìm thư mục output của app đã build (để lấy users.db làm seed).
    /// </summary>
    private static string ResolveAppBinDir(string[] args)
    {
        if (args.Length > 0 && Directory.Exists(args[0]))
        {
            return Path.GetFullPath(args[0]);
        }

        // tools/UiAuditHarness/bin/Debug/net10.0 -> repo root
        var repoRoot = Path.GetFullPath(
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));

        var candidate = Path.Combine(repoRoot, "TanTienQR", "bin", "Debug", "net10.0");
        return Directory.Exists(candidate) ? candidate : AppContext.BaseDirectory;
    }

    // =====================================================================
    //  Kịch bản audit
    // =====================================================================

    private static async Task RunAuditAsync()
    {
        // ---- 1. Dựng DI container giống App nhưng KHÔNG chạy background worker ----
        // Dựng container thủ công (không Host.Start()) -> KHÔNG có PLC heartbeat,
        // KHÔNG có camera worker, KHÔNG có PO activation worker, KHÔNG có TCP
        // listener 6999. Đây là điểm quan trọng cho an toàn sandbox.
        var services = new ServiceCollection();
        var config = BuildSandboxConfiguration();

        services.AddTanTienServices(config);

        var provider = services.BuildServiceProvider();

        // Nạp license + config giống App (không chặn UI).
        InvokeIfExists(provider, "TanTienQR.Services.LicenseManager", "Load");
        InvokeIfExists(provider, "TanTienQR.ViewModels.ConfigEditorViewModel", "Load");

        // Bàn phím ảo là singleton toàn cục (giống App).
        VirtualKeyboardHost.Register(provider.GetRequiredService<KeyboardService>());

        // ---- 2. Đăng nhập ----
        var loginVmType = Reflect.GetType(LoginVmTypeName)
            ?? throw new InvalidOperationException("Không tìm thấy LoginViewModel.");

        var loginVm = provider.GetRequiredService(loginVmType);

        var loginWindow = new LoginShellWindow { DataContext = loginVm };
        Reflect.Invoke(loginVm, "Attach", loginWindow);
        provider.GetRequiredService<KeyboardService>().Attach(loginWindow);

        loginWindow.Show();
        AuditCapture.Settle(900);

        CaptureView("LoginShellWindow", "01_login_signin", loginWindow);

        // Nhập tài khoản
        SetProp(loginVm, "Username", AdminUser);
        SetProp(loginVm, "Password", AdminPass);
        AuditCapture.Settle(300);

        await Reflect.ExecuteCommandAsync(loginVm, "LoginCommand");
        AuditCapture.Settle(800);

        // Bước 2: bắt buộc đổi mật khẩu (nếu có)
        if (Reflect.GetBool(loginVm, "IsChangePasswordStep"))
        {
            CaptureView("LoginShellWindow", "02_login_changepassword", loginWindow);

            SetProp(loginVm, "CurrentPassword", AdminPass);
            SetProp(loginVm, "NewPassword", SandboxNewPass);
            SetProp(loginVm, "ConfirmPassword", SandboxNewPass);
            AuditCapture.Settle(300);

            await Reflect.ExecuteCommandAsync(loginVm, "ConfirmPasswordChangeCommand");
            AuditCapture.Settle(800);
        }

        if (!Reflect.GetBool(loginVm, "IsAuthenticated"))
        {
            var err = Reflect.GetProperty(loginVm, "ErrorMessage") as string;
            Limitations.Add(
                "Đăng nhập tự động KHÔNG thành công (IsAuthenticated = false" +
                (string.IsNullOrEmpty(err) ? "" : $", lỗi: {err}") +
                "). Các view sau cửa sổ chính không được audit.");

            AuditSandbox.Log("Dang nhap that bai: " + err);
            loginWindow.Close();
            return;
        }

        // ---- 3. Cửa sổ chính ----
        var mainVmType = Reflect.GetType(MainVmTypeName)
            ?? throw new InvalidOperationException("Không tìm thấy MainViewModel.");

        var mainVm = provider.GetRequiredService(mainVmType);

        var mainWindow = new MainWindow { DataContext = mainVm };
        provider.GetRequiredService<KeyboardService>().Attach(mainWindow);

        // Đóng cửa sổ login (App kiểm tra IsAuthenticated rồi mở MainWindow).
        loginWindow.Close();
        mainWindow.Show();
        AuditCapture.Settle(1200);

        Reflect.Invoke(mainVm, "Start", mainWindow);
        AuditCapture.Settle(800);

        // ---- 4. Duyệt từng view ----
        await AuditTabAsync("InfoView", mainVm, mainWindow, "10_info", "ShowInfoTabCommand", "IsInfoVisible");
        await AuditTabAsync("CodeLookupView", mainVm, mainWindow, "20_codelookup", "ShowCodeLookupTabCommand", "IsCodeLookupVisible");
        await AuditTabAsync("ConfigView", mainVm, mainWindow, "30_config", "ShowConfigTabCommand", "IsConfigVisible");
        await AuditTabAsync("PlcSettingsView", mainVm, mainWindow, "40_plcsettings", "ShowPlcSettingsTabCommand", "IsPlcSettingsVisible");
        await AuditTabAsync("HistoryView", mainVm, mainWindow, "50_history", "ShowHistoryTabCommand", "IsHistoryVisible");
        await AuditTabAsync("ProductionOrdersView", mainVm, mainWindow, "60_productionorders", "ShowProductionOrdersTabCommand", "IsProductionOrdersVisible");
        await AuditTabAsync("ProductionView", mainVm, mainWindow, "70_production", "ShowProductionTabCommand", "IsProductionVisible");
        await AuditTabAsync("AccountsView", mainVm, mainWindow, "80_accounts", "ShowAccountsTabCommand", "IsAccountsVisible");
        await AuditTabAsync("DevView", mainVm, mainWindow, "90_dev", "ShowDevTabCommand", "IsDevVisible");

        // ---- 5. Các hộp thoại ----
        AuditDialogs(mainVm, mainWindow, provider);

        // ---- 6. Bàn phím ảo ----
        AuditVirtualKeyboard(mainVm, mainWindow);

        // ---- 7. Popup license mini ----
        AuditLicensePopup(mainVm, mainWindow);

        mainWindow.Close();
        AuditSandbox.Log("Audit hoan tat.");
        await Task.CompletedTask;
    }

    private static void InvokeIfExists(IServiceProvider provider, string typeName, string method)
    {
        try
        {
            var type = Reflect.GetType(typeName);
            if (type is null)
            {
                return;
            }

            var svc = provider.GetService(type);
            if (svc is null)
            {
                return;
            }

            var mi = type.GetMethod(
                method,
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

            mi?.Invoke(svc, null);
        }
        catch (Exception ex)
        {
            AuditSandbox.Log($"Invoke {typeName}.{method} failed: {ex.Message}");
        }
    }

    private static void SetProp(object instance, string name, object? value)
    {
        var prop = instance.GetType().GetProperty(
            name,
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

        if (prop is null || !prop.CanWrite)
        {
            throw new MissingMemberException(instance.GetType().FullName, name);
        }

        prop.SetValue(instance, value);
    }

    /// <summary>Tạo cấu hình cô lập cho sandbox (trỏ DB về sandbox, tắt mạng).</summary>
    private static IConfigurationRoot BuildSandboxConfiguration()
    {
        return new ConfigurationBuilder()
            .SetBasePath(AuditSandbox.Root)
            .AddJsonFile("appsettings.json", optional: false, reloadOnChange: false)
            .AddEnvironmentVariables("TANTIEN_")
            .Build();
    }

    // =====================================================================
    //  Audit từng tab
    // =====================================================================

    private static async Task AuditTabAsync(
        string viewName,
        object vm,
        Window window,
        string slug,
        string commandName,
        string visibleProperty)
    {
        try
        {
            await Reflect.ExecuteCommandAsync(vm, commandName);
        }
        catch (Exception ex)
        {
            Limitations.Add($"Không chuyển được sang tab {viewName}: {ex.Message}");
            AuditSandbox.Log($"Switch {viewName} failed: {ex}");
            return;
        }

        // Tab Dev có timer tự refresh -> chờ thêm cho ổn định.
        AuditCapture.Settle(viewName == "DevView" ? 1500 : 900);

        if (!Reflect.GetBool(vm, visibleProperty))
        {
            Limitations.Add(
                $"Tab/view '{viewName}' KHÔNG hiển thị với tài khoản đang đăng nhập " +
                "(quyền không đủ) — view này KHÔNG được audit.");
            AuditSandbox.Log($"Tab {viewName} not visible for current role.");
            return;
        }

        CaptureView(viewName, slug, window);
    }

    // =====================================================================
    //  Audit hộp thoại
    // =====================================================================

    private static void AuditDialogs(object mainVm, Window mainWindow, IServiceProvider provider)
    {
        // ---- LicenseWindow ----
        AuditDialog(
            "LicenseWindow",
            () =>
            {
                var dlg = new LicenseWindow { DataContext = mainVm };
                dlg.Show();
                return dlg;
            },
            "100_dialog_license");

        // ---- PasswordChangeWindow ----
        AuditDialog(
            "PasswordChangeWindow",
            () =>
            {
                var user = Reflect.GetProperty(mainVm, "CurrentUser") as UserAccount
                    ?? NewFallbackUser();

                var pvmType = Reflect.GetType("TanTienQR.ViewModels.PasswordChangeViewModel");
                var pvm = Activator.CreateInstance(
                    pvmType!,
                    provider.GetRequiredService<IUserService>(),
                    user);

                var dlg = new PasswordChangeWindow { DataContext = pvm };
                dlg.Show();
                return dlg;
            },
            "110_dialog_passwordchange");

        // ---- UserEditWindow (tạo mới) ----
        AuditDialog(
            "UserEditWindow",
            () =>
            {
                var uvmType = Reflect.GetType("TanTienQR.ViewModels.UserEditViewModel");
                var uvm = Activator.CreateInstance(
                    uvmType!,
                    provider.GetRequiredService<IUserService>(),
                    provider.GetRequiredService<IUserActionLogger>(),
                    null);

                var dlg = new UserEditWindow { DataContext = uvm };
                dlg.Show();
                return dlg;
            },
            "120_dialog_useredit");

        // ---- PlcParamEditWindow (thêm mới) ----
        AuditDialog(
            "PlcParamEditWindow",
            () =>
            {
                var pvmType = Reflect.GetType("TanTienQR.ViewModels.PlcParamEditViewModel");
                var pvm = Activator.CreateInstance(
                    pvmType!,
                    provider.GetRequiredService<IPlcSettingsService>(),
                    null);

                var dlg = new PlcParamEditWindow { DataContext = pvm };
                dlg.Show();
                return dlg;
            },
            "130_dialog_plcparamedit");

        // ---- RecipeDeleteConfirmWindow ----
        AuditDialog(
            "RecipeDeleteConfirmWindow",
            () =>
            {
                var rvmType = Reflect.GetType("TanTienQR.ViewModels.RecipeDeleteConfirmViewModel");
                var rvm = Activator.CreateInstance(
                    rvmType!,
                    provider.GetRequiredService<IUserService>(),
                    provider.GetRequiredService<ISessionContext>(),
                    "Recipe_Mau_Hien_Tai");

                var dlg = new RecipeDeleteConfirmWindow { DataContext = rvm };
                dlg.Show();
                return dlg;
            },
            "140_dialog_recipedelete");
    }

    private static UserAccount NewFallbackUser()
    {
        // UserAccount.Role là read-only -> dùng constructor nếu có, nếu không
        // thì tạo object rỗng (UI chỉ cần Username/DisplayName).
        var ctor = typeof(UserAccount).GetConstructor(
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
            binder: null,
            types: Type.EmptyTypes,
            modifiers: null);

        var ua = ctor is not null
            ? (UserAccount)ctor.Invoke(null)
            : (UserAccount)System.Runtime.CompilerServices.RuntimeHelpers
                .GetUninitializedObject(typeof(UserAccount));

        return ua;
    }

    /// <summary>Mở một hộp thoại, chụp ảnh + đo lỗi, rồi đóng lại.</summary>
    private static void AuditDialog(string name, Func<Window> factory, string slug)
    {
        Window? dlg = null;

        try
        {
            dlg = factory();
            AuditCapture.Settle(800);

            CaptureView(name, slug, dlg);
        }
        catch (Exception ex)
        {
            Limitations.Add($"Không mở được hộp thoại {name}: {ex.Message}");
            AuditSandbox.Log($"Dialog {name} failed: {ex}");
        }
        finally
        {
            try
            {
                dlg?.Close();
                AuditCapture.Settle(300);
            }
            catch (Exception ex)
            {
                AuditSandbox.Log($"Close {name} failed: {ex}");
            }
        }
    }

    // =====================================================================
    //  Bàn phím ảo
    // =====================================================================

    private static void AuditVirtualKeyboard(object mainVm, Window mainWindow)
    {
        try
        {
            var ctrl = mainWindow.GetVisualDescendants()
                .OfType<VirtualKeyboard>()
                .FirstOrDefault();

            if (ctrl is null)
            {
                Limitations.Add("Không tìm thấy control VirtualKeyboard trong cây trực quan.");
                return;
            }

            // Bàn phím ảo chỉ hiện khi có ô nhập đang focus. Trong headless không
            // có thao tác chạm thật, nên ta thử focus ô nhập rồi ép bật.
            var textBox = mainWindow.GetVisualDescendants()
                .OfType<TextBox>()
                .FirstOrDefault(tb => tb.IsVisible && tb.IsEnabled);

            textBox?.Focus();
            AuditCapture.Settle(500);

            if (!ctrl.IsVisible)
            {
                TryForceKeyboardVisible();
                AuditCapture.Settle(500);
            }

            if (ctrl.IsVisible)
            {
                CaptureView("VirtualKeyboard", "150_virtual_keyboard", mainWindow);
            }
            else
            {
                Limitations.Add(
                    "Bàn phím ảo (VirtualKeyboard) KHÔNG hiện được trong headless " +
                    "(không có thao tác chạm thật để kích hoạt ô nhập) — " +
                    "không audit được giao diện bàn phím ảo.");
            }
        }
        catch (Exception ex)
        {
            Limitations.Add("Lỗi khi audit bàn phím ảo: " + ex.Message);
            AuditSandbox.Log("VirtualKeyboard audit failed: " + ex);
        }
    }

    private static void TryForceKeyboardVisible()
    {
        var host = VirtualKeyboardHost.ViewModel;
        if (host is null)
        {
            return;
        }

        var prop = host.GetType().GetProperty(
            "IsVisible",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

        prop?.SetValue(host, true);
    }

    // =====================================================================
    //  Popup license mini
    // =====================================================================

    private static void AuditLicensePopup(object mainVm, Window mainWindow)
    {
        try
        {
            SetProp(mainVm, "IsLicensePanelOpen", true);
            AuditCapture.Settle(700);

            var popup = mainWindow.GetVisualDescendants()
                .OfType<Avalonia.Controls.Primitives.Popup>()
                .FirstOrDefault(p => p.IsOpen);

            if (popup is null)
            {
                Limitations.Add(
                    "Popup license (overlay trong MainWindow) không mở được trong headless " +
                    "(IsOpen = false) — không audit được.");
                SetProp(mainVm, "IsLicensePanelOpen", false);
                return;
            }

            var child = popup.Child;
            if (child is null)
            {
                Limitations.Add("Popup license mở được nhưng không có nội dung con.");
                return;
            }

            // Popup là vật con cửa sổ riêng -> cần layout thủ công trước khi render.
            child.Measure(new Size(4000, 4000));
            child.Arrange(new Rect(child.DesiredSize));
            AuditCapture.Settle(400);

            var png = Path.Combine(AuditSandbox.ShotsDir, "160_license_popup.png");
            if (!AuditCapture.SavePng(child, png, out var err))
            {
                Limitations.Add($"Không render được popup license: {err}");
                return;
            }

            var node = AuditTree.Dump(child);
            var findings = AuditTree.Analyze(node, child.Bounds.Width, child.Bounds.Height);
            AuditTree.Save(node, Path.Combine(AuditSandbox.DumpDir, "160_license_popup.json"));

            Reports.Add(new AuditViewReport
            {
                View = "LicensePopup (overlay MainWindow)",
                Screenshot = "160_license_popup.png",
                Dump = "160_license_popup.json",
                WindowWidth = Math.Round(child.Bounds.Width, 1),
                WindowHeight = Math.Round(child.Bounds.Height, 1),
                Findings = findings,
            });

            Console.WriteLine(
                $"  [OK] LicensePopup           {child.Bounds.Width}x{child.Bounds.Height} " +
                $"-> 160_license_popup.png ({findings.Count})");

            SetProp(mainVm, "IsLicensePanelOpen", false);
            AuditCapture.Settle(250);
        }
        catch (Exception ex)
        {
            Limitations.Add("Lỗi khi audit popup license: " + ex.Message);
            AuditSandbox.Log("License popup audit failed: " + ex);
        }
    }

    // =====================================================================
    //  Chụp + đo
    // =====================================================================

    private static void CaptureView(string viewName, string slug, Control target)
    {
        try
        {
            var png = Path.Combine(AuditSandbox.ShotsDir, slug + ".png");

            if (!AuditCapture.SavePng(target, png, out var err))
            {
                Limitations.Add($"Không render được '{viewName}': {err}");
                AuditSandbox.Log($"Render {viewName} failed: {err}");
                Console.WriteLine($"  [!!] {viewName}: render failed — {err}");
                return;
            }

            var node = AuditTree.Dump(target);
            var findings = AuditTree.Analyze(node, target.Bounds.Width, target.Bounds.Height);

            var dumpFile = slug + ".json";
            AuditTree.Save(node, Path.Combine(AuditSandbox.DumpDir, dumpFile));

            Reports.Add(new AuditViewReport
            {
                View = viewName,
                Screenshot = slug + ".png",
                Dump = dumpFile,
                WindowWidth = Math.Round(target.Bounds.Width, 1),
                WindowHeight = Math.Round(target.Bounds.Height, 1),
                Findings = findings,
            });

            Console.WriteLine(
                $"  [OK] {viewName,-24} {target.Bounds.Width}x{target.Bounds.Height} " +
                $"-> {slug}.png  ({findings.Count} phát hiện)");
        }
        catch (Exception ex)
        {
            Limitations.Add($"Lỗi khi chụp '{viewName}': {ex.Message}");
            AuditSandbox.Log($"Capture {viewName} failed: {ex}");
            Console.WriteLine($"  [!!] {viewName}: {ex.Message}");
        }
    }

    // =====================================================================
    //  Tổng kết
    // =====================================================================

    private static void WriteSummary()
    {
        Directory.CreateDirectory(AuditSandbox.OutDir);

        File.WriteAllText(
            Path.Combine(AuditSandbox.OutDir, "findings.json"),
            JsonSerializer.Serialize(Reports, JsonOptions));

        File.WriteAllText(
            Path.Combine(AuditSandbox.OutDir, "limitations.txt"),
            string.Join(Environment.NewLine, Limitations));

        Console.WriteLine();
        Console.WriteLine("==============================================");
        Console.WriteLine($"Views đã audit      : {Reports.Count}");
        Console.WriteLine($"Ảnh PNG             : {AuditSandbox.ShotsDir}");
        Console.WriteLine($"Dump JSON           : {AuditSandbox.DumpDir}");
        Console.WriteLine($"Phát hiện tự động    : {Reports.Sum(r => r.Findings.Count)}");
        Console.WriteLine($"Giới hạn ghi nhận    : {Limitations.Count}");
        Console.WriteLine("==============================================");

        foreach (var lim in Limitations)
        {
            Console.WriteLine("  ! " + lim);
        }
    }
}

/// <summary>Báo cáo cho một view đã audit.</summary>
internal sealed class AuditViewReport
{
    public string View { get; set; } = string.Empty;

    public string Screenshot { get; set; } = string.Empty;

    public string Dump { get; set; } = string.Empty;

    public double WindowWidth { get; set; }

    public double WindowHeight { get; set; }

    public List<AuditMetric> Findings { get; set; } = [];
}
