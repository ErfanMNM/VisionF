namespace ClassLibraryMT.Scans;

/// <summary>
/// Trạng thái của một bản ghi quét mã từ camera.
/// </summary>
public enum ScanStatus
{
    /// <summary>Mã hợp lệ → Pass (đã ghi D12 = 1).</summary>
    Pass = 0,

    /// <summary>
    /// Trạng thái lỗi tổng hợp của dữ liệu cũ (đã ghi D12 = 0).
    /// </summary>
    Fail = 1,

    /// <summary>
    /// Mã bị từ chối vì hệ thống đang xử lý mã trước đó.
    ///
    /// Không ghi xuống PLC — mã bị bỏ qua hoàn toàn.
    /// </summary>
    Overload = 2,

    /// <summary>Mã đã được kích hoạt trong PO đang chạy (D12 = 0).</summary>
    Duplicate = 3,

    /// <summary>Nội dung mã không nằm trong PO đang chạy (D12 = 0).</summary>
    InvalidForOrder = 4,

    /// <summary>Camera không đọc được mã (D12 = 0).</summary>
    NoRead = 5,

    /// <summary>Mã không đúng cấu trúc GS1 (D12 = 0).</summary>
    InvalidStructure = 6,
}

/// <summary>
/// Các giá trị chuỗi ổn định dùng để lưu trạng thái vào SQLite.
///
/// Lưu chuỗi (không lưu số) để sau này đổi thứ tự enum không làm hỏng dữ
/// liệu cũ, và để đọc trực tiếp bằng SQL dễ hiểu.
/// </summary>
public static class ScanStatuses
{
    /// <summary>Mã hợp lệ.</summary>
    public const string Pass = "Pass";

    /// <summary>Mã lỗi.</summary>
    public const string Fail = "Fail";

    /// <summary>Mã bị từ chối do quá tải (đang xử lý mã khác).</summary>
    public const string Overload = "Overload";

    /// <summary>Mã đã được kích hoạt trong PO đang chạy.</summary>
    public const string Duplicate = "Duplicate";

    /// <summary>Mã không thuộc PO đang chạy.</summary>
    public const string InvalidForOrder = "InvalidForOrder";

    /// <summary>Camera không đọc được mã.</summary>
    public const string NoRead = "NoRead";

    /// <summary>Mã không đúng cấu trúc GS1.</summary>
    public const string InvalidStructure = "InvalidStructure";

    /// <summary>
    /// Chuyển enum sang chuỗi ổn định để lưu vào database.
    /// </summary>
    /// <param name="status">Trạng thái.</param>
    /// <returns>Chuỗi tương ứng.</returns>
    public static string ToStorage(ScanStatus status) => status switch
    {
        ScanStatus.Pass => Pass,
        ScanStatus.Fail => Fail,
        ScanStatus.Overload => Overload,
        ScanStatus.Duplicate => Duplicate,
        ScanStatus.InvalidForOrder => InvalidForOrder,
        ScanStatus.NoRead => NoRead,
        ScanStatus.InvalidStructure => InvalidStructure,
        _ => Fail,
    };
}

/// <summary>
/// Kết quả ghi xuống PLC của một lần quét mã.
/// </summary>
public static class ScanPlcResults
{
    /// <summary>Đã ghi xuống PLC thành công.</summary>
    public const string Ok = "Ok";

    /// <summary>Đã thử ghi nhưng PLC không phản hồi / báo lỗi.</summary>
    public const string Failed = "Failed";

    /// <summary>Không ghi xuống PLC (mã bị từ chối do quá tải).</summary>
    public const string Skipped = "Skipped";
}

/// <summary>
/// Giá trị mặc định cho số batch sản xuất.
///
/// Số batch chưa được triển khai — cột này để sẵn trong schema để sau này
/// chỉ cần cập nhật giá trị, không phải migration.
/// </summary>
public static class ScanBatchDefaults
{
    /// <summary>Giá trị mặc định của số batch.</summary>
    public const string NoBatch = "NoBatch";
}
