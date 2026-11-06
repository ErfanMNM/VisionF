using ClassLibraryMT.Auth;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace TanTienQR.ViewModels;

/// <summary>
/// ViewModel cho hộp thoại đổi mật khẩu (bắt buộc ở lần đăng nhập đầu tiên).
/// </summary>
internal sealed partial class PasswordChangeViewModel : ViewModelBase
{
    private readonly IUserService _users;
    private readonly UserAccount _user;

    public PasswordChangeViewModel(IUserService users, UserAccount user)
    {
        _users = users;
        _user = user;
    }

    /// <summary>Tên tài khoản đang đổi mật khẩu (để hiển thị).</summary>
    public string Username => _user.Username;

    /// <summary>
    /// Bàn phím ảo cho máy cảm ứng (singleton dùng chung toàn ứng dụng).
    /// </summary>
    public VirtualKeyboardViewModel? Keyboard => Controls.VirtualKeyboardHost.ViewModel;

    /// <summary>Tên hiển thị của tài khoản.</summary>
    public string DisplayName =>
        string.IsNullOrWhiteSpace(_user.DisplayName) ? _user.Username : _user.DisplayName;

    /// <summary>Mật khẩu hiện tại.</summary>
    [ObservableProperty]
    public partial string CurrentPassword { get; set; } = string.Empty;

    /// <summary>Mật khẩu mới.</summary>
    [ObservableProperty]
    public partial string NewPassword { get; set; } = string.Empty;

    /// <summary>Xác nhận mật khẩu mới.</summary>
    [ObservableProperty]
    public partial string ConfirmPassword { get; set; } = string.Empty;

    /// <summary>Thông báo lỗi (rỗng = không lỗi).</summary>
    [ObservableProperty]
    public partial string ErrorMessage { get; set; } = string.Empty;

    /// <summary>True khi đang xử lý.</summary>
    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    /// <summary>True khi có thông báo lỗi.</summary>
    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);

    /// <summary>True khi nút xác nhận có thể bấm.</summary>
    public bool CanSubmit => !IsBusy;

    partial void OnErrorMessageChanged(string value) => OnPropertyChanged(nameof(HasError));

    partial void OnIsBusyChanged(bool value) => OnPropertyChanged(nameof(CanSubmit));

    /// <summary>
    /// Xác nhận đổi mật khẩu.
    /// </summary>
    [RelayCommand]
    private async Task ConfirmAsync()
    {
        if (IsBusy)
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
                .SetPasswordAsync(_user.Id, CurrentPassword, NewPassword)
                .ConfigureAwait(true);

            if (!result.Ok)
            {
                ErrorMessage = result.Message;
                return;
            }

            // Cờ bắt buộc đổi mật khẩu đã được xoá trong DB -> cập nhật lại
            // object trong bộ nhớ để các nơi khác không còn yêu cầu đổi nữa.
            _user.MustChangePassword = false;

            OnSucceeded?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            ErrorMessage = "Lỗi khi đổi mật khẩu: " + ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Sự kiện phát ra khi đổi mật khẩu thành công.</summary>
    public event EventHandler? OnSucceeded;

    /// <summary>Sự kiện phát ra khi người dùng huỷ đổi mật khẩu.</summary>
    public event EventHandler? OnCancelled;

    /// <summary>
    /// Huỷ đổi mật khẩu (đóng hộp thoại mà không lưu).
    /// </summary>
    [RelayCommand]
    private void Cancel() => OnCancelled?.Invoke(this, EventArgs.Empty);
}
