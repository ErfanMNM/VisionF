using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using ClassLibraryMT.Logging;
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
/// Lưu ý: <c>OmronPlc</c>, <c>Reader</c> và <c>HandheldScannerService</c> là
/// singleton được tạo một lần khi app khởi động, nên thay đổi thông số kết nối
/// (IP/host/port/timeout/...) chỉ có hiệu lực sau khi khởi động lại ứng dụng.
/// Vì vậy sau khi lưu có thay đổi quan trọng, ứng dụng đánh dấu trạng thái
/// "cần restart" và hiển thị banner kèm nút <c>Restart Now</c> / <c>Restart Later</c>.
/// </summary>
internal partial class ConfigEditorViewModel : ViewModelBase
{
    /// <summary>Tên file cấu hình chính (dùng cho thông báo).</summary>
    private const string ConfigFileName = "appsettings.json";

    private readonly ConfigStore _store;
    private readonly IConfiguration _configuration;
    private readonly IUserActionLogger _actionLog;

    /// <summary>
    /// Bản chụp cấu hình đang chạy tại thời điểm nạp form — dùng làm mốc so sánh
    /// để tính trạng thái dirty (form khác với cấu hình đang chạy).
    /// </summary>
    private AppConfigSnapshot _baseline = new();

    /// <summary>
    /// Bản chụp cấu hình đã lưu xuống disk nhưng chưa áp dụng (cần restart).
    /// Null nghĩa là không có thay đổi nào đang chờ restart.
    /// </summary>
    private AppConfigSnapshot? _pendingRestartSnapshot;

