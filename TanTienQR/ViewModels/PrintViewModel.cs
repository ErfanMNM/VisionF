using System.Collections.ObjectModel;
using System.Collections.Specialized;
using Avalonia.Threading;
using ClassLibraryMT.Print;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TanTienQR.Services;

namespace TanTienQR.ViewModels;

internal sealed partial class PrintViewModel : ViewModelBase, IDisposable
{
    private readonly PrintService _print;
    private readonly IPrintLicenseGate _license;
    private readonly PrintOptions _options;
    private readonly PrinterMonitorService _monitor;
    private readonly DispatcherTimer _timer;
    private readonly CancellationTokenSource _cts = new();
    private int _lastCommandVersion = -1;
    private bool _disposed;

    public PrintViewModel(
        PrintService print,
        IPrintLicenseGate license,
        PrintOptions options,
        PrinterMonitorService monitor,
        PrinterRemoteViewModel remote)
    {
        _print = print;
        _license = license;
        _options = options;
        _monitor = monitor;
        Remote = remote;
        Quantity = 100;

        CommandHistory.CollectionChanged += OnHistoryChanged;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _timer.Tick += OnMonitorTick;
        _timer.Start();

        RefreshLicense();
        PullMonitor();
    }

    public bool CanUse => _license.CanUse;
    public PrinterRemoteViewModel Remote { get; }
    public string JobText => string.IsNullOrWhiteSpace(_print.Printer.Status.Job) ? "Chưa xác định bản tin" : _print.Printer.Status.Job;
    public bool CanConnect => CanUse && _options.Enabled;
    public string LicenseMessage => _license.Message;
    public string ModuleMessage => CanUse
        ? (_options.Enabled ? "Module máy in đã bật. Tự kết nối đang chạy." : "Module máy in chưa được bật trong Cấu hình; thay đổi cần khởi động lại app.")
        : _license.Message;
    public string PrinterName => _options.Printers.FirstOrDefault(p => p.Id.Equals(_options.DefaultPrinterId, StringComparison.OrdinalIgnoreCase))?.Name ?? _print.Printer.PrinterId;
    public string PrinterEndpoint => _monitor.Snapshot.Endpoint;

    // ---- Trạng thái thiết bị: cùng mô hình LED với camera/PLC ----
    public bool IsPrinterConnected => ConnectionText == "Đã kết nối";
    public string PrinterLedClass => ConnectionText switch
    {
        "Đã kết nối" => "on",
        "Đang kết nối lại" => "warn",
        _ => "off",
    };
    public string PrinterCardClass => ConnectionText switch
    {
        "Đã kết nối" => "ok",
        "Đang kết nối lại" => "warn blink",
        _ => "fault",
    };
    public bool IsPrinterBlinking => ConnectionText == "Đang kết nối lại";

