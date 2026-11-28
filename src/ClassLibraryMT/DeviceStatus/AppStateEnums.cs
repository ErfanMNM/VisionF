namespace ClassLibraryMT.DeviceStatus;

/// <summary>
/// Trạng thái vận hành của app (theo mẫu dự án TApp).
/// </summary>
public enum e_AppState
{
    /// <summary>Khởi động ứng dụng, chưa sẵn sàng.</summary>
    Initializing = 0,

    /// <summary>Đang chạy, sẵn sàng làm việc.</summary>
    Ready = 1,

    /// <summary>Mất kết nối thiết bị -&gt; dừng máy.</summary>
    Stopped = 2,

    /// <summary>Lỗi (ví dụ license không hợp lệ).</summary>
    Error = 3,
}

/// <summary>
/// Trạng thái kết nối camera (theo mẫu dự án TApp).
/// </summary>
public enum CameraStatus
{
    /// <summary>Chưa kết nối.</summary>
    Disconnected = 0,

    /// <summary>Đã kết nối, sẵn sàng quét.</summary>
    Connected = 1,

    /// <summary>Lỗi camera.</summary>
    Error = 2,

    /// <summary>Đang kết nối lại.</summary>
    Reconnecting = 3,
}

    /// <summary>Trạng thái PLC (theo mẫu dự án TApp).</summary>
public enum PLCStatus
{
    /// <summary>Đã kết nối (lần ghi gần nhất thành công).</summary>
    Connected = 0,

    /// <summary>Mất kết nối (lần ghi gần nhất thất bại).</summary>
    Disconnect = 1,

    /// <summary>Đang thử kết nối lại (sau khi mất kết nối).</summary>
    Reconnecting = 2,
}
