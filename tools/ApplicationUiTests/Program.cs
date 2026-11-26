using System.Collections;
using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ClassLibraryMT.Auth;
using ClassLibraryMT.DeviceStatus;
using ClassLibraryMT.Scans;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TanTienQR;
using TanTienQR.Models;
using TanTienQR.Services;
using TanTienQR.ViewModels;
using TanTienQR.Views;

// Off-screen verification only. The host is never started, no production files
// are copied, all DB paths are absolute and unique, and hardware workers stay off.
internal static class Program
{
    private static readonly Assembly AppAssembly = typeof(App).Assembly;
    private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
    private static readonly List<object> Checks = [];
    private static readonly List<object> Screenshots = [];
    private static string _output = string.Empty;

    public static int Main(string[] args)
    {
        try
        {
            AppBuilder.Configure<App>()
                .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
                .UseSkia().WithInterFont().SetupWithoutStarting();
            _output = args.Length > 0 ? Path.GetFullPath(args[0])
                : Path.Combine(FindRepoRoot(), "artifacts", "application-ui-codecontent");
            Directory.CreateDirectory(_output);
            var sandbox = Path.Combine(Path.GetTempPath(), "VisionF-ApplicationUi-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(sandbox);
            using var host = BuildHost(sandbox);
            var main = Service(host.Services, "TanTienQR.ViewModels.MainViewModel");
            var writer = Service(host.Services, "TanTienQR.Services.PlcWriter");
            var session = host.Services.GetRequiredService<ISessionContext>();
            session.SignIn(new UserAccount
            {
                Id = 1, Username = "ui-fixture", DisplayName = "Kiểm thử giao diện",
                RoleValue = (int)UserRole.Developer,
            });
            Pump(50);

            // Fixture injection bypasses hardware polling only inside this harness.
            // Dashboard values still enter the real ViewModel via RefreshPlcCounters.
            var snapshotType = AppAssembly.GetType("TanTienQR.Services.PlcCounterSnapshot", true)!;
            writer.GetType().GetField("_latestCounters", All)!.SetValue(writer,
                Activator.CreateInstance(snapshotType, [int.MaxValue, int.MaxValue, int.MaxValue, int.MaxValue]));
            AppStateManager.SetLicenseValid(true);
            AppStateManager.SetCameraStatus(CameraStatus.Connected);
            AppStateManager.SetPlcStatus(PLCStatus.Connected);
            Invoke(main, "RefreshAppState");
            var original = Metrics(main);
            Check(original == (int.MaxValue, int.MaxValue, 4_294_967_294L), "Dashboard áp dụng đúng snapshot PLC Int32/Int64");

            // Switching the dashboard source must use runtime processing counters,
            // even while PLC polling is disabled, without sending a machine command.
            var appCounters = Service(host.Services, "TanTienQR.Services.RuntimeCounters");
            Invoke(appCounters, "AddScan", true);
            Invoke(appCounters, "AddScan", false);
            Complete((Task)Invoke(writer, "ApplyConfigurationAsync", new PlcConfig
            {
                Enabled = false, Ip = "127.0.0.1", Port = 1, CounterDisplaySource = "App",
            }, CancellationToken.None)!);
            Invoke(main, "RefreshAppState");
            Check(Metrics(main) == (2L, 1L, 1L) && Read<double>(main, "PassRate") == 50,
                "Nguồn App hiển thị số đã xử lý và tỷ lệ đúng kể cả khi tắt PLC");
            Check(Read<string>(main, "CounterSourceText") == "APP"
                && Read<string>(main, "TotalScansLabel") == "TỔNG SỐ · APP",
                "Nhãn nguồn và số tổng chuyển đồng thời sang APP");
            Invoke(main, "RefreshAppState");
            Check(Metrics(main) == (2L, 1L, 1L), "Refresh không tự tăng số app");
            Complete((Task)Invoke(writer, "ApplyConfigurationAsync", new PlcConfig
            {
                Enabled = true, Ip = "127.0.0.1", Port = 1, ResetCounterAddress = "D60",
                ClearErrorAddress = "D62", CounterDisplaySource = "PLC",
            }, CancellationToken.None)!);
            writer.GetType().GetField("_latestCounters", All)!.SetValue(writer,
                Activator.CreateInstance(snapshotType, [int.MaxValue, int.MaxValue, int.MaxValue, int.MaxValue]));
            AppStateManager.SetPlcStatus(PLCStatus.Connected);
            Invoke(main, "RefreshAppState");
            Check(Metrics(main) == original && Read<string>(main, "CounterSourceText") == "PLC",
                "Đổi lại PLC dùng snapshot thiết bị và không trộn số app");

            var longCode = "PO-CODE-CONTENT://full/" + string.Concat(Enumerable.Repeat("A9_ĐầyĐủ_%25/CaseSensitive?x=1&y=2-", 20));
            foreach (var outcome in new[] { ScanOutcome.Pass, ScanOutcome.Duplicate, ScanOutcome.InvalidForOrder, ScanOutcome.NoRead, ScanOutcome.InvalidStructure })
                Invoke(main, "AddScan", new ScanRecord(longCode, outcome, DateTime.UtcNow, true, 0.333));
            Check(Metrics(main) == original, "Sự kiện camera không cộng hoặc thay thế các số lớn lấy từ PLC");
            var structureRow = new ScanRowViewModel(new ScanRecord(longCode, ScanOutcome.InvalidStructure, DateTime.UtcNow, true, 0.333));
            Check(structureRow.OutcomeText == "Sai cấu trúc" && structureRow.IsInvalid
                && structureRow.OutcomeBadgeClass == "fault" && structureRow.CodeText == longCode,
                "Bảng quét hiển thị chính xác Sai cấu trúc bằng badge đỏ và giữ nguyên mã");
            var skippedStructure = new ScanRowViewModel(new ScanRecord(longCode, ScanOutcome.InvalidStructure,
                DateTime.UtcNow, false, 0.333, PlcSkipped: true));
            Invoke(main, "AddScan", new ScanRecord(longCode, ScanOutcome.InvalidStructure, DateTime.UtcNow,
                false, 0.333, PlcSkipped: true));
            Check(skippedStructure.OutcomeText == "Sai cấu trúc" && skippedStructure.PlcText == "Bỏ qua"
                && skippedStructure.PlcBadgeClass == "warning",
                "Chưa chạy PO hiển thị Sai cấu trúc và PLC Bỏ qua, không coi là lỗi PLC");
            var events = ((IEnumerable)Read<object>(main, "Events")).Cast<ScanEvent>().ToArray();
            Check(events.Any(item => item.Message.Contains("Sai cấu trúc", StringComparison.Ordinal)
                    && item.Message.Contains(longCode, StringComparison.Ordinal))
                && !events.Any(item => item.Message.Contains("Ghi kết quả xuống PLC thất bại", StringComparison.Ordinal)),
                "Sự kiện Sai cấu trúc chứa đầy đủ mã, không báo lỗi PLC khi Fail đã ghi thành công");

            var lookup = Read<object>(main, "CodeLookup");
            var historyStore = host.Services.GetRequiredService<IScanSendStatusStore>();
            var pendingAttempt = historyStore.BeginSendAttemptAsync([new ScanRecordEntity
            {
                Id = 1001, CodeContent = longCode, Raw = longCode, OrderNo = "PO-UI-001",
            }], "04006381333931", "PO-UI-001", "DECLARE");
            Complete(pendingAttempt);
            Complete((Task)Invoke(lookup, "LoadSendHistoryAsync")!);
            var liveAttemptRow = ((IList)Read<object>(lookup, "SendHistoryRows"))[0]!;
            Complete((Task)Invoke(lookup, "ShowSendAttemptAsync", liveAttemptRow)!);
            Check(Read<bool>(lookup, "IsSendDetailsOpen")
                && Read<string>(liveAttemptRow, "StatusText") == "Chưa xác định",
                "Nhấn lần gửi chưa có ACK mở chi tiết chưa xác định từ kho lịch sử thật");
            Complete(historyStore.CompleteSendAttemptAsync(pendingAttempt.Result,
                CheckeeSendAttemptStatuses.Sent, 200, "Checkee xác nhận batch."));
            Complete((Task)Invoke(lookup, "LoadSendHistoryAsync")!);
            var refreshedAttempt = Read<object>(lookup, "SelectedSendAttempt");
            var refreshedDetails = ((IList)Read<object>(lookup, "SendDetailRows"));
            Check(Read<string>(refreshedAttempt, "StatusText") == "OK" && refreshedDetails.Count == 1
                && Read<string>(refreshedDetails[0]!, "StatusText") == "OK",
                "Làm mới lịch sử cập nhật cả chi tiết đang mở sau ACK, không giữ kết quả cũ");
            Read<ICommand>(lookup, "CloseSendDetailsCommand").Execute(null);
            ((IList)Read<object>(lookup, "SendHistoryRows")).Clear();
            var row = new CodeLookupRowViewModel(new ScanRecordEntity
            {
                CodeContent = longCode, Raw = longCode, OrderNo = "PO-UI-001",
                Status = ScanStatuses.Pass, PlcResult = ScanPlcResults.Ok,
                ReceivedUtc = DateTime.UtcNow, ProcessingTimeMs = 0.333,
            });
            Set(lookup, "CodeInput", longCode);
            Set(lookup, "Latest", row);
            Set(lookup, "Rows", new[] { row });
            Set(lookup, "TotalCount", 1L);
            var historyRow = Activator.CreateInstance(AppAssembly.GetType(
                "TanTienQR.ViewModels.CheckeeSendHistoryRowViewModel", true)!, [new CheckeeSendAttempt
            {
                Id = 1, StartedUtc = DateTime.UtcNow, CompletedUtc = DateTime.UtcNow,
                ProductGtin = "04006381333931", BatchCode = "PO-UI-001", SerialType = "DECLARE",
                TotalCount = 2, OkCount = 1, FailCount = 1, Status = CheckeeSendAttemptStatuses.Failed,
                StatusCode = 422, Message = "Kết quả lịch sử của lần gửi.",
            }])!;
            ((IList)Read<object>(lookup, "SendHistoryRows")).Add(historyRow);
            Check(row.CodeText == longCode, "Tra cứu giữ nguyên toàn bộ CodeContent");
            foreach (var (status, label) in new[]
            {
                (ScanStatuses.Duplicate, "Trùng"), (ScanStatuses.InvalidForOrder, "Không tồn tại"), (ScanStatuses.NoRead, "NoRead"),
                (ScanStatuses.InvalidStructure, "Sai cấu trúc"),
            })
            {
                var failure = new CodeLookupRowViewModel(new ScanRecordEntity { CodeContent = longCode, Status = status });
                Check(failure.StatusText == label && failure.IsFail, "Tra cứu hiển thị đúng kết quả " + status);
            }
            Check(Read<bool>(main, "CanResetPlcCounters"), "Phiên có EditConfig được bật lệnh reset đã cấu hình");
            Check(Read<bool>(main, "CanClearPlcErrors"), "Phiên có EditConfig được bật lệnh Clear đã cấu hình");

            foreach (var (width, height) in new[] { (1280, 720), (1440, 900) })
            {
                Capture(new InfoView { DataContext = main }, $"Info_{width}x{height}", width, height, longCode, info: true);
                Capture(new CodeLookupView { DataContext = main }, $"CodeLookup_{width}x{height}", width, height, longCode, info: false);
            }
            // A MainWindow also reserves 232px for its sidebar and a top header.
            Capture(new InfoView { DataContext = main }, "Info_content_1048x540", 1048, 540, longCode, info: true);
            Capture(new CodeLookupView { DataContext = main }, "CodeLookup_content_1048x540", 1048, 540, longCode, info: false);

            Set(lookup, "SelectedSendAttempt", historyRow);
            ((IList)Read<object>(lookup, "SendDetailRows")).Clear();
            var detailRow = Activator.CreateInstance(AppAssembly.GetType(
                "TanTienQR.ViewModels.CheckeeSendDetailRowViewModel", true)!, [new CheckeeSendAttemptItem
            {
                ScanRecordId = 1, CodeContent = longCode, OrderNo = "PO-UI-001",
                Status = CheckeeSendAttemptStatuses.Failed, StatusCode = 422,
                Message = "Thông điệp phản hồi dùng cho kiểm thử hiển thị đầy đủ nội dung mã.",
            }])!;
            ((IList)Read<object>(lookup, "SendDetailRows")).Add(detailRow);
            Set(lookup, "IsSendDetailsOpen", true);
            Capture(new CodeLookupView { DataContext = main }, "CodeLookup_send_details_1280x720", 1280, 720, longCode, info: false);
            Capture(new CodeLookupView { DataContext = main }, "CodeLookup_send_details_1048x540", 1048, 540, longCode, info: false);

            var production = Read<object>(main, "ProductionInfo");
            var orders = Read<object>(production, "Orders");
            Set(orders, "IsHalted", true);
            Pump(50);
            Check(Read<bool>(production, "IsHalted"), "ProductionInfo phản ánh trạng thái halt để đối soát");
            Capture(new InfoView { DataContext = main }, "Info_halted_1280x720", 1280, 720, longCode, info: true);
            Set(orders, "IsHalted", false);

            session.SignOut();
            Pump(50);
            Check(!Read<bool>(main, "CanResetPlcCounters"), "Đăng xuất tắt quyền reset trong ViewModel");
            Check(!Read<bool>(main, "CanClearPlcErrors"), "Đăng xuất tắt quyền Clear trong ViewModel");
            var resetCommand = Read<ICommand>(main, "ResetPlcCountersCommand");
            Check(!resetCommand.CanExecute(null), "Đăng xuất tắt CanExecute của lệnh reset");
            var denied = (Task)Invoke(main, "ResetPlcCountersAsync")!;
            Complete(denied);
            var clearCommand = Read<ICommand>(main, "ClearPlcErrorsCommand");
            Check(!clearCommand.CanExecute(null), "Đăng xuất tắt CanExecute của lệnh Clear");
            Complete((Task)Invoke(main, "ClearPlcErrorsAsync")!);
            Check(Metrics(main) == original, "Gọi trực tiếp lệnh reset không quyền không xóa số hiển thị cục bộ");
            File.WriteAllText(Path.Combine(_output, "results.json"), JsonSerializer.Serialize(new
            {
                sandbox, checks = Checks, screenshots = Screenshots,
                note = "Avalonia.Headless + Skia; host/workers not started; synthetic PLC snapshot; no production DBs read or written.",
            }, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine($"Passed {Checks.Count} UI checks. Screenshots: {_output}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            if (!string.IsNullOrEmpty(_output))
                File.WriteAllText(Path.Combine(_output, "results.json"), JsonSerializer.Serialize(new
                {
                    succeeded = false, error = ex.ToString(), checks = Checks, screenshots = Screenshots,
                }, new JsonSerializerOptions { WriteIndented = true }));
            return 1;
        }
    }

    private static void Capture(UserControl view, string stem, int width, int height, string longCode, bool info)
    {
        var window = new Window
        {
            Width = width, Height = height, CanResize = false, WindowDecorations = WindowDecorations.None,
            WindowStartupLocation = WindowStartupLocation.Manual, Position = new PixelPoint(-4000, -4000),
            Background = new SolidColorBrush(Color.Parse("#F6F9FB")), Content = view,
        };
        try
        {
            window.Show();
            for (var pass = 0; pass < 3; pass++) { window.UpdateLayout(); Pump(100); }
            var blocks = view.GetVisualDescendants().OfType<TextBlock>().ToArray();
            var nodes = blocks.Where(block => block.IsVisible).Select(block =>
            {
                var at = block.TranslatePoint(default, window) ?? default;
                return new { block.Text, x = at.X, y = at.Y, width = block.Bounds.Width, height = block.Bounds.Height,
                    wrap = block.TextWrapping.ToString(), trimming = block.TextTrimming.ToString() };
            }).ToArray();
            File.WriteAllText(Path.Combine(_output, stem + ".tree.json"), JsonSerializer.Serialize(nodes,
                new JsonSerializerOptions { WriteIndented = true }));
            using var bitmap = new RenderTargetBitmap(new PixelSize(width, height), new Vector(96, 96));
            bitmap.Render(window);
            bitmap.Save(Path.Combine(_output, stem + ".png"), new PngBitmapEncoderOptions());
            Screenshots.Add(new { file = stem + ".png", width, height, codeLength = longCode.Length });
            var codes = blocks.Where(block => block.Text == longCode).ToArray();
            Check(codes.Length > 0 && codes.All(block => block.TextWrapping == TextWrapping.Wrap
                && block.TextTrimming == TextTrimming.None), stem + ": nội dung mã đầy đủ dùng wrap, không ellipsis");
            Check(codes.All(block => block.Bounds.Width > 0 && block.Bounds.Width <= view.Bounds.Width),
                stem + ": mã dài có vùng hiển thị hữu hạn theo chiều ngang");
            Check(!blocks.Any(block => block.Text is "GTIN" or "MÃ / SERIAL"), stem + ": không còn cột định danh GTIN/serial");
            if (info)
            {
                var headerCode = codes.FirstOrDefault(block => block.GetVisualAncestors().OfType<ScrollViewer>()
                    .Any(scroll => scroll.MaxHeight <= 72));
                Check(headerCode is not null, stem + ": mã cuối có vùng cuộn giới hạn 72px");
                var scalableKpis = view.GetVisualDescendants().OfType<Viewbox>()
                    .Count(box => box.StretchDirection == StretchDirection.DownOnly);
                Check(scalableKpis >= 3, stem + ": ba bộ đếm PLC lớn tự co vừa ô KPI");
                if (stem.Contains("halted", StringComparison.Ordinal))
                {
                    Check(blocks.Any(block => block.IsEffectivelyVisible && block.Text is not null
                        && block.Text.Contains("đối soát mã, kết quả PLC và dữ liệu PO", StringComparison.Ordinal)),
                        stem + ": banner halt hướng dẫn đối soát PLC/PO, không khẳng định huỷ PO");
                    Check(!blocks.Any(block => block.IsEffectivelyVisible && block.Text is not null
                        && block.Text.Contains("huỷ PO", StringComparison.Ordinal)),
                        stem + ": không hiện nguyên nhân huỷ PO không được xác nhận");
                }
            }
            else
            {
                var history = view.GetVisualDescendants().OfType<ItemsControl>()
                    .FirstOrDefault(items => items.Items.OfType<CodeLookupRowViewModel>().Any());
                var viewport = history?.GetVisualAncestors().OfType<ScrollViewer>().FirstOrDefault();
                Check(viewport is not null && viewport.Bounds.Height >= 24,
                    stem + ": bảng các lần quét còn vùng cuộn nhìn thấy ít nhất một dòng");
                var firstRow = history!.Items.OfType<CodeLookupRowViewModel>().First();
                var metadata = new[] { firstRow.TimeText, firstRow.StatusText, firstRow.PlcText,
                    firstRow.OrderNoText, firstRow.ProcessingTimeText, firstRow.SendStatusText };
                Check(metadata.All(text => blocks.Any(block => block.Text == text
                    && block.GetVisualAncestors().OfType<ItemsControl>().Contains(history)
                    && block.TranslatePoint(default, viewport!) is { } point
                    && point.Y >= 0 && point.Y + block.Bounds.Height <= viewport!.Bounds.Height)),
                    stem + ": thời gian/kết quả/PLC/PO/gửi hiện cùng đầu mã dài");
            }
        }
        finally { window.Close(); Pump(30); }
    }

    private static IHost BuildHost(string root)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.Sources.Clear();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Config:BasePath"] = root,
            ["Camera:Ip"] = "127.0.0.1", ["Camera:Port"] = "1",
            ["Plc:Ip"] = "127.0.0.1", ["Plc:Port"] = "1", ["Plc:Enabled"] = "true",
            ["Plc:TimeoutMs"] = "100", ["Plc:Retries"] = "0", ["Plc:CounterAddress"] = "D100", ["Plc:ResetCounterAddress"] = "D60",
            ["Scanner:Enabled"] = "false", ["Web:Enabled"] = "false", ["Checkee:Enabled"] = "false",
            ["Print:Enabled"] = "false", ["Print:Remote:Enabled"] = "false", ["PoServer:Enabled"] = "false",
            ["MultiConnect:Enabled"] = "false", ["MesSimulator:Enabled"] = "false",
            ["Po:BaseUrl"] = "http://127.0.0.1:1", ["Po:DbRoot"] = Path.Combine(root, "po"),
            ["Auth:DbPath"] = Path.Combine(root, "users.db"), ["Scans:DbPath"] = Path.Combine(root, "scans.db"),
            ["PlcSettings:DbPath"] = Path.Combine(root, "plc.db"), ["Logging:Sqlite:Path"] = Path.Combine(root, "app.db"),
        });
        builder.Logging.ClearProviders();
        builder.Services.AddTanTienServices(builder.Configuration);
        return builder.Build();
    }

    private static (long Total, long Pass, long Fail) Metrics(object main) =>
        (Read<long>(main, "TotalScans"), Read<long>(main, "PassCount"), Read<long>(main, "FailCount"));
    private static object Service(IServiceProvider provider, string type) => provider.GetRequiredService(AppAssembly.GetType(type, true)!);
    private static T Read<T>(object target, string property) => (T)target.GetType().GetProperty(property, All)!.GetValue(target)!;
    private static void Set(object target, string property, object value) => target.GetType().GetProperty(property, All)!.SetValue(target, value);
    private static object? Invoke(object target, string method, params object?[] args) => target.GetType().GetMethod(method, All)!.Invoke(target, args);
    private static void Pump(int milliseconds)
    {
        var watch = Stopwatch.StartNew();
        do { Dispatcher.UIThread.RunJobs(); Thread.Sleep(5); } while (watch.ElapsedMilliseconds < milliseconds);
    }
    private static void Complete(Task task)
    {
        var watch = Stopwatch.StartNew();
        while (!task.IsCompleted && watch.Elapsed < TimeSpan.FromSeconds(10)) Pump(5);
        if (!task.IsCompleted) throw new TimeoutException("UI command did not complete in 10 seconds.");
        task.GetAwaiter().GetResult();
    }
    private static void Check(bool condition, string description)
    {
        Checks.Add(new { description, passed = condition });
        if (!condition) throw new InvalidOperationException("[FAIL] " + description);
        Console.WriteLine("[OK] " + description);
    }
    private static string FindRepoRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (Directory.Exists(Path.Combine(directory.FullName, "TanTienQR"))) return directory.FullName;
        throw new DirectoryNotFoundException("Cannot locate the VisionF repository.");
    }
}
