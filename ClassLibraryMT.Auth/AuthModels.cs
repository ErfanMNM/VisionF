namespace ClassLibraryMT.Auth;

/// <summary>
/// Vai trò của người dùng trong hệ thống.
///
/// Giá trị số được lưu thẳng vào database nên KHÔNG được đổi thứ tự sau khi
/// đã phát hành — nếu cần thêm vai trò mới thì append vào cuối.
/// </summary>
public enum UserRole
{
    /// <summary>Nhân viên: chỉ xem thông tin và màn hình sản xuất.</summary>
    Worker = 0,

    /// <summary>Vận hành: thêm sửa cấu hình, quản lý license, xuất dữ liệu.</summary>
    Operator = 1,

    /// <summary>Quản trị: toàn quyền, gồm quản lý tài khoản.</summary>
    Administrator = 2,

    /// <summary>Lập trình viên: toàn quyền như quản trị.</summary>
    Developer = 3,
}

/// <summary>
/// Tên hiển thị tiếng Việt của các vai trò, cùng helper parse an toàn.
/// </summary>
public static class UserRoleNames
{
    /// <summary>Tên hiển thị của <see cref="UserRole.Worker"/>.</summary>
    public const string Worker = "Nhân viên";

    /// <summary>Tên hiển thị của <see cref="UserRole.Operator"/>.</summary>
    public const string Operator = "Vận hành";

    /// <summary>Tên hiển thị của <see cref="UserRole.Administrator"/>.</summary>
    public const string Administrator = "Quản trị";

    /// <summary>Tên hiển thị của <see cref="UserRole.Developer"/>.</summary>
    public const string Developer = "Lập trình viên";

    /// <summary>Tất cả các vai trò, theo thứ tự từ thấp đến cao.</summary>
    public static readonly IReadOnlyList<UserRole> All =
    [
        UserRole.Worker,
        UserRole.Operator,
        UserRole.Administrator,
        UserRole.Developer,
    ];

    /// <summary>
    /// Lấy tên hiển thị tiếng Việt của một vai trò.
    /// </summary>
    /// <param name="role">Vai trò.</param>
    /// <returns>Tên hiển thị.</returns>
    public static string ToDisplay(UserRole role) => role switch
    {
        UserRole.Worker => Worker,
        UserRole.Operator => Operator,
        UserRole.Administrator => Administrator,
        UserRole.Developer => Developer,
        _ => role.ToString(),
    };

    /// <summary>
    /// Lấy tên hiển thị từ giá trị số trong database (có thể là vai trò lạ).
    /// </summary>
    /// <param name="value">Giá trị số trong database.</param>
    /// <returns>Tên hiển thị, hoặc chuỗi số nếu không xác định.</returns>
    public static string ToDisplay(int value) =>
        Enum.IsDefined(typeof(UserRole), value)
            ? ToDisplay((UserRole)value)
            : value.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// Parse tên vai trò từ chuỗi (không phân biệt hoa thường).
    /// </summary>
    /// <param name="text">Chuỗi cần parse.</param>
    /// <param name="role">Kết quả nếu parse thành công.</param>
    /// <returns>True nếu parse được.</returns>
    public static bool TryParse(string? text, out UserRole role)
    {
        role = UserRole.Worker;

        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var trimmed = text.Trim();

        // Cho phép nhập theo tên tiếng Anh để tiện khi lập trình / import.
        if (Enum.TryParse(trimmed, ignoreCase: true, out role))
        {
            return true;
        }

        switch (trimmed.ToLowerInvariant())
        {
            case "nhan vien":
            case "worker":
                role = UserRole.Worker;
                return true;
            case "van hanh":
            case "operator":
                role = UserRole.Operator;
                return true;
            case "quan tri":
            case "administrator":
            case "admin":
                role = UserRole.Administrator;
                return true;
            case "lap trinh vien":
            case "developer":
                role = UserRole.Developer;
                return true;
            default:
                return false;
        }
    }
}

