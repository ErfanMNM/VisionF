using System.Collections;
using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using System.Windows.Input;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ClassLibraryMT.Auth;
using ClassLibraryMT.DeviceStatus;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TanTienQR;
using TanTienQR.Models;
using TanTienQR.Services;
using TanTienQR.ViewModels;
using TanTienQR.Views;

// No host/workers are started. Every saved file and DB is in a fresh temp
// directory; a fake device applier verifies confirmation without hardware I/O.
const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
var assembly = typeof(App).Assembly;
var sandbox = Path.Combine(Path.GetTempPath(), "VisionF-SettingsV2-" + Guid.NewGuid().ToString("N"));
var output = Path.GetFullPath(args.FirstOrDefault() ?? Path.Combine(sandbox, "screenshots"));
Directory.CreateDirectory(sandbox);
Directory.CreateDirectory(output);
var configPath = Path.Combine(sandbox, "appsettings.json");
File.WriteAllText(configPath, "{\"preservedExtension\":{\"value\":42}}");
var checks = new List<object>();
try
{
    AppBuilder.Configure<App>()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
        .UseSkia().WithInterFont().SetupWithoutStarting();
    var builder = Host.CreateApplicationBuilder();
    builder.Configuration.Sources.Clear();
    builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["Config:BasePath"] = sandbox,
        ["Camera:Ip"] = "127.0.0.1", ["Camera:Port"] = "9000",
        ["Plc:Ip"] = "127.0.0.1", ["Plc:Port"] = "9600", ["Plc:Enabled"] = "true",
        ["Scanner:Enabled"] = "false", ["Web:Enabled"] = "false", ["Checkee:Enabled"] = "false",
        ["Print:Enabled"] = "false", ["Print:Remote:Enabled"] = "false", ["PoServer:Enabled"] = "false",
        ["MultiConnect:Enabled"] = "false",
        ["Po:BaseUrl"] = "http://127.0.0.1:1", ["Po:DbRoot"] = Path.Combine(sandbox, "po"),
        ["Auth:DbPath"] = Path.Combine(sandbox, "users.db"), ["Scans:DbPath"] = Path.Combine(sandbox, "scans.db"),
        ["PlcSettings:DbPath"] = Path.Combine(sandbox, "plc.db"), ["Logging:Sqlite:Path"] = Path.Combine(sandbox, "app.db"),
        ["ManualActivation:HistoryPath"] = Path.Combine(sandbox, "manual-activation.db"),
        ["Checkee:AuditLogPath"] = Path.Combine(sandbox, "checkee-send-audit.jsonl"),
    });
    builder.Logging.ClearProviders();
    builder.Services.AddTanTienServices(builder.Configuration);
    var applier = new FakeDeviceApplier();
    builder.Services.AddSingleton<IDeviceConfigurationApplier>(applier);
    using var host = builder.Build();
    var main = host.Services.GetRequiredService(assembly.GetType("TanTienQR.ViewModels.MainViewModel", true)!);
    var editor = Read<object>(main, "ConfigEditor");
    var session = host.Services.GetRequiredService<ISessionContext>();
    session.SignIn(new UserAccount { Id = 1, Username = "settings-v2-fixture", RoleValue = (int)UserRole.Developer });
    Pump();
    Command(main, "ShowSettingsV2TabCommand").Execute(null);
    Check(Read<AppSection>(main, "CurrentSection") == AppSection.ConfigurationV2
          && Read<bool>(main, "IsSettingsV2Visible"), "Authorized menu opens the new configuration tab");
    var settings = Read<object>(main, "SettingsV2");
    var sections = Read<IEnumerable>(editor, "Sections").Cast<ConfigSectionViewModel>().ToArray();
    var qrBox = Read<object>(settings, "QrBox");
    Check(ReferenceEquals(Read<object>(qrBox, "Section"), sections.Single(section => section.Key == "Plc")),
        "QRBox edits the real PLC section, sharing existing field instances");
    var nodes = Read<IEnumerable>(settings, "AllNodes").Cast<object>().ToArray();
    var other = Read<IEnumerable>(settings, "OtherSections").Cast<ConfigSectionViewModel>().ToArray();
    var flowSections = nodes.Select(node => Read<ConfigSectionViewModel>(node, "Section")).ToArray();
    Check(flowSections.Concat(other).Distinct().Count() == sections.Length
          && !flowSections.Intersect(other).Any(), "Every config section is reachable exactly once in flow or other settings");
    Check(sections.All(section => section.Key != "MesSimulator")
        && sections.Single(section => section.Key == "Checkee").Fields.All(field =>
            field.JsonPath is not ("Checkee:ProductGtin" or "Checkee:BatchCode" or "Checkee:OnlyPassCodes")),
        "Both config views omit retired simulator and PO-derived / unsafe Checkee fields");
    var scannerNode = Read<object>(settings, "Scanner");
    var scannerFields = sections.Single(section => section.Key == "Scanner").Fields;
    var scannerMode = scannerFields.Single(field => field.JsonPath == "Scanner:Mode");
    Check(scannerMode.Options.Contains("Serial") && Read<string>(scannerNode, "Endpoint") == "COM3 · 9600 baud",
        "Serial scanner shows the real configured COM endpoint rather than unused TCP host");
    scannerFields.Single(field => field.JsonPath == "Scanner:ComPort").TextValue = "COM7";
    Check(Read<string>(scannerNode, "Endpoint") == "COM7 · 9600 baud", "Scanner node follows COM draft edits immediately");
    scannerFields.Single(field => field.JsonPath == "Scanner:ComPort").TextValue = "COM3";

    var cameraField = sections.Single(section => section.Key == "Camera").Fields.Single(field => field.JsonPath == "Camera:Ip");
    cameraField.TextValue = "192.168.20.15";
    Check(Read<bool>(editor, "HasUnsavedChanges"), "Editing a V2 field enables the real editor dirty state");
    Command(main, "ShowConfigTabCommand").Execute(null);
    Check(cameraField.TextValue == "192.168.20.15", "Switching to original config preserves the V2 draft");
    Command(main, "ShowSettingsV2TabCommand").Execute(null);
    Check(cameraField.TextValue == "192.168.20.15", "Returning to V2 preserves the shared draft");

    var confirmations = 0;
    Set(editor, "ConfirmConnectionChangesAsync", (Func<string, Task<bool>>)(_ =>
    {
        confirmations++;
        return Task.FromResult(false);
    }));
    var baselineFile = File.ReadAllText(configPath);
    Complete(((IAsyncRelayCommand)Command(editor, "SaveCommand")).ExecuteAsync(null));
    Check(confirmations == 1 && applier.ApplyCount == 0 && File.ReadAllText(configPath) == baselineFile,
        "Cancelled connection confirmation changes neither persisted settings nor devices");
    Set(editor, "ConfirmConnectionChangesAsync", (Func<string, Task<bool>>)(_ => Task.FromResult(true)));
    cameraField.TextValue = "invalid camera host!!";
    Complete(((IAsyncRelayCommand)Command(editor, "SaveCommand")).ExecuteAsync(null));
    Check(Read<bool>(editor, "HasErrorMessage") && applier.ApplyCount == 0 && File.ReadAllText(configPath) == baselineFile,
        "V2 uses existing validation and blocks invalid host before saving/hot-apply");
    cameraField.TextValue = "192.168.20.15";
    Complete(((IAsyncRelayCommand)Command(editor, "SaveCommand")).ExecuteAsync(null));
    using (var saved = JsonDocument.Parse(File.ReadAllText(configPath)))
        Check(applier.ApplyCount == 1 && saved.RootElement.GetProperty("Camera").GetProperty("Ip").GetString() == "192.168.20.15"
              && saved.RootElement.GetProperty("preservedExtension").GetProperty("value").GetInt32() == 42,
            "Confirmed V2 save uses device applier, persists real configuration and preserves extension keys");

    var checkeeEnabled = sections.Single(section => section.Key == "Checkee").Fields
        .Single(field => field.JsonPath == "Checkee:Enabled");
    var checkeeWorker = host.Services.GetRequiredService(assembly.GetType("TanTienQR.Services.CheckeeSendWorker", true)!);
    Check(!Read<bool>(checkeeWorker, "SendingEnabled") && !checkeeEnabled.RequiresRestart,
        "Saved disabled Checkee state remains disabled without requiring restart");
    checkeeEnabled.TextValue = "true";
    cameraField.TextValue = "invalid camera host!!";
    Complete(((IAsyncRelayCommand)Command(editor, "SaveCommand")).ExecuteAsync(null));
    Check(!Read<bool>(checkeeWorker, "SendingEnabled"), "Validation failure does not apply an unsaved Checkee toggle");
    cameraField.TextValue = "192.168.20.15";
    Complete(((IAsyncRelayCommand)Command(editor, "SaveCommand")).ExecuteAsync(null));
    Check(Read<bool>(checkeeWorker, "SendingEnabled") && !Read<bool>(editor, "HasUnsavedChanges")
        && !Read<bool>(editor, "RestartRequired") && applier.ApplyCount == 1,
        "Checkee enable applies to the existing worker after successful save without reconnecting devices or restart");
    checkeeEnabled.TextValue = "false";
    Complete(((IAsyncRelayCommand)Command(editor, "SaveCommand")).ExecuteAsync(null));
    using (var saved = JsonDocument.Parse(File.ReadAllText(configPath)))
        Check(!Read<bool>(checkeeWorker, "SendingEnabled") && !Read<bool>(editor, "RestartRequired")
            && !saved.RootElement.GetProperty("Checkee").GetProperty("Enabled").GetBoolean(),
            "Checkee disable updates runtime and disk together without stale restart state");

    AppStateManager.SetLicenseValid(true);
    AppStateManager.SetCameraStatus(CameraStatus.Connected);
    AppStateManager.SetPlcStatus(PLCStatus.Connected);
    Invoke(main, "RefreshAppState");
    Check(Read<string>(qrBox, "StatusText") == "Đã kết nối", "QRBox connection state follows real app runtime state");
    Check(Read<string>(Read<object>(settings, "Checkee"), "StatusText") != "Đã kết nối",
        "Service nodes do not fabricate connected status from an enabled flag");

    foreach (var size in new[] { (1048, 540), (1280, 720) })
    {
        Command(settings, "ShowFlowCommand").Execute(null);
        Capture(size.Item1, size.Item2, "flow");
        Command(settings, "ShowOtherSettingsCommand").Execute(null);
        Capture(size.Item1, size.Item2, "other");
    }

    // Saving from a selected other category still uses exactly the same field
    // objects; password fields keep masking through the shared field wrapper.
    Set(settings, "SelectedOtherSection", other.Single(section => section.Key == "Auth"));
    Capture(1048, 540, "security");
    Command(main, "ShowInfoTabCommand").Execute(null);
    session.SignOut();
    Pump();
    Command(main, "ShowSettingsV2TabCommand").Execute(null);
    Check(Read<AppSection>(main, "CurrentSection") == AppSection.Info && !Read<bool>(main, "IsSettingsV2Visible"),
        "Signed-out callers cannot navigate to V2 even through its command");
    File.WriteAllText(Path.Combine(output, "results.json"), JsonSerializer.Serialize(new { sandbox, checks }, new JsonSerializerOptions { WriteIndented = true }));
    Console.WriteLine($"RESULT: {checks.Count} passed / 0 failed");
    return 0;

    void Capture(int width, int height, string mode)
    {
        var view = new SettingsV2View { DataContext = main };
        var window = new Window { Width = width, Height = height, Content = view };
        window.Show();
        try
        {
            Pump();
            for (var frame = 0; frame < 3; frame++)
            {
                window.UpdateLayout();
                AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                Pump();
            }
            var descendantControls = view.GetVisualDescendants().OfType<Control>().ToArray();
            Check(descendantControls.OfType<TextBox>().Any(box => box.Bounds.Width >= 200 && box.Bounds.Height >= 32),
                $"{mode} {width}x{height}: property fields remain readable and editable");
            if (mode == "flow")
            {
                var inspector = view.FindControl<Border>("PropertyInspector")!;
                Check(inspector.Bounds.Width == 350 && inspector.Bounds.Height >= 200,
                    $"flow {width}x{height}: property panel keeps usable dimensions");
                var qrBoxButton = view.FindControl<Button>("QrBoxNode")!;
                Check(InsideFlowViewport(qrBoxButton),
                    $"flow {width}x{height}: default QRBox node is fully inside the scrolled viewport");
                var buttons = descendantControls.OfType<Button>()
                    .Where(button => button.DataContext?.GetType().Name == "SettingsV2NodeViewModel").ToArray();
                Check(buttons.Length == 8 && buttons.All(button => button.Bounds.Width >= 200 && button.Bounds.Height >= 90),
                    $"flow {width}x{height}: all eight device/service nodes retain touch target size");
                buttons.First(button => AutomationProperties.GetName(button) == "Camera").Command!.Execute(null);
                Pump();
                Check(Read<ConfigSectionViewModel>(settings, "SelectedSection").Key == "Camera",
                    $"flow {width}x{height}: bound Camera node selects the real camera editor");
                Check(InsideFlowViewport(buttons.First(button => AutomationProperties.GetName(button) == "Camera")),
                    $"flow {width}x{height}: selecting Camera brings its full node into the viewport");
            }
            if (mode == "security")
                Check(descendantControls.OfType<TextBox>().Any(box => box.PasswordChar != '\0'),
                    "Other settings preserve password masking");
            using var bitmap = window.CaptureRenderedFrame() ?? throw new InvalidOperationException("No headless frame");
            var path = Path.Combine(output, $"settings-v2-{mode}-{width}x{height}.png");
            bitmap.Save(path, new PngBitmapEncoderOptions());
            Console.WriteLine("SCREENSHOT: " + path);

            bool InsideFlowViewport(Control control)
            {
                var scroller = view.FindControl<ScrollViewer>("FlowScroller")!;
                var presenter = scroller.GetVisualDescendants()
                    .OfType<Avalonia.Controls.Presenters.ScrollContentPresenter>().First();
                var point = control.TranslatePoint(default, presenter);
                return point is { } p && p.X >= -1 && p.Y >= -1
                    && p.X + control.Bounds.Width <= presenter.Bounds.Width + 1
                    && p.Y + control.Bounds.Height <= presenter.Bounds.Height + 1;
            }
        }
        finally { window.Close(); Pump(); }
    }
}
catch (Exception error)
{
    Console.Error.WriteLine(error);
    File.WriteAllText(Path.Combine(output, "failure.txt"), error.ToString());
    return 1;
}