    public ConfigEditorViewModel(
        ConfigStore store,
        IConfiguration configuration,
        IUserActionLogger actionLog)
    {
        _store = store;
        _configuration = configuration;
        _actionLog = actionLog;
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

    // ---------- Máy quét cầm tay (Scanner) ----------

    [ObservableProperty] public partial bool ScannerEnabled { get; set; } = true;
    [ObservableProperty] public partial string ScannerMode { get; set; } = "TcpServer";
    [ObservableProperty] public partial string ScannerTcpHost { get; set; } = "127.0.0.1";
    [ObservableProperty] public partial int ScannerTcpPort { get; set; } = 6999;
    [ObservableProperty] public partial string ScannerComPort { get; set; } = "COM3";
    [ObservableProperty] public partial int ScannerBaudRate { get; set; } = 9600;

    // ---------- Server Production Order (Po) ----------

    [ObservableProperty] public partial string PoBaseUrl { get; set; } = "http://127.0.0.1:5080";
    [ObservableProperty] public partial string PoDbRoot { get; set; } = "logs/po";
    [ObservableProperty] public partial int PoActivationIntervalMs { get; set; } = 2500;
    [ObservableProperty] public partial bool PoAutoActivate { get; set; } = true;
    [ObservableProperty] public partial int PoCodeSampleSize { get; set; } = 100;

    // ---------- Web ----------

    [ObservableProperty] public partial string WebUrl { get; set; } = "https://estrace.checkee.vn";
    [ObservableProperty] public partial bool WebEnabled { get; set; } = true;

    // ---------- Logging ----------

    [ObservableProperty] public partial string LogDbPath { get; set; } = "logs/app.db";
    [ObservableProperty] public partial string LogMinLevel { get; set; } = "Information";
    [ObservableProperty] public partial int LogRetentionDays { get; set; } = 30;
    [ObservableProperty] public partial bool LogEnableUserActions { get; set; } = true;
    [ObservableProperty] public partial int LogBatchSize { get; set; } = 100;
    [ObservableProperty] public partial int LogFlushIntervalMs { get; set; } = 1000;
    [ObservableProperty] public partial int LogPruneIntervalMs { get; set; } = 3600000;
    [ObservableProperty] public partial bool HistoryEnabled { get; set; } = true;
    [ObservableProperty] public partial int HistoryPageSize { get; set; } = 200;

    // ---------- Auth / Database ----------

    [ObservableProperty] public partial string AuthDbPath { get; set; } = "logs/users.db";
    [ObservableProperty] public partial int AuthMaxFailedAttempts { get; set; } = 5;
    [ObservableProperty] public partial int AuthLockoutMinutes { get; set; } = 5;
    [ObservableProperty] public partial int AuthPbkdf2Iterations { get; set; } = 210000;
    [ObservableProperty] public partial string AuthSeedAdminUser { get; set; } = "admin";
    [ObservableProperty] public partial string AuthSeedAdminPassword { get; set; } = "admin";
    [ObservableProperty] public partial string PlcSettingsDbPath { get; set; } = "logs/plc.db";
    [ObservableProperty] public partial string ScansDbPath { get; set; } = "logs/scans.db";
    [ObservableProperty] public partial int ScansBatchSize { get; set; } = 100;
    [ObservableProperty] public partial int ScansFlushIntervalMs { get; set; } = 1000;
    [ObservableProperty] public partial int ScansQueueCapacity { get; set; } = 20000;
    [ObservableProperty] public partial int ScansRetentionDays { get; set; }
    [ObservableProperty] public partial int ScansPruneIntervalMs { get; set; } = 3600000;

    // ---------- Trạng thái ----------

    /// <summary>Thông báo kết quả lưu / lỗi.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMessage))]
    [NotifyPropertyChangedFor(nameof(HasErrorMessage))]
    public partial string ConfigMessageText { get; set; } = string.Empty;

    /// <summary>True khi có thông báo để hiện.</summary>
    public bool HasMessage => !string.IsNullOrWhiteSpace(ConfigMessageText);

    /// <summary>True khi thông báo hiện tại là lỗi (hiển thị màu cảnh báo).</summary>
    public bool HasErrorMessage => HasMessage && ConfigMessageKind == MessageKind.Error;

    /// <summary>True khi cấu hình Development đang ghi đè appsettings.json.</summary>
    public bool HasDevelopmentOverride => _store.HasDevelopmentOverride;

    /// <summary>
    /// True khi form có thay đổi chưa lưu so với cấu hình đang chạy.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDirty))]
    [NotifyPropertyChangedFor(nameof(DirtyBadgeText))]
    [NotifyPropertyChangedFor(nameof(CanSave))]
    public partial bool HasUnsavedChanges { get; set; }

    /// <summary>True khi có thay đổi chưa lưu.</summary>
    public bool IsDirty => HasUnsavedChanges;

    /// <summary>Nhãn badge trạng thái dirty.</summary>
    public string DirtyBadgeText => IsDirty ? "Chưa lưu" : string.Empty;

    /// <summary>
    /// True khi đã lưu cấu hình mới nhưng chưa khởi động lại ứng dụng.
    /// Banner yêu cầu restart sẽ hiện khi cờ này bật.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RequiresRestart))]
    [NotifyPropertyChangedFor(nameof(RestartBannerText))]
    [NotifyPropertyChangedFor(nameof(CanRestartNow))]
    public partial bool RestartRequired { get; set; }

    /// <summary>True khi cần restart ứng dụng.</summary>
    public bool RequiresRestart => RestartRequired;

    /// <summary>Nội dung mô tả các thông số đã thay đổi cần restart.</summary>
    [ObservableProperty]
    public partial string RestartBannerText { get; set; } = string.Empty;

    /// <summary>
    /// True khi nút "Restart Now" được phép bấm (đã có thay đổi chờ restart).
    /// </summary>
    public bool CanRestartNow => RestartRequired;

    /// <summary>
    /// True khi được phép áp dụng thay đổi NGAY (hot-apply) mà không cần restart.
    ///
    /// Luôn false: các thông số kết nối (camera, PLC, máy quét, server PO, web)
    /// đều được bind một lần khi app khởi động nên không thể áp dụng nóng.
    /// UI dùng cờ này để khóa nút Lưu khi không có thay đổi nào để lưu.
    /// </summary>
    public bool CanHotApply => false;

    /// <summary>
    /// True khi nút "Lưu cấu hình" được phép bấm: chỉ khi form có thay đổi
    /// chưa lưu so với cấu hình đang chạy.
    /// </summary>
    public bool CanSave => HasUnsavedChanges;

    /// <summary>Kiểu thông báo hiện tại (thông tin / lỗi).</summary>
    private MessageKind ConfigMessageKind { get; set; } = MessageKind.Info;

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

        _isLoading = true;
        try
        {
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

            ScannerEnabled = ReadBool("Scanner:Enabled", snapshot.Scanner.Enabled);
            ScannerMode = ReadString("Scanner:Mode", snapshot.Scanner.Mode);
            ScannerTcpHost = ReadString("Scanner:TcpHost", snapshot.Scanner.TcpHost);
            ScannerTcpPort = ReadInt("Scanner:TcpPort", snapshot.Scanner.TcpPort);
            ScannerComPort = ReadString("Scanner:ComPort", snapshot.Scanner.ComPort);
            ScannerBaudRate = ReadInt("Scanner:BaudRate", snapshot.Scanner.BaudRate);

            PoBaseUrl = ReadString("Po:BaseUrl", snapshot.Po.BaseUrl);
            PoDbRoot = ReadString("Po:DbRoot", snapshot.Po.DbRoot);
            PoActivationIntervalMs = ReadInt("Po:ActivationIntervalMs", snapshot.Po.ActivationIntervalMs);
            PoAutoActivate = ReadBool("Po:AutoActivate", snapshot.Po.AutoActivate);
            PoCodeSampleSize = ReadInt("Po:CodeSampleSize", snapshot.Po.CodeSampleSize);

            WebUrl = ReadString("Web:Url", snapshot.Web.Url);
            WebEnabled = ReadBool("Web:Enabled", snapshot.Web.Enabled);

            LogDbPath = ReadString("Logging:Sqlite:Path", snapshot.Logging.Sqlite.Path);
            LogMinLevel = ReadString("Logging:Sqlite:MinLevel", snapshot.Logging.Sqlite.MinLevel);
            LogRetentionDays = ReadInt("Logging:Sqlite:RetentionDays", snapshot.Logging.Sqlite.RetentionDays);
            LogEnableUserActions = ReadBool("Logging:Sqlite:EnableUserActions", snapshot.Logging.Sqlite.EnableUserActions);
            LogBatchSize = ReadInt("Logging:Sqlite:BatchSize", snapshot.Logging.Sqlite.BatchSize);
            LogFlushIntervalMs = ReadInt("Logging:Sqlite:FlushIntervalMs", snapshot.Logging.Sqlite.FlushIntervalMs);
            LogPruneIntervalMs = ReadInt("Logging:Sqlite:PruneIntervalMs", snapshot.Logging.Sqlite.PruneIntervalMs);
            HistoryEnabled = ReadBool("Logging:History:Enabled", snapshot.Logging.History.Enabled);
            HistoryPageSize = ReadInt("Logging:History:PageSize", snapshot.Logging.History.PageSize);

            AuthDbPath = ReadString("Auth:DbPath", snapshot.Auth.DbPath);
            AuthMaxFailedAttempts = ReadInt("Auth:MaxFailedAttempts", snapshot.Auth.MaxFailedAttempts);
            AuthLockoutMinutes = ReadInt("Auth:LockoutMinutes", snapshot.Auth.LockoutMinutes);
            AuthPbkdf2Iterations = ReadInt("Auth:Pbkdf2Iterations", snapshot.Auth.Pbkdf2Iterations);
            AuthSeedAdminUser = ReadString("Auth:SeedAdminUser", snapshot.Auth.SeedAdminUser);
            AuthSeedAdminPassword = ReadString("Auth:SeedAdminPassword", snapshot.Auth.SeedAdminPassword);
            PlcSettingsDbPath = ReadString("PlcSettings:DbPath", snapshot.PlcSettings.DbPath);
            ScansDbPath = ReadString("Scans:DbPath", snapshot.Scans.DbPath);
            ScansBatchSize = ReadInt("Scans:BatchSize", snapshot.Scans.BatchSize);
            ScansFlushIntervalMs = ReadInt("Scans:FlushIntervalMs", snapshot.Scans.FlushIntervalMs);
            ScansQueueCapacity = ReadInt("Scans:QueueCapacity", snapshot.Scans.QueueCapacity);
            ScansRetentionDays = ReadInt("Scans:RetentionDays", snapshot.Scans.RetentionDays);
            ScansPruneIntervalMs = ReadInt("Scans:PruneIntervalMs", snapshot.Scans.PruneIntervalMs);
        }
        finally
        {
            _isLoading = false;
        }

        // Mốc so sánh = cấu hình đang chạy.
        _baseline = BuildSnapshot();
        _runningSnapshot = _baseline;

        ConfigMessageText = string.Empty;
        ConfigMessageKind = MessageKind.Info;
        HasUnsavedChanges = false;

        // Trạng thái chờ restart được giữ lại nếu cấu hình đã lưu trước đó
        // khác với cấu hình đang chạy (app có thể vừa được mở lại).
        // Nếu tiến trình này là tiến trình con sau khi restart thì cấu hình
        // đã được áp dụng -> không hiện banner.
        var saved = _store.Load();
        var changes = DescribeCriticalChanges(_baseline, saved);
        if (changes.Count > 0 && !Program.IsRestartChild)
        {
            _pendingRestartSnapshot = saved;
            RestartRequired = true;
            RestartBannerText = BuildRestartMessage(changes);
        }
        else
        {
            _pendingRestartSnapshot = null;
            RestartRequired = false;
            RestartBannerText = string.Empty;
        }
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
    /// Gọi lại mỗi khi một property của ViewModel thay đổi.
    ///
    /// Khi người dùng sửa một ô nhập trong form Cấu hình, ta tự đánh dấu
    /// "chưa lưu" (dirty) bằng cách so sánh form hiện tại với mốc cấu hình
    /// đã lưu. Nhờ vậy người dùng luôn thấy trạng thái rõ ràng trước khi
    /// rời khỏi trang.
    /// </summary>
    partial void OnCameraIpChanged(string value) => OnEditableFieldChanged();

    partial void OnCameraPortChanged(int value) => OnEditableFieldChanged();

    partial void OnCameraDuplicateWindowSecondsChanged(int value) => OnEditableFieldChanged();

    partial void OnPlcIpChanged(string value) => OnEditableFieldChanged();

    partial void OnPlcPortChanged(int value) => OnEditableFieldChanged();

    partial void OnPlcRemoteNodeChanged(int value) => OnEditableFieldChanged();

    partial void OnPlcLocalNodeChanged(int value) => OnEditableFieldChanged();

    partial void OnPlcTimeoutMsChanged(int value) => OnEditableFieldChanged();

    partial void OnPlcRetriesChanged(int value) => OnEditableFieldChanged();

    partial void OnResultAddressChanged(string value) => OnEditableFieldChanged();

    partial void OnReadyAddressChanged(string value) => OnEditableFieldChanged();

    partial void OnRefreshIntervalMsChanged(int value) => OnEditableFieldChanged();

    partial void OnScannerEnabledChanged(bool value) => OnEditableFieldChanged();

    partial void OnScannerModeChanged(string value) => OnEditableFieldChanged();

    partial void OnScannerTcpHostChanged(string value) => OnEditableFieldChanged();

    partial void OnScannerTcpPortChanged(int value) => OnEditableFieldChanged();

    partial void OnScannerComPortChanged(string value) => OnEditableFieldChanged();

    partial void OnScannerBaudRateChanged(int value) => OnEditableFieldChanged();

    partial void OnPoBaseUrlChanged(string value) => OnEditableFieldChanged();

    partial void OnPoDbRootChanged(string value) => OnEditableFieldChanged();

    partial void OnPoActivationIntervalMsChanged(int value) => OnEditableFieldChanged();

    partial void OnPoAutoActivateChanged(bool value) => OnEditableFieldChanged();

    partial void OnPoCodeSampleSizeChanged(int value) => OnEditableFieldChanged();

    partial void OnWebUrlChanged(string value) => OnEditableFieldChanged();

    partial void OnWebEnabledChanged(bool value) => OnEditableFieldChanged();

    partial void OnLogDbPathChanged(string value) => OnEditableFieldChanged();
    partial void OnLogMinLevelChanged(string value) => OnEditableFieldChanged();
    partial void OnLogRetentionDaysChanged(int value) => OnEditableFieldChanged();
    partial void OnLogEnableUserActionsChanged(bool value) => OnEditableFieldChanged();
    partial void OnLogBatchSizeChanged(int value) => OnEditableFieldChanged();
    partial void OnLogFlushIntervalMsChanged(int value) => OnEditableFieldChanged();
    partial void OnLogPruneIntervalMsChanged(int value) => OnEditableFieldChanged();
    partial void OnHistoryEnabledChanged(bool value) => OnEditableFieldChanged();
    partial void OnHistoryPageSizeChanged(int value) => OnEditableFieldChanged();
    partial void OnAuthDbPathChanged(string value) => OnEditableFieldChanged();
    partial void OnAuthMaxFailedAttemptsChanged(int value) => OnEditableFieldChanged();
    partial void OnAuthLockoutMinutesChanged(int value) => OnEditableFieldChanged();
    partial void OnAuthPbkdf2IterationsChanged(int value) => OnEditableFieldChanged();
    partial void OnAuthSeedAdminUserChanged(string value) => OnEditableFieldChanged();
    partial void OnAuthSeedAdminPasswordChanged(string value) => OnEditableFieldChanged();
    partial void OnPlcSettingsDbPathChanged(string value) => OnEditableFieldChanged();
    partial void OnScansDbPathChanged(string value) => OnEditableFieldChanged();
    partial void OnScansBatchSizeChanged(int value) => OnEditableFieldChanged();
    partial void OnScansFlushIntervalMsChanged(int value) => OnEditableFieldChanged();
    partial void OnScansQueueCapacityChanged(int value) => OnEditableFieldChanged();
    partial void OnScansRetentionDaysChanged(int value) => OnEditableFieldChanged();
    partial void OnScansPruneIntervalMsChanged(int value) => OnEditableFieldChanged();

    /// <summary>Cờ nội bộ: đang nạp dữ liệu vào form.</summary>
    private bool _isLoading;

    /// <summary>
    /// Được gọi mỗi khi một ô nhập của form thay đổi — dùng để cập nhật
    /// cờ "chưa lưu" (dirty) và làm mới trạng thái nút Lưu.
    /// </summary>
    private void OnEditableFieldChanged()
    {
        // Bỏ qua việc đánh dấu dirty trong lúc đang nạp form.
        if (_isLoading)
        {
            return;
        }

        UpdateDirtyState();
    }

    /// <summary>Cập nhật cờ dirty dựa trên so sánh form với mốc đã lưu.</summary>
    private void UpdateDirtyState()
    {
        if (_baseline is null)
        {
            return;
        }

        var dirty = !AreEquivalent(BuildSnapshot(), _baseline);
        if (dirty != HasUnsavedChanges)
        {
            HasUnsavedChanges = dirty;
        }
    }

    /// <summary>
    /// So sánh hai snapshot theo toàn bộ trường hiển thị trên form.
    /// </summary>
    private static bool AreEquivalent(AppConfigSnapshot a, AppConfigSnapshot b) =>
        ConfigDiff.AreEquivalent(a, b);

    /// <summary>
    /// Hoàn tác các thay đổi chưa lưu về giá trị trong file.
    /// </summary>
    [RelayCommand]
    private void Reset()
    {
        Load();
        ConfigMessageText = "Đã hoàn tác thay đổi chưa lưu.";
        ConfigMessageKind = MessageKind.Info;

        _actionLog.Log("Config.Reset", "Config", "Hoan tac thay doi chua luu", UserActionResults.Success);
    }

    /// <summary>
    /// Kiểm tra giá trị nhập trước khi lưu.
    /// </summary>
    private bool Validate()
    {
        if (!ConfigValidation.Validate(BuildSnapshot(), out var error))
        {
            ConfigMessageText = error ?? "Cấu hình không hợp lệ.";
            ConfigMessageKind = MessageKind.Error;
            return false;
        }

        return true;
    }

    /// <summary>Đặt thông báo lỗi và trả về false (dùng cho Validate).</summary>
    private bool Fail(string message)
    {
        ConfigMessageText = message;
        ConfigMessageKind = MessageKind.Error;
        return false;
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

        var snapshot = BuildSnapshot();

        if (!_store.Save(snapshot))
        {
            ConfigMessageText = "Không ghi được file cấu hình. Kiểm tra quyền truy cập thư mục ứng dụng.";
            ConfigMessageKind = MessageKind.Error;
            _actionLog.Log(
                "Config.Save",
                "Config",
                "Khong ghi duoc file cau hinh",
                UserActionResults.Failed);
            return;
        }

        // Mốc so sánh mới = cấu hình vừa lưu (đây cũng là cấu hình sẽ chạy sau restart).
        _baseline = snapshot;

        // Tính các thông số quan trọng đã thay đổi so với cấu hình ĐANG CHẠY.
        // Dùng _pendingRestartSnapshot nếu có (trường hợp app được mở lại mà
        // cấu hình đã lưu vẫn chưa được áp dụng), ngược lại so với cấu hình
        // lúc nạp form.
        var runningSnapshot = _pendingRestartSnapshot ?? _runningSnapshot;
        var criticalChanges = DescribeCriticalChanges(runningSnapshot, snapshot);

        HasUnsavedChanges = false;

        if (criticalChanges.Count > 0)
        {
            _pendingRestartSnapshot = snapshot;
            RestartRequired = true;
            RestartBannerText = BuildRestartMessage(criticalChanges);
            ConfigMessageText = HasDevelopmentOverride
                ? "Đã lưu cấu hình. Cần khởi động lại ứng dụng để áp dụng thông số mới. Lưu ý: appsettings.Development.json đang tồn tại và sẽ ghi đè các giá trị này khi app khởi động."
                : "Đã lưu cấu hình. Cần khởi động lại ứng dụng để áp dụng thông số mới.";
            ConfigMessageKind = MessageKind.Warning;
        }
        else
        {
            _pendingRestartSnapshot = null;
            RestartRequired = false;
            RestartBannerText = string.Empty;
            ConfigMessageText = HasDevelopmentOverride
                ? "Đã lưu cấu hình. Lưu ý: appsettings.Development.json đang tồn tại và sẽ ghi đè các giá trị này khi app khởi động."
                : "Đã lưu cấu hình. Thông số không yêu cầu khởi động lại ứng dụng.";
            ConfigMessageKind = MessageKind.Info;
        }

        _actionLog.Log(
            "Config.Save",
            "Config",
            criticalChanges.Count > 0
                ? $"Luu cau hinh, yeu cau restart ({criticalChanges.Count} thong so)"
                : "Luu cau hinh",
            UserActionResults.Success);
    }

    /// <summary>
    /// Khởi động lại ứng dụng ngay: mở tiến trình mới rồi đóng tiến trình hiện tại.
    /// </summary>
    [RelayCommand]
    private void RestartNow()
    {
        if (!RestartRequired)
        {
            return;
        }

        try
        {
            if (!TryBuildRestartStartInfo(out var startInfo, out var errorMessage))
            {
                ConfigMessageText = errorMessage;
                ConfigMessageKind = MessageKind.Error;
                return;
            }

            using (var process = Process.Start(startInfo))
            {
                if (process is null)
                {
                    ConfigMessageText = "Không khởi động lại được ứng dụng. Hãy tự đóng và mở lại ứng dụng.";
                    ConfigMessageKind = MessageKind.Error;
                    return;
                }
            }

            _actionLog.Log("Config.RestartNow", "Config", "Khoi dong lai ung dung", UserActionResults.Success);

            // Đóng ứng dụng hiện tại (tiến trình con đã chạy).
            RequestApplicationShutdown();
        }
        catch (Exception ex)
        {
            ConfigMessageText = "Không khởi động lại được ứng dụng: " + ex.Message;
            ConfigMessageKind = MessageKind.Error;
            _actionLog.Log(
                "Config.RestartNow",
                "Config",
                $"Loi khoi dong lai ung dung: {ex.Message}",
                UserActionResults.Failed);
        }
    }

    /// <summary>
    /// Tạo lệnh khởi động lại ứng dụng.
    ///
    /// Trường hợp phổ biến (chạy bằng file .exe đã publish / bin): dùng trực tiếp
    /// <see cref="Environment.ProcessPath"/>.
    ///
    /// Trường hợp chạy bằng `dotnet run` / `dotnet TanTienQR.dll`: tiến trình hiện tại
    /// là `dotnet.exe`, nên phải chạy lại `dotnet <dll của app>`. Khi đó dùng
    /// <c>Assembly.Location</c> của entry assembly để tìm DLL.
    /// </summary>
    private static bool TryBuildRestartStartInfo(
        out ProcessStartInfo startInfo,
        out string errorMessage)
    {
        startInfo = null!;
        errorMessage = string.Empty;

        var processPath = Environment.ProcessPath;

        // App chạy như .exe (không qua dotnet host).
        if (!string.IsNullOrWhiteSpace(processPath)
            && !string.Equals(
                Path.GetFileNameWithoutExtension(processPath),
                "dotnet",
                StringComparison.OrdinalIgnoreCase))
        {
            if (!File.Exists(processPath))
            {
                errorMessage = "Không tìm thấy tệp thực thi để khởi động lại. Hãy tự đóng và mở lại ứng dụng.";
                return false;
            }

            startInfo = new ProcessStartInfo
            {
                FileName = processPath,
                UseShellExecute = false,
                WorkingDirectory = AppContext.BaseDirectory,
            };
            startInfo.ArgumentList.Add(Program.RestartMarkerArg);
            return true;
        }

        // App chạy qua dotnet host -> chạy lại bằng DLL của app.
        // Không dùng Assembly.Location vì trong single-file/AOT nó có thể rỗng
        // (IL3000); suy ra đường dẫn DLL từ thư mục base + tên assembly.
        var assemblyName = System.Reflection.Assembly.GetEntryAssembly()?.GetName().Name;
        if (string.IsNullOrWhiteSpace(assemblyName))
        {
            errorMessage = "Không xác định được tệp ứng dụng để khởi động lại. Hãy tự đóng và mở lại ứng dụng.";
            return false;
        }

        var dllPath = Path.Combine(AppContext.BaseDirectory, assemblyName + ".dll");
        if (!File.Exists(dllPath))
        {
            errorMessage = "Không tìm thấy tệp ứng dụng để khởi động lại. Hãy tự đóng và mở lại ứng dụng.";
            return false;
        }

        startInfo = new ProcessStartInfo
        {
            FileName = processPath ?? "dotnet",
            UseShellExecute = false,
            WorkingDirectory = AppContext.BaseDirectory,
        };
        startInfo.ArgumentList.Add(dllPath);
        startInfo.ArgumentList.Add(Program.RestartMarkerArg);
        return true;
    }

    /// <summary>
    /// "Restart Later": giữ nguyên banner yêu cầu restart, người dùng có thể
    /// tiếp tục làm việc và khởi động lại app sau.
    /// </summary>
    [RelayCommand]
    private void RestartLater()
    {
        if (!RestartRequired)
        {
            return;
        }

        ConfigMessageText = "Đã ghi nhận. Thông số mới sẽ có hiệu lực sau khi khởi động lại ứng dụng.";
        ConfigMessageKind = MessageKind.Warning;

        _actionLog.Log("Config.RestartLater", "Config", "Deo restart ung dung", UserActionResults.Success);
    }

    /// <summary>
    /// Yêu cầu ứng dụng hiện tại đóng lại.
    /// </summary>
    private static void RequestApplicationShutdown()
    {
        var app = Avalonia.Application.Current;
        if (app?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.Shutdown();
            return;
        }

        Environment.Exit(0);
    }

    /// <summary>
    /// Cấu hình đang chạy — dùng làm mốc so sánh cho các thông số quan trọng.
    /// </summary>
    private AppConfigSnapshot _runningSnapshot = new();

    /// <summary>
    /// Dựng snapshot từ các giá trị hiện tại trên form.
    /// </summary>
    private AppConfigSnapshot BuildSnapshot() => new()
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
        Scanner = new ScannerConfig
        {
            Enabled = ScannerEnabled,
            Mode = NormalizeScannerMode(ScannerMode),
            TcpHost = ScannerTcpHost.Trim(),
            TcpPort = ScannerTcpPort,
            ComPort = ScannerComPort.Trim(),
            BaudRate = ScannerBaudRate,
        },
        Po = new PoConfig
        {
            BaseUrl = PoBaseUrl.Trim(),
            DbRoot = PoDbRoot.Trim(),
            ActivationIntervalMs = PoActivationIntervalMs,
            AutoActivate = PoAutoActivate,
            CodeSampleSize = PoCodeSampleSize,
        },
        Web = new WebConfig
        {
            Url = WebUrl.Trim(),
            Enabled = WebEnabled,
        },
        Logging = new LoggingConfig
        {
            Sqlite = new SqliteLoggingConfig
            {
                Path = LogDbPath.Trim(), MinLevel = LogMinLevel.Trim(), RetentionDays = LogRetentionDays,
                EnableUserActions = LogEnableUserActions, BatchSize = LogBatchSize,
                FlushIntervalMs = LogFlushIntervalMs, PruneIntervalMs = LogPruneIntervalMs,
            },
            History = new LoggingHistoryConfig { Enabled = HistoryEnabled, PageSize = HistoryPageSize },
        },
        Auth = new AuthConfig
        {
            DbPath = AuthDbPath.Trim(), MaxFailedAttempts = AuthMaxFailedAttempts,
            LockoutMinutes = AuthLockoutMinutes, Pbkdf2Iterations = AuthPbkdf2Iterations,
            SeedAdminUser = AuthSeedAdminUser.Trim(), SeedAdminPassword = AuthSeedAdminPassword,
        },
        PlcSettings = new PlcSettingsConfig { DbPath = PlcSettingsDbPath.Trim() },
        Scans = new ScanConfig
        {
            DbPath = ScansDbPath.Trim(), BatchSize = ScansBatchSize, FlushIntervalMs = ScansFlushIntervalMs,
            QueueCapacity = ScansQueueCapacity, RetentionDays = ScansRetentionDays,
            PruneIntervalMs = ScansPruneIntervalMs,
        },
    };

