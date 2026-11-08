namespace ClassLibraryMT.Po;

/// <summary>
/// Trạng thái của một lệnh sản xuất (Production Order — PO).
/// </summary>
public enum ProductionOrderStatus
{
    /// <summary>Còn hiệu lực trên server, chưa được SCADA tải về.</summary>
    Available = 0,

    /// <summary>Đã tải về máy SCADA nhưng chưa bắt đầu sản xuất.</summary>
    Downloaded = 1,

    /// <summary>Đang chạy sản xuất (đang khóa PO).</summary>
    Running = 2,

    /// <summary>Đã hoàn tất sản xuất.</summary>
    Completed = 3,

    /// <summary>Đã đóng (không dùng nữa).</summary>
    Closed = 4,
}
