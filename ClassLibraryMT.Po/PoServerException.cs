namespace ClassLibraryMT.Po;

/// <summary>
/// Lỗi khi gọi server PO (không kết nối được, timeout, HTTP lỗi, JSON sai).
/// <see cref="Exception.Message"/> là câu tiếng Việt có thể hiển thị thẳng cho người dùng.
/// </summary>
public sealed class PoServerException : Exception
{
    public PoServerException(string message, Exception? inner = null)
        : base(message, inner)
    {
    }
}
