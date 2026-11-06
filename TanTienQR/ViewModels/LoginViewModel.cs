using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using ClassLibraryMT.Auth;
using ClassLibraryMT.Logging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Options;
using TanTienQR.Views;

namespace TanTienQR.ViewModels;

/// <summary>
/// ViewModel cho màn hình đăng nhập.
///
/// Đăng nhập là BẮT BUỘC: nếu người dùng huỷ hoặc đóng cửa sổ, ứng dụng
/// cũng tắt theo.
/// </summary>
internal sealed partial class LoginViewModel : ViewModelBase
{
    private readonly IUserService _users;
    private readonly ISessionContext _session;
    private readonly IUserActionLogger _actionLog;
    private readonly AuthOptions _authOptions;

    /// <summary>Cửa sổ chính — dùng để mở hộp thoại đổi mật khẩu.</summary>
    private Window? _owner;

    /// <summary>True khi đăng nhập thành công (App dùng để mở MainWindow).</summary>
    public bool IsAuthenticated { get; private set; }

    public LoginViewModel(
        IUserService users,
        ISessionContext session,
        IUserActionLogger actionLog,
        IOptions<AuthOptions> authOptions)
    {
        _users = users;
        _session = session;
        _actionLog = actionLog;
        _authOptions = authOptions.Value;
    }

    /// <summary>
    /// Gắn cửa sổ chính để có thể mở hộp thoại đổi mật khẩu bắt buộc.
    /// </summary>
    /// <param name="owner">Cửa sổ cha.</param>
    public void Attach(Window? owner) => _owner = owner;

    // ---------- Form ----------

    /// <summary>Tên đăng nhập.</summary>
    [ObservableProperty]
    public partial string Username { get; set; } = string.Empty;

    /// <summary>Mật khẩu.</summary>
    [ObservableProperty]
    public partial string Password { get; set; } = string.Empty;

    /// <summary>Thông báo lỗi (rỗng = không lỗi).</summary>
    [ObservableProperty]
    public partial string ErrorMessage { get; set; } = string.Empty;

    /// <summary>Cảnh báo (không chặn đăng nhập).</summary>
    [ObservableProperty]
    public partial string WarningMessage { get; set; } = string.Empty;

