using Avalonia.Controls;
using Avalonia.Media;
using ClassLibraryMT.Auth;
using ClassLibraryMT.Logging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Options;

namespace TanTienQR.ViewModels;

/// <summary>
/// Bước hiện tại của wizard đăng nhập trong <see cref="Views.LoginShellWindow"/>.
/// </summary>
internal enum LoginStep
{
    /// <summary>Bước 1: nhập tên đăng nhập + mật khẩu.</summary>
    SignIn,

    /// <summary>Bước 2: bắt buộc đổi mật khẩu (tài khoản seed / vừa bị reset).</summary>
    ChangePassword,
}

/// <summary>
/// ViewModel cho khung đăng nhập full-size (wizard 2 bước ngay trong khung).
///
/// Bước 1 đăng nhập qua <see cref="IUserService.AuthenticateAsync"/>. Nếu tài
/// khoản có <c>MustChangePassword</c> thì chuyển sang bước 2 và gọi
/// <see cref="IUserService.SetPasswordAsync"/> trực tiếp — không mở dialog riêng.
/// Đăng nhập thành công (hoặc huỷ) sẽ phát <see cref="RequestClose"/> để shell
/// tự đóng; <c>App</c> dựa vào <see cref="IsAuthenticated"/> để mở MainWindow.
/// </summary>
internal sealed partial class LoginViewModel : ViewModelBase
{
    private readonly IUserService _users;
    private readonly ISessionContext _session;
    private readonly IUserActionLogger _actionLog;
    private readonly AuthOptions _authOptions;

    /// <summary>Bàn phím ảo (dùng chung với cửa sổ chính).</summary>
    private readonly VirtualKeyboardViewModel _keyboard;

    /// <summary>Cửa sổ shell — dùng để tự đóng khi xong wizard.</summary>
    private Window? _owner;

    /// <summary>Tài khoản đang chờ đổi mật khẩu ở bước 2.</summary>
    private UserAccount? _pendingUser;

    /// <summary>True khi đăng nhập thành công (App dùng để mở MainWindow).</summary>
    public bool IsAuthenticated { get; private set; }

    /// <summary>Shell đăng ký để tự đóng khi wizard xong / huỷ.</summary>
    public event EventHandler? RequestClose;

    /// <summary>Shell đăng ký để focus ô nhập khi chuyển bước.</summary>
    public event EventHandler? StepChanged;

    public LoginViewModel(
        IUserService users,
        ISessionContext session,
        IUserActionLogger actionLog,
        VirtualKeyboardViewModel keyboard,
        IOptions<AuthOptions> authOptions)
    {
        _users = users;
        _session = session;
        _actionLog = actionLog;
        _keyboard = keyboard;
        _authOptions = authOptions.Value;
    }

    /// <summary>
    /// Bàn phím ảo cho máy cảm ứng (hiện khi chạm vào ô nhập).
    /// </summary>
    public VirtualKeyboardViewModel Keyboard => _keyboard;

    /// <summary>
    /// Gắn cửa sổ shell để có thể tự đóng khi xong wizard.
    /// </summary>
    /// <param name="owner">Cửa sổ cha.</param>
    public void Attach(Window? owner)
    {
        if (_owner is not null)
        {
            RequestClose -= OnRequestCloseOwner;
        }

        _owner = owner;

        if (_owner is not null)
        {
            RequestClose += OnRequestCloseOwner;
        }
    }

    private void OnRequestCloseOwner(object? sender, EventArgs e) => _owner?.Close();

    // ---------- Bước wizard ----------

    /// <summary>Bước hiện tại của wizard.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSignInStep))]
    [NotifyPropertyChangedFor(nameof(IsChangePasswordStep))]
    [NotifyPropertyChangedFor(nameof(IsSignInDone))]
    [NotifyPropertyChangedFor(nameof(Step1Glyph))]
    [NotifyPropertyChangedFor(nameof(Step2Glyph))]
    [NotifyPropertyChangedFor(nameof(CanSubmitPasswordChange))]
    public partial LoginStep Step { get; set; } = LoginStep.SignIn;

    /// <summary>True khi đang ở bước 1.</summary>
    public bool IsSignInStep => Step == LoginStep.SignIn;

    /// <summary>True khi đang ở bước 2.</summary>
    public bool IsChangePasswordStep => Step == LoginStep.ChangePassword;

