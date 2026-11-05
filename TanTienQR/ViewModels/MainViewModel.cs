using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using ClassLibraryMT.DeviceStatus;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Options;
using TanTienQR.Services;
using TanTienQR.Views;

namespace TanTienQR.ViewModels;

/// <summary>
/// Trạng thái toàn cục của ứng dụng TanTienQR: trạng thái app, trạng thái
/// camera/PLC, đồng hồ, các chỉ số và quản lý license.
///
/// Nguồn sự thật cho trạng thái là <see cref="AppStateManager"/> trong
/// <c>ClassLibraryMT</c> (static registry, không có event). Vì vậy ViewModel
/// poll mỗi 500ms — tần suất thấp, an toàn luồng, độ trễ tối đa 500ms.
/// </summary>
internal partial class MainViewModel : ViewModelBase, IDisposable
{
    private static readonly CultureInfo ViCulture = CreateViCulture();

    private readonly ScanFeed _feed;
    private readonly LicenseManager _license;
    private readonly ConfigEditorViewModel _configEditor;
    private readonly CancellationTokenSource _cts = new();

    private DispatcherTimer? _pollTimer;
    private Task? _feedTask;
    private Window? _window;

    /// <summary>Đánh dấu đã dispose — chặn Dispose() chạy 2 lần.</summary>
    private bool _disposed;

    public MainViewModel(
        ScanFeed feed,
        LicenseManager license,
        IOptions<WebOptions> webOptions,
        ConfigEditorViewModel configEditor)
    {
        _feed = feed;
        _license = license;
        _configEditor = configEditor;

        // URL web lấy từ cấu hình; nếu cấu hình sai thì dùng URL mặc định.
        var web = webOptions.Value;
        IsWebEnabled = web.Enabled;

        var url = web.Url;
        WebUrl = string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out _)
            ? DefaultWebUrl
            : url.Trim();
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

    // ---------- Trạng thái ứng dụng ----------

    /// <summary>Trạng thái vận hành (Initializing / Ready / Stopped / Error).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AppStateBadgeClass))]
    [NotifyPropertyChangedFor(nameof(AppStateDetail))]
    public partial string AppStateText { get; set; } = "Đang khởi động";

    /// <summary>Class style cho badge trạng thái app (running / warning / fault).</summary>
    public string AppStateBadgeClass => AppStateText switch
    {
        "Sẵn sàng" => "running",
        "Đã dừng" => "warning",
        "Lỗi" => "fault",
        _ => "",
    };

    /// <summary>Mô tả ngắn trạng thái để hiển thị dưới tiêu đề.</summary>
    public string AppStateDetail => AppStateText switch
    {
        "Sẵn sàng" => "Camera và PLC đang hoạt động bình thường",
        "Đã dừng" => "Mất kết nối thiết bị — kiểm tra camera hoặc PLC",
        "Lỗi" => LicenseStatusText == "Không hợp lệ"
            ? "License không hợp lệ — nhập license để chạy"
            : "Ứng dụng đang gặp lỗi",
        _ => "Đang thiết lập kết nối thiết bị",
    };

    /// <summary>True khi app sẵn sàng (D16 = 1).</summary>
    [ObservableProperty]
    public partial bool IsReady { get; set; }

    // ---------- Trạng thái thiết bị ----------

    /// <summary>Trạng thái camera.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCameraConnected))]
    [NotifyPropertyChangedFor(nameof(CameraLedClass))]
    [NotifyPropertyChangedFor(nameof(CameraCardClass))]
    [NotifyPropertyChangedFor(nameof(IsCameraBlinking))]
    public partial string CameraStatusText { get; set; } = "Chưa kết nối";

    /// <summary>True khi camera đang kết nối.</summary>
    public bool IsCameraConnected => CameraStatusText == "Đã kết nối";

    /// <summary>Class cho LED camera (on / off / warn).</summary>
    public string CameraLedClass => CameraStatusText switch
    {
        "Đã kết nối" => "on",
        "Đang kết nối lại" => "warn",
        _ => "off",
    };

    /// <summary>
    /// Class cho card thiết bị camera: viền + nền theo trạng thái.
    /// Khi đang kết nối lại thì thêm "blink" để LED và viền chớp nhấp nháy.
    /// </summary>
    public string CameraCardClass => CameraStatusText switch
    {
        "Đã kết nối" => "ok",
        "Đang kết nối lại" => "warn blink",
        _ => "fault",
    };

