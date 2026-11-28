namespace ClassLibraryMT.DeviceStatus;

/// <summary>
/// Registry trạng thái toàn app.
///
/// Đọc được từ mọi module (DemoQR, T8T, tool khác...). Mọi thay đổi phải đi
/// qua <see cref="AppStateManager"/> để bảo đảm an toàn luồng và tính lại
/// <see cref="Ready"/> cho nhất quán.
/// </summary>
public static class GlobalVariables
{
    /// <summary>
    /// Trạng thái vận hành hiện tại của app.
    /// </summary>
    public static e_AppState CurrentAppState { get; internal set; } = e_AppState.Initializing;

    /// <summary>
    /// Trạng thái kết nối camera.
    /// </summary>
    public static CameraStatus CameraStatus { get; internal set; } = CameraStatus.Disconnected;

    /// <summary>
    /// Trạng thái kết nối PLC.
    /// </summary>
    public static PLCStatus PLCStatus { get; internal set; } = PLCStatus.Disconnect;

    /// <summary>
    /// Giá trị sẽ ghi xuống vùng D Ready (0 hoặc 1).
    ///
    /// Tự động theo <see cref="CurrentAppState"/>: 1 khi Ready, 0 còn lại.
    /// </summary>
    public static int Ready { get; internal set; }

    /// <summary>
    /// License đã kiểm tra và hợp lệ hay chưa.
    /// </summary>
    public static bool LicenseValid { get; internal set; }

    /// <summary>
    /// Danh sách lỗi gần nhất, mới nhất đứng đầu.
    ///
    /// CHỈ chứa lỗi thiết bị / ổn định. Lỗi dữ liệu mã quét chỉ ghi log.
    /// </summary>
    public static IReadOnlyList<AppError> Errors => AppStateManager.SnapshotErrors();
}
