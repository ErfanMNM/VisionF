using System.IO;

namespace ClassLibraryMT.Auth;

/// <summary>
/// Cấu hình xác thực (section <c>Auth</c> trong appsettings.json).
/// </summary>
public sealed class AuthOptions
{
    /// <summary>
    /// Đường dẫn file database tài khoản.
    ///
    /// Nếu là đường dẫn tương đối thì được hiểu là tương đối so với thư mục
    /// ứng dụng (<see cref="AppContext.BaseDirectory"/>). Mặc định
    /// <c>logs/users.db</c>.
    /// </summary>
    public string DbPath { get; set; } = "logs/users.db";

    /// <summary>
    /// Số lần đăng nhập sai liên tiếp trước khi khoá tài khoản.
    /// Mặc định <c>5</c>.
    /// </summary>
    public int MaxFailedAttempts { get; set; } = 5;

    /// <summary>
    /// Thời gian khoá tài khoản (phút) sau khi đạt ngưỡng đăng nhập sai.
    /// Mặc định <c>5</c>.
    /// </summary>
    public int LockoutMinutes { get; set; } = 5;

    /// <summary>
    /// Số vòng lặp PBKDF2 khi băm mật khẩu.
    /// Mặc định <c>210000</c>.
    /// </summary>
    public int Pbkdf2Iterations { get; set; } = 210_000;

    /// <summary>
    /// Tên đăng nhập của tài khoản quản trị được tạo sẵn ở lần chạy đầu tiên.
    /// Mặc định <c>admin</c>.
    /// </summary>
    public string SeedAdminUser { get; set; } = "admin";

    /// <summary>
    /// Mật khẩu ban đầu của tài khoản quản trị được tạo sẵn.
    ///
    /// Tài khoản này luôn được đánh dấu bắt buộc đổi mật khẩu, nên mật khẩu
    /// mặc định chỉ dùng được cho tới lần đăng nhập đầu tiên.
    /// Mặc định <c>admin</c>.
    /// </summary>
    public string SeedAdminPassword { get; set; } = "admin";

    /// <summary>
    /// Đường dẫn file database đã phân giải thành đường dẫn tuyệt đối.
    /// </summary>
    public string ResolveFullPath()
    {
        if (string.IsNullOrWhiteSpace(DbPath))
        {
            return System.IO.Path.Combine(AppContext.BaseDirectory, "logs", "users.db");
        }

        return System.IO.Path.IsPathRooted(DbPath)
            ? DbPath
            : System.IO.Path.GetFullPath(
                System.IO.Path.Combine(AppContext.BaseDirectory, DbPath));
    }
}
