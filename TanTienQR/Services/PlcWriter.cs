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
        return result.IsSuccess;
    }

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
            var result = await Task.Run(() => _plc.WriteInt16(address, value), ct).ConfigureAwait(false);
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
