using ClassLibraryMT.Auth;
using ClassLibraryMT.Logging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace TanTienQR.ViewModels;

internal partial class MainViewModel
{
    public string TotalScansText => TotalScans >= 0 ? TotalScans.ToString(ViCulture) : "—";
    public string PassCountText => PassCount >= 0 ? PassCount.ToString(ViCulture) : "—";
    public string FailCountText => FailCount >= 0 ? FailCount.ToString(ViCulture) : "—";
    public string PassRateText => TotalScans >= 0 ? PassRate.ToString("0.#", ViCulture) : "—";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ResetPlcCountersCommand))]
    public partial bool IsResettingPlcCounters { get; set; }

    public bool CanResetPlcCounters => !IsResettingPlcCounters && _session.IsAuthenticated
        && _session.Can(Permission.EditConfig) && _plcWriter.CanResetCounters;

    private void RefreshPlcCounters()
    {
        var counters = _plcWriter.LatestCounters;
        TotalScans = counters?.Total ?? -1;
        PassCount = counters?.Pass ?? -1;
        FailCount = counters?.Fail ?? -1;
        PassRate = TotalScans > 0 ? Math.Round(PassCount * 100.0 / TotalScans, 1) : 0;
        OnPropertyChanged(nameof(TotalScansText));
        OnPropertyChanged(nameof(PassCountText));
        OnPropertyChanged(nameof(FailCountText));
        OnPropertyChanged(nameof(PassRateText));
        OnPropertyChanged(nameof(CanResetPlcCounters));
        ResetPlcCountersCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanResetPlcCounters))]
    private async Task ResetPlcCountersAsync()
    {
        // Kiểm tra lại quyền khi thực thi; không tin riêng IsEnabled của nút.
        if (!CanResetPlcCounters) return;
        IsResettingPlcCounters = true;
        var detail = $"Total={TotalScansText}; Pass={PassCountText}; Fail={FailCountText}";
        try
        {
            var ok = await _plcWriter.ResetCountersAuthorizedAsync(
                () => _session.IsAuthenticated && _session.Can(Permission.EditConfig), _cts.Token);
            _actionLog.Log("Plc.ResetCounters", "PLC", detail,
                ok ? UserActionResults.Success : UserActionResults.Failed);
            AddEvent(ok ? "Đã gửi lệnh xóa số PLC; chờ PLC cập nhật bộ đếm."
                : "Không gửi được lệnh xóa số PLC.", ok ? EventLevel.Info : EventLevel.Warning);
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested) { }
        catch (Exception ex)
        {
            _actionLog.Log("Plc.ResetCounters", "PLC", detail + "; " + ex.Message, UserActionResults.Failed);
            AddEvent("Lỗi gửi lệnh xóa số PLC: " + ex.Message, EventLevel.Warning);
        }
        finally { IsResettingPlcCounters = false; }
    }
}
