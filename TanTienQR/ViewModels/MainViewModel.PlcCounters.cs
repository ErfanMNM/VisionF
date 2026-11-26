using ClassLibraryMT.Auth;
using ClassLibraryMT.DeviceStatus;
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
    public string ScanHistoryCountText => CounterSourceText + ": " + TotalScansText;
    public string CounterSourceDescription => _plcWriter.CounterDisplaySource == "App"
        ? "Kết quả camera đã xử lý từ lúc mở ứng dụng; không phải tiến độ PO. Xóa số PLC không xóa số App."
        : "Số bộ đếm đọc từ PLC; cách cộng Timeout theo cấu hình PLC.";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanResetPlcCounters), nameof(CanClearPlcErrors),
        nameof(ResetPlcCountersHint), nameof(ClearPlcErrorsHint), nameof(PlcActionStatusText))]
    [NotifyCanExecuteChangedFor(nameof(ResetPlcCountersCommand))]
    [NotifyCanExecuteChangedFor(nameof(ClearPlcErrorsCommand))]
    public partial bool IsResettingPlcCounters { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanResetPlcCounters), nameof(CanClearPlcErrors),
        nameof(ResetPlcCountersHint), nameof(ClearPlcErrorsHint), nameof(PlcActionStatusText))]
    [NotifyCanExecuteChangedFor(nameof(ResetPlcCountersCommand))]
    [NotifyCanExecuteChangedFor(nameof(ClearPlcErrorsCommand))]
    public partial bool IsClearingPlcErrors { get; set; }

    public bool CanResetPlcCounters => PlcActionBlockedReason(clearErrors: false).Length == 0;
    public bool CanClearPlcErrors => PlcActionBlockedReason(clearErrors: true).Length == 0;
    public string ResetPlcCountersHint => CanResetPlcCounters
        ? "Ghi reset=1 tới địa chỉ PLC đã lưu; không xóa số App."
        : "Xóa số PLC: " + PlcActionBlockedReason(clearErrors: false);
    public string ClearPlcErrorsHint => CanClearPlcErrors
        ? "Chỉ gửi Clear=1 tới địa chỉ PLC đã lưu khi nhấn nút; kiểm tra lại lỗi trên máy."
        : "Xóa lỗi PLC: " + PlcActionBlockedReason(clearErrors: true);
    public string PlcActionStatusText
    {
        get
        {
            var reset = PlcActionBlockedReason(clearErrors: false);
            var clear = PlcActionBlockedReason(clearErrors: true);
            if (reset.Length == 0 && clear.Length == 0)
                return "Lệnh PLC đã sẵn sàng; chỉ gửi khi nhấn nút. Xóa số PLC không xóa số App.";
            if (reset == clear) return reset;
            return string.Join(" · ", new[] { reset.Length == 0 ? null : "Xóa số: " + reset,
                clear.Length == 0 ? null : "Xóa lỗi: " + clear }.Where(reason => reason is not null));
        }
    }

    private bool HasPlcActionAuthorityAndConnection => _session.IsAuthenticated
        && _session.Can(Permission.EditConfig) && GlobalVariables.PLCStatus == PLCStatus.Connected;

    private string PlcActionBlockedReason(bool clearErrors)
    {
        if (!_session.IsAuthenticated) return "Đăng nhập tài khoản có quyền sửa cấu hình để gửi lệnh PLC.";
        if (!_session.Can(Permission.EditConfig)) return "Tài khoản không có quyền sửa cấu hình / gửi lệnh PLC.";
        if (IsResettingPlcCounters || IsClearingPlcErrors) return "Đang gửi lệnh PLC; vui lòng chờ hoàn tất.";
        if (!_plcWriter.Enabled) return "PLC đang tắt trong cấu hình đã áp dụng.";
        if (_plcWriter.IsResetSuspended) return "PLC tạm khóa thao tác trong khi bảo trì hoặc áp dụng cấu hình.";
        if (!(clearErrors ? _plcWriter.HasClearErrorAddress : _plcWriter.HasResetCounterAddress))
            return clearErrors ? "Chưa cấu hình D Clear (ClearErrorAddress) của máy."
                : "Chưa cấu hình D reset counter (ResetCounterAddress) của máy.";
        if (GlobalVariables.PLCStatus != PLCStatus.Connected) return "PLC chưa kết nối; chờ trạng thái kết nối thực tế.";
        return string.Empty;
    }

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
        OnPropertyChanged(nameof(ScanHistoryCountText));
        OnPropertyChanged(nameof(CounterSourceDescription));
        OnPropertyChanged(nameof(CanResetPlcCounters));
        OnPropertyChanged(nameof(CanClearPlcErrors));
        OnPropertyChanged(nameof(ResetPlcCountersHint));
        OnPropertyChanged(nameof(ClearPlcErrorsHint));
        OnPropertyChanged(nameof(PlcActionStatusText));
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
                () => HasPlcActionAuthorityAndConnection, _cts.Token);
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
                () => HasPlcActionAuthorityAndConnection, _cts.Token);
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