/// <summary>
/// Quyền của người dùng. Dùng cờ bit để gộp nhiều quyền lại.
///
/// Ma trận quyền cố định theo vai trò nằm ở <see cref="RolePermissions"/>.
/// </summary>
[Flags]
public enum Permission
{
    /// <summary>Không có quyền nào.</summary>
    None = 0,

    /// <summary>Xem màn hình thông tin (trạng thái thiết bị, chỉ số).</summary>
    ViewInfo = 1 << 0,

    /// <summary>Xem màn hình sản xuất (WebView Checkee).</summary>
    ViewProduction = 1 << 1,

    /// <summary>Sửa cấu hình ứng dụng (camera, PLC, web).</summary>
    EditConfig = 1 << 2,

    /// <summary>Quản lý license (nhập, huỷ).</summary>
    ManageLicense = 1 << 3,

    /// <summary>Xem lịch sử thao tác.</summary>
    ViewHistory = 1 << 4,

    /// <summary>Quản lý tài khoản người dùng.</summary>
    ManageUsers = 1 << 5,

    /// <summary>Xuất dữ liệu (JSON / CSV).</summary>
    ExportData = 1 << 6,
}

/// <summary>
/// Ma trận quyền cố định theo vai trò.
///
/// Ma trận nằm trong code (không cấu hình được) để đảm bảo mọi app dùng
/// chung lib đều có cùng hành vi bảo mật.
/// </summary>
public static class RolePermissions
{
    /// <summary>
    /// Lấy tập quyền của một vai trò.
    /// </summary>
    /// <param name="role">Vai trò.</param>
    /// <returns>Tập quyền tương ứng.</returns>
    public static Permission For(UserRole role) => role switch
    {
        // Nhân viên: chỉ xem, không sửa gì.
        UserRole.Worker => Permission.ViewInfo | Permission.ViewProduction,

        // Vận hành: thêm sửa cấu hình, quản lý license, xuất dữ liệu.
        UserRole.Operator =>
            Permission.ViewInfo
            | Permission.ViewProduction
            | Permission.EditConfig
            | Permission.ManageLicense
            | Permission.ExportData,

        // Quản trị: toàn quyền.
        UserRole.Administrator =>
            Permission.ViewInfo
            | Permission.ViewProduction
            | Permission.EditConfig
            | Permission.ManageLicense
            | Permission.ViewHistory
            | Permission.ManageUsers
            | Permission.ExportData,

        // Lập trình viên: toàn quyền như quản trị.
        UserRole.Developer =>
            Permission.ViewInfo
            | Permission.ViewProduction
            | Permission.EditConfig
            | Permission.ManageLicense
            | Permission.ViewHistory
            | Permission.ManageUsers
            | Permission.ExportData,

        _ => Permission.None,
    };

    /// <summary>
    /// Kiểm tra vai trò có quyền yêu cầu hay không.
    /// </summary>
    /// <param name="role">Vai trò.</param>
    /// <param name="permission">Quyền cần kiểm tra.</param>
    /// <returns>True nếu có đủ quyền.</returns>
    public static bool Can(UserRole role, Permission permission) =>
        (For(role) & permission) == permission;

    /// <summary>
    /// Liệt kê tên các quyền mà vai trò có (dùng để hiển thị).
    /// </summary>
    /// <param name="role">Vai trò.</param>
    /// <returns>Danh sách tên quyền.</returns>
    public static IReadOnlyList<string> Describe(UserRole role)
    {
        var all = For(role);
        var names = new List<string>(7);

        if ((all & Permission.ViewInfo) != 0) names.Add("Xem thông tin");
        if ((all & Permission.ViewProduction) != 0) names.Add("Xem sản xuất");
        if ((all & Permission.EditConfig) != 0) names.Add("Sửa cấu hình");
        if ((all & Permission.ManageLicense) != 0) names.Add("Quản lý license");
        if ((all & Permission.ViewHistory) != 0) names.Add("Xem lịch sử");
        if ((all & Permission.ManageUsers) != 0) names.Add("Quản lý tài khoản");
        if ((all & Permission.ExportData) != 0) names.Add("Xuất dữ liệu");

        return names;
    }
}

