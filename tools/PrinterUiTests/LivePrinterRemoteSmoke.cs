using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Microsoft.Extensions.Configuration;
using TanTienQR;
using TanTienQR.Controls;
using TanTienQR.Services.PrinterRemote;
using TanTienQR.ViewModels;

internal static class LivePrinterRemoteSmoke
{
    // Chỉ kết nối thiết bị khi người chạy truyền cờ --live-remote-smoke tường minh.
    public static int Run(string configPath)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Kiểm tra remote thật yêu cầu Windows.");
        var configuration = new ConfigurationBuilder().AddJsonFile(Path.GetFullPath(configPath)).Build();
        var print = configuration.GetSection("Print");
        var section = print.GetSection("Remote");
        var primaryId = print["DefaultPrinterId"] ?? "g10-1";
        var primary = print.GetSection("Printers").GetChildren().FirstOrDefault(p => p["Id"] == primaryId);
        var options = new PrinterRemoteOptions
        {
            Enabled = bool.TryParse(section["Enabled"], out var enabled) && enabled,
            Host = string.IsNullOrWhiteSpace(section["Host"]) ? print["Ip"] ?? primary?["Ip"] ?? "" : section["Host"]!,
            Port = int.TryParse(section["Port"], out var port) ? port : 3389,
            Username = section["Username"] ?? "",
            CredentialTarget = string.IsNullOrWhiteSpace(section["CredentialTarget"]) ? "TanTienQR/Printer/" + primaryId : section["CredentialTarget"]!,
            AutoConnect = false,
            ConnectTimeoutSeconds = int.TryParse(section["ConnectTimeoutSeconds"], out var seconds) ? seconds : 15,
        };
        var credentials = new WindowsPrinterCredentialStore();
        if (!options.Enabled || !credentials.Exists(options.CredentialTarget))
            throw new InvalidOperationException("Cần bật remote và lưu credential trước khi kiểm tra.");

        AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont().SetupWithoutStarting();
        var session = new WindowsPrinterRemoteSession(options);
        using var remote = new PrinterRemoteViewModel(options, session, credentials);
        remote.SetPresentationState(true, true, false);
        var native = new PrinterRemoteHost { Remote = remote };
        var ratio = new AspectRatioPanel { Children = { new Border { Child = native, Background = Brushes.Black } } };
        var status = new TextBlock { Text = "Đang chuẩn bị kết nối remote…", Margin = new Thickness(12), TextWrapping = TextWrapping.Wrap };
        var layout = new DockPanel();
        DockPanel.SetDock(status, Dock.Top);
        layout.Children.Add(status);
        layout.Children.Add(ratio);
        var window = new Window { Title = "Kiểm tra remote máy in thật · chỉ quan sát", Width = 1000, Height = 650, Content = layout };
        using var done = new CancellationTokenSource();
        var finishing = false;
        var connected = false;
        var result = 1;

        async Task FinishAsync(bool success)
        {
            if (finishing) return;
            finishing = true;
            result = success ? 0 : 1;
            try { await session.DisconnectAsync(); await session.DisposeAsync(); }
            catch (Exception ex) { Console.Error.WriteLine("Dọn remote thất bại: " + ex.GetType().Name); result = 1; }
            remote.Dispose();
            window.Close();
            done.Cancel();
        }

        remote.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(PrinterRemoteViewModel.State)) return;
            status.Text = remote.StatusText;
            Console.WriteLine($"REMOTE: {remote.State} · {remote.StatusText}");
            if (remote.IsConnected && !connected)
            {
                connected = true;
                DispatcherTimer.RunOnce(() => _ = FinishAsync(remote.IsConnected), TimeSpan.FromSeconds(15));
            }
            else if (remote.State == PrinterRemoteState.Faulted && !finishing) _ = FinishAsync(false);
        };
        window.Closed += (_, _) => { if (!finishing) _ = FinishAsync(false); };
        window.Show();
        DispatcherTimer.RunOnce(async () =>
        {
            try
            {
                if (!remote.CanConnect) throw new InvalidOperationException("Khung native chưa sẵn sàng để kết nối.");
                await remote.ConnectCommand.ExecuteAsync(null);
            }
            catch (Exception ex) { Console.Error.WriteLine("Remote: " + ex.GetType().Name + " · " + ex.Message); await FinishAsync(false); }
        }, TimeSpan.FromMilliseconds(500));
        DispatcherTimer.RunOnce(() => _ = FinishAsync(false), TimeSpan.FromSeconds(35));
        Dispatcher.UIThread.MainLoop(done.Token);
        Console.WriteLine(result == 0 ? "PASS: Phiên remote thật hiển thị ổn định trong 15 giây và đã dọn helper." : "FAIL: Chưa xác minh được phiên remote thật.");
        return result;
    }
}
