namespace ClassLibraryMT.DeviceStatus;

/// <summary>
/// Mã lỗi chuẩn hóa để tra cứu nhanh, không phụ thuộc chuỗi thông báo.
///
/// CHỈ chứa lỗi thiết bị / ổn định. Lỗi dữ liệu mã quét (QR sai định dạng,
/// NoRead) là lỗi của code xử lý -&gt; chỉ ghi log, KHÔNG đưa vào AppState.
/// </summary>
public enum AppErrorCode
{
    /// <summary>Không có lỗi.</summary>
    None = 0,

    /// <summary>Mất kết nối camera.</summary>
    CameraDisconnected,

    /// <summary>Không giao tiếp được với PLC.</summary>
    PlcUnreachable,

    /// <summary>Lỗi gọi API Checkee.</summary>
    CheckeeApiFailed,

    /// <summary>License không hợp lệ.</summary>
    LicenseInvalid,

    /// <summary>Lỗi không xác định.</summary>
    Unknown,
}

/// <summary>
/// Một lỗi xảy ra trong app.
/// </summary>
/// <param name="Code">Mã lỗi chuẩn hóa.</param>
/// <param name="Message">Thông điệp mô tả lỗi.</param>
/// <param name="OccurredUtc">Thời điểm xảy ra lỗi (UTC).</param>
public sealed record AppError(AppErrorCode Code, string Message, DateTime OccurredUtc);
