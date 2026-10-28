using System;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using T8T.Models;

namespace T8T.ViewModels;

/// <summary>
/// Trạng thái toàn cục của ứng dụng: điều hướng giữa hai màn hình, đồng hồ và Web Dashboard.
/// Phần mô phỏng dữ liệu SCADA nằm trong MainViewModel.Scada.cs.
/// </summary>
public partial class MainViewModel : ViewModelBase, IDisposable
{
    public const string DashboardUrl = "https://estrace.checkee.vn";

    public static CultureInfo ViCulture { get; } = CreateViCulture();

    private bool _webRequested;

    public MainViewModel()
    {
        BuildMachines();
        SeedAlarms();
        UpdatePace();
        RebuildChart();
        UpdateClock();
        RefreshMetrics();
    }

    private static CultureInfo CreateViCulture()
    {
        try
        {
            return new CultureInfo("vi-VN");
        }
        catch (CultureNotFoundException)
        {
            return CultureInfo.InvariantCulture;
        }
    }

    // ---------- Điều hướng ----------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsScadaVisible))]
    [NotifyPropertyChangedFor(nameof(IsWebVisible))]
    [NotifyPropertyChangedFor(nameof(PageTitle))]
    [NotifyPropertyChangedFor(nameof(PageSubtitle))]
    public partial AppSection CurrentSection { get; set; } = AppSection.Scada;

    public bool IsScadaVisible => CurrentSection == AppSection.Scada;
    public bool IsWebVisible => CurrentSection == AppSection.Web;

    public string PageTitle => CurrentSection == AppSection.Scada ? "Giám sát dây chuyền" : "Estrace Dashboard";

    public string PageSubtitle => CurrentSection == AppSection.Scada
        ? "Nhà máy T8T  ·  Dây chuyền ép phun số 1"
        : "estrace.checkee.vn";

    [RelayCommand]
    private void ShowScada() => CurrentSection = AppSection.Scada;

    [RelayCommand]
    private void ShowWeb()
    {
        CurrentSection = AppSection.Web;

        // Chỉ tải trang ở lần mở đầu tiên để không tốn tài nguyên khi chưa dùng.
        if (!_webRequested)
        {
            _webRequested = true;
            IsWebLoading = true;
            WebStatusText = "Đang tải...";
            WebSource = new Uri(DashboardUrl);
        }
    }

    // ---------- Đồng hồ ----------

    [ObservableProperty]
    public partial string ClockText { get; set; } = "--:--:--";

    [ObservableProperty]
    public partial string DateText { get; set; } = string.Empty;

    private void UpdateClock()
    {
        var now = DateTime.Now;
        ClockText = now.ToString("HH:mm:ss", ViCulture);
        DateText = now.ToString("dddd, dd/MM/yyyy", ViCulture);
    }

    // ---------- Web Dashboard ----------

    [ObservableProperty]
    public partial Uri WebSource { get; set; } = new("about:blank");

    [ObservableProperty]
    public partial bool IsWebLoading { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsWebOk))]
    public partial bool HasWebError { get; set; }

    [ObservableProperty]
    public partial string WebStatusText { get; set; } = "Chưa tải";

    public bool IsWebOk => !HasWebError;

    /// <summary>View gọi khi WebView bắt đầu điều hướng.</summary>
    public void OnWebNavigationStarted()
    {
        if (!_webRequested)
        {
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

    public void Dispose()
    {
        StopSimulation();
        GC.SuppressFinalize(this);
    }
}