/// <summary>
/// Một tài khoản người dùng trong hệ thống.
///
/// Không chứa mật khẩu (kể cả hash) — hash chỉ nằm trong lớp lưu trữ.
/// </summary>
public sealed class UserAccount
{
    /// <summary>Khoá chính tự tăng.</summary>
    public long Id { get; set; }

    /// <summary>Tên đăng nhập (duy nhất, không phân biệt hoa thường).</summary>
    public string Username { get; set; } = string.Empty;

    /// <summary>Tên hiển thị cho người dùng.</summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>Vai trò (dạng số, khớp với <see cref="UserRole"/>).</summary>
    public int RoleValue { get; set; }

    /// <summary>Vai trò đã parse.</summary>
    public UserRole Role => Enum.IsDefined(typeof(UserRole), RoleValue)
        ? (UserRole)RoleValue
        : UserRole.Worker;

    /// <summary>Tên hiển thị của vai trò.</summary>
    public string RoleName => UserRoleNames.ToDisplay(Role);

    /// <summary>Tài khoản còn hoạt động hay không.</summary>
    public bool IsActive { get; set; } = true;

    /// <summary>Tài khoản có bắt buộc đổi mật khẩu ở lần đăng nhập kế tiếp không.</summary>
    public bool MustChangePassword { get; set; }

    /// <summary>Tài khoản đang bị khoá tạm thời do đăng nhập sai nhiều lần.</summary>
    public bool IsLocked { get; set; }

    /// <summary>Số lần đăng nhập sai liên tiếp.</summary>
    public int FailedAttempts { get; set; }

    /// <summary>Thời điểm hết khoá (UTC), null nếu không bị khoá.</summary>
    public DateTime? LockedUntilUtc { get; set; }

    /// <summary>Thời điểm đăng nhập thành công gần nhất (UTC).</summary>
    public DateTime? LastLoginUtc { get; set; }

    /// <summary>Thời điểm tạo tài khoản (UTC).</summary>
    public DateTime CreatedUtc { get; set; }

    /// <summary>Thời điểm cập nhật gần nhất (UTC).</summary>
    public DateTime UpdatedUtc { get; set; }
}

/// <summary>
/// Kết quả của một thao tác (thành công / thất bại kèm thông báo).
/// </summary>
/// <param name="Ok">True nếu thao tác thành công.</param>
/// <param name="Message">Thông báo kết quả (tiếng Việt).</param>
public sealed record Result(bool Ok, string Message)
{
    /// <summary>Kết quả thành công.</summary>
    /// <param name="message">Thông báo.</param>
    /// <returns>Result thành công.</returns>
    public static Result Success(string message = "Thành công.") => new(true, message);

    /// <summary>Kết quả thất bại.</summary>
    /// <param name="message">Lý do thất bại.</param>
    /// <returns>Result thất bại.</returns>
    public static Result Fail(string message) => new(false, message);
}

/// <summary>
/// Kết quả đăng nhập.
/// </summary>
/// <param name="Ok">True nếu đăng nhập thành công.</param>
/// <param name="Message">Thông báo kết quả.</param>
/// <param name="User">Thông tin tài khoản khi đăng nhập thành công.</param>
public sealed record AuthResult(bool Ok, string Message, UserAccount? User)
{
    /// <summary>Kết quả thành công.</summary>
    /// <param name="user">Tài khoản.</param>
    /// <param name="message">Thông báo.</param>
    /// <returns>AuthResult thành công.</returns>
    public static AuthResult Success(UserAccount user, string message = "Đăng nhập thành công.") =>
        new(true, message, user);

    /// <summary>Kết quả thất bại.</summary>
    /// <param name="message">Lý do thất bại.</param>
    /// <returns>AuthResult thất bại.</returns>
    public static AuthResult Fail(string message) => new(false, message, null);
}
