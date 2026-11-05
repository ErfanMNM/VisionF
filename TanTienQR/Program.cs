using Avalonia;

namespace TanTienQR;

internal static class Program
{
    // Khong dung Avalonia hay API ben thu ba truoc khi AppMain duoc goi.
    [STAThread]
    public static void Main(string[] args) => BuildAvaloniaApp()
        .StartWithClassicDesktopLifetime(args);

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
