namespace ClassLibraryMT.Auth;

/// <summary>
/// API quản lý tài khoản người dùng và xác thực.
/// </summary>
public interface IUserService
{
    /// <summary>
    /// Kiểm tra đăng nhập.
    ///
    /// Tự động khoá tài khoản sau <see cref="AuthOptions.MaxFailedAttempts"/>
    /// lần sai liên tiếp, và tự mở khoá khi hết thời gian chờ.
    /// </summary>
    /// <param name="username">Tên đăng nhập.</param>
    /// <param name="password">Mật khẩu.</param>
    /// <param name="ct">Token hủy.</param>
    /// <returns>Kết quả đăng nhập.</returns>
    Task<AuthResult> AuthenticateAsync(
        string username, string password, CancellationToken ct = default);

    /// <summary>
    /// Lấy toàn bộ danh sách tài khoản.
    /// </summary>
    /// <param name="ct">Token hủy.</param>
    /// <returns>Danh sách tài khoản (sắp xếp theo tên đăng nhập).</returns>
    Task<IReadOnlyList<UserAccount>> GetAllAsync(CancellationToken ct = default);

    /// <summary>
    /// Lấy một tài khoản theo khoá chính.
    /// </summary>
    /// <param name="id">Khoá chính.</param>
    /// <param name="ct">Token hủy.</param>
    /// <returns>Tài khoản hoặc null.</returns>
    Task<UserAccount?> GetByIdAsync(long id, CancellationToken ct = default);

    /// <summary>
    /// Lấy một tài khoản theo tên đăng nhập.
    /// </summary>
    /// <param name="username">Tên đăng nhập.</param>
    /// <param name="ct">Token hủy.</param>
    /// <returns>Tài khoản hoặc null.</returns>
    Task<UserAccount?> GetByUsernameAsync(string username, CancellationToken ct = default);

    /// <summary>
    /// Kiểm tra mật khẩu của một tài khoản mà KHÔNG gây ra hậu quả đăng nhập.
    ///
    /// Dùng cho các thao tác nhạy cảm cần xác nhận thêm (vd xoá recipe).
    /// Khác với <see cref="AuthenticateAsync"/>: không ghi nhật lần đăng nhập
    /// thất bại, không khoá tài khoản và không sửa bộ đếm — chỉ trả về kết quả
    /// đúng / sai.
    /// </summary>
    /// <param name="username">Tên đăng nhập.</param>
    /// <param name="password">Mật khẩu cần kiểm tra.</param>
    /// <param name="ct">Token hủy.</param>
    /// <returns>Kết quả kiểm tra.</returns>
    Task<Result> VerifyPasswordAsync(
        string username,
        string password,
        CancellationToken ct = default);

    /// <summary>
    /// Tạo tài khoản mới.
    /// </summary>
    /// <param name="username">Tên đăng nhập (duy nhất, không phân biệt hoa thường).</param>
    /// <param name="displayName">Tên hiển thị.</param>
    /// <param name="password">Mật khẩu.</param>
    /// <param name="role">Vai trò.</param>
    /// <param name="ct">Token hủy.</param>
    /// <returns>Kết quả tạo.</returns>
    Task<Result> CreateAsync(
        string username,
        string displayName,
        string password,
        UserRole role,
        CancellationToken ct = default);

    /// <summary>
    /// Cập nhật thông tin tài khoản (tên hiển thị, vai trò, trạng thái).
    /// </summary>
    /// <param name="id">Khoá chính.</param>
    /// <param name="displayName">Tên hiển thị mới (null = giữ nguyên).</param>
    /// <param name="role">Vai trò mới (null = giữ nguyên).</param>
    /// <param name="isActive">Trạng thái mới (null = giữ nguyên).</param>
    /// <param name="ct">Token hủy.</param>
    /// <returns>Kết quả cập nhật.</returns>
    Task<Result> UpdateAsync(
        long id,
        string? displayName,
        UserRole? role,
        bool? isActive,
        CancellationToken ct = default);

    /// <summary>
    /// Đặt lại mật khẩu cho một tài khoản (quản trị thực hiện).
    /// </summary>
    /// <param name="id">Khoá chính.</param>
    /// <param name="newPassword">Mật khẩu mới.</param>
    /// <param name="ct">Token hủy.</param>
    /// <returns>Kết quả đặt lại.</returns>
    Task<Result> ResetPasswordAsync(long id, string newPassword, CancellationToken ct = default);

    /// <summary>
    /// Tự đổi mật khẩu (người dùng nhập mật khẩu cũ).
    /// </summary>
    /// <param name="id">Khoá chính.</param>
    /// <param name="currentPassword">Mật khẩu hiện tại.</param>
    /// <param name="newPassword">Mật khẩu mới.</param>
    /// <param name="ct">Token hủy.</param>
    /// <returns>Kết quả đổi mật khẩu.</returns>
    Task<Result> SetPasswordAsync(
        long id, string currentPassword, string newPassword, CancellationToken ct = default);

    /// <summary>
    /// Xoá tài khoản.
    /// </summary>
    /// <param name="id">Khoá chính.</param>
    /// <param name="ct">Token hủy.</param>
    /// <returns>Kết quả xoá.</returns>
    Task<Result> DeleteAsync(long id, CancellationToken ct = default);

    /// <summary>
    /// Đảm bảo tài khoản quản trị mặc định tồn tại.
    ///
    /// Chỉ tạo khi database chưa có tài khoản nào — không ghi đè nếu đã tồn tại.
    /// </summary>
    /// <param name="ct">Token hủy.</param>
    /// <returns>True nếu vừa tạo tài khoản mới.</returns>
    Task<bool> EnsureSeedAdminAsync(CancellationToken ct = default);
}