T Read<T>(object target, string property) => (T)target.GetType().GetProperty(property, flags)!.GetValue(target)!;
void Set(object target, string property, object value) => target.GetType().GetProperty(property, flags)!.SetValue(target, value);
object? Invoke(object target, string method) => target.GetType().GetMethod(method, flags)!.Invoke(target, null);
ICommand Command(object target, string property) => Read<ICommand>(target, property);
void Check(bool condition, string description)
{
    checks.Add(new { description, passed = condition });
    if (!condition) throw new InvalidOperationException("[FAIL] " + description);
    Console.WriteLine("[OK] " + description);
}
void Pump()
{
    for (var iteration = 0; iteration < 5; iteration++)
    {
        Dispatcher.UIThread.RunJobs();
        Thread.Sleep(5);
    }
}
void Complete(Task task)
{
    var watch = Stopwatch.StartNew();
    while (!task.IsCompleted && watch.Elapsed < TimeSpan.FromSeconds(15)) Pump();
    if (!task.IsCompleted) throw new TimeoutException("Config command timed out");
    task.GetAwaiter().GetResult();
    Pump();
}

internal sealed class FakeDeviceApplier : IDeviceConfigurationApplier
{
    public int ApplyCount { get; private set; }
    public Task ApplyAndSaveAsync(AppConfigSnapshot target, AppConfigSnapshot running, Func<bool> persist, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        ApplyCount++;
        if (!persist()) throw new IOException("Fixture persist failed");
        return Task.CompletedTask;
    }
}
