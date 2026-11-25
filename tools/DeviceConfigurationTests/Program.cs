using System.Reflection;
using ClassLibraryMT.Communications.CodeReaderProtocol;
using ClassLibraryMT.Communications.Plc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TanTienQR;
using TanTienQR.Services;

var passed = 0;
void Check(bool condition, string description)
{
    if (!condition) throw new InvalidOperationException("[FAIL] " + description);
    passed++;
    Console.WriteLine("[OK] " + description);
}

static T Read<T>(object owner, string property) => (T)owner.GetType().GetProperty(property)!.GetValue(owner)!;
static AppConfigSnapshot Snapshot(int cameraPort, int seconds, int plcPort, string result = "D12", string ready = "D16") => new()
{
    Camera = new CameraConfig { Ip = "127.0.0.1", Port = cameraPort, DuplicateWindowSeconds = seconds },
    Plc = new PlcConfig
    {
        Enabled = false, Ip = "127.0.0.1", Port = plcPort, RemoteNode = 0, LocalNode = 1,
        TimeoutMs = 20, Retries = 0, ResultAddress = result, ReadyAddress = ready, RefreshIntervalMs = 1000,
    },
};

async Task<Exception> FailsAsync(Func<Task> operation)
{
    try { await operation(); }
    catch (Exception ex) { return ex; }
    throw new InvalidOperationException("Thao tác đáng lẽ phải thất bại.");
}

