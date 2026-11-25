using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using ClassLibraryMT.Print;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TanTienQR;
using TanTienQR.Controls;
using TanTienQR.Services;
using TanTienQR.Services.PrinterRemote;
using TanTienQR.ViewModels;
using TanTienQR.Views;

internal static class Program
{
    private static int _passed;

    [STAThread]
    public static int Main(string[] args)
    {
        try
        {
            if (args.Length == 2 && args[0] == "--live-remote-smoke") return LivePrinterRemoteSmoke.Run(args[1]);
            if (args.Contains("--native-smoke")) return RunNativeSmoke();
            AppBuilder.Configure<App>()
                .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
                .UseSkia().WithInterFont().SetupWithoutStarting();
            RunPanelChecks();
            RunViewChecks();
            Console.WriteLine($"Đã qua {_passed} kiểm tra giao diện máy in.");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }

    private static void RunPanelChecks()
    {
        var child = new Border { UseLayoutRounding = false };
        var panel = new AspectRatioPanel { Children = { child }, UseLayoutRounding = false };
        var cases = new[]
        {
            new Size(1100, 700), new Size(1360, 820), new Size(1920, 1080),
            new Size(316, 620), new Size(800, 220), new Size(100, 56.25),
            new Size(533.333, 290.666), new Size(0, 300), new Size(300, 0)
        };
        // Dùng lại cùng panel qua nhiều lượt resize để kiểm tra cache và giới hạn hai chiều.
        foreach (var size in cases.Concat(cases.Reverse()))
        {
            panel.Measure(size);
            panel.Arrange(new Rect(size));
            var rect = child.Bounds;
            var expected = AspectRatioPanel.Fit(size);
            Check(Math.Abs(rect.Width - expected.Width) < 0.001 && Math.Abs(rect.Height - expected.Height) < 0.001, $"Bounds đúng sau resize {size}");
            Check(rect.Left >= -0.001 && rect.Top >= -0.001 && rect.Right <= size.Width + 0.001 && rect.Bottom <= size.Height + 0.001, $"Không vượt vùng khả dụng {size}");
            Check(Math.Abs(rect.Left * 2 + rect.Width - size.Width) < 0.001 && Math.Abs(rect.Top * 2 + rect.Height - size.Height) < 0.001, $"Căn giữa trong vùng {size}");
            if (rect.Height > 0) Check(Math.Abs(rect.Width / rect.Height - 16d / 9d) < 0.000001, $"Giữ đúng tỷ lệ 16:9 ở {size}");
        }
        Check(AspectRatioPanel.Fit(new Size(double.PositiveInfinity, 270)) == new Size(480, 270), "Đo khi chỉ giới hạn chiều cao");
        Check(AspectRatioPanel.Fit(new Size(960, double.PositiveInfinity)) == new Size(960, 540), "Đo khi chỉ giới hạn chiều rộng");
        Check(AspectRatioPanel.Fit(Size.Infinity) == new Size(960, 540), "Không trả kích thước vô hạn");
    }

    private static void RunViewChecks()
    {
        var sandbox = Path.Combine(Path.GetTempPath(), "VisionFPrinterUi-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(sandbox);
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.Sources.Clear();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Config:BasePath"] = sandbox,
            ["Print:Enabled"] = "true", ["Print:Ip"] = "127.0.0.1", ["Print:Remote:Enabled"] = "false",
            ["Print:Remote:AutoConnect"] = "false", ["Print:Remote:CredentialTarget"] = "TanTien/Test/" + Guid.NewGuid().ToString("N"),
            ["Po:DbRoot"] = Path.Combine(sandbox, "po"), ["Auth:DbPath"] = Path.Combine(sandbox, "auth.db"),
            ["Scans:DbPath"] = Path.Combine(sandbox, "scans.db"), ["PlcSettings:DbPath"] = Path.Combine(sandbox, "plc.db"),
            ["Logging:Sqlite:Path"] = Path.Combine(sandbox, "app.db")
        });
        builder.Logging.ClearProviders();
        builder.Services.AddTanTienServices(builder.Configuration);
        builder.Services.AddSingleton<IPrintLicenseGate>(new TestLicenseGate());
        builder.Services.AddSingleton<IPrinterCredentialStore>(new TestCredentialStore());
        builder.Services.AddSingleton<IPrinterRemoteSession>(new TestRemoteSession());
        using var host = builder.Build();
        // Không Start host: không worker, cổng mạng hoặc máy in thật.
        var assembly = typeof(PrintView).Assembly;
        var printType = assembly.GetType("TanTienQR.ViewModels.PrintViewModel")!;
        var print = host.Services.GetRequiredService(printType);
        var mainType = assembly.GetType("TanTienQR.ViewModels.MainViewModel")!;
        // View chỉ dùng Print; tránh khởi tạo các tab/service không liên quan trong bài kiểm tra này.
        var main = RuntimeHelpers.GetUninitializedObject(mainType);
        mainType.GetField("<Print>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(main, print);
        var remote = (PrinterRemoteViewModel)printType.GetProperty("Remote")!.GetValue(print)!;
        var view = new PrintView { DataContext = main };
        var window = new Window { Content = view, Width = 1092, Height = 660 };
        window.Show();
        var screen = view.FindControl<Border>("RemoteScreen")!;
        var work = view.FindControl<Grid>("WorkArea")!;
        var operations = view.FindControl<Grid>("OperationPanel")!;
        var native = view.FindControl<PrinterRemoteHost>("PrinterScreen")!;
        var shots = Path.Combine(sandbox, "shots");
        Directory.CreateDirectory(shots);
        foreach (var sample in new[] { (1092d, 660d, "wide"), (832d, 500d, "narrow"), (832d, 300d, "keyboard"), (1648d, 900d, "large") })
        {
            window.Width = sample.Item1; window.Height = sample.Item2;
            Layout(window);
            CheckRatio(screen.Bounds.Size, $"Khung trong PrintView {sample.Item3}");
            Check(work.Bounds.Width < 1000 ? Grid.GetRow(operations) == 1 : Grid.GetColumn(operations) == 1, $"Chuyển bố cục {sample.Item3}");
            Check(screen.Bounds.Width > 0 && screen.Bounds.Height > 0, $"Màn hình có diện tích {sample.Item3}");
            Check(ReferenceEquals(native, view.FindControl<PrinterRemoteHost>("PrinterScreen")), "Resize giữ nguyên host");
            SaveShot(window, Path.Combine(shots, sample.Item3 + ".png"));
        }
        remote.IsExpanded = true;
        Layout(window);
        Check(!operations.IsVisible, "Phóng to ẩn cột thao tác");
        CheckRatio(screen.Bounds.Size, "Khung phóng to");
        Check(work.ColumnDefinitions.Count == 1 && work.RowDefinitions.Count == 1, "Phóng to dùng toàn bộ vùng trong tab");
        SaveShot(window, Path.Combine(shots, "expanded.png"));
        remote.IsExpanded = false;
        window.Width = 1092; window.Height = 660;
        Layout(window);
        Check(operations.IsVisible && Grid.GetColumn(operations) == 1, "Thu gọn phục hồi bố cục");
        window.Close();
        Console.WriteLine("Ảnh kiểm tra: " + shots);
    }

    private static int RunNativeSmoke()
    {
        if (!OperatingSystem.IsWindows()) { Console.WriteLine("Bỏ qua smoke native ngoài Windows."); return 0; }
        AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont().SetupWithoutStarting();
        var session = new TestRemoteSession();
        using var remote = new PrinterRemoteViewModel(new PrinterRemoteOptions { Enabled = true, AutoConnect = false, Host = "127.0.0.1", Username = "test" }, session, new TestCredentialStore());
        remote.SetPresentationState(true, true, false);
        var native = new PrinterRemoteHost { Remote = remote };
        var frame = new Border { Child = native };
        var panel = new AspectRatioPanel { Children = { frame } };
        // Cửa sổ native thật nhưng trong suốt và không giành focus/taskbar; tự đóng sau bài kiểm tra.
        var window = new Window { Content = panel, Width = 960, Height = 600, Opacity = 0, ShowInTaskbar = false, ShowActivated = false };
        using var done = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        Exception? error = null;
        void Stage(Action action) { try { action(); } catch (Exception ex) { error = ex; window.Close(); done.Cancel(); } }
        window.Show();
        DispatcherTimer.RunOnce(() => Stage(() =>
        {
            Check(session.HasSurface && session.Handle != 0 && IsWindow(session.Handle), "Native HWND được tạo trước khi Connected");
            Check(!native.IsVisible && !IsWindowVisible(session.Handle), "Chưa kết nối chưa hiện HWND");
            Check(remote.CanConnect, "Có thể Connect từ trạng thái chưa kết nối");
            session.Emit(new(PrinterRemoteState.Connected, "Đã kết nối thử"));
        }), TimeSpan.FromMilliseconds(250));
        DispatcherTimer.RunOnce(() => Stage(() =>
        {
            Check(native.IsVisible && remote.IsSurfaceVisible && session.Visible, "Phiên connected hiện surface");
            CheckRatio(frame.Bounds.Size, "Khung native ban đầu");
            window.Width = 720; window.Height = 400;
        }), TimeSpan.FromMilliseconds(550));
        DispatcherTimer.RunOnce(() => Stage(() =>
        {
            CheckRatio(frame.Bounds.Size, "Khung native sau resize");
            remote.SetPresentationState(true, true, true);
            Check(!native.IsVisible && !session.Visible && !IsWindowVisible(session.Handle), "Overlay ẩn HWND ngay");
            remote.SetPresentationState(true, true, false);
        }), TimeSpan.FromMilliseconds(850));
        DispatcherTimer.RunOnce(() => Stage(() =>
        {
            Check(native.IsVisible && session.Visible, "Gỡ overlay phục hồi surface");
            window.Close();
        }), TimeSpan.FromMilliseconds(1150));
        DispatcherTimer.RunOnce(() => Stage(() =>
        {
            Check(!session.HasSurface, "Đóng cửa sổ tháo surface");
            done.Cancel();
        }), TimeSpan.FromMilliseconds(1500));
        Dispatcher.UIThread.MainLoop(done.Token);
        if (error is not null) throw new InvalidOperationException("Smoke native thất bại", error);
        Check(_passed >= 8, "Hoàn thành đủ các bước smoke native");
        Console.WriteLine($"Đã qua {_passed} kiểm tra native, không kết nối máy in.");
        return 0;
    }

    private static void Layout(Window window)
    {
        for (var i = 0; i < 4; i++) { Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); }
    }
    private static void SaveShot(Window window, string path)
    {
        using var bitmap = new RenderTargetBitmap(new PixelSize((int)window.Bounds.Width, (int)window.Bounds.Height));
        bitmap.Render(window); bitmap.Save(path, new PngBitmapEncoderOptions());
    }
    private static void CheckRatio(Size size, string label) => Check(size.Height > 0 && Math.Abs(size.Width * 9 - size.Height * 16) <= 16, label + " giữ 16:9 sau làm tròn pixel");
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
        _passed++; Console.WriteLine("[OK] " + message);
    }
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindow(nint handle);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindowVisible(nint handle);
}