    /// <summary>True khi đã xong bước 1 (đang ở bước 2).</summary>
    public bool IsSignInDone => Step == LoginStep.ChangePassword;

    /// <summary>Ký tự hiển thị trong vòng tròn bước 1.</summary>
    public string Step1Glyph => IsChangePasswordStep ? "✓" : "1";

    /// <summary>Ký tự hiển thị trong vòng tròn bước 2.</summary>
    public string Step2Glyph => "2";

    partial void OnStepChanged(LoginStep value) => StepChanged?.Invoke(this, EventArgs.Empty);

    /// <summary>Tên tài khoản đang chờ đổi mật khẩu (bước 2).</summary>
    public string PendingUsername => _pendingUser?.Username ?? string.Empty;

    /// <summary>Tên hiển thị của tài khoản đang chờ đổi mật khẩu.</summary>
    public string PendingDisplayName =>
        string.IsNullOrWhiteSpace(_pendingUser?.DisplayName)
            ? (_pendingUser?.Username ?? string.Empty)
            : _pendingUser!.DisplayName;

    // ---------- Form bước 1 ----------

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

    /// <summary>True khi đang xử lý (disable nút).</summary>
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

    partial void OnIsBusyChanged(bool value)
    {
        OnPropertyChanged(nameof(CanSubmit));
        OnPropertyChanged(nameof(CanSubmitPasswordChange));
    }

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

    // ---------- Form bước 2 ----------

    /// <summary>Mật khẩu hiện tại (bước 2).</summary>
    [ObservableProperty]
    public partial string CurrentPassword { get; set; } = string.Empty;

    /// <summary>Mật khẩu mới (bước 2).</summary>
    [ObservableProperty]
    public partial string NewPassword { get; set; } = string.Empty;

    /// <summary>Xác nhận mật khẩu mới (bước 2).</summary>
    [ObservableProperty]
    public partial string ConfirmPassword { get; set; } = string.Empty;

    /// <summary>Nhãn độ mạnh mật khẩu mới.</summary>
    [ObservableProperty]
    public partial string PasswordStrengthText { get; set; } = string.Empty;

    /// <summary>Độ rộng thanh mạnh mật khẩu (px, tối đa 300).</summary>
    [ObservableProperty]
    public partial double PasswordStrengthWidth { get; set; }

    /// <summary>Màu thanh mạnh mật khẩu.</summary>
    [ObservableProperty]
    public partial IBrush PasswordStrengthBrush { get; set; } =
        new SolidColorBrush(Color.Parse("#D8E6EC"));

    /// <summary>True khi nút lưu mật khẩu có thể bấm.</summary>
    public bool CanSubmitPasswordChange => !IsBusy && IsChangePasswordStep;

    partial void OnNewPasswordChanged(string value) => RefreshPasswordStrength(value);

    private void RefreshPasswordStrength(string? password)
    {
        if (string.IsNullOrEmpty(password))
        {
            PasswordStrengthText = string.Empty;
            PasswordStrengthWidth = 0;
            PasswordStrengthBrush = new SolidColorBrush(Color.Parse("#D8E6EC"));
            return;
        }

        var score = 0;
        if (password.Length >= 6) score++;
        if (password.Length >= 8) score++;
        if (password.Any(char.IsDigit)) score++;
        if (password.Any(char.IsUpper) && password.Any(char.IsLower)) score++;
        if (password.Any(c => !char.IsLetterOrDigit(c))) score++;
        score = Math.Min(score, 4);

        PasswordStrengthWidth = score / 4.0 * 300;
        (PasswordStrengthText, PasswordStrengthBrush) = score switch
        {
            <= 1 => ("Yếu", new SolidColorBrush(Color.Parse("#D93025"))),
            2 => ("Trung bình", new SolidColorBrush(Color.Parse("#E08600"))),
            3 => ("Khá", new SolidColorBrush(Color.Parse("#00A88E"))),
            _ => ("Mạnh", new SolidColorBrush(Color.Parse("#12A150"))),
        };
    }

    // ---------- Lệnh bước 1 ----------

