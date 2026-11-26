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
    public string CounterSourceText => _plcWriter.CounterDisplaySource == "App" ? "APP" : "PLC";
    public string TotalScansLabel => "TỔNG SỐ · " + CounterSourceText;
    public string CounterSourceDescription => _plcWriter.CounterDisplaySource == "App"
        ? "Kết quả camera đã xử lý từ lúc mở ứng dụng; không phải tiến độ PO. Xóa số PLC không xóa số App."
        : "Số bộ đếm đọc từ PLC; cách cộng Timeout theo cấu hình PLC.";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ResetPlcCountersCommand))]
    public partial bool IsResettingPlcCounters { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ClearPlcErrorsCommand))]
    public partial bool IsClearingPlcErrors { get; set; }

    public bool CanResetPlcCounters => !IsResettingPlcCounters && _session.IsAuthenticated
        && _session.Can(Permission.EditConfig) && _plcWriter.CanResetCounters;
    public bool CanClearPlcErrors => !IsClearingPlcErrors && _session.IsAuthenticated
        && _session.Can(Permission.EditConfig) && _plcWriter.CanClearErrors;

    private void RefreshPlcCounters()
    {
        if (_plcWriter.CounterDisplaySource == "App")
        {
            var counters = _plcWriter.AppCounters;
            TotalScans = counters.Total;
            PassCount = counters.Pass;
            FailCount = counters.Fail;
        }
        else
        {
            var counters = _plcWriter.LatestCounters;
            TotalScans = counters?.Total ?? -1;
            PassCount = counters?.Pass ?? -1;
            FailCount = counters?.Fail ?? -1;
        }
        PassRate = TotalScans > 0 ? Math.Round(PassCount * 100.0 / TotalScans, 1) : 0;
        OnPropertyChanged(nameof(TotalScansText));
        OnPropertyChanged(nameof(PassCountText));
        OnPropertyChanged(nameof(FailCountText));
        OnPropertyChanged(nameof(PassRateText));
        OnPropertyChanged(nameof(CounterSourceText));
        OnPropertyChanged(nameof(TotalScansLabel));
        OnPropertyChanged(nameof(CounterSourceDescription));
        OnPropertyChanged(nameof(CanResetPlcCounters));
        OnPropertyChanged(nameof(CanClearPlcErrors));
        ResetPlcCountersCommand.NotifyCanExecuteChanged();
        ClearPlcErrorsCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanResetPlcCounters))]
    private async Task ResetPlcCountersAsync()
    {
        // Kiểm tra lại quyền khi thực thi; không tin riêng IsEnabled của nút.
        if (!CanResetPlcCounters) return;
        IsResettingPlcCounters = true;
        var detail = $"DisplaySource={CounterSourceText}; Total={TotalScansText}; Pass={PassCountText}; Fail={FailCountText}";
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

    [RelayCommand(CanExecute = nameof(CanClearPlcErrors))]
    private async Task ClearPlcErrorsAsync()
    {
        if (!CanClearPlcErrors) return;
        IsClearingPlcErrors = true;
        try
        {
            var ok = await _plcWriter.ClearErrorsAuthorizedAsync(
                () => _session.IsAuthenticated && _session.Can(Permission.EditConfig), _cts.Token);
            _actionLog.Log("Plc.ClearErrors", "PLC", "Gửi lệnh Clear lỗi PLC = 1",
                ok ? UserActionResults.Success : UserActionResults.Failed);
            AddEvent(ok ? "Đã gửi lệnh xóa lỗi PLC; cần kiểm tra trạng thái lỗi trên máy."
                : "Không gửi được lệnh xóa lỗi PLC.", ok ? EventLevel.Info : EventLevel.Warning);
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested) { }
        catch (Exception ex)
        {
            _actionLog.Log("Plc.ClearErrors", "PLC", ex.Message, UserActionResults.Failed);
            AddEvent("Lỗi gửi lệnh Clear PLC: " + ex.Message, EventLevel.Warning);
        }
        finally { IsClearingPlcErrors = false; }
    }
}
