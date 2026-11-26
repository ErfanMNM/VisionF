using System.Collections.ObjectModel;
using Avalonia.Threading;
using ClassLibraryMT.Scans;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace TanTienQR.ViewModels;

internal sealed partial class CodeLookupViewModel
{
    public ObservableCollection<CheckeeMonitorRowViewModel> PendingSendRows { get; } = [];
    public ObservableCollection<CheckeeMonitorRowViewModel> SentSendRows { get; } = [];
    public ObservableCollection<CheckeeMonitorRowViewModel> FailedSendRows { get; } = [];
    [ObservableProperty] public partial bool IsSendMonitorBusy { get; set; }
    [ObservableProperty] public partial string PendingSendCountText { get; set; } = "Mã đang chờ gửi (0)";
    [ObservableProperty] public partial string SentSendCountText { get; set; } = "Mã đã gửi (0)";
    [ObservableProperty] public partial string FailedSendCountText { get; set; } = "Mã gửi lỗi (0)";
    [ObservableProperty] public partial string SendMonitorSummary { get; set; } = "Chưa tải dữ liệu.";

    public bool SendingEnabled
    {
        get => _sendWorker.SendingEnabled;
        set { _sendWorker.SendingEnabled = value; OnPropertyChanged(); }
    }

    private void OnCheckeeStateChanged()
    {
        var epoch = _activationEpoch;
        Dispatcher.UIThread.Post(async () =>
        {
            if (!_isActive || _disposed || epoch != _activationEpoch) return;
            CheckeeStatusText = _sendWorker.State.Status;
            OnPropertyChanged(nameof(HasCheckeeError));
            OnPropertyChanged(nameof(SendingEnabled));
            await RefreshSendMonitorAsync().ConfigureAwait(true);
        });
    }

    [RelayCommand]
    public async Task RefreshSendMonitorAsync()
    {
        if (_disposed || IsSendMonitorBusy) return;
        IsSendMonitorBusy = true;
        try
        {
            var counts = await _sendStore.CountBySendStatusAsync().ConfigureAwait(true);
            await LoadRows(ScanSendStatuses.Pending, PendingSendRows).ConfigureAwait(true);
            await LoadRows(ScanSendStatuses.Sent, SentSendRows).ConfigureAwait(true);
            await LoadRows(ScanSendStatuses.Failed, FailedSendRows).ConfigureAwait(true);
            PendingSendCountText = $"Mã đang chờ gửi ({counts.GetValueOrDefault(ScanSendStatuses.Pending)})";
            SentSendCountText = $"Mã đã gửi ({counts.GetValueOrDefault(ScanSendStatuses.Sent)})";
            FailedSendCountText = $"Mã gửi lỗi ({counts.GetValueOrDefault(ScanSendStatuses.Failed)})";
            SendMonitorSummary = $"Mã Pass của mọi PO · tối đa {MaxErrorRows} dòng mỗi tab · cập nhật {DateTime.Now:HH:mm:ss}";
            CheckeeStatusText = _sendWorker.State.Status;
            OnPropertyChanged(nameof(HasCheckeeError));
            await LoadSendHistoryAsync().ConfigureAwait(true);
        }
        catch (Exception ex) { StatusMessage = "Không tải được monitor gửi Checkee: " + ex.Message; }
        finally { IsSendMonitorBusy = false; }
    }

    private async Task LoadRows(string status, ObservableCollection<CheckeeMonitorRowViewModel> target)
    {
        var records = await _sendStore.GetBySendStatusAsync(new ScanSendFilter
            { SendStatus = status, OnlyPass = true, Limit = MaxErrorRows }).ConfigureAwait(true);
        target.Clear();
        foreach (var record in records) target.Add(new(record));
    }
}

public sealed class CheckeeMonitorRowViewModel(ScanRecordEntity record)
{
    public string CodeText { get; } = record.CodeContent;
    public string OrderNoText { get; } = record.OrderNo;
    public string TimeText { get; } = (record.SendTimeUtc ?? record.SendAttemptUtc ?? record.ReceivedUtc)
        .ToLocalTime().ToString("dd/MM HH:mm:ss");
    public string StatusText { get; } = ScanSendStatuses.Normalize(record.SendStatus) switch
    {
        ScanSendStatuses.Sent => "Đã gửi",
        ScanSendStatuses.Failed => "Gửi lỗi",
        _ => "Đang chờ gửi",
    };
    public string DetailText { get; } = (record.SendStatusCode > 0 ? $"HTTP {record.SendStatusCode} · " : "")
        + (string.IsNullOrEmpty(record.SendMessage) ? "Chưa có lần gửi; chỉ gửi khi đúng phiên PO và GTIN." : record.SendMessage);
}
