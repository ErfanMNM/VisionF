using ClassLibraryMT.Logging;

namespace ClassLibraryMT.Auth;

/// <summary>
/// Cài đặt <see cref="ISessionContext"/>.
///
/// Cài đặt luôn <see cref="IUserContext"/> của lib Logging để nhật ký thao tác
/// tự động ghi tên người dùng đang đăng nhập. Nhờ vậy lib Logging không cần
/// tham chiếu lib Auth (tránh phụ thuộc vòng).
/// </summary>
internal sealed class SessionContext : ISessionContext, IUserContext
{
    private readonly Lock _gate = new();
    private UserAccount? _current;

    /// <inheritdoc />
    public UserAccount? CurrentUser
    {
        get
        {
            lock (_gate)
            {
                return _current;
            }
        }
    }

    /// <inheritdoc />
    public bool IsAuthenticated => CurrentUser is not null;

    /// <inheritdoc />
    public UserRole CurrentRole => CurrentUser?.Role ?? UserRole.Worker;

    /// <inheritdoc />
    public string? Username => CurrentUser?.Username;

    /// <inheritdoc />
    public long? UserId => CurrentUser?.Id;

    /// <inheritdoc />
    public void SignIn(UserAccount user)
    {
        ArgumentNullException.ThrowIfNull(user);

        lock (_gate)
        {
            _current = user;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <inheritdoc />
    public void SignOut()
    {
        lock (_gate)
        {
            if (_current is null)
            {
                return;
            }

            _current = null;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <inheritdoc />
    public bool Can(Permission permission) => RolePermissions.Can(CurrentRole, permission);

    /// <inheritdoc />
    public event EventHandler? Changed;
}
