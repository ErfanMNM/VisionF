using ClassLibraryMT.Communications.Plc;
using ClassLibraryMT.DeviceStatus;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace TanTienQR.Services;

/// <summary>
/// Wrapper quanh <see cref="OmronPlc"/> để ghi kết quả và heartbeat xuống PLC.
///
/// <see cref="OmronPlc"/> là client đồng bộ, KHÔNG thread-safe. Vì vậy mọi lệnh
/// ghi đều được serialize qua một semaphore, và cả ứng dụng dùng chung một
/// instance (đăng ký singleton).
/// </summary>
internal sealed class PlcWriter
{
    private readonly OmronPlc _plc;
    private readonly PlcOptions _opt;
    private readonly ILogger<PlcWriter> _logger;

    // Chỉ cho phép 1 lệnh ghi chạy tại một thời điểm.
    private readonly SemaphoreSlim _gate = new(1, 1);

    public PlcWriter(
        OmronPlc plc,
        IOptions<PlcOptions> options,
        ILogger<PlcWriter> logger)
    {
        _plc = plc;
        _opt = options.Value;
        _logger = logger;
    }

    /// <summary>
    /// Ghi kết quả quét mã xuống PLC: 1 = Pass, 0 = Fail.
    /// Trả về true nếu ghi thành công.
    /// </summary>
    public async Task<bool> WriteResultAsync(bool pass, CancellationToken ct)
    {
        var value = pass ? (short)1 : (short)0;
        var result = await WriteAsync(_opt.ResultAddress, value, ct);
        if (!result.IsSuccess)
        {
            _logger.LogError(
                "Ghi ket qua quet ma that bai ({Address} = {Value}): {Code} - {Message}",
                _opt.ResultAddress, value, result.ErrorCode, result.Message);
            return false;
        }

        return true;
    }

    /// <summary>
    /// Ghi trạng thái Ready xuống PLC: 1 = sẵn sàng, 0 = chưa sẵn sàng.
    /// </summary>
    public async Task WriteReadyAsync(int ready, CancellationToken ct)
    {
        var value = (short)(ready != 0 ? 1 : 0);
        var result = await WriteAsync(_opt.ReadyAddress, value, ct);
        if (!result.IsSuccess)
        {
            _logger.LogError(
                "Ghi trang thai Ready that bai ({Address} = {Value}): {Code} - {Message}",
                _opt.ReadyAddress, value, result.ErrorCode, result.Message);
        }
    }

    private async Task<PlcResult> WriteAsync(string address, short value, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            // OmronPlc ghi đồng bộ (blocking I/O). Chạy trên thread pool để
            // không chặn luồng async của worker/heartbeat.
            var result = await Task.Run(() => _plc.WriteInt16(address, value), ct);

            // Trạng thái kết nối PLC suy ra từ kết quả lần ghi gần nhất
            // (giống cách WK_Update của dự án TApp hoạt động).
            //
            // Khi ghi thất bại nhưng trước đó vẫn đang kết nối -> chuyển sang
            // "Reconnecting" để UI hiện LED chớp nhấp nháy thay vì đỏ đặc.
            if (result.IsSuccess)
            {
                AppStateManager.SetPlcStatus(PLCStatus.Connected);
            }
            else if (GlobalVariables.PLCStatus == PLCStatus.Connected)
            {
                AppStateManager.SetPlcStatus(PLCStatus.Reconnecting);
            }
            else
            {
                AppStateManager.SetPlcStatus(PLCStatus.Disconnect);
            }

            return result;
        }
        finally
        {
            _gate.Release();
        }
    }
}
