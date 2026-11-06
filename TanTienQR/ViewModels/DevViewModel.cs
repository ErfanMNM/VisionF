using System.Diagnostics;
using System.Globalization;
using Avalonia.Threading;
using ClassLibraryMT.DeviceStatus;
using ClassLibraryMT.Logging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using TanTienQR.Services;

namespace TanTienQR.ViewModels;

/// <summary>
/// ViewModel cho tab "Dev" — bảng theo dõi trạng thái runtime.
///
/// Hiển thị trực tiếp (không lưu file, không ghi DB):
/// - Trạng thái global (<see cref="GlobalVariables"/>).
/// - Bộ đếm runtime (<see cref="RuntimeCounters"/>).
/// - Ảnh chụp môi trường + cấu hình đọc được.
/// - Danh sách service đã đăng ký trong DI.
/// - Tail log kỹ thuật từ bảng <c>AppLogs</c>.
/// - Số liệu bộ nhớ + nút giải phóng RAM.
///
/// Hai timer: 1 giây (state + bộ đếm + môi trường + DI + bộ nhớ) và 3 giây
/// (tail log). Cả hai CHỈ chạy khi tab Dev đang mở (<see cref="Activate"/> /
/// <see cref="Deactivate"/>) nên không tăng tải khi người dùng ở tab khác.
/// </summary>
internal sealed partial class DevViewModel : ViewModelBase, IDisposable
{
    /// <summary>Chu kỳ refresh trạng thái / bộ đếm / môi trường / bộ nhớ (ms).</summary>
    private const int StateRefreshMs = 1000;

    /// <summary>Chu kỳ refresh tail log (ms).</summary>
    private const int LogRefreshMs = 3000;

    /// <summary>Số dòng log tối đa tải về mỗi lần.</summary>
    private const int LogPageSize = 200;

    private static readonly CultureInfo ViCulture = CreateViCulture();

    private readonly RuntimeCounters _counters;
    private readonly DevServiceRegistry _registry;
    private readonly ILogQueryService _query;
    private readonly IConfiguration _config;
    private readonly IUserActionLogger _actionLog;

    private DispatcherTimer? _stateTimer;
    private DispatcherTimer? _logTimer;

    /// <summary>Cờ chống 2 lần tải log chồng nhau.</summary>
    private bool _logLoading;

    /// <summary>Cờ đánh dấu đã dispose.</summary>
    private bool _disposed;

    /// <summary>
    /// Cờ báo đã khởi tạo xong.
    ///
    /// Các property có sẵn giá trị khởi tạo (VD <c>LogCategoryFilter = ""</c>)
    /// được gán TRƯỚC constructor body — nên partial method gọi vào lúc đó sẽ
    /// chạy khi dependency còn là <c>null</c>. Cờ này chặn cho tới khi mọi
    /// dependency đã sẵn sàng.
    /// </summary>
    private bool _initialized;

    public DevViewModel(
        RuntimeCounters counters,
        DevServiceRegistry registry,
        ILogQueryService query,
        IConfiguration config,
        IUserActionLogger actionLog)
    {
        _counters = counters;
        _registry = registry;
        _query = query;
        _config = config;
        _actionLog = actionLog;

        BuildServiceRows();
        BuildEnvironmentRows();
        BuildConfigRows();
        RefreshState();
        RefreshMemory();

        // Mặc định không lọc theo mức -> ComboBox hiện "Tất cả".
        // Đặt SAU khi các bảng đã dựng xong để không tải log 2 lần.
        _initialized = true;
        SelectedLogLevel = LogLevelOptions[0];
    }

    // ---------- Trạng thái global ----------

    /// <summary>Các biến trạng thái global (app, camera, PLC, license).</summary>
    [ObservableProperty]
    public partial IReadOnlyList<DevKeyValueRow> GlobalStates { get; set; } = [];

    /// <summary>Danh sách lỗi hiện tại của app.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<DevKeyValueRow> CurrentErrors { get; set; } = [];