    [ObservableProperty] public partial int Quantity { get; set; }
    [ObservableProperty] public partial bool IsBusy { get; set; }
    [ObservableProperty] public partial string StatusText { get; set; } = "Chưa kết nối máy in.";
    [ObservableProperty] public partial string OrderText { get; set; } = "Chưa có PO đang chạy.";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPrinterConnected))]
    [NotifyPropertyChangedFor(nameof(PrinterLedClass))]
    [NotifyPropertyChangedFor(nameof(PrinterCardClass))]
    [NotifyPropertyChangedFor(nameof(IsPrinterBlinking))]
    public partial string ConnectionText { get; set; } = "Chưa kết nối";
    [ObservableProperty] public partial string PrinterStatusText { get; set; } = "Trạng thái máy in: —";
    [ObservableProperty] public partial string InkText { get; set; } = "Mực: —";
    [ObservableProperty] public partial string QueueText { get; set; } = "Chưa có dữ liệu hàng đợi.";

    // ---- Lịch sử lệnh G10 (hiện ở cột phải) ----
    public ObservableCollection<PrinterCommandLog> CommandHistory { get; } = new();
    public bool HasCommandHistory => CommandHistory.Count > 0;

    // ---- Điều khiển máy in ----
    [ObservableProperty] public partial bool IsControlBusy { get; set; }
    [ObservableProperty] public partial string ControlMessage { get; set; } = "";
    [ObservableProperty] public partial bool IsControlError { get; set; }

    /// <summary>Trạng thái in lấy từ lần đọc trạng thái gần nhất.</summary>
    public bool IsPrinting => _print.Printer.Status.IsPrinting;

    public bool HasControlMessage => !string.IsNullOrWhiteSpace(ControlMessage);

    partial void OnControlMessageChanged(string value) => OnPropertyChanged(nameof(HasControlMessage));

    private void OnHistoryChanged(object? sender, NotifyCollectionChangedEventArgs e) =>
        OnPropertyChanged(nameof(HasCommandHistory));

    /// <summary>Đồng bộ snapshot từ worker nền lên luồng UI.</summary>
    private void PullMonitor()
    {
        var snapshot = _monitor.Snapshot;
        ConnectionText = snapshot.StatusText;
        PrinterStatusText = snapshot.DetailText;
        InkText = snapshot.InkText;
        QueueText = snapshot.QueueText;
        OnPropertyChanged(nameof(PrinterEndpoint));
        OnPropertyChanged(nameof(IsPrinting));
        OnPropertyChanged(nameof(JobText));

        if (_monitor.CommandVersion == _lastCommandVersion) return;
        _lastCommandVersion = _monitor.CommandVersion;

        CommandHistory.Clear();
        foreach (var log in _monitor.RecentCommands)
            CommandHistory.Add(log);
    }

    [RelayCommand]
    private void ClearHistory()
    {
        _monitor.ClearHistory();
        _lastCommandVersion = _monitor.CommandVersion;
        CommandHistory.Clear();
    }

    [RelayCommand]
    private async Task ConnectAsync()
    {
        if (!CanConnect || IsBusy) return;
        IsBusy = true;
        try
        {
            if (await RefreshStatusCoreAsync().ConfigureAwait(true))
                StatusText = "Đã kết nối và đọc được trạng thái máy in.";
        }
        catch (Exception ex) { StatusText = ex.Message; }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task DisconnectAsync()
    {
        await _print.DisconnectAsync().ConfigureAwait(true);
        ConnectionText = "Đã ngắt kết nối";
        StatusText = "Đã ngắt kết nối máy in.";
    }

    [RelayCommand]
    private async Task RefreshAsync() => await RefreshStatusCoreAsync().ConfigureAwait(true);

    private async Task<bool> RefreshStatusCoreAsync()
    {
        RefreshLicense();
        if (!CanUse) return false;
        if (!CanConnect)
        {
            StatusText = ModuleMessage;
            return false;
        }
        try
        {
            var order = _print.CurrentOrder;
            OrderText = order is null ? "Chưa có PO đang chạy." : $"PO: {order.OrderNo} · {order.OrderQty:N0} mã";
            var status = await _print.RefreshStatusAsync(_cts.Token).ConfigureAwait(true);
            ConnectionText = status.Connection == PrinterConnectionState.Connected
                ? "Đã kết nối"
                : "Đang kết nối lại";
            PrinterStatusText = $"{(status.IsPrinting ? "Đang in" : "Đang chờ")} · buffer {status.BufferLength?.ToString() ?? "—"} · {status.Message}";
            InkText = $"Mực: {(status.Ink1Percent?.ToString("F1") ?? "—")}% / {(status.Ink2Percent?.ToString("F1") ?? "—")}%";
            var summary = await _print.GetSummaryAsync(_cts.Token).ConfigureAwait(true);
            QueueText = $"Chờ {summary.Pending:N0} · Đã gửi {summary.Sent:N0} · Đã in {summary.Printed:N0} · Không phản hồi {summary.NoResponse:N0}";
            OnPropertyChanged(nameof(IsPrinting));
            return status.Connection == PrinterConnectionState.Connected;
        }
        catch (Exception ex) { StatusText = ex.Message; return false; }
    }

    [RelayCommand]
    private async Task SendAsync()
    {
        if (!CanUse || IsBusy) return;
        if (Quantity <= 0) { StatusText = "Số lượng phải lớn hơn 0."; return; }
        IsBusy = true;
        try
        {
            var result = await _print.SendNextBatchAsync(Quantity, _cts.Token).ConfigureAwait(true);
            StatusText = result?.Message ?? "Không còn mã chưa in/chưa active để gửi.";
            await RefreshAsync().ConfigureAwait(true);
        }
        catch (Exception ex) { StatusText = ex.Message; }
        finally { IsBusy = false; }
    }

    // ---------- Lệnh điều khiển ----------

    [RelayCommand]
    private Task StartPrintingAsync() => RunControlAsync(ct => _print.StartPrintingAsync(ct));

    [RelayCommand]
    private Task PausePrintingAsync() => RunControlAsync(ct => _print.PausePrintingAsync(ct));

    /// <summary>Nút toggle: đang in thì tạm dừng, đang dừng thì bật in.</summary>
    [RelayCommand]
    private Task TogglePrintingAsync() =>
        IsPrinting ? PausePrintingAsync() : StartPrintingAsync();

    [RelayCommand]
    private Task ClearDataAsync() => RunControlAsync(ct => _print.ClearDataAsync(ct));

    [RelayCommand]
    private Task ResetCounterAsync() => RunControlAsync(ct => _print.ResetCounterAsync(ct));

    [RelayCommand]
    private Task CleanNozzleAsync() => RunControlAsync(ct => _print.CleanNozzleAsync(ct));

    [RelayCommand]
    private Task TriggerJetAsync() => RunControlAsync(ct => _print.TriggerJetAsync(ct));

    [RelayCommand]
    private Task RefreshFilesAsync() => RunControlAsync(ct => _print.RefreshFilesAsync(ct));

    private async Task RunControlAsync(Func<CancellationToken, Task<PrinterCommandResult>> action)
    {
        if (IsControlBusy || !CanUse || !CanConnect)
            return;

        IsControlBusy = true;
        try
        {
            var result = await action(_cts.Token).ConfigureAwait(true);
            ControlMessage = result.Message;
            IsControlError = !result.Ok;
            await RefreshAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            ControlMessage = ex.Message;
            IsControlError = true;
        }
        finally
        {
            IsControlBusy = false;
        }
    }

    public void RefreshLicense()
    {
        OnPropertyChanged(nameof(CanUse));
        OnPropertyChanged(nameof(CanConnect));
        OnPropertyChanged(nameof(LicenseMessage));
        OnPropertyChanged(nameof(ModuleMessage));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Remote.Dispose();
        _timer.Stop();
        _timer.Tick -= OnMonitorTick;
        _cts.Cancel();
        _cts.Dispose();
    }

    private void OnMonitorTick(object? sender, EventArgs e) => PullMonitor();
}
