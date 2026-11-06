using ClassLibraryMT.Auth;
using ClassLibraryMT.Logging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace TanTienQR.ViewModels;

/// <summary>
/// ViewModel cho hộp thoại tạo / sửa tài khoản.
/// </summary>
internal sealed partial class UserEditViewModel : ViewModelBase
{
    private readonly IUserService _users;
    private readonly IUserActionLogger _actionLog;

    /// <summary>Tài khoản đang sửa (null = đang tạo mới).</summary>
    private readonly UserAccount? _editing;

    public UserEditViewModel(
        IUserService users,
        IUserActionLogger actionLog,
        UserAccount? editing = null)
    {
        _users = users;
        _actionLog = actionLog;
        _editing = editing;

        if (_editing is not null)
        {
            DisplayName = _editing.DisplayName;
            Role = _editing.Role;
        }
    }

    /// <summary>True nếu đang tạo tài khoản mới.</summary>
    public bool IsNew => _editing is null;

    /// <summary>Tiêu đề hộp thoại.</summary>
    public string Title => IsNew ? "Tạo tài khoản mới" : "Sửa tài khoản";

    /// <summary>Tên đăng nhập (chỉ nhập được khi tạo mới).</summary>
    [ObservableProperty]
    public partial string Username { get; set; } = string.Empty;

    /// <summary>Tên hiển thị.</summary>
    [ObservableProperty]
    public partial string DisplayName { get; set; } = string.Empty;

    /// <summary>Mật khẩu (chỉ bắt buộc khi tạo mới).</summary>
    [ObservableProperty]
    public partial string Password { get; set; } = string.Empty;

    /// <summary>Xác nhận mật khẩu.</summary>
    [ObservableProperty]
    public partial string ConfirmPassword { get; set; } = string.Empty;

    /// <summary>Vai trò được chọn.</summary>
    [ObservableProperty]
    public partial UserRole Role { get; set; } = UserRole.Worker;

    /// <summary>Thông báo lỗi.</summary>
    [ObservableProperty]
    public partial string ErrorMessage { get; set; } = string.Empty;

    /// <summary>True khi đang xử lý.</summary>
    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    /// <summary>Danh sách vai trò có thể chọn.</summary>
    public IReadOnlyList<UserRole> AvailableRoles => UserRoleNames.All;

    /// <summary>True khi có thông báo lỗi.</summary>
    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);

    /// <summary>True khi nút lưu có thể bấm.</summary>
    public bool CanSubmit => !IsBusy;

    partial void OnErrorMessageChanged(string value) => OnPropertyChanged(nameof(HasError));

    partial void OnIsBusyChanged(bool value) => OnPropertyChanged(nameof(CanSubmit));

    /// <summary>
    /// Lưu tài khoản (tạo mới hoặc cập nhật).
    /// </summary>
    [RelayCommand]
    private async Task SaveAsync()
    {
        if (IsBusy)
        {
            return;
        }

        ErrorMessage = string.Empty;

        if (IsNew)
        {
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

            if (!string.Equals(Password, ConfirmPassword, StringComparison.Ordinal))
            {
                ErrorMessage = "Mật khẩu và xác nhận không khớp.";
                return;
            }
        }

        IsBusy = true;

        try
        {
            Result result;

            if (_editing is null)
            {
                result = await _users
                    .CreateAsync(Username.Trim(), DisplayName, Password, Role)
                    .ConfigureAwait(true);

                if (result.Ok)
                {
                    _actionLog.Log(
                        "User.Create",
                        "User",
                        $"Tao tai khoan {Username.Trim()} voi vai tro {UserRoleNames.ToDisplay(Role)}",
                        UserActionResults.Success);
                }
            }
            else
            {
                result = await _users
                    .UpdateAsync(_editing.Id, DisplayName, Role, null)
                    .ConfigureAwait(true);

                if (result.Ok)
                {
                    _actionLog.Log(
                        "User.Update",
                        "User",
                        $"Cap nhat tai khoan {_editing.Username}",
                        UserActionResults.Success);
                }
            }

            if (!result.Ok)
            {
                ErrorMessage = result.Message;
                return;
            }

            OnSaved?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            ErrorMessage = "Lỗi khi lưu tài khoản: " + ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Sự kiện phát ra khi lưu thành công.</summary>
    public event EventHandler? OnSaved;
}