    /// <summary>True khi không có lỗi nào (dùng để hiện thông báo).</summary>
    public bool HasNoErrors => CurrentErrors.Count == 0;

    // ---------- Bộ đếm runtime ----------

    /// <summary>Các số liệu bộ đếm runtime.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<DevKeyValueRow> Counters { get; set; } = [];

    /// <summary>Thời gian app đã chạy.</summary>
    [ObservableProperty]
    public partial string UptimeText { get; set; } = "—";

    // ---------- Môi trường & cấu hình ----------

    /// <summary>Các dòng môi trường chạy ứng dụng.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<DevKeyValueRow> EnvironmentRows { get; set; } = [];

    /// <summary>Các dòng cấu hình đọc được (key = value).</summary>
    [ObservableProperty]
    public partial IReadOnlyList<DevKeyValueRow> ConfigRows { get; set; } = [];

    // ---------- Bộ nhớ ----------

    /// <summary>Các dòng số liệu bộ nhớ.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<DevKeyValueRow> MemoryRows { get; set; } = [];

    /// <summary>Tóm tắt bộ nhớ (dòng đầu panel).</summary>
    [ObservableProperty]
    public partial string MemorySummary { get; set; } = "—";

    // ---------- DI ----------

    /// <summary>Các dòng service đã đăng ký.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<DevServiceRow> ServiceRows { get; set; } = [];

    /// <summary>Tổng số service đã đăng ký (trước khi lọc).</summary>
    [ObservableProperty]
    public partial int TotalServiceCount { get; set; }

    /// <summary>Ô lọc danh sách service theo tên.</summary>
    [ObservableProperty]
    public partial string ServiceFilter { get; set; } = string.Empty;

    // ---------- Log kỹ thuật ----------

    /// <summary>Các dòng log kỹ thuật.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<DevLogRow> LogRows { get; set; } = [];

    /// <summary>Các mức log có thể chọn để lọc.</summary>
    public IReadOnlyList<DevLogLevelOption> LogLevelOptions { get; } =
    [
        new("Tất cả", null),
        new("Trace", LogLevel.Trace),
        new("Debug", LogLevel.Debug),
        new("Information", LogLevel.Information),
        new("Warning", LogLevel.Warning),
        new("Error", LogLevel.Error),
        new("Critical", LogLevel.Critical),
    ];

    /// <summary>Mức log đang chọn (null = tất cả).</summary>
    [ObservableProperty]
    public partial DevLogLevelOption? SelectedLogLevel { get; set; }

    /// <summary>Lọc theo category (chuỗi con, để trống = tất cả).</summary>
    [ObservableProperty]
    public partial string LogCategoryFilter { get; set; } = string.Empty;

    /// <summary>Lọc theo nội dung log (chuỗi con, để trống = tất cả).</summary>
    [ObservableProperty]
    public partial string LogMessageFilter { get; set; } = string.Empty;

    /// <summary>Tóm tắt số bản ghi log đang hiển thị.</summary>
    [ObservableProperty]
    public partial string LogSummary { get; set; } = "Chưa tải";

    /// <summary>True khi tự động làm mới log đang bật.</summary>
    [ObservableProperty]
    public partial bool IsLogAutoRefresh { get; set; } = true;

    // ---------- Thông tin chung ----------

    /// <summary>Phiên bản ứng dụng.</summary>
    public string AppVersionText { get; } = BuildAppVersion();

    /// <summary>Đường dẫn thư mục ứng dụng.</summary>
    public string AppDirectory => AppContext.BaseDirectory;

    // ---------- Vòng đời ----------

    /// <summary>
    /// Bật timer refresh (gọi khi người dùng mở tab Dev).
    /// </summary>
    public void Activate()
    {
        if (_disposed)
        {
            return;
        }

        if (_stateTimer is null)
        {
            _stateTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(StateRefreshMs),
            };
            _stateTimer.Tick += (_, _) => TickState();
        }