    /// <summary>
    /// Đăng nhập. Nếu tài khoản bắt buộc đổi mật khẩu thì chuyển sang bước 2
    /// ngay trong khung thay vì mở dialog riêng.
    /// </summary>
    [RelayCommand]
    private async Task LoginAsync()
    {
        if (IsBusy || IsChangePasswordStep)
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
            // quản trị vừa reset mật khẩu) -> sang bước 2 ngay trong khung.
            if (user.MustChangePassword)
            {
                _pendingUser = user;
                CurrentPassword = Password;
                Password = string.Empty;
                NewPassword = string.Empty;
                ConfirmPassword = string.Empty;
                RefreshPasswordStrength(null);
                ErrorMessage = string.Empty;
                Step = LoginStep.ChangePassword;

                OnPropertyChanged(nameof(PendingUsername));
                OnPropertyChanged(nameof(PendingDisplayName));
                return;
            }

            _session.SignIn(user);

            IsAuthenticated = true;

            _actionLog.Log(
                "Auth.Login",
                "User",
                $"Dang nhap thanh cong voi vai tro {user.RoleName}",
                UserActionResults.Success);

            RequestClose?.Invoke(this, EventArgs.Empty);
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
    /// Huỷ đăng nhập -> đóng shell, ứng dụng tắt (đăng nhập là bắt buộc).
    /// </summary>
    [RelayCommand]
    private void Cancel()
    {
        if (IsChangePasswordStep && _pendingUser is not null)
        {
            _actionLog.Log(
                "Auth.Login",
                "User",
                $"Tai khoan {_pendingUser.Username} chua doi mat khau",
                UserActionResults.Cancelled);
        }

        IsAuthenticated = false;
        RequestClose?.Invoke(this, EventArgs.Empty);
    }

    // ---------- Lệnh bước 2 ----------

    /// <summary>
    /// Quay lại bước 1 (cho phép đổi tài khoản khác).
    /// </summary>
    [RelayCommand]
    private void BackToLogin()
    {
        if (IsBusy)
        {
            return;
        }

        if (_pendingUser is not null)
        {
            _actionLog.Log(
                "Auth.Login",
                "User",
                $"Tai khoan {_pendingUser.Username} chua doi mat khau",
                UserActionResults.Cancelled);
        }

        _pendingUser = null;
        CurrentPassword = string.Empty;
        NewPassword = string.Empty;
        ConfirmPassword = string.Empty;
        RefreshPasswordStrength(null);
        ErrorMessage = string.Empty;
        Password = string.Empty;
        Step = LoginStep.SignIn;

        OnPropertyChanged(nameof(PendingUsername));
        OnPropertyChanged(nameof(PendingDisplayName));
    }

    /// <summary>
    /// Lưu mật khẩu mới ở bước 2 rồi vào app.
    /// </summary>
    [RelayCommand]
    private async Task ConfirmPasswordChangeAsync()
    {
        if (IsBusy || !IsChangePasswordStep || _pendingUser is null)
        {
            return;
        }

        ErrorMessage = string.Empty;

        if (string.IsNullOrEmpty(CurrentPassword))
        {
            ErrorMessage = "Vui lòng nhập mật khẩu hiện tại.";
            return;
        }

        if (string.IsNullOrEmpty(NewPassword))
        {
            ErrorMessage = "Vui lòng nhập mật khẩu mới.";
            return;
        }

        if (!string.Equals(NewPassword, ConfirmPassword, StringComparison.Ordinal))
        {
            ErrorMessage = "Mật khẩu mới và xác nhận không khớp.";
            return;
        }

        IsBusy = true;

        try
        {
            var result = await _users
                .SetPasswordAsync(_pendingUser.Id, CurrentPassword, NewPassword)
                .ConfigureAwait(true);

            if (!result.Ok)
            {
                ErrorMessage = result.Message;
                return;
            }

            // Cờ bắt buộc đổi mật khẩu đã được xoá trong DB -> cập nhật lại
            // object trong bộ nhớ để phiên đăng nhập không còn yêu cầu đổi nữa.
            _pendingUser.MustChangePassword = false;

            var user = _pendingUser;
            _pendingUser = null;

            _session.SignIn(user);

            IsAuthenticated = true;

            _actionLog.Log(
                "Auth.Login",
                "User",
                $"Dang nhap thanh cong voi vai tro {user.RoleName}",
                UserActionResults.Success);

            RequestClose?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            ErrorMessage = "Lỗi khi đổi mật khẩu: " + ex.Message;

            _actionLog.Log(
                "Auth.Login",
                "User",
                $"Loi khi doi mat khau: {ex.Message}",
                UserActionResults.Failed);
        }
        finally
        {
            IsBusy = false;
        }
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
}
