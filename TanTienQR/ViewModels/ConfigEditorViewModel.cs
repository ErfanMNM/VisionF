using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Configuration;
using TanTienQR.Services;

namespace TanTienQR.ViewModels;

/// <summary>
/// Trình chỉnh sửa cấu hình runtime cho tab Cấu hình.
///
/// <see cref="Load"/> đọc từ <see cref="IConfiguration"/> (cấu hình đã merge
/// giữa appsettings.json và appsettings.Development.json) nên form hiển thị
/// đúng giá trị đang chạy. <see cref="Save"/> ghi xuống appsettings.json.
///
/// Lưu ý: <c>OmronPlc</c> và <c>Reader</c> là singleton được tạo một lần khi
/// app khởi động, nên thay đổi IP/Port chỉ có hiệu lực sau khi khởi động lại
/// ứng dụng. UI hiển thị cảnh báo này.
/// </summary>
internal partial class ConfigEditorViewModel : ViewModelBase
{
    private readonly ConfigStore _store;
    private readonly IConfiguration _configuration;

    public ConfigEditorViewModel(ConfigStore store, IConfiguration configuration)
    {
        _store = store;
        _configuration = configuration;
    }

    // ---------- Camera ----------

    [ObservableProperty] public partial string CameraIp { get; set; } = "127.0.0.1";
    [ObservableProperty] public partial int CameraPort { get; set; } = 9000;
    [ObservableProperty] public partial int CameraDuplicateWindowSeconds { get; set; } = 30;

    // ---------- PLC ----------

    [ObservableProperty] public partial string PlcIp { get; set; } = "127.0.0.1";
    [ObservableProperty] public partial int PlcPort { get; set; } = 9600;
    [ObservableProperty] public partial int PlcRemoteNode { get; set; }
    [ObservableProperty] public partial int PlcLocalNode { get; set; } = 1;
    [ObservableProperty] public partial int PlcTimeoutMs { get; set; } = 1000;
    [ObservableProperty] public partial int PlcRetries { get; set; }

    // ---------- Địa chỉ vùng D ----------

    [ObservableProperty] public partial string ResultAddress { get; set; } = "D12";
    [ObservableProperty] public partial string ReadyAddress { get; set; } = "D16";
    [ObservableProperty] public partial int RefreshIntervalMs { get; set; } = 1000;

    // ---------- Web ----------

    [ObservableProperty] public partial string WebUrl { get; set; } = "https://estrace.checkee.vn";
    [ObservableProperty] public partial bool WebEnabled { get; set; } = true;

    // ---------- Trạng thái ----------

    /// <summary>Thông báo kết quả lưu / lỗi.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMessage))]
    public partial string ConfigMessageText { get; set; } = string.Empty;

    /// <summary>True khi có thông báo để hiện.</summary>
    public bool HasMessage => !string.IsNullOrWhiteSpace(ConfigMessageText);

    /// <summary>True khi cấu hình Development đang ghi đè appsettings.json.</summary>
    public bool HasDevelopmentOverride => ConfigStore.HasDevelopmentOverride;

    /// <summary>
    /// Nạp cấu hình đang chạy vào form.
    ///
    /// Đọc từ <see cref="IConfiguration"/> để lấy giá trị đã merge (bao gồm cả
    /// appsettings.Development.json và biến môi trường TANTIEN_*).
    /// Nếu không đọc được (app chưa có config) thì fallback sang file JSON.
    /// </summary>
    public void Load()
    {
        var snapshot = _store.Load();

        CameraIp = ReadString("Camera:Ip", snapshot.Camera.Ip);
        CameraPort = ReadInt("Camera:Port", snapshot.Camera.Port);
        CameraDuplicateWindowSeconds = ReadInt(
            "Camera:DuplicateWindowSeconds", snapshot.Camera.DuplicateWindowSeconds);

        PlcIp = ReadString("Plc:Ip", snapshot.Plc.Ip);
        PlcPort = ReadInt("Plc:Port", snapshot.Plc.Port);
        PlcRemoteNode = ReadInt("Plc:RemoteNode", snapshot.Plc.RemoteNode);
        PlcLocalNode = ReadInt("Plc:LocalNode", snapshot.Plc.LocalNode);
        PlcTimeoutMs = ReadInt("Plc:TimeoutMs", snapshot.Plc.TimeoutMs);
        PlcRetries = ReadInt("Plc:Retries", snapshot.Plc.Retries);

        ResultAddress = ReadString("Plc:ResultAddress", snapshot.Plc.ResultAddress);
        ReadyAddress = ReadString("Plc:ReadyAddress", snapshot.Plc.ReadyAddress);
        RefreshIntervalMs = ReadInt("Plc:RefreshIntervalMs", snapshot.Plc.RefreshIntervalMs);

        WebUrl = ReadString("Web:Url", snapshot.Web.Url);
        WebEnabled = ReadBool("Web:Enabled", snapshot.Web.Enabled);

        ConfigMessageText = string.Empty;
    }