        if (_logTimer is null)
        {
            _logTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(LogRefreshMs),
            };
            _logTimer.Tick += (_, _) => _ = RefreshLogsAsync();
        }

        _stateTimer.Start();
        _logTimer.Start();

        // Cập nhật ngay để không phải chờ hết nhịp đầu tiên.
        TickState();
        _ = RefreshLogsAsync();
    }

    /// <summary>
    /// Tắt timer refresh (gọi khi người dùng rời khỏi tab Dev).
    /// </summary>
    public void Deactivate()
    {
        _stateTimer?.Stop();
        _logTimer?.Stop();
    }

    /// <summary>
    /// Giải phóng tài nguyên (timer).
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        _stateTimer?.Stop();
        _stateTimer = null;

        _logTimer?.Stop();
        _logTimer = null;

        GC.SuppressFinalize(this);
    }

    // ---------- Lệnh ----------

    /// <summary>Làm mới toàn bộ dữ liệu của tab Dev.</summary>
    [RelayCommand]
    private void RefreshAll()
    {
        TickState();
        _ = RefreshLogsAsync();
    }

    /// <summary>Xoá ô lọc danh sách service.</summary>
    [RelayCommand]
    private void ClearServiceFilter() => ServiceFilter = string.Empty;

    /// <summary>Tải lại log kỹ thuật theo bộ lọc hiện tại.</summary>
    [RelayCommand]
    private async Task RefreshLogsAsync()
    {
        if (_logLoading)
        {
            return;
        }

        _logLoading = true;

        try
        {
            var filter = new AppLogFilter
            {
                MinLevel = SelectedLogLevel?.Level,
                CategoryContains = string.IsNullOrWhiteSpace(LogCategoryFilter)
                    ? null
                    : LogCategoryFilter.Trim(),
                MessageContains = string.IsNullOrWhiteSpace(LogMessageFilter)
                    ? null
                    : LogMessageFilter.Trim(),
            };

            var result = await _query
                .GetAppLogsAsync(filter, page: 1, pageSize: LogPageSize)
                .ConfigureAwait(true);

            var rows = new List<DevLogRow>(result.Items.Count);
            foreach (var entry in result.Items)
            {
                rows.Add(new DevLogRow(
                    entry.TimestampUtc,
                    entry.Level,
                    entry.Category,
                    entry.Message));
            }

            LogRows = rows;
            LogSummary = $"{result.TotalCount} bản ghi · hiện {rows.Count} dòng mới nhất";
        }
        catch (Exception ex)
        {
            LogSummary = "Không đọc được log: " + ex.Message;
        }
        finally
        {
            _logLoading = false;
        }
    }

    /// <summary>
    /// Giải phóng bộ nhớ: thu gom toàn bộ heap và nén lại.
    ///
    /// Gọi 2 vòng: vòng 1 thu gom, chờ finalizer chạy xong (object có
    /// finalizer giữ bộ nhớ tới khi finalizer chạy), vòng 2 thu gom lại phần
    /// vừa được giải phóng.
    ///
    /// Chỉ gọi thủ công khi người dùng bấm nút — không tự động, vì lúc app đang
    /// chạy (heartbeat 1s, luồng camera) <c>GC.Collect</c> tạo pause nhìn thấy.
    /// </summary>
    [RelayCommand]
    private void CollectGarbage()
    {
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);

        var message = $"Đã giải phóng bộ nhớ lúc {DateTime.Now:HH:mm:ss}.";
        MemorySummary = message;

        RefreshMemory();

        _actionLog.Log(
            "Dev.GC.Collect",
            "Memory",
            message,
            UserActionResults.Success);
    }

    // ---------- Refresh nội bộ ----------

    /// <summary>
    /// Nhịp 1 giây: cập nhật trạng thái global, bộ đếm và bộ nhớ.
    /// </summary>
    private void TickState()
    {
        if (_disposed)
        {
            return;
        }

        RefreshState();
        RefreshMemory();
    }

    private void RefreshState()
    {
        GlobalStates = BuildGlobalStateRows();
        CurrentErrors = BuildErrorRows();
        Counters = BuildCounterRows();
        UptimeText = _counters.UptimeText;

        OnPropertyChanged(nameof(HasNoErrors));
    }

    private void RefreshMemory()
    {
        MemoryRows = BuildMemoryRows();
        MemorySummary = BuildMemorySummary();
    }

    // ---------- Dựng dòng ----------

    private IReadOnlyList<DevKeyValueRow> BuildGlobalStateRows()
    {
        var rows = new List<DevKeyValueRow>(8);

        var appState = GlobalVariables.CurrentAppState;
        rows.Add(new DevKeyValueRow(
            "CurrentAppState",
            appState.ToString(),
            DescribeAppState(appState),
            appState == e_AppState.Ready ? "running" : appState == e_AppState.Error ? "fault" : "warning"));

        var camera = GlobalVariables.CameraStatus;
        rows.Add(new DevKeyValueRow(
            "CameraStatus",
            camera.ToString(),
            null,
            camera == CameraStatus.Connected ? "running" : "warning"));

        var plc = GlobalVariables.PLCStatus;
        rows.Add(new DevKeyValueRow(
            "PLCStatus",
            plc.ToString(),
            null,
            plc == PLCStatus.Connected ? "running" : "fault"));

        rows.Add(new DevKeyValueRow(
            "Ready",
            GlobalVariables.Ready.ToString(),
            "Giá trị ghi xuống PLC (D16)",
            GlobalVariables.Ready != 0 ? "running" : "warning"));

        rows.Add(new DevKeyValueRow(
            "LicenseValid",
            GlobalVariables.LicenseValid ? "true" : "false",
            null,
            GlobalVariables.LicenseValid ? "running" : "fault"));

        rows.Add(new DevKeyValueRow(
            "Errors.Count",
            GlobalVariables.Errors.Count.ToString(),
            "Số lỗi đang lưu (tối đa 50)",
            GlobalVariables.Errors.Count > 0 ? "fault" : "running"));

        return rows;
    }

    private IReadOnlyList<DevKeyValueRow> BuildErrorRows()
    {
        var errors = GlobalVariables.Errors;

        if (errors.Count == 0)
        {
            return [];
        }

        var rows = new List<DevKeyValueRow>(errors.Count);
        foreach (var error in errors)
        {
            rows.Add(new DevKeyValueRow(
                error.Code.ToString(),
                error.Message,
                error.OccurredUtc.ToLocalTime().ToString("dd/MM/yyyy HH:mm:ss", ViCulture),
                "fault"));
        }

        return rows;
    }

    private IReadOnlyList<DevKeyValueRow> BuildCounterRows()
    {
        var heartbeatFail = _counters.HeartbeatFailures;
        var resultFail = _counters.ResultFailures;

        return
        [
            new DevKeyValueRow(
                "HeartbeatWrites",
                _counters.HeartbeatWrites.ToString("N0", ViCulture),
                "Số lần ghi D16 xuống PLC",
                heartbeatFail > 0 ? "warning" : "running"),
            new DevKeyValueRow(
                "HeartbeatFailures",
                heartbeatFail.ToString("N0", ViCulture),
                "Số lần ghi D16 thất bại",
                heartbeatFail > 0 ? "fault" : "running"),
            new DevKeyValueRow(
                "ResultWrites",
                _counters.ResultWrites.ToString("N0", ViCulture),
                "Số lần ghi D12 xuống PLC",
                resultFail > 0 ? "warning" : "running"),
            new DevKeyValueRow(
                "ResultFailures",
                resultFail.ToString("N0", ViCulture),
                "Số lần ghi D12 thất bại",
                resultFail > 0 ? "fault" : "running"),
            new DevKeyValueRow(
                "ScansTotal",
                _counters.ScansTotal.ToString("N0", ViCulture),
                "Tổng số mã đã quét"),
            new DevKeyValueRow(
                "ScansPass",
                _counters.ScansPass.ToString("N0", ViCulture),
                "Số mã hợp lệ",
                "running"),
            new DevKeyValueRow(
                "ScansFail",
                _counters.ScansFail.ToString("N0", ViCulture),
                "Số mã lỗi",
                _counters.ScansFail > 0 ? "warning" : ""),
            new DevKeyValueRow(
                "Uptime",
                _counters.UptimeText,
                "Thời gian app đã chạy"),
        ];
    }

    private void BuildEnvironmentRows()
    {
        EnvironmentRows =
        [
            new DevKeyValueRow("OS", Environment.OSVersion.ToString()),
            new DevKeyValueRow("Runtime", Environment.Version.ToString()),
            new DevKeyValueRow("Process 64-bit", Environment.Is64BitProcess ? "true" : "false"),
            new DevKeyValueRow("CPU count", Environment.ProcessorCount.ToString()),
            new DevKeyValueRow("Machine name", Environment.MachineName),
            new DevKeyValueRow("User name", Environment.UserName),
            new DevKeyValueRow("Working set (.NET)", FormatBytes(Environment.WorkingSet)),
            new DevKeyValueRow("Base directory", AppContext.BaseDirectory),
            new DevKeyValueRow("Config path", ConfigStore.ConfigPath),
            new DevKeyValueRow("License path", LicenseManager.LicensePath),
        ];
    }

    private void BuildConfigRows()
    {
        var rows = new List<DevKeyValueRow>();

        // AsEnumerable() trả về cả key rỗng (gốc cây) -> bỏ, chỉ giữ key thật.
        foreach (var item in _config.AsEnumerable().OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            if (string.IsNullOrEmpty(item.Key))
            {
                continue;
            }

            rows.Add(new DevKeyValueRow(item.Key, item.Value ?? "—"));
        }

        ConfigRows = rows;
    }

    private IReadOnlyList<DevKeyValueRow> BuildMemoryRows()
    {
        long workingSet = 0;
        long peakWorkingSet = 0;

        try
        {
            using var process = Process.GetCurrentProcess();
            workingSet = process.WorkingSet64;
            peakWorkingSet = process.PeakWorkingSet64;
        }
        catch (InvalidOperationException)
        {
            // Process đã kết thúc -> để 0.
        }
        catch (PlatformNotSupportedException)
        {
            // Nền tảng không hỗ trợ -> để 0.
        }

        return
        [
            new DevKeyValueRow("RAM (process working set)", FormatBytes(workingSet),
                "Bộ nhớ thật process đang chiếm trong RAM"),
            new DevKeyValueRow("RAM (peak working set)", FormatBytes(peakWorkingSet),
                "Đỉnh cao nhất từng ghi nhận"),
            new DevKeyValueRow("Managed heap", FormatBytes(GC.GetTotalMemory(false)),
                "Bộ nhớ managed đang dùng"),
            new DevKeyValueRow("Total allocated", FormatBytes(GC.GetTotalAllocatedBytes()),
                "Tổng số byte đã cấp phát từ đầu app"),
            new DevKeyValueRow("GC gen 0", GC.CollectionCount(0).ToString("N0", ViCulture)),
            new DevKeyValueRow("GC gen 1", GC.CollectionCount(1).ToString("N0", ViCulture)),
            new DevKeyValueRow("GC gen 2", GC.CollectionCount(2).ToString("N0", ViCulture)),
            new DevKeyValueRow("GC latency mode", System.Runtime.GCSettings.LatencyMode.ToString()),
            new DevKeyValueRow("CPU time", RuntimeCounters.GetCpuTime().ToString("c", ViCulture),
                "Tổng thời gian CPU đã dùng"),
        ];
    }

    private string BuildMemorySummary()
    {
        long workingSet = 0;
        long managed = GC.GetTotalMemory(false);

        try
        {
            using var process = Process.GetCurrentProcess();
            workingSet = process.WorkingSet64;
        }
        catch (InvalidOperationException)
        {
            // Bỏ qua.
        }
        catch (PlatformNotSupportedException)
        {
            // Bỏ qua.
        }

        return $"RAM {FormatBytes(workingSet)} · Managed {FormatBytes(managed)} · GC gen2 {GC.CollectionCount(2)} lần";
    }

    private void BuildServiceRows()
    {
        var descriptors = _registry.Descriptors;
        TotalServiceCount = descriptors.Count;

        var rows = new List<DevServiceRow>(descriptors.Count);
        foreach (var descriptor in descriptors)
        {
            rows.Add(new DevServiceRow(
                descriptor.ServiceType.Name,
                descriptor.Lifetime.ToString(),
                DevServiceRegistry.DescribeImplementation(descriptor)));
        }

        ServiceRows = rows;
    }

    // ---------- Helper ----------

    /// <summary>
    /// Áp dụng bộ lọc danh sách service (theo tên service hoặc implementation).
    /// </summary>
    partial void OnServiceFilterChanged(string value)
    {
        if (_initialized)
        {
            ApplyServiceFilter();
        }
    }

    private void ApplyServiceFilter()
    {
        var filter = ServiceFilter?.Trim() ?? string.Empty;

        if (filter.Length == 0)
        {
            ServiceRows = _registry.Descriptors
                .Select(d => new DevServiceRow(
                    d.ServiceType.Name,
                    d.Lifetime.ToString(),
                    DevServiceRegistry.DescribeImplementation(d)))
                .ToList();
            return;
        }

        ServiceRows = _registry.Descriptors
            .Where(d =>
                d.ServiceType.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)
                || DevServiceRegistry.DescribeImplementation(d)
                    .Contains(filter, StringComparison.OrdinalIgnoreCase))
            .Select(d => new DevServiceRow(
                d.ServiceType.Name,
                d.Lifetime.ToString(),
                DevServiceRegistry.DescribeImplementation(d)))
            .ToList();
    }

    /// <summary>
    /// Bộ lọc log thay đổi -> tải lại ngay (không chờ nhịp timer).
    /// </summary>
    partial void OnSelectedLogLevelChanged(DevLogLevelOption? value)
    {
        if (_initialized)
        {
            _ = RefreshLogsAsync();
        }
    }

    partial void OnLogCategoryFilterChanged(string value)
    {
        if (_initialized)
        {
            _ = RefreshLogsAsync();
        }
    }

    partial void OnLogMessageFilterChanged(string value)
    {
        if (_initialized)
        {
            _ = RefreshLogsAsync();
        }
    }

    /// <summary>
    /// Bật/tắt tự động làm mới log.
    /// </summary>
    partial void OnIsLogAutoRefreshChanged(bool value)
    {
        if (!_initialized)
        {
            return;
        }

        if (value)
        {
            _logTimer?.Start();
        }
        else
        {
            _logTimer?.Stop();
        }
    }

    private static string DescribeAppState(e_AppState state) => state switch
    {
        e_AppState.Ready => "Camera + PLC + license đều sẵn sàng",
        e_AppState.Stopped => "Mất kết nối thiết bị",
        e_AppState.Error => "Lỗi (thường là license không hợp lệ)",
        _ => "Đang khởi tạo",
    };

    /// <summary>Định dạng số byte thành KB / MB / GB.</summary>
    private static string FormatBytes(long bytes)
    {
        if (bytes <= 0)
        {
            return "0 B";
        }

        const double kb = 1024;
        const double mb = kb * 1024;
        const double gb = mb * 1024;

        if (bytes >= gb)
        {
            return $"{bytes / gb:F2} GB";
        }

        if (bytes >= mb)
        {
            return $"{bytes / mb:F1} MB";
        }

        if (bytes >= kb)
        {
            return $"{bytes / kb:F0} KB";
        }

        return $"{bytes} B";
    }

    private static string BuildAppVersion()
    {
        var version = typeof(DevViewModel).Assembly.GetName().Version;
        return version is null ? "—" : version.ToString();
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
}

/// <summary>
/// Một mức log có thể chọn trong bộ lọc của tab Dev.
/// </summary>
/// <param name="Label">Tên hiển thị.</param>
/// <param name="Level">Mức log tương ứng (null = không lọc).</param>
public sealed record DevLogLevelOption(string Label, LogLevel? Level)
{
    /// <summary>Tên hiển thị (dùng cho ComboBox).</summary>
    public override string ToString() => Label;
}
