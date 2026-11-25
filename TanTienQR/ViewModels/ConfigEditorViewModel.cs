using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using ClassLibraryMT.Logging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Configuration;
using TanTienQR.Services;
using TanTienQR.Services.Config;

namespace TanTienQR.ViewModels;

/// <summary>
/// Trình chỉnh sửa cấu hình runtime cho tab Cấu hình.
///
/// Trang Cấu hình được SINH ĐỘNG: toàn bộ section + trường đến từ
/// <see cref="ConfigSchemaProvider"/> (quét attribute <c>[ConfigSection]</c> /
/// <c>[ConfigField]</c> trên <see cref="AppConfigSnapshot"/>).
///
/// <see cref="Load"/> đọc từ <see cref="IConfiguration"/> (cấu hình đã merge
/// giữa appsettings.json và appsettings.Development.json) nên form hiển thị
/// đúng giá trị đang chạy. <see cref="Save"/> ghi xuống appsettings.json.
///
/// Camera và PLC hỗ trợ áp dụng nóng sau khi người vận hành xác nhận; các
/// nhóm cấu hình khác vẫn hiển thị trạng thái khởi động lại khi cần.
///
/// ===== THÊM SETTING MỚI =====
/// 1) Thêm property vào class section trong <c>Services/AppConfigModels.cs</c>
///    kèm <c>[JsonPropertyName("TenKey")]</c> + <c>[ConfigField("Nhãn", ...)]</c>.
/// 2) (Tuỳ chọn) Thêm giá trị mặc định vào <c>appsettings.json</c>.
/// 3) (Tuỳ chọn) Nếu service đọc lúc start: thêm 1 dòng bind trong
///    <c>Services/TanTienAppServices.cs</c>.
///
/// Form, validation và diff tự động hỗ trợ — không cần sửa XAML.
/// </summary>
internal partial class ConfigEditorViewModel : ViewModelBase
{
    /// <summary>Tên file cấu hình chính (dùng cho thông báo).</summary>
    private const string ConfigFileName = "appsettings.json";

    private readonly ConfigStore _store;
    private readonly IConfiguration _configuration;
    private readonly IUserActionLogger _actionLog;
    private readonly IDeviceConfigurationApplier _deviceApplier;
    private bool _loaded;

    /// <summary>Chủ cửa sổ gắn callback; thiếu xác nhận thì không đổi thiết bị.</summary>
    public Func<string, Task<bool>>? ConfirmConnectionChangesAsync { get; set; }