    /// <summary>True khi đang xử lý đăng nhập (disable nút).</summary>
    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    /// <summary>True khi có thông báo lỗi.</summary>
    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);

    /// <summary>True khi có cảnh báo.</summary>
    public bool HasWarning => !string.IsNullOrWhiteSpace(WarningMessage);

    /// <summary>True khi nút đăng nhập có thể bấm.</summary>
    public bool CanSubmit => !IsBusy;

    partial void OnErrorMessageChanged(string value) => OnPropertyChanged(nameof(HasError));

    partial void OnWarningMessageChanged(string value) => OnPropertyChanged(nameof(HasWarning));

    partial void OnIsBusyChanged(bool value) => OnPropertyChanged(nameof(CanSubmit));

    /// <summary>
    /// Hiển thị cảnh báo ngay khi người dùng gõ tên đăng nhập của tài khoản
    /// quản trị mặc định được tạo sẵn (admin/admin) — nhắc họ đổi mật khẩu.
    /// </summary>
    partial void OnUsernameChanged(string value)
    {
        WarningMessage = IsSeedAdminUsername(value)
            ? "Tài khoản quản trị mặc định — hãy đổi mật khẩu ở lần đăng nhập đầu tiên."
            : string.Empty;
    }

    // ---------- Lệnh ----------

    /// <summary>
    /// Đăng nhập. Nếu thành công và tài khoản bắt buộc đổi mật khẩu thì mở
    /// hộp thoại đổi mật khẩu trước khi vào app.
    /// </summary>
    [RelayCommand]
    private async Task LoginAsync()
    {
        if (IsBusy)
        {
            return;
        }

        ErrorMessage = string.Empty;

        // Cảnh báo tài khoản seed do OnUsernameChanged quản lý -> không xoá ở đây
        // để người dùng vẫn thấy sau khi đăng nhập sai.

        if (string.IsNullOrWhiteSpace(Username))
        {
            ErrorMessage = "Vui lòng nhập tên đăng nhập.";
            return;
        }

        if (string.IsNullOrEmpty(Password))
        {
            ErrorMessage = "Vui lòng nhập mật khẩu.";
            return;
        }

        IsBusy = true;

        try
        {
            var result = await _users
                .AuthenticateAsync(Username.Trim(), Password)
                .ConfigureAwait(true);

            if (!result.Ok || result.User is null)
            {
                ErrorMessage = result.Message;
                Password = string.Empty;

                _actionLog.Log(
                    "Auth.Login",
                    "User",
                    $"Dang nhap that bai cho ten tai khoan \"{Username.Trim()}\"",
                    UserActionResults.Failed);

                return;
            }

            var user = result.User;

            // Tài khoản bắt buộc đổi mật khẩu (lần đầu tài khoản seed, hoặc
            // quản trị vừa reset mật khẩu) -> bắt buộc đổi trước khi vào app.
            if (user.MustChangePassword)
            {
                var changed = await ChangePasswordDialogAsync(user).ConfigureAwait(true);

                if (!changed)
                {
                    ErrorMessage = "Bạn phải đổi mật khẩu trước khi sử dụng ứng dụng.";

                    _actionLog.Log(
                        "Auth.Login",
                        "User",
                        $"Tai khoan {user.Username} chua doi mat khau",
                        UserActionResults.Cancelled);

                    return;
                }
            }

            _session.SignIn(user);

            IsAuthenticated = true;

            _actionLog.Log(
                "Auth.Login",
                "User",
                $"Dang nhap thanh cong voi vai tro {user.RoleName}",
                UserActionResults.Success);
        }
        catch (Exception ex)
        {
            ErrorMessage = "Lỗi khi đăng nhập: " + ex.Message;

            _actionLog.Log(
                "Auth.Login",
                "User",
                $"Loi khi dang nhap: {ex.Message}",
                UserActionResults.Failed);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Huỷ đăng nhập -> ứng dụng tắt (đăng nhập là bắt buộc).
    /// </summary>
    [RelayCommand]
    private void Cancel()
    {
        IsAuthenticated = false;
    }

    /// <summary>
    /// True nếu tên đăng nhập nhập vào trùng với tài khoản quản trị mặc định
    /// được tạo sẵn (theo cấu hình <c>Auth:SeedAdminUser</c>).
    /// </summary>
    /// <param name="username">Tên đăng nhập đang nhập.</param>
    /// <returns>True nếu trùng tài khoản seed mặc định.</returns>
    private bool IsSeedAdminUsername(string? username)
    {
        var seedUser = _authOptions.SeedAdminUser;

        if (string.IsNullOrWhiteSpace(seedUser) || string.IsNullOrWhiteSpace(username))
        {
            return false;
        }

        return string.Equals(
            username.Trim(), seedUser.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Mở hộp thoại bắt buộc đổi mật khẩu.
    /// </summary>
    /// <param name="user">Tài khoản cần đổi mật khẩu.</param>
    /// <returns>True nếu đổi mật khẩu thành công.</returns>
    private async Task<bool> ChangePasswordDialogAsync(UserAccount user)
    {
        var owner = _owner;

        // Owner phải đã hiển thị mới mở được dialog.
        if (owner is null || !owner.IsVisible)
        {
            return false;
        }

        var dialog = new PasswordChangeWindow
        {
            DataContext = new PasswordChangeViewModel(_users, user),
        };

        try
        {
            await dialog.ShowDialog(owner).ConfigureAwait(true);
            return dialog.IsSaved;
        }
        catch (InvalidOperationException ex)
        {
            ErrorMessage = "Không mở được hộp thoại đổi mật khẩu: " + ex.Message;
            return false;
        }
    }
}