    /// <summary>Chuẩn hóa chế độ scanner về TcpServer / TcpClient.</summary>
    private static string NormalizeScannerMode(string mode) =>
        string.Equals(mode?.Trim(), "TcpClient", StringComparison.OrdinalIgnoreCase)
            ? "TcpClient"
            : "TcpServer";

    /// <summary>
    /// So sánh hai snapshot và trả về danh sách các thông số QUAN TRỌNG đã đổi.
    /// Các thông số này được bind một lần khi app khởi động nên bắt buộc restart.
    /// </summary>
    private static List<string> DescribeCriticalChanges(AppConfigSnapshot before, AppConfigSnapshot after) =>
        ConfigDiff.DescribeCriticalChanges(before, after);

    /// <summary>
    /// Tạo nội dung banner yêu cầu restart từ danh sách thông số đã đổi.
    /// </summary>
    private static string BuildRestartMessage(List<string> changes)
    {
        if (changes.Count == 0)
        {
            return string.Empty;
        }

        var list = string.Join(", ", changes);
        return $"Cần khởi động lại ứng dụng để áp dụng: {list}. "
            + "Các kết nối camera, PLC và máy quét chỉ được khởi tạo lại khi app khởi động lại.";
    }

    /// <summary>Kiểu thông báo.</summary>
    private enum MessageKind
    {
        Info,
        Warning,
        Error,
    }
}