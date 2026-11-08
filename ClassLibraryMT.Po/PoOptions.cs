namespace ClassLibraryMT.Po;

/// <summary>
/// Cấu hình cho luồng quản lý Production Order (section <c>Po</c>).
/// </summary>
public sealed class PoOptions
{
    /// <summary>Tên section cấu hình.</summary>
    public const string SectionName = "Po";

    /// <summary>
    /// Địa chỉ base của server quản lý PO (ví dụ <c>http://127.0.0.1:5080</c>).
    /// </summary>
    public string BaseUrl { get; set; } = "http://127.0.0.1:5080";

    /// <summary>
    /// Thư mục gốc lưu DB của từng PO.
    ///
    /// Đường dẫn tương đối được hiểu là tương đối so với thư mục ứng dụng.
    /// </summary>
    public string DbRoot { get; set; } = "logs/po";

    /// <summary>
    /// Chu kỳ (ms) worker gửi mã kích hoạt. Phải lớn hơn 2000 (2 giây).
    /// </summary>
    public int ActivationIntervalMs { get; set; } = 2500;

    /// <summary>True nếu worker tự động gửi mã kích hoạt định kỳ.</summary>
    public bool AutoActivate { get; set; } = true;

    /// <summary>
    /// Số mã mẫu sinh cho mỗi PO khi tạo PO mới trên server (seed).
    /// </summary>
    public int CodeSampleSize { get; set; } = 100;

    /// <summary>
    /// Phân giải <see cref="DbRoot"/> thành đường dẫn tuyệt đối.
    /// </summary>
    public string ResolveDbRoot()
    {
        if (string.IsNullOrWhiteSpace(DbRoot))
            return Path.Combine(AppContext.BaseDirectory, "logs", "po");

        return Path.IsPathRooted(DbRoot)
            ? Path.GetFullPath(DbRoot)
            : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, DbRoot));
    }
}
