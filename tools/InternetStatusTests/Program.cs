using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using TanTienQR;
using TanTienQR.Services;
using TanTienQR.ViewModels;
using TanTienQR.Views;
using Path = System.IO.Path;

internal static class Program
{
    private static int _passed;

    [STAThread]
    public static int Main()
    {
        try
        {
            RunThresholdChecks();
            AppBuilder.Configure<App>()
                .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
                .UseSkia().WithInterFont().SetupWithoutStarting();
            RunViewChecks();
            // Headless Avalonia cài SynchronizationContext nhưng chương trình console
            // không chạy dispatcher loop. Chạy kiểm tra worker trên ThreadPool để
            // continuation không bị chờ vô hạn trên dispatcher vừa dùng render.
            Task.Run(RunMonitorChecksAsync).GetAwaiter().GetResult();
            Console.WriteLine($"Đã qua {_passed} kiểm tra card Internet. Không gửi ping thật.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
    }

    private static void RunThresholdChecks()
    {
        foreach (var (milliseconds, expected) in new (long?, int)[]
        {
            (null, 0), (-1, 0), (0, 5), (30, 5), (31, 4), (60, 4),
            (61, 3), (100, 3), (101, 2), (200, 2), (201, 1), (1500, 1),
        })
            Check(InternetLatencyQuality.Classify(milliseconds) == expected,
                $"Độ trễ {milliseconds?.ToString() ?? "null"} ms có mức {expected}");
        Check(new InternetPingSample(false, 20).Level == 0, "Ping lỗi không giữ mức tốt từ RTT cũ");
        Check(InternetLatencyQuality.GetLabel(5) == "Rất tốt"
            && InternetLatencyQuality.GetLabel(1) == "Rất yếu", "Tên hai đầu thang mức chính xác");
    }

    private static void RunViewChecks()
    {
        using var monitor = new InternetConnectionMonitor(new NeverNetworkProbe());
        using var vm = new InternetStatusViewModel(monitor, action => action());
        var card = new InternetStatusCard { DataContext = vm };
        var window = new Window { Width = 200, Height = 155, Content = card, ShowInTaskbar = false };
        window.Show();
        Pump(window);

        Check(vm.Level == 0 && vm.StatusText == "Đang kiểm tra…", "Trạng thái ban đầu chưa kết luận mất mạng");
        Check(card.GetVisualDescendants().OfType<Button>().Count() == 0, "Internet là card, không có nút bấm");
        var border = card.FindControl<Border>("InternetCard")!;
        Check(border is not null, "Card có khung InternetCard riêng");
        Check(border!.Bounds.Width <= 200 && border.Bounds.Width > 150, "Card vừa chiều rộng sidebar 200 px");
        var bars = card.GetVisualDescendants().OfType<Rectangle>()
            .Where(r => r.Classes.Contains("signalBar")).ToArray();
        Check(bars.Length == 5, "Card có đúng 5 vạch chất lượng");
        var output = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../artifacts/internet-status"));
        Directory.CreateDirectory(output);
        foreach (var (latency, level) in new (long, int)[] { (23, 5), (45, 4), (80, 3), (150, 2), (250, 1) })
        {
            vm.ApplySample(new InternetPingSample(true, latency));
            Pump(window);
            Check(bars.Count(b => b.Classes.Contains("active")) == level, $"Hiện đúng {level}/5 vạch");
            Check(vm.StatusText.Contains($"{latency} ms") && vm.DetailText.Contains($"{level}/5"),
                $"Card mức {level} có số ping và mức bằng chữ");
            Check(border.Classes.Contains(level >= 4 ? "ok" : "warn"), $"Màu card theo mức {level}");
            using var frame = window.CaptureRenderedFrame();
            frame?.Save(Path.Combine(output, $"level-{level}.png"), new PngBitmapEncoderOptions());
        }

        vm.ApplySample(InternetPingSample.Unavailable);
        Pump(window);
        Check(vm.Level == 0 && !vm.StatusText.Contains("250") && !vm.StatusText.Contains("ms"),
            "Không có phản hồi xóa số ping cũ");
        Check(bars.All(b => !b.Classes.Contains("active")) && border.Classes.Contains("unavailable"),
            "Không phản hồi: toàn bộ vạch tắt, card trung tính");
        Check(vm.ToolTipText.Contains("ICMP bị chặn") && vm.ToolTipText.Contains("Mbps"),
            "Tooltip phân biệt độ trễ với băng thông và giải thích ICMP");
        using (var frame = window.CaptureRenderedFrame())
            frame?.Save(Path.Combine(output, "unavailable.png"), new PngBitmapEncoderOptions());
        var oldStatus = vm.StatusText;
        vm.Dispose();
        vm.ApplySample(new InternetPingSample(true, 1));
        Check(vm.StatusText == oldStatus, "Không cập nhật card sau khi dispose");
        window.Close();
        Console.WriteLine("Ảnh kiểm tra: " + output);
    }

    private static async Task RunMonitorChecksAsync()
    {
        var blocked = new BlockingProbe();
        var monitor = new InternetConnectionMonitor(blocked, TimeSpan.FromMilliseconds(10));
        monitor.Start();
        monitor.Start();
        await blocked.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
        Check(blocked.Calls == 1, "Gọi Start hai lần chỉ tạo một request");
        monitor.Dispose();
        await monitor.Completion.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
        Check(blocked.Active == 0 && blocked.Calls == 1, "Dispose hủy request đang chờ và không lặp thêm");
        monitor.Start();
        Check(blocked.Calls == 1, "Start sau Dispose không khởi động lại");
        monitor.Dispose();

        var failure = new ThrowingProbe();
        using var failedMonitor = new InternetConnectionMonitor(failure, TimeSpan.FromMilliseconds(10));
        var results = new TaskCompletionSource<InternetPingSample>(TaskCreationOptions.RunContinuationsAsynchronously);
        failedMonitor.SampleReceived += sample => results.TrySetResult(sample);
        failedMonitor.Start();
        var unavailable = await results.Task.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
        Check(!unavailable.IsReachable && unavailable.Level == 0, "Lỗi stack mạng trở thành không phản hồi, không crash");
        failedMonitor.Dispose();
        await failedMonitor.Completion.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);

        var waiting = new InternetConnectionMonitor(new FixedProbe(), TimeSpan.FromMinutes(1));
        var measured = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        waiting.SampleReceived += _ => measured.TrySetResult();
        waiting.Start();
        await measured.Task.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
        waiting.Dispose();
        await waiting.Completion.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
        Check(waiting.Completion.IsCompletedSuccessfully, "Dispose hủy ngay cả thời gian chờ lần đo sau");
    }

    private static void Pump(Window window)
    {
        for (var i = 0; i < 3; i++)
        {
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
    }

    private static void Check(bool condition, string description)
    {
        if (!condition) throw new InvalidOperationException("[FAIL] " + description);
        _passed++;
        Console.WriteLine("[OK] " + description);
    }

    private sealed class NeverNetworkProbe : IInternetPingProbe
    {
        public Task<InternetPingSample> ProbeAsync(CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Bài test giao diện không được gọi mạng.");
    }

    private sealed class FixedProbe : IInternetPingProbe
    {
        public Task<InternetPingSample> ProbeAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new InternetPingSample(true, 20));
    }

    private sealed class ThrowingProbe : IInternetPingProbe
    {
        public Task<InternetPingSample> ProbeAsync(CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Giả lập lỗi ICMP.");
    }

    private sealed class BlockingProbe : IInternetPingProbe
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Calls;
        public int Active;
        public async Task<InternetPingSample> ProbeAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Calls);
            Interlocked.Increment(ref Active);
            Entered.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
                return new InternetPingSample(true, 20);
            }
            finally { Interlocked.Decrement(ref Active); }
        }
    }
}
