using System.Collections.ObjectModel;
using Avalonia.Controls;
using ClassLibraryMT.Auth;
using ClassLibraryMT.Logging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TanTienQR.Views;

namespace TanTienQR.ViewModels;

/// <summary>
/// ViewModel cho tab quản lý tài khoản (chỉ Quản trị / Lập trình viên).
/// </summary>
internal sealed partial class AccountsViewModel : ViewModelBase
{
    private readonly IUserService _users;
    private readonly ISessionContext _session;
    private readonly IUserActionLogger _actionLog;

    /// <summary>Cửa sổ chính — dùng để mở hộp thoại tạo/sửa tài khoản.</summary>
    private Window? _window;

    /// <summary>Thông báo trạng thái (lỗi / thành công).</summary>
    [ObservableProperty]
    public partial string StatusMessage { get; set; } = string.Empty;

    /// <summary>True khi thông báo là lỗi (hiển thị màu đỏ).</summary>
    [ObservableProperty]
    public partial bool IsStatusError { get; set; }

    /// <summary>True khi đang tải danh sách.</summary>
    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    /// <summary>Danh sách tài khoản hiển thị trên bảng.</summary>
    public ObservableCollection<UserRowViewModel> Items { get; } = [];

    /// <summary>Các vai trò có thể chọn khi tạo/sửa tài khoản.</summary>
    public IReadOnlyList<UserRole> AvailableRoles => UserRoleNames.All;

    public AccountsViewModel(
        IUserService users,
        ISessionContext session,
        IUserActionLogger actionLog)
    {
        _users = users;
        _session = session;
        _actionLog = actionLog;
    }

    /// <summary>
    /// Gắn cửa sổ chính để có thể mở hộp thoại tạo/sửa tài khoản.
    /// </summary>
    /// <param name="window">Cửa sổ cha.</param>
    public void Attach(Window? window) => _window = window;

    /// <summary>
    /// Tải lại danh sách tài khoản từ database.
    /// </summary>
    [RelayCommand]
    private async Task LoadAsync()
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;

