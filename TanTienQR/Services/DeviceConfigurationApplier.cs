using Microsoft.Extensions.Logging;
using TanTienQR.Services.Config;

namespace TanTienQR.Services;

/// <summary>Áp dụng Camera/PLC và chỉ lưu khi chuyển cấu hình thành công.</summary>
public interface IDeviceConfigurationApplier
{
    Task ApplyAndSaveAsync(AppConfigSnapshot target, AppConfigSnapshot running,
        Func<bool> persist, CancellationToken cancellationToken);
}

public static class DeviceConfigurationChanges
{
    public static List<string> Describe(AppConfigSnapshot before, AppConfigSnapshot after,
        string? section = null)
    {
        var result = new List<string>();
        foreach (var field in ConfigSchemaProvider.AllFields)
        {
            if (!field.JsonPath.StartsWith("Camera:", StringComparison.Ordinal)
                && !field.JsonPath.StartsWith("Plc:", StringComparison.Ordinal)) continue;
            if (section is not null && !field.JsonPath.StartsWith(section + ":", StringComparison.Ordinal)) continue;
            var oldValue = field.GetValue(field.Section.ResolveOwner(before));
            var newValue = field.GetValue(field.Section.ResolveOwner(after));
            if (Equals(oldValue, newValue)) continue;
            result.Add($"{(field.JsonPath.StartsWith("Camera:", StringComparison.Ordinal) ? "Camera" : "PLC")} · {field.Label}: {Format(oldValue)} → {Format(newValue)}");
        }
        return result;
    }

    private static string Format(object? value) => value is bool flag
        ? (flag ? "Bật" : "Tắt") : Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? "—";
}

internal sealed class DeviceConfigurationApplier(
    CameraSignalWorker camera, PlcWriter plc, ScanIntakeGate intake,
    ILogger<DeviceConfigurationApplier> logger) : IDeviceConfigurationApplier
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IDisposable? _failedSuspension;

    public async Task ApplyAndSaveAsync(AppConfigSnapshot target, AppConfigSnapshot running,
        Func<bool> persist, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        IDisposable? suspension = null;
        try
        {
            suspension = intake.TrySuspendForDeviceApply(_failedSuspension is null ? 0 : 1);
            if (suspension is null)
                throw new InvalidOperationException("Ứng dụng đang bảo trì hoặc tạm dừng nhận mã. Hãy đợi thao tác hiện tại hoàn tất trước khi đổi cấu hình thiết bị.");
            var cameraChanged = _failedSuspension is not null || DeviceConfigurationChanges.Describe(running, target, "Camera").Count > 0;
            var plcChanged = _failedSuspension is not null || DeviceConfigurationChanges.Describe(running, target, "Plc").Count > 0;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            await camera.DrainAsync(timeout.Token).ConfigureAwait(false);
            if (plcChanged)
            {
                // PLC cũ có thể offline; lỗi phản hồi không ngăn lưu endpoint mới.
                try { await plc.WriteReadyAsync(0, timeout.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { logger.LogWarning(ex, "Không ghi được Ready=0 trước khi đổi cấu hình PLC."); }
            }

            var cameraAttempted = false;
            var plcAttempted = false;
            try
            {
                if (cameraChanged)
                {
                    cameraAttempted = true;
                    await camera.ApplyConfigurationAsync(target.Camera, timeout.Token).ConfigureAwait(false);
                }
                if (plcChanged)
                {
                    plcAttempted = true;
                    await plc.ApplyConfigurationAsync(target.Plc, timeout.Token).ConfigureAwait(false);
                }
                timeout.Token.ThrowIfCancellationRequested();
                if (!persist()) throw new IOException("Không ghi được file cấu hình. Cấu hình thiết bị cũ sẽ được khôi phục.");
            }
            catch (Exception applyError)
            {
                try
                {
                    using var rollbackTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                    var rollbackErrors = new List<Exception>();
                    if (plcAttempted)
                    {
                        try { await plc.ApplyConfigurationAsync(running.Plc, rollbackTimeout.Token).ConfigureAwait(false); }
                        catch (Exception ex) { rollbackErrors.Add(ex); }
                    }
                    if (cameraAttempted)
                    {
                        try { await camera.ApplyConfigurationAsync(running.Camera, rollbackTimeout.Token).ConfigureAwait(false); }
                        catch (Exception ex) { rollbackErrors.Add(ex); }
                    }
                    if (rollbackErrors.Count > 0) throw new AggregateException(rollbackErrors);
                }
                catch (Exception rollbackError)
                {
                    _failedSuspension?.Dispose();
                    _failedSuspension = suspension;
                    suspension = null;
                    logger.LogError(rollbackError, "Khôi phục cấu hình thiết bị thất bại; giữ tạm dừng nhận mã.");
                    throw new InvalidOperationException("Không áp dụng được cấu hình và chưa khôi phục đủ thiết bị. Đã tạm dừng nhận mã; hãy kiểm tra cấu hình và lưu lại.", new AggregateException(applyError, rollbackError));
                }
                throw new InvalidOperationException("Không áp dụng được cấu hình; đã khôi phục cấu hình thiết bị cũ. " + applyError.Message, applyError);
            }

            _failedSuspension?.Dispose();
            _failedSuspension = null;
        }
        finally
        {
            suspension?.Dispose();
            _gate.Release();
        }
    }
}
