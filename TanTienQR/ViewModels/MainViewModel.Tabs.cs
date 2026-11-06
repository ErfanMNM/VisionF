using ClassLibraryMT.Auth;
using ClassLibraryMT.Logging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TanTienQR.Models;

namespace TanTienQR.ViewModels;

/// <summary>
/// Điều hướng giữa các tab của giao diện SCADA và trạng thái WebView.
///
/// Phần này tách riêng khỏi <see cref="MainViewModel"/> để file không quá dài.
///
/// Mỗi tab được ẩn/hiện theo quyền của người dùng đang đăng nhập (xem
/// <see cref="RolePermissions"/>): Worker chỉ xem Thông tin + Sản xuất, Operator
/// thêm Cấu hình, Quản trị / Lập trình viên có thêm Lịch sử + Tài khoản.
/// </summary>
internal partial class MainViewModel
{
    /// <summary>URL mặc định khi cấu hình không hợp lệ.</summary>
    public const string DefaultWebUrl = "https://estrace.checkee.vn";

    /// <summary>Đã từng yêu cầu nạp trang web chưa (để chỉ nạp lần đầu).</summary>
    private bool _webRequested;

    // ---------- Tab hiện tại ----------

    /// <summary>Tab đang hiển thị.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsInfoVisible))]
    [NotifyPropertyChangedFor(nameof(IsProductionVisible))]
    [NotifyPropertyChangedFor(nameof(IsConfigVisible))]
    [NotifyPropertyChangedFor(nameof(IsHistoryVisible))]
    [NotifyPropertyChangedFor(nameof(IsAccountsVisible))]
    [NotifyPropertyChangedFor(nameof(PageTitle))]
    [NotifyPropertyChangedFor(nameof(PageSubtitle))]
    public partial AppSection CurrentSection { get; set; } = AppSection.Info;

    /// <summary>True khi đang ở tab Thông tin.</summary>
    public bool IsInfoVisible => CurrentSection == AppSection.Info;

    /// <summary>True khi đang ở tab Sản xuất (và có quyền xem).</summary>
    public bool IsProductionVisible =>
        CurrentSection == AppSection.Production && CanViewProduction;

    /// <summary>True khi đang ở tab Cấu hình (và có quyền sửa).</summary>
    public bool IsConfigVisible =>
        CurrentSection == AppSection.Configuration && CanEditConfig;

    /// <summary>True khi đang ở tab Lịch sử (và có quyền xem).</summary>
    public bool IsHistoryVisible =>
        CurrentSection == AppSection.History && CanViewHistory;

    /// <summary>True khi đang ở tab Tài khoản (và có quyền quản lý).</summary>
    public bool IsAccountsVisible =>
        CurrentSection == AppSection.Accounts && CanManageUsers;

    /// <summary>Tiêu đề trên thanh header, đổi theo tab.</summary>
    public string PageTitle => CurrentSection switch
    {
        AppSection.Production => "Sản xuất",
        AppSection.Configuration => "Cấu hình",
        AppSection.History => "Lịch sử thao tác",
        AppSection.Accounts => "Tài khoản",
        _ => "Thông tin",
    };

    /// <summary>Mô tả phụ trên thanh header, đổi theo tab.</summary>
    public string PageSubtitle => CurrentSection switch
    {
        AppSection.Production => "Theo dõi sản xuất trên hệ thống Checkee",
        AppSection.Configuration => "Tham số camera, PLC và web — áp dụng sau khi khởi động lại",
        AppSection.History => "Nhật ký thao tác của người dùng — dành cho nhà phát triển",
        AppSection.Accounts => "Quản lý tài khoản người dùng và phân quyền truy cập",
        _ => "Nhận tín hiệu camera và gửi kết quả xuống PLC Omron (D12 = kết quả, D16 = Ready)",
    };

    // ---------- Quyền hiển thị tab ----------

    /// <summary>True khi người dùng có quyền xem tab Thông tin.</summary>
    public bool CanViewInfo => _session.Can(Permission.ViewInfo);

    /// <summary>True khi người dùng có quyền xem tab Sản xuất.</summary>
    public bool CanViewProduction => _session.Can(Permission.ViewProduction);

    /// <summary>True khi người dùng có quyền mở tab Cấu hình.</summary>
    public bool CanEditConfig => _session.Can(Permission.EditConfig);

    /// <summary>True khi người dùng có quyền mở tab Lịch sử.</summary>
    public bool CanViewHistory => _session.Can(Permission.ViewHistory);

    /// <summary>
    /// Cập nhật lại tất cả cờ quyền (gọi khi phiên đăng nhập thay đổi).
    /// </summary>
    public void RefreshPermissionFlags()
    {
        OnPropertyChanged(nameof(CanViewInfo));
        OnPropertyChanged(nameof(CanViewProduction));
        OnPropertyChanged(nameof(CanEditConfig));
        OnPropertyChanged(nameof(CanViewHistory));
        OnPropertyChanged(nameof(CanManageUsers));
        OnPropertyChanged(nameof(IsInfoVisible));
        OnPropertyChanged(nameof(IsProductionVisible));
        OnPropertyChanged(nameof(IsConfigVisible));
        OnPropertyChanged(nameof(IsHistoryVisible));
        OnPropertyChanged(nameof(IsAccountsVisible));
    }