        try
        {
            var list = await _users.GetAllAsync().ConfigureAwait(true);

            Items.Clear();
            foreach (var user in list)
            {
                Items.Add(new UserRowViewModel(user));
            }

            SetStatus($"Đã tải {Items.Count} tài khoản.", isError: false);
        }
        catch (Exception ex)
        {
            SetStatus("Lỗi khi tải danh sách tài khoản: " + ex.Message, isError: true);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Mở hộp thoại tạo tài khoản mới.
    /// </summary>
    [RelayCommand]
    private async Task AddUserAsync()
    {
        var saved = await ShowEditDialogAsync(null).ConfigureAwait(true);

        if (saved)
        {
            await LoadAsync().ConfigureAwait(true);
        }
    }

    /// <summary>
    /// Mở hộp thoại sửa tài khoản.
    /// </summary>
    /// <param name="row">Dòng cần sửa.</param>
    [RelayCommand]
    private async Task EditUserAsync(UserRowViewModel? row)
    {
        if (row is null)
        {
            return;
        }

        var user = await _users.GetByIdAsync(row.Id).ConfigureAwait(true);
        if (user is null)
        {
            SetStatus("Không tìm thấy tài khoản.", isError: true);
            return;
        }

        var saved = await ShowEditDialogAsync(user).ConfigureAwait(true);

        if (saved)
        {
            await LoadAsync().ConfigureAwait(true);
        }
    }

    /// <summary>
    /// Bật / khoá một tài khoản.
    /// </summary>
    /// <param name="row">Dòng cần đổi trạng thái.</param>
    [RelayCommand]
    private async Task ToggleActiveAsync(UserRowViewModel? row)
    {
        if (row is null)
        {
            return;
        }

        // Không cho tự khoá tài khoản đang đăng nhập.
        if (row.Id == _session.CurrentUser?.Id && row.IsActive)
        {
            SetStatus("Không thể khoá tài khoản đang đăng nhập.", isError: true);
            return;
        }

        var newState = !row.IsActive;

        var result = await _users
            .UpdateAsync(row.Id, null, null, newState)
            .ConfigureAwait(true);

        SetStatus(result.Message, isError: !result.Ok);

        if (result.Ok)
        {
            _actionLog.Log(
                "User.ToggleActive",
                "User",
                $"{(newState ? "Khoa" : "Mo khoa")} tai khoan {row.Username}",
                UserActionResults.Success);

            await LoadAsync().ConfigureAwait(true);
        }
    }

    /// <summary>
    /// Xoá một tài khoản.
    /// </summary>
    /// <param name="row">Dòng cần xoá.</param>
    [RelayCommand]
    private async Task DeleteAsync(UserRowViewModel? row)
    {
        if (row is null)
        {
            return;
        }

        // Không cho tự xoá tài khoản đang đăng nhập.
        if (row.Id == _session.CurrentUser?.Id)
        {
            SetStatus("Không thể xoá tài khoản đang đăng nhập.", isError: true);
            return;
        }

        var result = await _users.DeleteAsync(row.Id).ConfigureAwait(true);

        SetStatus(result.Message, isError: !result.Ok);

        if (result.Ok)
        {
            _actionLog.Log(
                "User.Delete",
                "User",
                $"Xoa tai khoan {row.Username}",
                UserActionResults.Success);

            await LoadAsync().ConfigureAwait(true);
        }
    }

    /// <summary>
    /// Đặt lại mật khẩu cho một tài khoản (sinh mật khẩu tạm ngẫu nhiên).
    /// </summary>
    /// <param name="row">Dòng cần đặt lại mật khẩu.</param>
    [RelayCommand]
    private async Task ResetPasswordAsync(UserRowViewModel? row)
    {
        if (row is null)
        {
            return;
        }

        var newPassword = GenerateTemporaryPassword();

        var result = await _users
            .ResetPasswordAsync(row.Id, newPassword)
            .ConfigureAwait(true);

        if (!result.Ok)
        {
            SetStatus(result.Message, isError: true);
            return;
        }

        SetStatus(
            $"Đã đặt lại mật khẩu cho \"{row.Username}\". Mật khẩu tạm: {newPassword}",
            isError: false);

        _actionLog.Log(
            "User.ResetPassword",
            "User",
            $"Dat lai mat khau cho tai khoan {row.Username}",
            UserActionResults.Success);

        await LoadAsync().ConfigureAwait(true);
    }

    /// <summary>
    /// Mở hộp thoại tạo / sửa tài khoản.
    /// </summary>
    /// <param name="user">Tài khoản đang sửa (null = tạo mới).</param>
    /// <returns>True nếu lưu thành công.</returns>
    private async Task<bool> ShowEditDialogAsync(UserAccount? user)
    {
        var owner = _window;

        // Owner phải đã hiển thị mới mở được dialog.
        if (owner is null || !owner.IsVisible)
        {
            return false;
        }

        var vm = new UserEditViewModel(_users, _actionLog, user);
        var dialog = new UserEditWindow
        {
            DataContext = vm,
        };

        try
        {
            await dialog.ShowDialog(owner).ConfigureAwait(true);
            return dialog.IsSaved;
        }
        catch (InvalidOperationException ex)
        {
            SetStatus("Không mở được hộp thoại: " + ex.Message, isError: true);
            return false;
        }
    }

    /// <summary>
    /// Sinh mật khẩu tạm ngẫu nhiên (dễ nhớ nhưng đủ mạnh).
    /// </summary>
    private static string GenerateTemporaryPassword()
    {
        const string upper = "ABCDEFGHJKLMNPQRSTUVWXYZ";
        const string lower = "abcdefghijkmnpqrstuvwxyz";
        const string digits = "23456789";
        const string symbols = "!@#$%";
        const string all = upper + lower + digits + symbols;

        var rng = Random.Shared;
        var chars = new char[10];

        // Ký tự đầu: chữ cái để dễ đọc.
        chars[0] = upper[rng.Next(upper.Length)];

        for (var i = 1; i < chars.Length; i++)
        {
            chars[i] = all[rng.Next(all.Length)];
        }

        // Xáo trộn (trừ vị trí đầu).
        for (var i = chars.Length - 1; i > 1; i--)
        {
            var j = rng.Next(1, i + 1);
            (chars[i], chars[j]) = (chars[j], chars[i]);
        }

        return new string(chars);
    }

    /// <summary>
    /// Đặt thông báo trạng thái.
    /// </summary>
    /// <param name="message">Nội dung.</param>
    /// <param name="isError">True nếu là lỗi.</param>
    private void SetStatus(string message, bool isError)
    {
        StatusMessage = message;
        IsStatusError = isError;
    }
}