try
{
    var sandbox = Path.Combine(Path.GetTempPath(), "VisionFDeviceConfig-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(sandbox);
    var configPath = Path.Combine(sandbox, "appsettings.json");
    File.WriteAllText(configPath, "original-file");
    var builder = Host.CreateApplicationBuilder();
    builder.Configuration.Sources.Clear();
    builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["Config:BasePath"] = sandbox,
        ["Camera:Ip"] = "127.0.0.1", ["Camera:Port"] = "19101", ["Camera:DuplicateWindowSeconds"] = "30",
        ["Plc:Enabled"] = "false", ["Plc:Ip"] = "127.0.0.1", ["Plc:Port"] = "19600",
        ["Plc:TimeoutMs"] = "20", ["Plc:Retries"] = "0", ["Plc:RemoteNode"] = "0", ["Plc:LocalNode"] = "1",
        ["Plc:ResultAddress"] = "D12", ["Plc:ReadyAddress"] = "D16", ["Plc:RefreshIntervalMs"] = "1000",
        ["Print:Enabled"] = "false", ["Print:Remote:Enabled"] = "false", ["Print:Remote:AutoConnect"] = "false",
        ["Po:DbRoot"] = Path.Combine(sandbox, "po"), ["Auth:DbPath"] = Path.Combine(sandbox, "auth.db"),
        ["Scans:DbPath"] = Path.Combine(sandbox, "scans.db"), ["PlcSettings:DbPath"] = Path.Combine(sandbox, "plc.db"),
        ["Logging:Sqlite:Path"] = Path.Combine(sandbox, "app.db"),
    });
    builder.Logging.ClearProviders();
    builder.Services.AddTanTienServices(builder.Configuration);
    using var host = builder.Build();
    // Không host.Start(): không worker camera/heartbeat, listener hoặc thiết bị thật.
    var applier = host.Services.GetRequiredService<IDeviceConfigurationApplier>();
    var intake = host.Services.GetRequiredService<ScanIntakeGate>();
    var reader = host.Services.GetRequiredService<Reader>();
    var plc = host.Services.GetRequiredService<OmronPlc>();
    var assembly = typeof(App).Assembly;
    object Options(string typeName)
    {
        var type = assembly.GetType("TanTienQR.Services." + typeName)!;
        var wrapped = host.Services.GetRequiredService(typeof(IOptions<>).MakeGenericType(type));
        return wrapped.GetType().GetProperty("Value")!.GetValue(wrapped)!;
    }
    var cameraOptions = Options("CameraOptions");
    var plcOptions = Options("PlcOptions");
    var suspend = typeof(ScanIntakeGate).GetMethod("Suspend", BindingFlags.Instance | BindingFlags.NonPublic)!;
    var setSuspended = typeof(ScanIntakeGate).GetMethod("SetSuspended", BindingFlags.Instance | BindingFlags.NonPublic)!;
    IDisposable Suspend() => (IDisposable)suspend.Invoke(intake, null)!;
    void SetSuspended(bool value) => setSuspended.Invoke(intake, new object[] { value });
    bool Matches(AppConfigSnapshot expected) => reader.IP == expected.Camera.Ip && reader.Port == expected.Camera.Port
        && Read<string>(cameraOptions, "Ip") == expected.Camera.Ip
        && Read<int>(cameraOptions, "Port") == expected.Camera.Port
        && Read<int>(cameraOptions, "DuplicateWindowSeconds") == expected.Camera.DuplicateWindowSeconds
        && plc.IpAddress == expected.Plc.Ip && plc.Port == expected.Plc.Port && plc.Enabled == expected.Plc.Enabled
        && Read<bool>(plcOptions, "Enabled") == expected.Plc.Enabled
        && Read<int>(plcOptions, "Port") == expected.Plc.Port
        && Read<string>(plcOptions, "ResultAddress") == expected.Plc.ResultAddress
        && Read<string>(plcOptions, "ReadyAddress") == expected.Plc.ReadyAddress
        && Read<int>(plcOptions, "TimeoutMs") == expected.Plc.TimeoutMs
        && Read<int>(plcOptions, "RefreshIntervalMs") == expected.Plc.RefreshIntervalMs;

    var original = Snapshot(19101, 30, 19600);
    Check(Matches(original) && !intake.IsSuspended, "DI dựng đúng runtime Camera/PLC ban đầu");
    Check(!plc.Enabled && typeof(OmronPlc).GetField("_transport", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(plc) is null,
        "PLC tắt không mở transport trong bài test offline");
    Check(!(bool)typeof(Reader).GetField("_isRunning", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(reader)!,
        "Reader không chạy socket TCP vì host chưa start");

    var target = Snapshot(19102, 60, 19601, "D20", "D24");
    var persisted = 0;
    await applier.ApplyAndSaveAsync(target, original, () =>
    {
        Check(intake.IsSuspended && Matches(target), "Cả Camera/PLC đã đổi và intake đang chặn trước khi ghi file");
        persisted++;
        File.WriteAllText(configPath, "saved-target");
        return true;
    }, CancellationToken.None);
    Check(persisted == 1 && File.ReadAllText(configPath) == "saved-target" && Matches(target),
        "Áp dụng thành công mới persist đúng một lần");
    Check(!intake.IsSuspended, "Apply thành công mở lại nhận mã");

    var rejectedTarget = Snapshot(19103, 5, 19602, "D30", "D34");
    var saveError = await FailsAsync(() => applier.ApplyAndSaveAsync(rejectedTarget, target, () => false, CancellationToken.None));
    Check(saveError.Message.Contains("khôi phục") && Matches(target), "Persist=false rollback cả Camera/PLC về runtime cũ");
    Check(!intake.IsSuspended && File.ReadAllText(configPath) == "saved-target", "Lưu lỗi giữ file cũ và mở lại intake sau rollback thành công");

    var beforeInvalid = File.ReadAllText(configPath);
    var invalidCamera = Snapshot(19104, 10, 19601, "D20", "D24");
    invalidCamera.Camera.Ip = "::1";
    var invalidPersistCalls = 0;
    await FailsAsync(() => applier.ApplyAndSaveAsync(invalidCamera, target, () =>
    {
        invalidPersistCalls++;
        File.WriteAllText(configPath, "must-not-write");
        return true;
    }, CancellationToken.None));
    Check(invalidPersistCalls == 0 && Matches(target) && File.ReadAllText(configPath) == beforeInvalid,
        "Endpoint camera không hỗ trợ không ghi file hoặc mất runtime cũ");
    Check(!intake.IsSuspended, "Lỗi validate camera giải phóng suspension sau rollback");

    var invalidPlc = Snapshot(19105, 12, 19602, "C12", "D34");
    await FailsAsync(() => applier.ApplyAndSaveAsync(invalidPlc, target, () =>
    {
        invalidPersistCalls++;
        return true;
    }, CancellationToken.None));
    Check(invalidPersistCalls == 0 && Matches(target) && !intake.IsSuspended,
        "PLC sai sau khi camera đã đổi vẫn rollback camera và không persist");

    using (var cancelled = new CancellationTokenSource())
    {
        cancelled.Cancel();
        var error = await FailsAsync(() => applier.ApplyAndSaveAsync(rejectedTarget, target,
            () => throw new InvalidOperationException("Không được persist khi hủy."), cancelled.Token));
        Check(error is OperationCanceledException && Matches(target) && !intake.IsSuspended,
            "Cancellation trước apply không đụng runtime/file hoặc giữ lease");
    }

    SetSuspended(true);
    var maintenancePersisted = false;
    await FailsAsync(() => applier.ApplyAndSaveAsync(rejectedTarget, target, () => maintenancePersisted = true, CancellationToken.None));
    Check(!maintenancePersisted && Matches(target) && intake.IsSuspended,
        "Từ chối đổi thiết bị khi bảo trì/update đã tạm dừng intake");
    SetSuspended(false);

    IDisposable? otherOperation = null;
    await applier.ApplyAndSaveAsync(rejectedTarget, target, () =>
    {
        otherOperation = Suspend();
        return true;
    }, CancellationToken.None);
    Check(Matches(rejectedTarget) && intake.IsSuspended, "Apply không mở lease nhận mã do tác vụ khác vừa giữ");
    otherOperation!.Dispose();
    otherOperation.Dispose();
    Check(!intake.IsSuspended, "Dispose lease idempotent không làm âm bộ đếm suspension");

    using (var firstLease = Suspend())
    {
        var secondLease = Suspend();
        SetSuspended(true);
        secondLease.Dispose();
        Check(intake.IsSuspended, "Giải phóng một lease không xóa lease/bảo trì khác");
    }
    Check(intake.IsSuspended, "Giải phóng mọi lease vẫn giữ legacy maintenance");
    SetSuspended(false);
    Check(!intake.IsSuspended, "Chỉ hết chặn khi cả legacy maintenance và mọi lease đã kết thúc");

    // Tiêm baseline rollback không hợp lệ để kiểm tra nhánh bảo vệ cuối cùng.
    // Runtime thật và target vẫn PLC disabled nên không phát UDP.
    var brokenRollbackBaseline = Snapshot(rejectedTarget.Camera.Port, rejectedTarget.Camera.DuplicateWindowSeconds, 0,
        rejectedTarget.Plc.ResultAddress, rejectedTarget.Plc.ReadyAddress);
    var recoveryTarget = Snapshot(19106, 15, 19603, "D40", "D44");
    var rollbackError = await FailsAsync(() => applier.ApplyAndSaveAsync(recoveryTarget, brokenRollbackBaseline,
        () => false, CancellationToken.None));
    Check(rollbackError.Message.Contains("tạm dừng") && intake.IsSuspended,
        "Rollback thất bại giữ intake tạm dừng thay vì nhận mã với cấu hình nửa chừng");
    Check(reader.IP == brokenRollbackBaseline.Camera.Ip && reader.Port == brokenRollbackBaseline.Camera.Port
        && Read<int>(cameraOptions, "DuplicateWindowSeconds") == brokenRollbackBaseline.Camera.DuplicateWindowSeconds,
        "Rollback vẫn khôi phục camera dù rollback PLC đã ném lỗi trước đó");

    SetSuspended(true);
    var maintenanceRecoveryPersisted = false;
    await FailsAsync(() => applier.ApplyAndSaveAsync(original, rejectedTarget,
        () => maintenanceRecoveryPersisted = true, CancellationToken.None));
    Check(!maintenanceRecoveryPersisted && intake.IsSuspended,
        "Lease rollback lỗi không cho phép recovery chen vào bảo trì/update");
    SetSuspended(false);

    using (Suspend())
    {
        var concurrentRecoveryPersisted = false;
        await FailsAsync(() => applier.ApplyAndSaveAsync(original, rejectedTarget,
            () => concurrentRecoveryPersisted = true, CancellationToken.None));
        Check(!concurrentRecoveryPersisted && intake.IsSuspended,
            "Lease rollback lỗi không cho phép recovery chen vào thao tác có lease khác");
    }

    await applier.ApplyAndSaveAsync(original, rejectedTarget, () => true, CancellationToken.None);
    Check(Matches(original) && !intake.IsSuspended, "Lưu lại hợp lệ phục hồi cả thiết bị và giải phóng lease thất bại trước");
    Check(!plc.Enabled && typeof(OmronPlc).GetField("_transport", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(plc) is null,
        "Toàn bộ kiểm thử coordinator không mở transport PLC thật");
    Console.WriteLine($"Đã qua {passed} kiểm tra coordinator Camera/PLC. Host không start, PLC tắt; sandbox: {sandbox}");
    return 0;
}
catch (Exception exception)
{
    Console.Error.WriteLine(exception);
    return 1;
}
