using System.Globalization;
using ClassLibraryMT.Auth;
using CommunityToolkit.Mvvm.ComponentModel;

namespace TanTienQR.ViewModels;

/// <summary>
/// Một dòng trong bảng tài khoản — chứa các trường đã định dạng sẵn để hiển thị.
/// </summary>
internal sealed partial class UserRowViewModel : ViewModelBase
{
    private static readonly CultureInfo ViCulture = CultureInfo.GetCultureInfo("vi-VN");

    public UserRowViewModel(UserAccount user)
    {
        Id = user.Id;
        Username = user.Username;
        DisplayName = string.IsNullOrWhiteSpace(user.DisplayName) ? user.Username : user.DisplayName;
        RoleName = user.RoleName;
        IsActive = user.IsActive;
        IsLocked = user.IsLocked;
        MustChangePassword = user.MustChangePassword;
        LastLoginText = user.LastLoginUtc is null
            ? "—"
            : user.LastLoginUtc.Value.ToLocalTime().ToString("dd/MM/yyyy HH:mm", ViCulture);
    }

    /// <summary>Khoá chính của tài khoản.</summary>
    public long Id { get; }

    /// <summary>Tên đăng nhập.</summary>
    public string Username { get; }

    /// <summary>Tên hiển thị.</summary>
    public string DisplayName { get; }

    /// <summary>Tên vai trò (tiếng Việt).</summary>
    public string RoleName { get; }

    /// <summary>True nếu tài khoản còn hoạt động.</summary>
    public bool IsActive { get; }

    /// <summary>True nếu tài khoản đang bị khoá.</summary>
    public bool IsLocked { get; }

    /// <summary>True nếu tài khoản bắt buộc đổi mật khẩu ở lần đăng nhập kế tiếp.</summary>
    public bool MustChangePassword { get; }

    /// <summary>Thời điểm đăng nhập cuối (đã định dạng).</summary>
    public string LastLoginText { get; }

    /// <summary>Chữ cái đầu tiên (dùng cho avatar tròn).</summary>
    public string Initial =>
        string.IsNullOrWhiteSpace(DisplayName) ? "?" : DisplayName[..1].ToUpperInvariant();

    /// <summary>Text trạng thái hiển thị.</summary>
    public string StatusText => IsLocked ? "Đang khoá" : IsActive ? "Hoạt động" : "Đã vô hiệu";

    /// <summary>Class style cho badge trạng thái.</summary>
    public string StatusBadgeClass => IsLocked
        ? "warning"
        : IsActive ? "running" : "fault";

    /// <summary>Class style cho badge vai trò (theo màu: worker / operator / admin / dev).</summary>
    public string RoleBadgeClass => RoleName switch
    {
        "Quản trị" => "accent",
        "Lập trình viên" => "developer",
        "Vận hành" => "running",
        _ => "idle",
    };

    /// <summary>True nếu vai trò là Nhân viên (Worker).</summary>
    public bool IsRoleWorker => RoleName == UserRoleNames.Worker;

    /// <summary>True nếu vai trò là Vận hành (Operator).</summary>
    public bool IsRoleOperator => RoleName == UserRoleNames.Operator;

    /// <summary>True nếu vai trò là Quản trị (Administrator).</summary>
    public bool IsRoleAdministrator => RoleName == UserRoleNames.Administrator;

    /// <summary>True nếu vai trò là Lập trình viên (Developer).</summary>
    public bool IsRoleDeveloper => RoleName == UserRoleNames.Developer;
}