    /// <summary>True khi camera đang kết nối lại (LED chớp).</summary>
    public bool IsCameraBlinking => CameraStatusText == "Đang kết nối lại";

    /// <summary>Trạng thái PLC.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPlcConnected))]
    [NotifyPropertyChangedFor(nameof(PlcLedClass))]
    [NotifyPropertyChangedFor(nameof(PlcCardClass))]
    [NotifyPropertyChangedFor(nameof(IsPlcBlinking))]
    public partial string PlcStatusText { get; set; } = "Chưa kết nối";

    /// <summary>True khi PLC kết nối được.</summary>
    public bool IsPlcConnected => PlcStatusText == "Đã kết nối";

    /// <summary>Class cho LED PLC (on / off / warn).</summary>
    public string PlcLedClass => PlcStatusText switch
    {
        "Đã kết nối" => "on",
        "Đang kết nối lại" => "warn",
        _ => "off",
    };

    /// <summary>Class cho card thiết bị PLC: viền + nền theo trạng thái.</summary>
    public string PlcCardClass => PlcStatusText switch
    {
        "Đã kết nối" => "ok",
        "Đang kết nối lại" => "warn blink",
        _ => "fault",
    };

    /// <summary>True khi PLC đang kết nối lại (LED chớp).</summary>
    public bool IsPlcBlinking => PlcStatusText == "Đang kết nối lại";

    // ---------- Đồng hồ ----------

    [ObservableProperty]
    public partial string ClockText { get; set; } = "--:--:--";

    [ObservableProperty]
    public partial string DateText { get; set; } = string.Empty;

    // ---------- Chỉ số ----------

    /// <summary>Tổng số lần quét.</summary>
    [ObservableProperty]
    public partial int TotalScans { get; set; }

    /// <summary>Số mã hợp lệ (Pass).</summary>
    [ObservableProperty]
    public partial int PassCount { get; set; }

    /// <summary>Số mã lỗi (Fail).</summary>
    [ObservableProperty]
    public partial int FailCount { get; set; }

    /// <summary>Tỷ lệ đạt (%), 0 khi chưa có lần quét nào.</summary>
    [ObservableProperty]
    public partial double PassRate { get; set; }

    /// <summary>Mã vừa quét gần nhất.</summary>
    [ObservableProperty]
    public partial string LastScanText { get; set; } = "—";

    /// <summary>Thời điểm lần quét gần nhất.</summary>
    [ObservableProperty]
    public partial string LastScanTimeText { get; set; } = "—";

    // ---------- License ----------

    /// <summary>
    /// Trình chỉnh sửa cấu hình (tab Cấu hình).
    /// </summary>
    public ConfigEditorViewModel ConfigEditor => _configEditor;

    /// <summary>Trạng thái license: "Hợp lệ" / "Không hợp lệ" / "Sắp hết hạn".</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLicenseValid))]
    [NotifyPropertyChangedFor(nameof(IsLicenseRunning))]
    [NotifyPropertyChangedFor(nameof(IsLicenseWarning))]
    [NotifyPropertyChangedFor(nameof(IsLicenseFault))]
    [NotifyPropertyChangedFor(nameof(AppStateDetail))]
    public partial string LicenseStatusText { get; set; } = "Không hợp lệ";

    /// <summary>True khi license hợp lệ (kể cả sắp hết hạn).</summary>
    public bool IsLicenseValid => LicenseStatusText is "Hợp lệ" or "Sắp hết hạn";

    /// <summary>Class style cho badge license.</summary>
    public string LicenseBadgeClass => LicenseStatusText switch
    {
        "Hợp lệ" => "running",
        "Sắp hết hạn" => "warning",
        _ => "fault",
    };

    /// <summary>true khi license hợp lệ và còn hạn (badge xanh).</summary>
    public bool IsLicenseRunning => LicenseStatusText == "Hợp lệ";

    /// <summary>true khi license sắp hết hạn (badge vàng).</summary>
    public bool IsLicenseWarning => LicenseStatusText == "Sắp hết hạn";

