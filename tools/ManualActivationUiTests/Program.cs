using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Threading.Channels;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TanTienQR;
using TanTienQR.Models;
using TanTienQR.Services;
using TanTienQR.ViewModels;
using TanTienQR.Views;

// Offline tests only. Host is never started; no COM/network listener, hardware,
// Checkee request or production DB is used. CEO owns execution/build slot.
internal static class Program
{
    private static int _passed;
    public static int Main(string[] args)
    {
        try
        {
            AppBuilder.Configure<App>()
                .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
                .UseSkia().WithInterFont().SetupWithoutStarting();
            ScannerTests();
            UiTests(args.FirstOrDefault());
            Console.WriteLine($"ManualActivationUiTests: {_passed} checks passed.");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }

    private static void ScannerTests()
    {
        var created = new ConcurrentQueue<FakeTransport>();
        var intake = new ScanIntakeGate();
        using var scanner = new HandheldScannerService(Options.Create(new HandheldOptions()),
            NullLogger<HandheldScannerService>.Instance, intake, _ =>
            { var fake = new FakeTransport(); created.Enqueue(fake); return fake; });
        var codes = new ConcurrentQueue<string>();
        scanner.CodeReceived += codes.Enqueue;
        Check(created.IsEmpty && !scanner.IsActive, "constructing the scanner performs no I/O");
        scanner.Activate(); scanner.Activate();
        Wait(() => created.Count == 1 && scanner.StatusText.StartsWith("Đã kết nối", StringComparison.Ordinal));
        var first = created.Single();
        Check(first.Opens == 1 && scanner.IsActive, "one open attempt per active visit");
        const string gs1 = "010893850000000121SERIAL\u001d10LÔ";
        var bytes = Encoding.UTF8.GetBytes(gs1);
        first.Feed(bytes[..^1]); first.Feed(bytes[^1..]);
        Pump(40);
        Check(codes.IsEmpty, "partial frame waits, including split UTF-8");
        first.Feed("\r\n\n");
        Wait(() => codes.Count == 1);
        Check(codes.Single() == gs1, "CRLF produces exactly one full frame and preserves GS1 separator/Unicode");
        first.Feed(new string('X', 513) + "VALID-SUFFIX\nNEXT\n");
        Wait(() => codes.Count == 2);
        Check(codes.Last() == "NEXT", "overlong frame and suffix are discarded until delimiter");
        using (intake.Suspend()) { first.Feed("BLOCKED\n"); Pump(40); }
        Check(codes.Count == 2, "maintenance intake gate suppresses complete frames");
        first.Feed("unfinished"); Pump(30); scanner.Stop();
        Check(first.Disposed && !scanner.IsActive && codes.Count == 2, "stop closes transport and never emits unfinished frame");
        scanner.Start();
        Wait(() => created.Count == 2 && created.Last().Opens == 1);
        created.Last().Feed("REOPEN\n");
        Wait(() => codes.Count == 3);
        Check(codes.Last() == "REOPEN", "Start/Stop aliases reopen with fresh cancellation state");
        created.Last().End();
        Wait(() => scanner.StatusText == "Không có tay quét");
        scanner.Activate(); Pump(50);
        Check(created.Count == 2 && scanner.IsActive, "disconnect reports absence without reconnecting");
        scanner.Deactivate();

        var attempts = 0;
        using var missing = new HandheldScannerService(Options.Create(new HandheldOptions()),
            NullLogger<HandheldScannerService>.Instance, intake, _ =>
            { Interlocked.Increment(ref attempts); return new FakeTransport { FailOpen = true }; });
        missing.Activate(); Wait(() => missing.StatusText == "Không có tay quét");
        missing.Activate(); Pump(50);
        Check(attempts == 1, "missing scanner is tried once even when Activate repeats");
        missing.Deactivate(); missing.Activate();
        Wait(() => attempts == 2 && missing.StatusText == "Không có tay quét");
        Check(attempts == 2, "leaving then returning permits exactly one retry");

        var disabledOpens = 0;
        using var disabled = new HandheldScannerService(Options.Create(new HandheldOptions { Enabled = false }),
            NullLogger<HandheldScannerService>.Instance, intake, _ =>
            { Interlocked.Increment(ref disabledOpens); return new FakeTransport(); });
        disabled.Activate(); Pump(30);
        Check(disabledOpens == 0 && disabled.StatusText == "Không có tay quét", "disabled scanner never constructs a transport");
    }

    private static void UiTests(string? output)
    {
        var root = Path.Combine(Path.GetTempPath(), "VisionF-ManualUi-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var transports = new ConcurrentQueue<FakeTransport>();
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.Sources.Clear();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Config:BasePath"] = root, ["Camera:Ip"] = "127.0.0.1", ["Camera:Port"] = "1",
            ["Plc:Ip"] = "127.0.0.1", ["Plc:Port"] = "1", ["Plc:Enabled"] = "false",
            ["Scanner:Enabled"] = "true", ["Scanner:Mode"] = "Serial", ["Web:Enabled"] = "false",
            ["Checkee:Enabled"] = "false", ["Print:Enabled"] = "false", ["Print:Remote:Enabled"] = "false",
            ["PoServer:Enabled"] = "false", ["MultiConnect:Enabled"] = "false",
            ["Po:BaseUrl"] = "http://127.0.0.1:1", ["Po:DbRoot"] = Path.Combine(root, "po"),
            ["Auth:DbPath"] = Path.Combine(root, "users.db"), ["Scans:DbPath"] = Path.Combine(root, "scans.db"),
            ["PlcSettings:DbPath"] = Path.Combine(root, "plc.db"), ["Logging:Sqlite:Path"] = Path.Combine(root, "app.db"),
            ["ManualActivation:HistoryPath"] = Path.Combine(root, "manual.db"),
            ["ManualActivation:HttpPrefix"] = "http://127.0.0.1:18080/",
        });
        builder.Logging.ClearProviders();
        builder.Services.AddTanTienServices(builder.Configuration);
        builder.Services.Replace(ServiceDescriptor.Singleton<HandheldScannerService>(sp => new(
            sp.GetRequiredService<IOptions<HandheldOptions>>(), NullLogger<HandheldScannerService>.Instance,
            sp.GetRequiredService<ScanIntakeGate>(), _ =>
            { var fake = new FakeTransport(); transports.Enqueue(fake); return fake; })));
        using var host = builder.Build();
        var main = host.Services.GetRequiredService<MainViewModel>();
        var vm = main.ManualActivation;
        var service = host.Services.GetRequiredService<ManualActivationService>();
        var history = host.Services.GetRequiredService<ManualActivationHistoryStore>();
        var scanner = host.Services.GetRequiredService<HandheldScannerService>();
        Check(!scanner.IsActive && transports.IsEmpty, "building DI does not open scanner");
        main.CurrentSection = AppSection.ManualActivation;
        Wait(() => transports.Count == 1 && transports.Last().Opens == 1);
        Check(vm.IsActive && !main.CodeLookup.IsActive, "manual tab exclusively owns scanner");
        Check(vm.PdaStatusText.Contains("chưa chạy", StringComparison.Ordinal), "PDA UI reports listener not started truthfully");
        var view = new ManualActivationView { DataContext = main };
        var window = new Window { Width = 1100, Height = 760, Content = view };
        window.Show(); Pump(80);
        try
        {
            var input = view.FindControl<TextBox>("CodeInputBox")!;
            var add = view.FindControl<Button>("AddCodeButton")!;
            input.Text = "KEYBOARD-CODE"; Pump(30);
            var enter = new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter };
            input.RaiseEvent(enter); Pump(40);
            Check(enter.Handled && Recent(service).Count == 0 && vm.CodeInput == "KEYBOARD-CODE",
                "typing and keyboard Enter never submit manual code");
            Check(Math.Abs(input.Bounds.Height - add.Bounds.Height) < 0.5 && input.Bounds.Height == 38,
                "manual textbox and action buttons align at 38px");
            Complete(vm.AddCommand.ExecuteAsync(null));
            Check(Recent(service).Count == 1 && Recent(service)[0].Source == "Desktop",
                "only explicit Add command records keyboard attempt");
            Check(vm.CurrentRows.Count == 1 && vm.HistoryRows.Count == 1,
                "current table and DB history refresh after submission");

            var rawFrames = 0;
            void Observe(string code) => Interlocked.Increment(ref rawFrames);
            scanner.CodeReceived += Observe;
            transports.Last().Feed("STALE-MANUAL\n");
            if (!SpinWait.SpinUntil(() => Volatile.Read(ref rawFrames) == 1, 2000)) throw new TimeoutException("frame event");
            // Deliberately switch before pumping the queued UI callback.
            main.CurrentSection = AppSection.CodeLookup;
            scanner.CodeReceived -= Observe;
            Pump(80);
            Check(Recent(service).Count == 1 && !vm.IsActive && main.CodeLookup.IsActive,
                "queued manual callback cannot submit after tab switch");
            Wait(() => transports.Count == 2 && transports.Last().Opens == 1);
            transports.Last().Feed("LOOKUP-ONLY\n");
            Wait(() => main.CodeLookup.CodeInput == "LOOKUP-ONLY");
            Check(Recent(service).Count == 1, "lookup scanner input never enters manual queue");
            main.CurrentSection = AppSection.Info;
            Check(!scanner.IsActive && transports.Last().Disposed, "leaving both scanner tabs disconnects transport");
            main.CurrentSection = AppSection.ManualActivation;
            Wait(() => transports.Count == 3 && transports.Last().Opens == 1);
            main.ResumeScannerTab(); main.ResumeScannerTab(); Pump(25);
            Check(transports.Count == 3, "same active tab/resume cannot create duplicate connection");
            transports.Last().Feed("ONE-COM-ATTEMPT\r\n");
            Wait(() => Recent(service).Count == 2 && !vm.IsBusy);
            Check(Recent(service).Count(r => r.CodeContent == "ONE-COM-ATTEMPT") == 1,
                "reopened COM tab has one handler and CRLF submits exactly once");

            main.CurrentSection = AppSection.Info;
            for (var i = 0; i < 13; i++)
                Complete(history.SaveAsync(new ManualActivationResult
                {
                    Id = "fixture-" + i, CodeContent = "010893850000000121DEMO-" + i + "\u001d10LOT-TEST",
                    Source = i % 2 == 0 ? "Desktop" : "PDA", Status = i % 3 == 0 ? "Duplicate" : "Pass",
                    Success = i % 3 != 0, OrderNo = "PO-UI-OFFLINE", Message = "Kết quả minh hoạ trong DB tạm.",
                    ReceivedUtc = DateTime.UtcNow.AddSeconds(i + 1),
                }));
            main.CurrentSection = AppSection.ManualActivation;
            Wait(() => vm.HistoryRows.Count == 10 && vm.HistoryRows[0].Id == "fixture-12");
            Check(vm.HistoryRows[^1].Id == "fixture-3", "reopening loads exactly ten latest persisted DB attempts");
            main.CurrentSection = AppSection.Info;
            main.ResumeScannerTab();
            Check(!scanner.IsActive, "resume on non-scanner tab remains disconnected");
            main.CurrentSection = AppSection.ManualActivation;
            Pump(50);
            if (!string.IsNullOrWhiteSpace(output))
            {
                Directory.CreateDirectory(Path.GetFullPath(output));
                foreach (var size in new[] { new PixelSize(1100, 760), new PixelSize(1366, 768) })
                {
                    window.Width = size.Width; window.Height = size.Height; Pump(80);
                    window.Measure(new Size(size.Width, size.Height));
                    window.Arrange(new Rect(0, 0, size.Width, size.Height));
                    Pump(40);
                    using var bitmap = new RenderTargetBitmap(size, new Vector(96, 96));
                    bitmap.Render(window);
                    var path = Path.Combine(Path.GetFullPath(output), $"manual-activation-{size.Width}x{size.Height}.png");
                    bitmap.Save(path); Console.WriteLine("Screenshot: " + path);
                    Check(view.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == "Thêm mã"),
                        $"manual activation view renders at {size.Width}x{size.Height}");
                }
            }
        }
        finally { main.CurrentSection = AppSection.Info; window.Close(); Pump(20); }
    }

    private static IReadOnlyList<ManualActivationResult> Recent(ManualActivationService service)
    { var task = service.GetRecentAsync(100); Complete(task); return task.GetAwaiter().GetResult(); }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("[FAIL] " + message);
        _passed++; Console.WriteLine("[OK] " + message);
    }
    private static void Wait(Func<bool> condition)
    {
        var watch = Stopwatch.StartNew();
        while (!condition() && watch.Elapsed < TimeSpan.FromSeconds(5)) Pump(5);
        if (!condition()) throw new TimeoutException("Condition not reached in 5 seconds.");
    }
    private static void Complete(Task task)
    {
        Wait(() => task.IsCompleted);
        task.GetAwaiter().GetResult();
    }
    private static void Pump(int milliseconds)
    {
        var watch = Stopwatch.StartNew();
        do { Dispatcher.UIThread.RunJobs(); Thread.Sleep(2); } while (watch.ElapsedMilliseconds < milliseconds);
    }

    private sealed class FakeTransport : IHandheldScannerTransport
    {
        private readonly Channel<byte[]> _packets = Channel.CreateUnbounded<byte[]>(new() { SingleReader = true });
        public bool FailOpen { get; init; }
        public int Opens;
        public volatile bool Disposed;
        public Task OpenAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref Opens);
            return FailOpen ? Task.FromException(new IOException("Fake port unavailable")) : Task.CompletedTask;
        }
        public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken)
        {
            if (!await _packets.Reader.WaitToReadAsync(cancellationToken)) return 0;
            var packet = await _packets.Reader.ReadAsync(cancellationToken);
            packet.AsSpan().CopyTo(buffer.Span); return packet.Length;
        }
        public void Feed(string value) => Feed(Encoding.UTF8.GetBytes(value));
        public void Feed(byte[] bytes) => _packets.Writer.TryWrite(bytes);
        public void End() => _packets.Writer.TryComplete();
        public void Dispose() { Disposed = true; _packets.Writer.TryComplete(); }
    }
}
