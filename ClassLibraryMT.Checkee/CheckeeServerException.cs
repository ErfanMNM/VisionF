namespace ClassLibraryMT.Checkee;

/// <summary>
/// Lỗi khi gọi Checkee Middleware API (không kết nối được, timeout, HTTP lỗi,
/// JSON sai).
///
/// <para>
/// <see cref="Exception.Message"/> là câu tiếng Việt có thể hiển thị thẳng cho
/// người dùng — giống cách <c>PoServerException</c> xử lý lỗi server PO.
/// </para>
/// </summary>
public sealed class CheckeeServerException : Exception
{
    /// <summary>
    /// Mã HTTP trả về (nếu có). 0 nghĩa là không có phản hồi HTTP
    /// (timeout / lỗi mạng).
    /// </summary>
    public int StatusCode { get; }

    /// <summary>
    /// Khởi tạo lỗi không kèm mã HTTP.
    /// </summary>
    public CheckeeServerException(string message, Exception? inner = null)
        : base(message, inner)
    {
        StatusCode = 0;
    }

    /// <summary>
    /// Khởi tạo lỗi kèm mã HTTP của phản hồi.
    /// </summary>
    public CheckeeServerException(string message, int statusCode, Exception? inner = null)
        : base(message, inner)
    {
        StatusCode = statusCode;
    }

    /// <summary>
    /// True nếu lỗi do server KHÔNG phản hồi (timeout / lỗi mạng) — cần
    /// giữ mã ở trạng thái chờ gửi và thử lại ở chu kỳ sau.
    /// </summary>
    public bool IsTransient => StatusCode == 0;
}
