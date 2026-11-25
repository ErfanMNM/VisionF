using ClassLibraryMT.Communications.Plc;
using ClassLibraryMT.DeviceStatus;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace TanTienQR.Services;

/// <summary>Serialize ghi tự động và đổi cấu hình; OmronPlc còn khóa chung với đọc/ghi thủ công.</summary>
internal sealed class PlcWriter
{
    private readonly OmronPlc _plc;
    private readonly PlcOptions _opt;
    private PlcOptions _current;
    private readonly RuntimeCounters _counters;
    private readonly ILogger<PlcWriter> _logger;
    private readonly ScanIntakeGate _intake;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _changeGate = new();
    private TaskCompletionSource _configurationChanged = NewChangeSignal();

    public PlcWriter(OmronPlc plc, IOptions<PlcOptions> options, RuntimeCounters counters,
        ScanIntakeGate intake, ILogger<PlcWriter> logger)
    {
        _plc = plc;
        _opt = options.Value;
        _current = _opt.Copy();
        _counters = counters;
        _intake = intake;
        _logger = logger;
        if (!_current.Enabled) AppStateManager.SetPlcStatus(PLCStatus.Disconnect);
    }

    public bool Enabled => Volatile.Read(ref _current).Enabled;
    private PlcCounterSnapshot? _latestCounters;
    public PlcCounterSnapshot? LatestCounters => Enabled ? Volatile.Read(ref _latestCounters) : null;
    public bool CanResetCounters => Enabled && !_intake.IsSuspended && !string.IsNullOrWhiteSpace(Volatile.Read(ref _current).ResetCounterAddress);
    public int RefreshIntervalMs => Volatile.Read(ref _current).RefreshIntervalMs;

    /// <summary>
    /// Áp dụng snapshot đã xác nhận. Nếu tạo socket mới thất bại, client và options
    /// cũ còn nguyên. Rollback gọi lại chính API này với snapshot trước đó.
    /// </summary>
    public async Task ApplyConfigurationAsync(PlcConfig target, CancellationToken ct = default)
    {
        // Sao chép trước await để caller không thể thay đổi snapshot đang áp dụng.
        var next = PlcOptions.FromConfig(target);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Một recipe giữ lease qua nhiều Write*. Đợi cả recipe kết thúc,
            // không chỉ đợi request UDP đang chạy, rồi mới đổi đích PLC.
            using var transaction = await _plc.AcquireConfigurationLeaseAsync(ct).ConfigureAwait(false);
            await Task.Run(() =>
            {
                ct.ThrowIfCancellationRequested();
                _plc.Reconfigure(next.Ip, next.Port, next.RemoteNode, next.LocalNode,
                    next.TimeoutMs, next.Retries, next.Enabled);
                next.CopyTo(_opt);
                Volatile.Write(ref _current, next);
                Volatile.Write(ref _latestCounters, null);
                AppStateManager.SetPlcStatus(next.Enabled ? PLCStatus.Reconnecting : PLCStatus.Disconnect);
            }, ct).ConfigureAwait(false);
        }
        finally { _gate.Release(); }