    /// <summary>Đọc chuỗi từ cấu hình đang chạy, fallback về giá trị từ file.</summary>
    private string ReadString(string key, string fallback)
    {
        var value = _configuration[key];
        return string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
    }

    /// <summary>Đọc số từ cấu hình đang chạy, fallback về giá trị từ file.</summary>
    private int ReadInt(string key, int fallback)
    {
        var value = _configuration[key];
        return int.TryParse(value, out var parsed) ? parsed : fallback;
    }

    /// <summary>Đọc bool từ cấu hình đang chạy, fallback về giá trị từ file.</summary>
    private bool ReadBool(string key, bool fallback)
    {
        var value = _configuration[key];
        return bool.TryParse(value, out var parsed) ? parsed : fallback;
    }

    /// <summary>
    /// Hoàn tác các thay đổi chưa lưu về giá trị trong file.
    /// </summary>
    [RelayCommand]
    private void Reset()
    {
        Load();
        ConfigMessageText = "Đã hoàn tác thay đổi chưa lưu.";
    }

    /// <summary>
    /// Kiểm tra giá trị nhập trước khi lưu.
    /// </summary>
    private bool Validate()
    {
        if (string.IsNullOrWhiteSpace(CameraIp))
        {
            ConfigMessageText = "IP camera không được để trống.";
            return false;
        }

        if (CameraPort is <= 0 or > 65535)
        {
            ConfigMessageText = "Port camera phải từ 1 đến 65535.";
            return false;
        }

        if (CameraDuplicateWindowSeconds <= 0)
        {
            ConfigMessageText = "Cửa sổ trùng mã phải lớn hơn 0 (giây).";
            return false;
        }

        if (string.IsNullOrWhiteSpace(PlcIp))
        {
            ConfigMessageText = "IP PLC không được để trống.";
            return false;
        }

        if (PlcPort is <= 0 or > 65535)
        {
            ConfigMessageText = "Port PLC phải từ 1 đến 65535.";
            return false;
        }

        if (PlcRemoteNode is < 0 or > 255 || PlcLocalNode is < 0 or > 255)
        {
            ConfigMessageText = "Node PLC phải từ 0 đến 255.";
            return false;
        }

        if (PlcTimeoutMs <= 0)
        {
            ConfigMessageText = "Timeout PLC phải lớn hơn 0 (ms).";
            return false;
        }

        if (PlcRetries < 0)
        {
            ConfigMessageText = "Số lần thử lại không được âm.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(ResultAddress) || string.IsNullOrWhiteSpace(ReadyAddress))
        {
            ConfigMessageText = "Địa chỉ vùng D (D12 / D16) không được để trống.";
            return false;
        }

        if (RefreshIntervalMs <= 0)
        {
            ConfigMessageText = "Chu kỳ heartbeat phải lớn hơn 0 (ms).";
            return false;
        }

        if (WebEnabled && !Uri.TryCreate(WebUrl, UriKind.Absolute, out _))
        {
            ConfigMessageText = "URL web không hợp lệ.";
            return false;
        }

        return true;
    }

    /// <summary>
    /// Lưu cấu hình xuống <c>appsettings.json</c>.
    /// </summary>
    [RelayCommand]
    private void Save()
    {
        if (!Validate())
        {
            return;
        }

        var snapshot = new AppConfigSnapshot
        {
            Camera = new CameraConfig
            {
                Ip = CameraIp.Trim(),
                Port = CameraPort,
                DuplicateWindowSeconds = CameraDuplicateWindowSeconds,
            },
            Plc = new PlcConfig
            {
                Ip = PlcIp.Trim(),
                Port = PlcPort,
                RemoteNode = PlcRemoteNode,
                LocalNode = PlcLocalNode,
                TimeoutMs = PlcTimeoutMs,
                Retries = PlcRetries,
                ResultAddress = ResultAddress.Trim(),
                ReadyAddress = ReadyAddress.Trim(),
                RefreshIntervalMs = RefreshIntervalMs,
            },
            Web = new WebConfig
            {
                Url = WebUrl.Trim(),
                Enabled = WebEnabled,
            },
        };

        if (!_store.Save(snapshot))
        {
            ConfigMessageText = "Không ghi được file cấu hình. Kiểm tra quyền truy cập thư mục ứng dụng.";
            return;
        }

        ConfigMessageText = HasDevelopmentOverride
            ? "Đã lưu cấu hình. Lưu ý: appsettings.Development.json đang tồn tại và sẽ ghi đè các giá trị này khi app khởi động."
            : "Đã lưu cấu hình. Thay đổi IP/Port sẽ có hiệu lực sau khi khởi động lại ứng dụng.";
    }
}
