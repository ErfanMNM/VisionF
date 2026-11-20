using Avalonia;
using System.Diagnostics;
using ClassLibraryMT.Updates;

namespace TanTienQR;

internal static class Program
{
    /// <summary>
    /// Argument đánh dấu tiến trình con được khởi động lại từ chính ứng dụng.
    ///
    /// Khi có cờ này, ứng dụng biết mình đang chạy với cấu hình MỚI đã lưu,
    /// nên không cần hiện banner "cần khởi động lại" nữa.
    /// </summary>
    internal const string RestartMarkerArg = "--restart-child";

    /// <summary>True nếu tiến trình hiện tại là tiến trình con sau khi restart.</summary>
    internal static bool IsRestartChild { get; private set; }

    // Khong dung Avalonia hay API ben thu ba truoc khi AppMain duoc goi.
    [STAThread]
    public static void Main(string[] args)
    {
        // Lối tắt cũ vẫn đi qua launcher để phục hồi giao dịch chưa hoàn tất.
        if (UpdateProtocol.LauncherSession is null && File.Exists(UpdateProtocol.GetLauncherPath(AppContext.BaseDirectory)))
        {
            var launcher = new ProcessStartInfo(UpdateProtocol.GetLauncherPath(AppContext.BaseDirectory))
            {
                WorkingDirectory = AppContext.BaseDirectory,
                UseShellExecute = false,
            };
            foreach (var argument in args)
                launcher.ArgumentList.Add(argument);
            Process.Start(launcher);
            return;
        }

        using var applicationLock = UpdateProtocol.AcquireApplicationLock();
        IsRestartChild = args.Any(a =>
            string.Equals(a, RestartMarkerArg, StringComparison.OrdinalIgnoreCase));

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    // Cau hinh Avalonia, dung chung cho trinh xem truoc.
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
#if DEBUG
            .WithDeveloperTools()
#endif
            .WithInterFont()
            .LogToTrace();
}
