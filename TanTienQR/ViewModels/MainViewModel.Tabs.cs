using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TanTienQR.Models;

namespace TanTienQR.ViewModels;

/// <summary>
/// Điều hướng giữa các tab của giao diện SCADA và trạng thái WebView.
///
/// Phần này tách riêng khỏi <see cref="MainViewModel"/> để file không quá dài.
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
    [NotifyPropertyChangedFor(nameof(PageTitle))]
    [NotifyPropertyChangedFor(nameof(PageSubtitle))]
    public partial AppSection CurrentSection { get; set; } = AppSection.Info;

    /// <summary>True khi đang ở tab Thông tin.</summary>
    public bool IsInfoVisible => CurrentSection == AppSection.Info;

    /// <summary>True khi đang ở tab Sản xuất.</summary>
    public bool IsProductionVisible => CurrentSection == AppSection.Production;

    /// <summary>True khi đang ở tab Cấu hình.</summary>
    public bool IsConfigVisible => CurrentSection == AppSection.Configuration;

    /// <summary>Tiêu đề trên thanh header, đổi theo tab.</summary>
    public string PageTitle => CurrentSection switch
    {
        AppSection.Production => "Sản xuất",
        AppSection.Configuration => "Cấu hình",
        _ => "Thông tin",
    };

    /// <summary>Mô tả phụ trên thanh header, đổi theo tab.</summary>
    public string PageSubtitle => CurrentSection switch
    {
        AppSection.Production => "Theo dõi sản xuất trên hệ thống Checkee",
        AppSection.Configuration => "Tham số camera, PLC và web — áp dụng sau khi khởi động lại",
        _ => "Nhận tín hiệu camera và gửi kết quả xuống PLC Omron (D12 = kết quả, D16 = Ready)",
    };

    // ---------- Lệnh chuyển tab ----------

    /// <summary>Chuyển sang tab Thông tin.</summary>
    [RelayCommand]
    private void ShowInfoTab() => CurrentSection = AppSection.Info;

    /// <summary>Chuyển sang tab Sản xuất (nạp web nếu chưa nạp).</summary>
    [RelayCommand]
    private void ShowProductionTab()
    {
        CurrentSection = AppSection.Production;

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
        CurrentSection = AppSection.Configuration;

        // Nạp lại giá trị từ file để không có thay đổi chưa lưu bị bỏ sót.
        ConfigEditor?.Load();
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
