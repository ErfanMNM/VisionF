using System.Collections.ObjectModel;
using System.Globalization;
using ClassLibraryMT.Scans;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace TanTienQR.ViewModels;

internal sealed partial class CodeLookupViewModel
{
    public ObservableCollection<CheckeeSendHistoryRowViewModel> SendHistoryRows { get; } = [];
    public ObservableCollection<CheckeeSendDetailRowViewModel> SendDetailRows { get; } = [];
    [ObservableProperty]
    public partial bool IsSendHistoryBusy { get; set; }
    [ObservableProperty]
    public partial bool IsSendDetailsOpen { get; set; }
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SendDetailTitle))]
    public partial CheckeeSendHistoryRowViewModel? SelectedSendAttempt { get; set; }
    private DateTime? _lastHistoryRunUtc;
    private long _detailRequest;
    public bool HasSendHistory => SendHistoryRows.Count > 0;
    public string SendHistoryCountText => $"{SendHistoryRows.Count}/60 lần gần nhất · Nhấn để xem từng mã";
    public string SendDetailTitle => SelectedSendAttempt is { } row
        ? $"Chi tiết lần gửi #{row.AttemptId} · {row.TimeText} · {row.CountText}" : "Chi tiết lần gửi";

    [RelayCommand]
    public async Task LoadSendHistoryAsync()
    {
        if (IsSendHistoryBusy || _disposed) return;
        IsSendHistoryBusy = true;
        var lastRun = _sendWorker.State.LastRunUtc;
        try
        {
            var attempts = await _sendStore.GetRecentSendAttemptsAsync(60).ConfigureAwait(true);
            SendHistoryRows.Clear();
            foreach (var attempt in attempts) SendHistoryRows.Add(new(attempt));
            _lastHistoryRunUtc = lastRun;
            OnPropertyChanged(nameof(HasSendHistory));
            OnPropertyChanged(nameof(SendHistoryCountText));
            // Cùng một lần gửi có thể được mở lúc đang chờ ACK. Khi worker
            // hoàn tất, cập nhật cả tiêu đề và chi tiết đang mở theo snapshot mới.
            if (IsSendDetailsOpen && SelectedSendAttempt is { } selected)
            {
                var refreshed = SendHistoryRows.FirstOrDefault(row => row.AttemptId == selected.AttemptId);
                if (refreshed is not null) await ShowSendAttemptAsync(refreshed).ConfigureAwait(true);
            }
        }
        catch (Exception ex) { StatusMessage = "Không tải được lịch sử gửi Checkee: " + ex.Message; }
        finally { IsSendHistoryBusy = false; }
    }

    [RelayCommand]
    public async Task ShowSendAttemptAsync(CheckeeSendHistoryRowViewModel? row)
    {
        if (row is null) return;
        var request = ++_detailRequest;
        SelectedSendAttempt = row;
        SendDetailRows.Clear();
        IsSendDetailsOpen = true;
        try
        {
            var items = await _sendStore.GetSendAttemptItemsAsync(row.AttemptId).ConfigureAwait(true);
            if (request != _detailRequest || !IsSendDetailsOpen) return;
            foreach (var item in items) SendDetailRows.Add(new(item));
            if (items.Count == 0) StatusMessage = "Lần gửi này đã ra ngoài 60 lần được lưu gần nhất. Hãy làm mới lịch sử.";
        }
        catch (Exception ex) { StatusMessage = "Không tải được chi tiết lần gửi: " + ex.Message; }
    }

    [RelayCommand]
    private void CloseSendDetails()
    {
        _detailRequest++;
        IsSendDetailsOpen = false;
    }
}

public sealed class CheckeeSendHistoryRowViewModel(CheckeeSendAttempt attempt)
{
    public long AttemptId { get; } = attempt.Id;
    public string TimeText { get; } = attempt.StartedUtc.ToLocalTime().ToString("dd/MM/yyyy HH:mm:ss", CultureInfo.InvariantCulture);
    public string BatchText { get; } = attempt.BatchCode;
    public string ProductGtin { get; } = attempt.ProductGtin;
    public string CountText { get; } = $"{attempt.TotalCount} mã · OK {attempt.OkCount} · Fail {attempt.FailCount}"
        + (attempt.UnknownCount > 0 ? $" · Chưa xác định {attempt.UnknownCount}" : string.Empty);
    public string OkText { get; } = $"OK {attempt.OkCount}";
    public string FailText { get; } = $"Fail {attempt.FailCount}";
    public string StatusText { get; } = CheckeeSendDetailRowViewModel.Describe(attempt.Status);
    public string Message { get; } = attempt.Message;
    public bool IsOk { get; } = attempt.Status == CheckeeSendAttemptStatuses.Sent;
}

public sealed class CheckeeSendDetailRowViewModel(CheckeeSendAttemptItem item)
{
    public string CodeText { get; } = item.CodeContent;
    public string OrderNoText { get; } = item.OrderNo;
    public string StatusText { get; } = Describe(item.Status);
    public string HttpText { get; } = item.StatusCode > 0 ? $"HTTP {item.StatusCode}" : "—";
    public string Message { get; } = item.Message;
    public bool IsOk { get; } = item.Status == CheckeeSendAttemptStatuses.Sent;
    public static string Describe(string status) => status switch
    {
        CheckeeSendAttemptStatuses.Sent => "OK",
        CheckeeSendAttemptStatuses.Failed => "Fail",
        CheckeeSendAttemptStatuses.RetryPending => "Fail · Chờ thử lại",
        _ => "Chưa xác định",
    };
}
