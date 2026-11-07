using ClassLibraryMT.Auth;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace TanTienQR.ViewModels;

/// <summary>
/// ViewModel cho hộp thoại xác nhận xoá recipe bằng mật khẩu.
///
/// Dùng cho thao tác nhạy cảm: trước khi xoá recipe, người dùng phải nhập
/// đúng mật khẩu của tài khoản đang đăng nhập.
///
/// Khác với <see cref="AuthenticateAsync"/> (có ghi nhận lần sai và khoá
/// tài khoản), lệnh này dùng <see cref="IUserService.VerifyPasswordAsync"/>
/// nên không gây hậu quả phụ.
/// </summary>
internal sealed partial class RecipeDeleteConfirmViewModel : ViewModelBase
{
    private readonly IUserService _users;
    private readonly ISessionContext _session;

    /// <summary>
    /// Khởi tạo view model.
    /// </summary>
    /// <param name="users">Service quản lý tài khoản.</param>
    /// <param name="session">Ngữ cảnh phiên đăng nhập.</param>
    /// <param name="recipeName">Tên recipe sắp xoá (hiển thị cho người dùng).</param>
    public RecipeDeleteConfirmViewModel(
        IUserService users,
        ISessionContext session,
        string recipeName)
    {
        _users = users;
        _session = session;
        RecipeName = recipeName;
    }

    /// <summary>Tên recipe sắp xoá.</summary>
    public string RecipeName { get; }

    /// <summary>
    /// Bàn phím ảo cho máy cảm ứng (singleton dùng chung toàn ứng dụng).
    /// </summary>
    public VirtualKeyboardViewModel? Keyboard => Controls.VirtualKeyboardHost.ViewModel;

    /// <summary>Tên tài khoản đang đăng nhập.</summary>
    public string Username => _session.CurrentUser?.Username ?? string.Empty;

    /// <summary>Mật khẩu xác nhận.</summary>
    [ObservableProperty]
    public partial string Password { get; set; } = string.Empty;

    /// <summary>Thông báo lỗi (rỗng = không lỗi).</summary>
    [ObservableProperty]
    public partial string ErrorMessage { get; set; } = string.Empty;

    /// <summary>True khi đang xác thực.</summary>
    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    /// <summary>True khi có thông báo lỗi.</summary>
    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);

    /// <summary>True khi nút xác nhận có thể bấm.</summary>
    public bool CanSubmit => !IsBusy && !string.IsNullOrEmpty(Password);

    partial void OnErrorMessageChanged(string value) => OnPropertyChanged(nameof(HasError));

    partial void OnIsBusyChanged(bool value) => OnPropertyChanged(nameof(CanSubmit));

    partial void OnPasswordChanged(string value) => OnPropertyChanged(nameof(CanSubmit));

    /// <summary>
    /// Xác thực mật khẩu rồi phát sự kiện xoá thành công.
    /// </summary>
    [RelayCommand]
    private async Task ConfirmAsync()
    {
        if (IsBusy)
        {
            return;
        }

        ErrorMessage = string.Empty;

        if (string.IsNullOrEmpty(Password))
        {
            ErrorMessage = "Vui lòng nhập mật khẩu để xác nhận xoá.";
            return;
        }

        var username = Username;
        if (string.IsNullOrWhiteSpace(username))
        {
            ErrorMessage = "Không xác định được tài khoản đang đăng nhập.";
            return;
        }

        IsBusy = true;

        try
        {
            var result = await _users
                .VerifyPasswordAsync(username, Password)
                .ConfigureAwait(true);

            if (!result.Ok)
            {
                ErrorMessage = result.Message;
                return;
            }

            OnConfirmed?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            ErrorMessage = "Lỗi khi xác thực mật khẩu: " + ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Sự kiện phát ra khi xác nhận thành công.</summary>
    public event EventHandler? OnConfirmed;

    /// <summary>Sự kiện phát ra khi người dùng huỷ.</summary>
    public event EventHandler? OnCancelled;

    /// <summary>
    /// Huỷ thao tác xoá.
    /// </summary>
    [RelayCommand]
    private void Cancel() => OnCancelled?.Invoke(this, EventArgs.Empty);
}
