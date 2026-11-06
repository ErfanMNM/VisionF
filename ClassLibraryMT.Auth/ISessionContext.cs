namespace ClassLibraryMT.Auth;

/// <summary>
/// Ngữ cảnh phiên đăng nhập hiện tại.
///
/// Đăng ký singleton trong DI. App gọi <see cref="SignIn"/> sau khi đăng nhập
/// thành công và <see cref="SignOut"/> khi người dùng đăng xuất.
/// </summary>
public interface ISessionContext
{
    /// <summary>Tài khoản đang đăng nhập, null nếu chưa đăng nhập.</summary>
    UserAccount? CurrentUser { get; }

    /// <summary>True nếu đang có phiên đăng nhập hợp lệ.</summary>
    bool IsAuthenticated { get; }

    /// <summary>Vai trò của người dùng hiện tại (mặc định <see cref="UserRole.Worker"/>).</summary>
    UserRole CurrentRole { get; }

    /// <summary>Bắt đầu phiên đăng nhập.</summary>
    /// <param name="user">Tài khoản đã xác thực.</param>
    void SignIn(UserAccount user);

    /// <summary>Kết thúc phiên đăng nhập.</summary>
    void SignOut();

    /// <summary>
    /// Kiểm tra người dùng hiện tại có quyền yêu cầu hay không.
    /// </summary>
    /// <param name="permission">Quyền cần kiểm tra.</param>
    /// <returns>True nếu có quyền.</returns>
    bool Can(Permission permission);

    /// <summary>Sự kiện phát ra khi phiên đăng nhập thay đổi.</summary>
    event EventHandler? Changed;
}
