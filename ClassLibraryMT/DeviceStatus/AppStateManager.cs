namespace ClassLibraryMT.DeviceStatus;

/// <summary>
/// Bộ ghi trạng thái toàn app, an toàn luồng.
///
/// Nhiều luồng cùng cập nhật: socket loop của camera, worker xử lý mã quét,
/// heartbeat service ghi PLC. Mọi thay đổi đi qua đây để:
/// - Không lệch logic tính <see cref="GlobalVariables.Ready"/>
/// - Danh sách lỗi luôn nhất quán với trạng thái thiết bị
/// </summary>
public static class AppStateManager
{
    /// <summary>Số lỗi tối đa giữ lại (mới nhất đứng đầu).</summary>
    private const int MaxErrors = 50;

    private static readonly object _sync = new();
    private static readonly List<AppError> _errors = [];

    /// <summary>
    /// Cập nhật trạng thái camera và tính lại trạng thái app.
    /// </summary>
    public static void SetCameraStatus(CameraStatus status)
    {
        lock (_sync)
        {
            if (GlobalVariables.CameraStatus == status) return;

            GlobalVariables.CameraStatus = status;
            if (status == CameraStatus.Disconnected)
                AddErrorCore(AppErrorCode.CameraDisconnected, "Mat ket noi camera RD.");
            else
                RemoveErrorCore(AppErrorCode.CameraDisconnected);
        }

        RecalculateAppState();
    }

    /// <summary>
    /// Cập nhật trạng thái PLC và tính lại trạng thái app.
    /// </summary>
    public static void SetPlcStatus(PLCStatus status)
    {
        lock (_sync)
        {
            if (GlobalVariables.PLCStatus == status) return;

            GlobalVariables.PLCStatus = status;

            // Chỉ báo lỗi "không giao tiếp được" khi thực sự mất kết nối.
            // Trạng thái Reconnecting (đang thử lại) chưa phải lỗi cuối cùng
            // nên không ghi vào danh sách lỗi.
            if (status == PLCStatus.Disconnect)
                AddErrorCore(AppErrorCode.PlcUnreachable, "Khong giao tiep duoc voi PLC.");
            else
                RemoveErrorCore(AppErrorCode.PlcUnreachable);
        }

        RecalculateAppState();
    }

    /// <summary>
    /// Đặt trạng thái vận hành của app (ví dụ khi license không hợp lệ).
    /// </summary>
    public static void SetAppState(e_AppState state)
    {
        lock (_sync)
        {
            GlobalVariables.CurrentAppState = state;
            GlobalVariables.Ready = state == e_AppState.Ready ? 1 : 0;
        }
    }

    /// <summary>
    /// Đặt cờ license và tính lại trạng thái app.
    /// </summary>
    public static void SetLicenseValid(bool valid)
    {
        lock (_sync)
        {
            if (GlobalVariables.LicenseValid == valid) return;

            GlobalVariables.LicenseValid = valid;
            if (!valid)
                AddErrorCore(AppErrorCode.LicenseInvalid, "License khong hop le.");
            else
                RemoveErrorCore(AppErrorCode.LicenseInvalid);
        }

        RecalculateAppState();
    }

    /// <summary>
    /// Ghi nhận một lỗi. Lỗi cùng mã sẽ được gộp, chỉ giữ lại lần mới nhất.
    /// </summary>
    public static void AddError(AppErrorCode code, string message)
    {
        lock (_sync)
        {
            AddErrorCore(code, message);
        }
    }

    /// <summary>
    /// Xoá toàn bộ danh sách lỗi.
    /// </summary>
    public static void ClearErrors()
    {
        lock (_sync)
        {
            _errors.Clear();
        }
    }

    /// <summary>
    /// Tính lại trạng thái vận hành của app dựa trên license + trạng thái thiết bị.
    ///
    /// <see cref="GlobalVariables.Ready"/> được cập nhật tự động theo kết quả,
    /// nên không ai cần set thủ công -&gt; tránh lệch logic.
    /// </summary>
    public static void RecalculateAppState()
    {
        lock (_sync)
        {
            e_AppState state;

            if (!GlobalVariables.LicenseValid)
                state = e_AppState.Error;
            else if (GlobalVariables.CameraStatus != CameraStatus.Connected ||
                     GlobalVariables.PLCStatus != PLCStatus.Connected)
                state = e_AppState.Stopped;
            else
                state = e_AppState.Ready;

            GlobalVariables.CurrentAppState = state;
            GlobalVariables.Ready = state == e_AppState.Ready ? 1 : 0;
        }
    }

    /// <summary>
    /// Lấy bản sao danh sách lỗi (an toàn đọc từ nhiều luồng).
    /// </summary>
    internal static IReadOnlyList<AppError> SnapshotErrors()
    {
        lock (_sync)
        {
            return _errors.ToArray();
        }
    }

    // Gọi bên trong lock.
    private static void AddErrorCore(AppErrorCode code, string message)
    {
        _errors.RemoveAll(e => e.Code == code);
        _errors.Insert(0, new AppError(code, message, DateTime.UtcNow));

        if (_errors.Count > MaxErrors)
            _errors.RemoveRange(MaxErrors, _errors.Count - MaxErrors);
    }

    // Gọi bên trong lock.
    private static void RemoveErrorCore(AppErrorCode code)
    {
        _errors.RemoveAll(e => e.Code == code);
    }
}