internal sealed class TestLicenseGate : IPrintLicenseGate { public bool CanUse => true; public string Message => ""; }
internal sealed class TestCredentialStore : IPrinterCredentialStore
{
    public bool IsSupported => true;
    public bool Exists(string target) => false;
    public string? ReadPassword(string target, string username) => null;
    public void Save(string target, string username, string password) => throw new InvalidOperationException("Bài kiểm tra không lưu thông tin đăng nhập thật.");
}
internal sealed class TestRemoteSession : IPrinterRemoteSession
{
    public bool IsSupported => true;
    public bool HasSurface => Handle != 0;
    public nint Handle { get; private set; }
    public bool Visible { get; private set; }
    public PrinterRemoteSnapshot Snapshot { get; private set; } = new(PrinterRemoteState.Disconnected, "Chưa kết nối máy in.");
    public event Action<PrinterRemoteSnapshot>? Changed;
    public void AttachSurface(nint handle) => Handle = handle;
    public void DetachSurface(nint handle) { if (Handle == handle) Handle = 0; }
    public void SetSurfaceVisible(bool visible) => Visible = visible;
    public Task ConnectAsync(string password, CancellationToken cancellationToken = default) => throw new InvalidOperationException("Bài kiểm tra không kết nối thiết bị.");
    public Task DisconnectAsync(CancellationToken cancellationToken = default) { Emit(new(PrinterRemoteState.Disconnected, "Đã ngắt")); return Task.CompletedTask; }
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    public void Emit(PrinterRemoteSnapshot snapshot) { Snapshot = snapshot; Changed?.Invoke(snapshot); }
}