    /// <summary>true khi license không hợp lệ (badge đỏ).</summary>
    public bool IsLicenseFault => LicenseStatusText == "Không hợp lệ";

    /// <summary>Tên khách hàng trong license.</summary>
    [ObservableProperty]
    public partial string LicenseCustomerText { get; set; } = "—";

    /// <summary>Ngày hết hạn license.</summary>
    [ObservableProperty]
    public partial string LicenseExpiryText { get; set; } = "—";

    /// <summary>Số ngày còn lại.</summary>
    [ObservableProperty]
    public partial string LicenseDaysLeftText { get; set; } = "—";

    /// <summary>Thông báo lỗi license (nếu có).</summary>
    [ObservableProperty]
    public partial string LicenseMessageText { get; set; } = string.Empty;

    /// <summary>Mã máy để gửi cho nhà cung cấp cấp license.</summary>
    [ObservableProperty]
    public partial string HwidText { get; set; } = "—";

    /// <summary>
    /// True khi hộp thoại license đang mở (ẩn sau dấu "?" ở góc màn hình).
    /// </summary>
    [ObservableProperty]
    public partial bool IsLicensePanelOpen { get; set; }

    /// <summary>Mở/đóng hộp thoại license nhanh trên sidebar.</summary>
    [RelayCommand]
    private void ToggleLicensePanel() => IsLicensePanelOpen = !IsLicensePanelOpen;