    // ---------- Lệnh chuyển tab ----------

    /// <summary>Chuyển sang tab Thông tin.</summary>
    [RelayCommand]
    private void ShowInfoTab() => SwitchTo(AppSection.Info, "Thông tin");

    /// <summary>Chuyển sang tab Sản xuất (nạp web nếu chưa nạp).</summary>
    [RelayCommand]
    private void ShowProductionTab()
    {
        if (!CanViewProduction)
        {
            return;
        }

        SwitchTo(AppSection.Production, "Sản xuất");

        if (!IsWebEnabled)
        {
            // Web bị tắt trong cấu hình -> không khởi tạo WebView2.
            HasWebError = false;
            IsWebLoading = false;
            WebStatusText = "Tính năng web đang bị tắt trong tab Cấu hình.";
            return;
        }

        // Chỉ tải trang ở lần mở đầu tiên để không tốn tài nguyên
        // (khởi tạo WebView2 tốn thời gian và bộ nhớ).
        if (!_webRequested)
        {
            _webRequested = true;
            IsWebLoading = true;
            HasWebError = false;
            WebStatusText = "Đang tải...";
            WebSource = new Uri(WebUrl);
        }
    }

    /// <summary>Chuyển sang tab Cấu hình.</summary>
    [RelayCommand]
    private void ShowConfigTab()
    {
        if (!CanEditConfig)
        {
            return;
        }

        SwitchTo(AppSection.Configuration, "Cấu hình");

        // Nạp lại giá trị từ file để không có thay đổi chưa lưu bị bỏ sót.
        ConfigEditor?.Load();
    }

    /// <summary>Chuyển sang tab Lịch sử (mục nhà phát triển).</summary>
    [RelayCommand]
    private void ShowHistoryTab()
    {
        if (!CanViewHistory)
        {
            return;
        }

        SwitchTo(AppSection.History, "Lịch sử");
        HistoryView?.LoadCommand.Execute(null);
    }

    /// <summary>Chuyển sang tab Tài khoản (quản trị tài khoản người dùng).</summary>
    [RelayCommand]
    private void ShowAccountsTab()
    {
        if (!CanManageUsers)
        {
            return;
        }

        SwitchTo(AppSection.Accounts, "Tài khoản");
        AccountsView?.LoadCommand.Execute(null);
    }

    /// <summary>
    /// Chuyển tab và ghi lại thao tác của người dùng.
    /// </summary>
    /// <param name="section">Tab đích.</param>
    /// <param name="label">Tên tab hiển thị.</param>
    private void SwitchTo(AppSection section, string label)
    {
        var changed = CurrentSection != section;
        CurrentSection = section;

        if (changed)
        {
            _actionLog.Log("Tab.Switch", "Tab", label, UserActionResults.Success);
        }
    }

    // ---------- WebView ----------

    /// <summary>URL trang web hiện tại (lấy từ cấu hình).</summary>
    public string WebUrl { get; private set; } = DefaultWebUrl;

    /// <summary>True khi tính năng web được bật trong cấu hình.</summary>
    public bool IsWebEnabled { get; private set; } = true;

    /// <summary>True khi tính năng web bị tắt trong cấu hình.</summary>
    public bool IsWebDisabled => !IsWebEnabled;

    /// <summary>Địa chỉ trang đang hiển thị trong WebView.</summary>
    [ObservableProperty]
    public partial Uri WebSource { get; set; } = new("about:blank");

    /// <summary>True khi trang đang tải.</summary>
    [ObservableProperty]
    public partial bool IsWebLoading { get; set; }

    /// <summary>True khi tải trang thất bại.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsWebOk))]
    public partial bool HasWebError { get; set; }

    /// <summary>Thông báo trạng thái WebView.</summary>
    [ObservableProperty]
    public partial string WebStatusText { get; set; } = "Chưa tải";

    /// <summary>True khi WebView không lỗi.</summary>
    public bool IsWebOk => !HasWebError;

    /// <summary>View gọi khi WebView bắt đầu điều hướng.</summary>
    public void OnWebNavigationStarted()
    {
        if (!_webRequested)
        {
            // Sự kiện của trang about:blank ban đầu.
            return;
        }

        IsWebLoading = true;
        HasWebError = false;
        WebStatusText = "Đang tải...";
    }

    /// <summary>View gọi khi WebView điều hướng xong (thành công hoặc thất bại).</summary>
    public void OnWebNavigationCompleted(bool success)
    {
        if (!_webRequested)
        {
            // Sự kiện của trang about:blank ban đầu.
            return;
        }

        IsWebLoading = false;
        HasWebError = !success;
        WebStatusText = success
            ? "Đã tải"
            : "Không tải được trang. Kiểm tra kết nối mạng hoặc WebView2 Runtime rồi bấm Tải lại.";
    }
}
