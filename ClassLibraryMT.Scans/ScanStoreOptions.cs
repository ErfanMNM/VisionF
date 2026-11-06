namespace ClassLibraryMT.Scans;

/// <summary>
/// Cấu hình lưu bản ghi quét mã vào SQLite (section <c>Scans</c>).
/// </summary>
public sealed class ScanStoreOptions
{
    /// <summary>
    /// Đường dẫn file database.
    ///
    /// Nếu là đường dẫn tương đối thì được hiểu là tương đối so với thư mục
    /// ứng dụng (<see cref="AppContext.BaseDirectory"/>). Mặc định
    /// <c>logs/scans.db</c>.
    /// </summary>
    public string Path { get; set; } = "logs/scans.db";

    /// <summary>
    /// Số bản ghi tối đa gom được trước khi ghi xuống database.
    /// Mặc định <c>100</c>.
    /// </summary>
    public int BatchSize { get; set; } = 100;

    /// <summary>
    /// Chu kỳ (ms) flush batch xuống database khi chưa đủ <see cref="BatchSize"/>.
    /// Mặc định <c>1000</c>.
    /// </summary>
    public int FlushIntervalMs { get; set; } = 1000;

    /// <summary>
    /// Sức chứa tối đa của hàng đợi ghi bất đồng bộ.
    ///
    /// Khi đầy thì bản ghi cũ nhất bị bỏ (<c>DropOldest</c>) — ưu tiên không
    /// làm chậm luồng xử lý mã của camera.
    /// </summary>
    public int QueueCapacity { get; set; } = 20_000;

    /// <summary>
    /// Số ngày giữ lại bản ghi. <c>0</c> nghĩa là giữ vĩnh viễn.
    /// Mặc định <c>0</c> (giữ vĩnh viễn — dữ liệu sản xuất cần đầy đủ).
    /// </summary>
    public int RetentionDays { get; set; }

    /// <summary>
    /// Chu kỳ (ms) chạy prune theo retention. Mặc định <c>3600000</c> (1 giờ).
    /// </summary>
    public int PruneIntervalMs { get; set; } = 3_600_000;

    /// <summary>Đường dẫn file database đã phân giải thành đường dẫn tuyệt đối.</summary>
    public string ResolveFullPath()
    {
        if (string.IsNullOrWhiteSpace(Path))
            return System.IO.Path.Combine(AppContext.BaseDirectory, "logs", "scans.db");

        return System.IO.Path.IsPathRooted(Path)
            ? Path
            : System.IO.Path.GetFullPath(System.IO.Path.Combine(AppContext.BaseDirectory, Path));
    }
}