    /// <summary>
    /// Tắt ứng dụng: đóng cửa sổ chính (hoặc cả ứng dụng nếu không có cửa sổ).
    /// </summary>
    [RelayCommand]
    private void ShutdownApp()
    {
        var window = _window;
        if (window is not null)
        {
            window.Close();
            return;
        }

        // Dự phòng: không có cửa sổ tham chiếu -> yêu cầu cả ứng dụng dừng.
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.Shutdown();
        }
    }

    /// <summary>
    /// Mở hộp thoại quản lý license.
    /// </summary>
    [RelayCommand]
    private async Task ShowLicenseWindow()
    {
        // Đóng popup nhanh trước khi mở cửa sổ quản lý (tránh chồng 2 lớp).
        IsLicensePanelOpen = false;

        var owner = _window;

        // Owner phải đã hiển thị mới mở được dialog. Nếu cửa sổ chính chưa
        // sẵn sàng (ví dụ gọi từ trình xem trước trong IDE) thì bỏ qua.
        if (owner is null || !owner.IsVisible)
            return;

        var dialog = new LicenseWindow
        {
            DataContext = this,
        };

        try
        {
            // ShowDialog là hàm của DIALOG, tham số là cửa sổ cha.
            // Gọi owner.ShowDialog(dialog) sẽ sai chiều và làm app crash.
            await dialog.ShowDialog(owner);
        }
        catch (InvalidOperationException ex)
        {
            // Owner có thể vừa đóng trong lúc đang mở -> không làm crash app.
            LicenseMessageText = "Không mở được hộp thoại license: " + ex.Message;
        }
    }

    /// <summary>
    /// Mở hộp thoại chọn file license, sau đó nhập vào thư mục ứng dụng.
    /// </summary>
    [RelayCommand]
    private async Task ImportLicenseAsync()
    {
        var storage = _window?.StorageProvider;
        if (storage is null || !storage.CanOpen)
            return;

        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Chọn file license.lic",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("License (*.lic)")
            {
                Patterns = ["*.lic"],
            }],
        });

        if (files.Count == 0)
            return;

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var source = files[0].TryGetLocalPath();
            if (source is null)
            {
                LicenseMessageText = "Không đọc được đường dẫn file đã chọn.";
                return;
            }

            _license.Import(source);
            RefreshLicenseInfo();
        });
    }

    /// <summary>
    /// Hủy license: xoá file license khỏi thư mục ứng dụng.
    /// </summary>
    [RelayCommand]
    private void RevokeLicense()
    {
        _license.Revoke();
        RefreshLicenseInfo();
    }

    /// <summary>
    /// Bắt đầu: nối kênh kết quả và bật timer poll trạng thái.
    /// Gọi từ <c>MainWindow.OnOpened</c>.
    /// </summary>
    public void Start(Window? window = null)
    {
        _window = window;

        if (_pollTimer != null) return;

        UpdateClock();
        RefreshAppState();
        RefreshLicenseInfo();

        _pollTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(500),
        };
        _pollTimer.Tick += (_, _) =>
        {
            UpdateClock();
            RefreshAppState();
        };
        _pollTimer.Start();

        // Đọc kênh kết quả ở nền, dispatch sang luồng UI khi có bản ghi.
        _feedTask = Task.Run(() => ConsumeFeedAsync(_cts.Token));
    }

    private async Task ConsumeFeedAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var record in _feed.ReadAllAsync(ct))
            {
                // Chuyển sang luồng UI trước khi chạm vào ObservableCollection.
                await Dispatcher.UIThread.InvokeAsync(() => AddScan(record));
            }
        }
        catch (OperationCanceledException)
        {
            // Shutdown bình thường.
        }
    }

    /// <summary>
    /// Đọc trạng thái hiện tại từ <see cref="AppStateManager"/> và cập nhật UI.
    /// </summary>
    private void RefreshAppState()
    {
        var state = GlobalVariables.CurrentAppState;
        var text = state switch
        {
            e_AppState.Ready => "Sẵn sàng",
            e_AppState.Stopped => "Đã dừng",
            e_AppState.Error => "Lỗi",
            _ => "Đang khởi động",
        };

        if (AppStateText != text)
            AppStateText = text;

        var ready = state == e_AppState.Ready;
        if (IsReady != ready)
            IsReady = ready;

        var camera = GlobalVariables.CameraStatus switch
        {
            CameraStatus.Connected => "Đã kết nối",
            CameraStatus.Reconnecting => "Đang kết nối lại",
            CameraStatus.Error => "Lỗi camera",
            _ => "Chưa kết nối",
        };
        if (CameraStatusText != camera)
            CameraStatusText = camera;

        var plc = GlobalVariables.PLCStatus switch
        {
            PLCStatus.Connected => "Đã kết nối",
            PLCStatus.Reconnecting => "Đang kết nối lại",
            _ => "Chưa kết nối",
        };
        if (PlcStatusText != plc)
            PlcStatusText = plc;
    }

    /// <summary>Cập nhật thông tin license hiển thị trên UI.</summary>
    private void RefreshLicenseInfo()
    {
        var check = _license.Current;

        HwidText = _license.Hwid;

        if (check.Ok && check.Payload is not null)
        {
            LicenseStatusText = check.DaysLeft <= 30 ? "Sắp hết hạn" : "Hợp lệ";
            LicenseCustomerText = string.IsNullOrWhiteSpace(check.Payload.Customer)
                ? "—"
                : check.Payload.Customer;
            LicenseExpiryText = check.Payload.ExpiresUtc.ToLocalTime().ToString("dd/MM/yyyy", ViCulture);
            LicenseDaysLeftText = $"còn {check.DaysLeft} ngày";
            LicenseMessageText = string.Empty;
        }
        else
        {
            LicenseStatusText = "Không hợp lệ";
            LicenseCustomerText = "—";
            LicenseExpiryText = "—";
            LicenseDaysLeftText = "—";
            LicenseMessageText = check.Message;
        }
    }

    private void UpdateClock()
    {
        var now = DateTime.Now;
        ClockText = now.ToString("HH:mm:ss", ViCulture);
        DateText = now.ToString("dddd, dd/MM/yyyy", ViCulture);
    }

    /// <summary>
    /// Dọn tài nguyên (timer, kênh dữ liệu, cancellation token).
    ///
    /// Hàm này có thể được gọi 2 lần: một lần từ
    /// <c>MainWindow.OnClosed</c>, một lần từ DI scope khi host dispose.
    /// Vì vậy phải idempotent — nếu không sẽ ném
    /// <see cref="ObjectDisposedException"/> khi <c>_cts.Cancel()</c> chạy
    /// trên token source đã dispose.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        _pollTimer?.Stop();
        _pollTimer = null;

        try
        {
            _cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Đã dispose rồi — bỏ qua.
        }

        _feed.Complete();

        try
        {
            _cts.Dispose();
        }
        catch (ObjectDisposedException)
        {
            // Đã dispose rồi — bỏ qua.
        }

        GC.SuppressFinalize(this);
    }
}
