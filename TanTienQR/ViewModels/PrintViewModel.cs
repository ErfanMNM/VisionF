using ClassLibraryMT.Print;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace TanTienQR.ViewModels;

internal sealed partial class PrintViewModel : ViewModelBase, IDisposable
{
    private readonly PrintService _print;
    private readonly IPrintLicenseGate _license;
    private readonly PrintOptions _options;
    private readonly CancellationTokenSource _cts = new();
    private bool _disposed;

    public PrintViewModel(PrintService print, IPrintLicenseGate license, PrintOptions options)
    {
        _print = print; _license = license; _options = options;
        Quantity = 100;
        RefreshLicense();
    }

    public bool CanUse => _license.CanUse;
    public bool CanConnect => CanUse && _options.Enabled;
    public string LicenseMessage => _license.Message;
    public string ModuleMessage => CanUse
        ? (_options.Enabled ? "Module máy in đã bật." : "Module máy in chưa được bật trong Cấu hình; thay đổi cần khởi động lại app.")
        : _license.Message;
    public string PrinterName => _options.Printers.FirstOrDefault(p => p.Id.Equals(_options.DefaultPrinterId, StringComparison.OrdinalIgnoreCase))?.Name ?? _print.Printer.PrinterId;
    public string PrinterEndpoint => _options.Printers.FirstOrDefault(p => p.Id.Equals(_options.DefaultPrinterId, StringComparison.OrdinalIgnoreCase)) is { } printer
        ? $"{printer.Protocol} · {printer.Ip}:{printer.Port} · buffer {printer.BufferCapacity}"
        : "Chưa cấu hình endpoint máy in";

    [ObservableProperty] public partial int Quantity { get; set; }
    [ObservableProperty] public partial bool IsBusy { get; set; }
    [ObservableProperty] public partial string StatusText { get; set; } = "Chưa kết nối máy in.";
    [ObservableProperty] public partial string OrderText { get; set; } = "Chưa có PO đang chạy.";
    [ObservableProperty] public partial string ConnectionText { get; set; } = "Chưa kết nối";
    [ObservableProperty] public partial string PrinterStatusText { get; set; } = "Trạng thái máy in: —";
    [ObservableProperty] public partial string InkText { get; set; } = "Mực: —";
    [ObservableProperty] public partial string QueueText { get; set; } = "Chưa có dữ liệu hàng đợi.";

    [RelayCommand]
    private async Task ConnectAsync()
    {
        if (!CanConnect || IsBusy) return;
        IsBusy = true;
        try
        {
            await _print.ConnectAsync(_cts.Token).ConfigureAwait(true);
            await RefreshAsync().ConfigureAwait(true);
            StatusText = "Đã kết nối máy in.";
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
    private async Task RefreshAsync()
    {
        RefreshLicense();
        if (!CanUse) return;
        if (!CanConnect)
        {
            StatusText = ModuleMessage;
            return;
        }
        try
        {
            var order = _print.CurrentOrder;
            OrderText = order is null ? "Chưa có PO đang chạy." : $"PO: {order.OrderNo} · {order.OrderQty:N0} mã";
            var status = await _print.RefreshStatusAsync(_cts.Token).ConfigureAwait(true);
            ConnectionText = status.Connection.ToString();
            PrinterStatusText = $"{(status.IsPrinting ? "Đang in" : "Đang chờ")} · buffer {status.BufferLength?.ToString() ?? "—"} · {status.Message}";
            InkText = $"Mực: {(status.Ink1Percent?.ToString("F1") ?? "—")}% / {(status.Ink2Percent?.ToString("F1") ?? "—")}%";
            var summary = await _print.GetSummaryAsync(_cts.Token).ConfigureAwait(true);
            QueueText = $"Chờ {summary.Pending:N0} · Đã gửi {summary.Sent:N0} · Đã in {summary.Printed:N0} · Không phản hồi {summary.NoResponse:N0}";
        }
        catch (Exception ex) { StatusText = ex.Message; }
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
        _cts.Cancel();
        _cts.Dispose();
    }
}
