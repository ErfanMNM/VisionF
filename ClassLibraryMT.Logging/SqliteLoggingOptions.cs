using Microsoft.Extensions.Logging;

namespace ClassLibraryMT.Logging;

/// <summary>
/// Cấu hình ghi log vào SQLite (section <c>Logging:Sqlite</c>).
/// </summary>
public sealed class SqliteLoggingOptions
{
    /// <summary>
    /// Đường dẫn file database.
    ///
    /// Nếu là đường dẫn tương đối thì được hiểu là tương đối so với thư mục
    /// ứng dụng (<see cref="AppContext.BaseDirectory"/>). Mặc định
    /// <c>logs/app.db</c> — tức <c>&lt;thư mục ứng dụng&gt;/logs/app.db</c>.
    /// </summary>
    public string Path { get; set; } = "logs/app.db";

    /// <summary>
    /// Mức log tối thiểu được ghi xuống SQLite.
    /// Mặc định <c>Information</c>.
    /// </summary>
    public LogLevel MinLevel { get; set; } = LogLevel.Information;

    /// <summary>
    /// Số ngày giữ lại log. <c>0</c> nghĩa là giữ vĩnh viễn.
    /// Mặc định <c>30</c>.
    /// </summary>
    public int RetentionDays { get; set; } = 30;

    /// <summary>
    /// Có ghi các thao tác người dùng (bảng <c>UserActions</c>) hay không.
    /// Mặc định <c>true</c>.
    /// </summary>
    public bool EnableUserActions { get; set; } = true;

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
    /// Chu kỳ (ms) chạy prune theo retention.
    /// Mặc định <c>3600000</c> (1 giờ).
    /// </summary>
    public int PruneIntervalMs { get; set; } = 3_600_000;

    /// <summary>
    /// Đường dẫn file database đã phân giải thành đường dẫn tuyệt đối.
    /// </summary>
    public string ResolveFullPath()
    {
        if (string.IsNullOrWhiteSpace(Path))
            return System.IO.Path.Combine(AppContext.BaseDirectory, "logs", "app.db");

        return System.IO.Path.IsPathRooted(Path)
            ? Path
            : System.IO.Path.GetFullPath(System.IO.Path.Combine(AppContext.BaseDirectory, Path));
    }
}