        TaskCompletionSource previous;
        lock (_changeGate)
        {
            previous = _configurationChanged;
            _configurationChanged = NewChangeSignal();
        }
        previous.TrySetResult();
    }

    /// <summary>Heartbeat đăng ký trước mỗi tick để không bỏ lỡ việc bật PLC hoặc đổi chu kỳ.</summary>
    public Task WaitForConfigurationChangeAsync(CancellationToken ct)
    {
        lock (_changeGate) return _configurationChanged.Task.WaitAsync(ct);
    }

    private static TaskCompletionSource NewChangeSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    public async Task<bool> WriteResultAsync(bool pass, CancellationToken ct)
    {
        var value = pass ? (short)1 : (short)0;
        var (result, address, attempted) = await WriteAsync(value, ct, readyWrite: false).ConfigureAwait(false);
        if (!attempted) return false;
        _counters.AddResult(result.IsSuccess);
        if (!result.IsSuccess)
            _logger.LogError("Ghi kết quả quét mã thất bại ({Address} = {Value}): {Code} - {Message}",
                address, value, result.ErrorCode, result.Message);
        if (pass && !result.IsSuccess && !IsDefiniteRejection(result.ErrorCode))
            throw new InvalidOperationException(
                $"Không xác định PLC đã nhận Pass tại {address}: {result.ErrorCode} - {result.Message}. "
                + "Không gửi lại mã trước khi đối soát PLC và database PO.");
        return result.IsSuccess;
    }

    private static bool IsDefiniteRejection(string errorCode) =>
        errorCode is "ARG" or "DISABLED" or "DISPOSED"
        || ushort.TryParse(errorCode, System.Globalization.NumberStyles.HexNumber,
            System.Globalization.CultureInfo.InvariantCulture, out var endCode) && endCode != 0;

    public async Task<bool> WriteReadyAsync(int ready, CancellationToken ct)
    {
        var value = (short)(ready != 0 ? 1 : 0);
        var (result, address, attempted) = await WriteAsync(value, ct, readyWrite: true).ConfigureAwait(false);
        if (!attempted) return false;
        _counters.AddHeartbeat(result.IsSuccess);
        if (!result.IsSuccess)
            _logger.LogError("Ghi trạng thái Ready thất bại ({Address} = {Value}): {Code} - {Message}",
                address, value, result.ErrorCode, result.Message);
        return result.IsSuccess;
    }

    /// <summary>Đọc cùng khối 5 Int32 như TApp, không suy ra từ số mã camera.</summary>
    public async Task<PlcCounterSnapshot?> ReadCountersAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var current = _current;
            if (!current.Enabled) return null;
            var result = await Task.Run(() => _plc.ReadInt32Array(current.CounterAddress, 5), ct).ConfigureAwait(false);
            PlcCounterSnapshot? snapshot = result.IsSuccess && result.Value is { Length: >= 5 } values
                ? new(values[0], values[1], values[2], values[4]) : null;
            Volatile.Write(ref _latestCounters, snapshot);
            if (snapshot is null)
            {
                AppStateManager.SetPlcStatus(PLCStatus.Reconnecting);
                _logger.LogWarning("Không đọc được bộ đếm PLC {Address}: {Message}", current.CounterAddress, result.Message);
            }
            return snapshot;
        }
        catch
        {
            Volatile.Write(ref _latestCounters, null);
            throw;
        }
        finally { _gate.Release(); }
    }

    /// <summary>Chỉ gửi xung reset Int16=1; số hiển thị chờ lần đọc PLC tiếp theo.</summary>
    public Task<bool> ResetCountersAsync(CancellationToken ct) => ResetCountersCoreAsync(null, ct);

    public Task<bool> ResetCountersAuthorizedAsync(Func<bool> authorize, CancellationToken ct) =>
        ResetCountersCoreAsync(authorize, ct);

    private async Task<bool> ResetCountersCoreAsync(Func<bool>? authorize, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var current = _current;
            if (!current.Enabled || _intake.IsSuspended || string.IsNullOrWhiteSpace(current.ResetCounterAddress)
                || authorize is not null && !authorize()) return false;
            var result = await Task.Run(() => _plc.WriteInt16(current.ResetCounterAddress, 1), ct).ConfigureAwait(false);
            if (!result.IsSuccess)
            {
                Volatile.Write(ref _latestCounters, null);
                _logger.LogWarning("Không gửi được lệnh xóa số PLC {Address}: {Message}", current.ResetCounterAddress, result.Message);
            }
            return result.IsSuccess;
        }
        finally { _gate.Release(); }
    }

    private async Task<(PlcResult Result, string Address, bool Attempted)> WriteAsync(
        short value, CancellationToken ct, bool readyWrite)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var current = _current;
            var address = readyWrite ? current.ReadyAddress : current.ResultAddress;
            if (!current.Enabled)
                return (PlcResult.Fail("DISABLED", "PLC đã tắt.", default), address, false);
            if (readyWrite && _intake.IsSuspended) value = 0;

            // Chọn địa chỉ sau khi lấy gate: lệnh xếp hàng không được dùng D cũ
            // với endpoint mới. I/O đồng bộ chỉ chạy trên thread pool.
            // Pass is a physical command: a lost ACK must never cause an automatic
            // second datagram, even when ordinary/manual PLC writes have retries.
            var result = await Task.Run(() => !readyWrite && value == 1
                ? _plc.WriteInt16Once(address, value)
                : _plc.WriteInt16(address, value), ct).ConfigureAwait(false);
            if (!result.IsSuccess) Volatile.Write(ref _latestCounters, null);
            if (result.IsSuccess)
                AppStateManager.SetPlcStatus(PLCStatus.Connected);
            else if (GlobalVariables.PLCStatus == PLCStatus.Connected)
                AppStateManager.SetPlcStatus(PLCStatus.Reconnecting);
            else
                AppStateManager.SetPlcStatus(PLCStatus.Disconnect);
            return (result, address, true);
        }
        finally { _gate.Release(); }
    }
}

/// <summary>Giá trị PLC nguyên bản; Fail gồm lỗi đọc và quá thời gian như TApp.</summary>
internal sealed record PlcCounterSnapshot(int Total, int ReadFail, int Pass, int Timeout)
{
    public long Fail => (long)ReadFail + Timeout;
}