    public event EventHandler? DeviceConfigurationApplied;

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
        IUserActionLogger actionLog,
        IDeviceConfigurationApplier deviceApplier)
    {
        _store = store;
        _configuration = configuration;
        _actionLog = actionLog;
        _deviceApplier = deviceApplier;

        BuildSectionsFromSchema();
    }

    // ---------- Form động ----------

    /// <summary>
    /// Các section cấu hình (mỗi section là 1 card trên UI).
    /// </summary>
    public ObservableCollection<ConfigSectionViewModel> Sections { get; } = new();

    /// <summary>
    /// Toàn bộ trường của mọi section (dạng phẳng) — dùng cho validate/diff.
    /// </summary>
    public ReadOnlyObservableCollection<ConfigFieldViewModel> AllFields { get; private set; } =
        new(new ObservableCollection<ConfigFieldViewModel>());

    /// <summary>
    /// Dựng form động từ schema (chạy 1 lần khi khởi tạo ViewModel).
    /// </summary>
    private void BuildSectionsFromSchema()
    {
        var all = new ObservableCollection<ConfigFieldViewModel>();

        foreach (var sectionSchema in ConfigSchemaProvider.Sections)
        {
            var sectionVm = new ConfigSectionViewModel(sectionSchema);

            foreach (var fieldSchema in sectionSchema.Fields)
            {
                var fieldVm = CreateFieldViewModel(fieldSchema, GetDefaultValue(fieldSchema));
                fieldVm.ValueChanged += OnFieldChanged;
                sectionVm.Fields.Add(fieldVm);
                all.Add(fieldVm);
            }

            Sections.Add(sectionVm);
        }

        AllFields = new ReadOnlyObservableCollection<ConfigFieldViewModel>(all);
    }

    /// <summary>
    /// Lấy giá trị mặc định của một trường từ model (instance mới).
    /// </summary>
    /// <param name="schema">Schema của trường.</param>
    /// <returns>Giá trị mặc định.</returns>
    private static object? GetDefaultValue(ConfigFieldSchema schema)
    {
        var instance = Activator.CreateInstance(schema.Section.ModelType);
        return schema.GetValue(instance);
    }

    /// <summary>
    /// Tạo ViewModel cho một trường, dùng đúng lớp vỏ theo loại control để
    /// Avalonia 12 chọn đúng DataTemplate (Avalonia 12 đã bỏ DataTemplateSelector).
    /// </summary>
    /// <param name="schema">Schema của trường.</param>
    /// <param name="value">Giá trị khởi tạo.</param>
    /// <returns>ViewModel của trường.</returns>
    private static ConfigFieldViewModel CreateFieldViewModel(ConfigFieldSchema schema, object? value) =>
        schema.Control switch
        {
            ConfigControlKind.Bool => new BoolField(schema, value),
            ConfigControlKind.Number => new NumberField(schema, value),
            ConfigControlKind.Combo => new ComboField(schema, value),
            ConfigControlKind.Password => new PasswordField(schema, value),
            _ => new TextField(schema, value),
        };

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
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
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
    public bool CanRestartNow => RestartRequired && !IsSaving;

    /// <summary>
    /// True khi thay đổi Camera/PLC có thể áp dụng ngay sau xác nhận.
    /// UI vẫn khóa nút Lưu nếu không có thay đổi nào để lưu.
    /// </summary>
    public bool CanHotApply => true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSave))]
    [NotifyPropertyChangedFor(nameof(CanReset))]
    [NotifyPropertyChangedFor(nameof(CanRestartNow))]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    [NotifyCanExecuteChangedFor(nameof(ResetCommand))]
    public partial bool IsSaving { get; set; }

    public bool CanReset => !IsSaving;

    /// <summary>
    /// True khi nút "Lưu cấu hình" được phép bấm: chỉ khi form có thay đổi
    /// chưa lưu so với cấu hình đang chạy.
    /// </summary>
    public bool CanSave => HasUnsavedChanges && !IsSaving;

    /// <summary>Kiểu thông báo hiện tại (thông tin / lỗi).</summary>
    private MessageKind _messageKind;
    private MessageKind ConfigMessageKind
    {
        get => _messageKind;
        set { _messageKind = value; OnPropertyChanged(nameof(HasErrorMessage)); }
    }

    /// <summary>
    /// Nạp cấu hình đang chạy vào form.
    ///
    /// Đọc từ <see cref="IConfiguration"/> để lấy giá trị đã merge (bao gồm cả
    /// appsettings.Development.json và biến môi trường TANTIEN_*).
    /// Nếu không đọc được (app chưa có config) thì fallback sang file JSON.
    /// </summary>
    public void Load()
    {
        if (_loaded)
        {
            foreach (var field in AllFields)
            {
                field.SetValueSilently(field.Schema.GetValue(field.Schema.Section.ResolveOwner(_baseline)), resetOriginal: true);
                field.SetError(null);
            }
            HasUnsavedChanges = false;
            return;
        }
        var snapshot = _store.Load();

        foreach (var field in AllFields)
        {
            var value = ReadValue(field.Schema, snapshot);
            field.SetValueSilently(value, resetOriginal: true);
            field.SetError(null);
        }

        // Icon là static resource -> nạp sau khi resource của app sẵn sàng.
        foreach (var section in Sections)
        {
            section.RefreshIcon();
        }

        // Mốc so sánh = cấu hình đang chạy.
        _baseline = BuildSnapshot();
        _runningSnapshot = _baseline;
        _loaded = true;

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

    /// <summary>
    /// Đọc giá trị của một trường từ snapshot, có overlay từ
    /// <see cref="IConfiguration"/> (Development.json + biến môi trường).
    /// </summary>
    /// <param name="schema">Schema của trường.</param>
    /// <param name="snapshot">Snapshot từ file.</param>
    /// <returns>Giá trị hiển thị.</returns>
    private object? ReadValue(ConfigFieldSchema schema, AppConfigSnapshot snapshot)
    {
        // 1) Giá trị trong file JSON.
        var sectionValue = schema.Section.ResolveOwner(snapshot);
        var fileValue = schema.GetValue(sectionValue);

        // 2) Overlay từ IConfiguration (đã merge Development + env).
        var configValue = _configuration[schema.JsonPath];
        if (string.IsNullOrWhiteSpace(configValue))
        {
            return fileValue;
        }

        if (schema.IsNumeric)
        {
            var type = schema.NumericType ?? typeof(int);
            try
            {
                return Convert.ChangeType(configValue.Trim(), type, System.Globalization.CultureInfo.InvariantCulture);
            }
            catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException)
            {
                return fileValue;
            }
        }

        if (schema.Control == ConfigControlKind.Bool)
        {
            return bool.TryParse(configValue.Trim(), out var parsed) ? parsed : fileValue;
        }

        return configValue.Trim();
    }

    /// <summary>
    /// Được gọi mỗi khi BẤT KỲ trường nào đó trong form thay đổi.
    ///
    /// Đây là điểm móc DUY NHẤT để tính lại trạng thái dirty và trạng thái
    /// nút Lưu — không cần mỗi property một hàm OnXChanged.
    /// </summary>
    /// <param name="sender">Trường vừa thay đổi.</param>
    /// <param name="e">Tham số sự kiện.</param>
    private void OnFieldChanged(object? sender, EventArgs e)
    {
        UpdateDirtyState();
    }

    /// <summary>Cập nhật cờ dirty dựa trên so sánh form với mốc đã lưu.</summary>
    private void UpdateDirtyState()
    {
        if (_baseline is null)
        {
            return;
        }

        var dirty = AllFields.Any(f => f.IsDirty);
        if (dirty != HasUnsavedChanges)
        {
            HasUnsavedChanges = dirty;
        }
    }

    /// <summary>
    /// Hoàn tác các thay đổi chưa lưu về giá trị trong file.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanReset))]
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
        // Xoá lỗi cũ trước khi validate lại.
        foreach (var field in AllFields)
        {
            field.SetError(null);
        }

        AppConfigSnapshot snapshot;
        try
        {
            snapshot = BuildSnapshot();
        }
        catch (InvalidOperationException ex)
        {
            ConfigMessageText = ex.Message;
            ConfigMessageKind = MessageKind.Error;
            return false;
        }

        if (!ConfigValidation.Validate(snapshot, out var error))
        {
            ConfigMessageText = error ?? "Cấu hình không hợp lệ.";
            ConfigMessageKind = MessageKind.Error;
            return false;
        }

        return true;
    }

    /// <summary>
    /// Lưu cấu hình xuống <c>appsettings.json</c>.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveAsync(CancellationToken cancellationToken)
    {
        if (!CanSave) return;
        IsSaving = true;
        try
        {
            if (!Validate())
            {
                return;
            }

            var snapshot = BuildSnapshot();
            var deviceChanges = DeviceConfigurationChanges.Describe(_runningSnapshot, snapshot);
            if (deviceChanges.Count > 0)
            {
                var confirmation = ConfirmConnectionChangesAsync;
                if (confirmation is null)
                    throw new InvalidOperationException("Chưa có cửa sổ xác nhận. Hãy mở trang Cấu hình trong ứng dụng để lưu thay đổi Camera/PLC.");
                var message = "Lưu và áp dụng các thay đổi sau?\n\n" + string.Join("\n", deviceChanges)
                    + "\n\nKết nối thiết bị sẽ tạm ngắt để chuyển cấu hình. Ứng dụng và phiên đăng nhập vẫn được giữ; thiết bị sẽ tự kết nối lại. Chỉ xác nhận khi dây chuyền có thể tạm dừng nhận mã."
                    + (HasDevelopmentOverride ? "\n\nLưu ý: cấu hình Development có thể ghi đè các giá trị này ở lần khởi động sau." : "");
                if (!await confirmation(message).WaitAsync(cancellationToken))
                {
                    ConfigMessageKind = MessageKind.Info;
                    ConfigMessageText = "Đã hủy lưu; cấu hình và kết nối hiện tại được giữ nguyên.";
                    return;
                }
            }
            await SaveSnapshotAsync(snapshot, deviceChanges.Count > 0, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            ConfigMessageKind = MessageKind.Warning;
            ConfigMessageText = "Đã dừng thao tác lưu cấu hình.";
        }
        catch (Exception ex)
        {
            ConfigMessageText = "Không lưu được cấu hình: " + ex.Message;
            ConfigMessageKind = MessageKind.Error;
            _actionLog.Log(
                "Config.Save",
                "Config",
                $"Loi khong mong doi khi luu: {ex.Message}",
                UserActionResults.Failed);
        }
        finally { IsSaving = false; }
    }

    /// <summary>
    /// Ghi snapshot đã validate xuống disk và cập nhật trạng thái restart/dirty.
    /// </summary>
    /// <param name="snapshot">Snapshot đã kiểm tra hợp lệ.</param>
    private async Task SaveSnapshotAsync(AppConfigSnapshot snapshot, bool deviceChanged, CancellationToken cancellationToken)
    {
        // Dùng SaveMerged để GIỮ NGUYÊN các key lạ có sẵn trong file.
        if (deviceChanged)
        {
            await _deviceApplier.ApplyAndSaveAsync(snapshot, _runningSnapshot,
                () => _store.SaveMerged(snapshot), cancellationToken);
            // Chỉ Camera/PLC đã được áp dụng; giữ runtime thật của các section khác.
            _runningSnapshot.Camera = snapshot.Camera;
            _runningSnapshot.Plc = snapshot.Plc;
            DeviceConfigurationApplied?.Invoke(this, EventArgs.Empty);
        }
        else if (!_store.SaveMerged(snapshot))
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
        var criticalChanges = DescribeCriticalChanges(_runningSnapshot, snapshot);

        // Cập nhật giá trị gốc của các trường -> form sạch.
        foreach (var field in AllFields)
        {
            field.SetValueSilently(field.Schema.GetValue(field.Schema.Section.ResolveOwner(snapshot)), resetOriginal: true);
        }

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
                ? "Đã lưu và áp dụng cấu hình Camera/PLC. Lưu ý: appsettings.Development.json có thể ghi đè ở lần khởi động sau."
                : deviceChanged ? "Đã lưu và áp dụng cấu hình Camera/PLC ngay. Thiết bị sẽ tự kết nối lại; không cần khởi động lại ứng dụng."
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
            if (ClassLibraryMT.Updates.UpdateProtocol.TryRequestRestart())
            {
                RequestApplicationShutdown();
                return;
            }

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
    /// Dựng snapshot từ các giá trị hiện tại trên form (đi qua schema).
    /// </summary>
    /// <returns>Snapshot cấu hình hiện tại.</returns>
    private AppConfigSnapshot BuildSnapshot()
    {
        var snapshot = new AppConfigSnapshot();

        foreach (var sectionSchema in ConfigSchemaProvider.Sections)
        {
            var sectionValue = Activator.CreateInstance(sectionSchema.ModelType);
            if (sectionValue is null)
            {
                continue;
            }

            foreach (var fieldSchema in sectionSchema.Fields)
            {
                var field = AllFields.FirstOrDefault(f =>
                    string.Equals(f.JsonPath, fieldSchema.JsonPath, StringComparison.Ordinal));

                if (field is null)
                {
                    continue;
                }

                fieldSchema.SetValue(sectionValue, NormalizeFieldValue(fieldSchema, field.Value));
            }

            // Gán section (và các section con lồng trong nó) vào snapshot.
            SetSectionValue(snapshot, sectionSchema, sectionValue);
        }

        return snapshot;
    }

    /// <summary>
    /// Gán giá trị của một section (kể cả section lồng nhau) vào snapshot.
    /// </summary>
    /// <param name="snapshot">Snapshot đích.</param>
    /// <param name="sectionSchema">Schema của section.</param>
    /// <param name="value">Giá trị section.</param>
    private static void SetSectionValue(
        AppConfigSnapshot snapshot,
        ConfigSectionSchema sectionSchema,
        object value)
    {
        var chain = sectionSchema.PropertyChain;
        if (chain.Count == 0)
        {
            return;
        }

        // Đi tới object cha (bỏ property cuối cùng).
        object? current = snapshot;
        for (var i = 0; i < chain.Count - 1; i++)
        {
            if (current is null)
            {
                return;
            }

            current = chain[i].GetValue(current);
        }

        if (current is null)
        {
            return;
        }

        chain[^1].SetValue(current, value);
    }

    /// <summary>
    /// Chuẩn hoá giá trị trước khi gán vào model: cắt khoảng trắng cho string,
    /// chuẩn hoá chế độ scanner, chuyển số về đúng kiểu của property.
    ///
    /// NumericUpDown của Avalonia trả về <c>decimal?</c> trong khi model dùng
    /// <c>int</c>/<c>long</c> — gán trực tiếp qua reflection sẽ ném
    /// <c>ArgumentException</c> và làm crash nút Lưu. Vì vậy mọi giá trị số
    /// đều được chuyển về đúng <c>PropertyType</c> ở đây.
    /// </summary>
    /// <param name="schema">Schema của trường.</param>
    /// <param name="value">Giá trị thô từ form.</param>
    /// <returns>Giá trị đã chuẩn hoá.</returns>
    private static object? NormalizeFieldValue(ConfigFieldSchema schema, object? value)
    {
        if (value is null)
        {
            return null;
        }

        if (value is string s)
        {
            var trimmed = s.Trim();

            // Chế độ máy quét chỉ nhận TcpServer / TcpClient.
            if (string.Equals(schema.JsonPath, "Scanner:Mode", StringComparison.Ordinal))
            {
                return string.Equals(trimmed, "TcpClient", StringComparison.OrdinalIgnoreCase)
                    ? "TcpClient"
                    : "TcpServer";
            }

            return trimmed;
        }

        if (schema.IsNumeric)
        {
            var targetType = Nullable.GetUnderlyingType(schema.Property.PropertyType)
                ?? schema.Property.PropertyType;

            // Đã đúng kiểu -> giữ nguyên (tránh Convert làm mất precision).
            if (value.GetType() == targetType)
            {
                return value;
            }

            try
            {
                // decimal (NumericUpDown) -> int/long/double/... đều chuyển được.
                if (targetType.IsEnum)
                {
                    return Enum.ToObject(targetType, value);
                }

                return Convert.ChangeType(value, targetType, System.Globalization.CultureInfo.InvariantCulture);
            }
            catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException or ArgumentException)
            {
                // Chuyển đổi thất bại -> để Validate bắt lỗi thay vì crash.
                // Trả về giá trị gốc để BuildSnapshot không ném ở SetValue:
                // thử parse qua double rồi ép về target, nếu vẫn lỗi thì trả gốc.
                try
                {
                    var asDouble = Convert.ToDouble(value, System.Globalization.CultureInfo.InvariantCulture);
                    var clamped = Math.Max(schema.Min, Math.Min(schema.Max, asDouble));
                    return Convert.ChangeType(clamped, targetType, System.Globalization.CultureInfo.InvariantCulture);
                }
                catch
                {
                    return value;
                }
            }
        }

        return value;
    }

    /// <summary>
    /// Tìm schema section theo key gốc.
    /// </summary>
    /// <param name="key">Key cần tìm (VD: "Sqlite").</param>
    /// <returns>Schema tương ứng, null nếu không có.</returns>
    private static ConfigSectionSchema? FindSection(string key) =>
        ConfigSchemaProvider.FindSection(key);

    /// <summary>
    /// So sánh hai snapshot và trả về danh sách các thông số QUAN TRỌNG đã đổi.
    /// Các thông số này được bind một lần khi app khởi động nên bắt buộc restart.
    /// </summary>
    private static List<string> DescribeCriticalChanges(AppConfigSnapshot before, AppConfigSnapshot after) =>
        ConfigDiff.DescribeCriticalChanges(before, after);

    /// <summary>
    /// Tạo nội dung banner yêu cầu restart từ danh sách thông số đã đổi.
    /// </summary>
    /// <param name="changes">Danh sách nhãn thông số đã đổi.</param>
    /// <returns>Nội dung banner.</returns>
    private static string BuildRestartMessage(List<string> changes)
    {
        if (changes.Count == 0)
        {
            return string.Empty;
        }

        var list = string.Join(", ", changes);
        return $"Cần khởi động lại ứng dụng để áp dụng: {list}. "
            + "Camera và PLC được áp dụng ngay; các mục liệt kê ở trên sẽ có hiệu lực sau khi khởi động lại.";
    }

    /// <summary>Kiểu thông báo.</summary>
    private enum MessageKind
    {
        Info,
        Warning,
        Error,
    }
}
