using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TanTienQR.Services;

namespace TanTienQR.ViewModels;

/// <summary>Manual input and active-tab scanner intake; processing remains in the shared worker.</summary>
internal sealed partial class ManualActivationViewModel : ViewModelBase, IDisposable
{
    private readonly ManualActivationService _service;
    private readonly HandheldScannerService _scanner;
    private readonly ManualActivationHttpServer _http;
    private volatile bool _active;
    private bool _disposed;
    private long _epoch;
    private int _inFlight;
    private int _historyVersion;

    public ManualActivationViewModel(ManualActivationService service, HandheldScannerService scanner,
        ManualActivationHttpServer http)
    {
        _service = service;
        _scanner = scanner;
        _http = http;
    }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddCommand))]
    public partial string CodeInput { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddCommand))]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStatus))]
    public partial string StatusMessage { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ScannerStatusText { get; set; } = "Chưa bật";

    [ObservableProperty]
    public partial string PdaStatusText { get; set; } = "PDA: chưa xác minh trạng thái listener.";

    public string PdaEndpointText => _http.Endpoint.Replace("://+", "://<IP máy>", StringComparison.Ordinal)
        .Replace("://*", "://<IP máy>", StringComparison.Ordinal).TrimEnd('/') + "/manual-activation";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCurrentResult))]
    public partial IReadOnlyList<ManualActivationRowViewModel> CurrentRows { get; set; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasHistory))]
    public partial IReadOnlyList<ManualActivationRowViewModel> HistoryRows { get; set; } = [];

    public bool HasStatus => !string.IsNullOrWhiteSpace(StatusMessage);
    public bool HasCurrentResult => CurrentRows.Count > 0;
    public bool HasHistory => HistoryRows.Count > 0;
    public bool IsActive => _active;
    public bool CanAdd => _active && !IsBusy && !string.IsNullOrWhiteSpace(CodeInput);

    public void Activate()
    {
        if (_disposed || _active) return;
        _active = true;
        Interlocked.Increment(ref _epoch);
        _scanner.CodeReceived += OnScannerCode;
        _scanner.StatusChanged += OnScannerStatus;
        _service.Completed += OnCompleted;
        _http.StateChanged += OnPdaStateChanged;
        PdaStatusText = _http.StatusText;
        _scanner.Activate();
        ScannerStatusText = _scanner.StatusText;
        AddCommand.NotifyCanExecuteChanged();
        _ = LoadHistoryAsync(Volatile.Read(ref _epoch));
    }

    public void Deactivate()
    {
        if (!_active) return;
        _active = false;
        Interlocked.Increment(ref _epoch);
        _scanner.CodeReceived -= OnScannerCode;
        _scanner.StatusChanged -= OnScannerStatus;
        _service.Completed -= OnCompleted;
        _http.StateChanged -= OnPdaStateChanged;
        _scanner.Deactivate();
        ScannerStatusText = _scanner.StatusText;
        AddCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanAdd))]
    public Task AddAsync() => CanAdd
        ? SubmitCodeAsync(CodeInput, "Desktop", Volatile.Read(ref _epoch))
        : Task.CompletedTask;

    [RelayCommand]
    private void ClearInput() => CodeInput = string.Empty;

    private void OnScannerCode(string code)
    {
        var epoch = Volatile.Read(ref _epoch);
        if (!_active) return;
        Dispatcher.UIThread.Post(async () =>
        {
            if (!IsCurrent(epoch) || _scanner.IsIntakeSuspended) return;
            CodeInput = code;
            await SubmitCodeAsync(code, "Handheld", epoch);
        });
    }

    private void OnScannerStatus(string status)
    {
        var epoch = Volatile.Read(ref _epoch);
        Dispatcher.UIThread.Post(() =>
        {
            if (IsCurrent(epoch)) ScannerStatusText = _scanner.StatusText;
        });
    }

    private void OnCompleted(ManualActivationResult result)
    {
        var epoch = Volatile.Read(ref _epoch);
        Dispatcher.UIThread.Post(async () =>
        {
            if (!IsCurrent(epoch)) return;
            ShowResult(result);
            await LoadHistoryAsync(epoch);
        });
    }

    private void OnPdaStateChanged()
    {
        var epoch = Volatile.Read(ref _epoch);
        Dispatcher.UIThread.Post(() =>
        {
            if (IsCurrent(epoch)) PdaStatusText = _http.StatusText;
        });
    }

    private async Task SubmitCodeAsync(string code, string source, long epoch)
    {
        if (!IsCurrent(epoch)) return;
        _inFlight++;
        IsBusy = true;
        StatusMessage = "Đang chờ xử lý mã...";
        try
        {
            // Leaving the tab cannot cancel a code already admitted by the service.
            var result = await _service.SubmitAsync(code, source);
            if (!IsCurrent(epoch)) return;
            ShowResult(result);
            await LoadHistoryAsync(epoch);
        }
        catch (Exception ex)
        {
            if (IsCurrent(epoch)) StatusMessage = "Không thêm được mã: " + ex.Message;
        }
        finally { IsBusy = --_inFlight > 0; }
    }

    private void ShowResult(ManualActivationResult result)
    {
        if (CurrentRows.Count > 0 && CurrentRows[0].ReceivedUtc > result.ReceivedUtc) return;
        // A failed audit finalization can leave Pending on disk after a known
        // successful RAM/outbox acceptance. Do not turn that into a retry hint.
        if (CurrentRows.Count > 0 && CurrentRows[0].Id == result.Id && CurrentRows[0].IsTerminal
            && result.Status is "Pending" or "Interrupted") return;
        CurrentRows = [new ManualActivationRowViewModel(result)];
        StatusMessage = result.Message;
    }

    private async Task LoadHistoryAsync(long epoch)
    {
        var version = ++_historyVersion;
        try
        {
            var recent = await _service.GetRecentAsync(10);
            if (!IsCurrent(epoch) || version != _historyVersion) return;
            HistoryRows = recent.Take(10).Select(r => new ManualActivationRowViewModel(r)).ToArray();
            if (recent.Count > 0) ShowResult(recent[0]);
        }
        catch (Exception ex)
        {
            if (IsCurrent(epoch) && version == _historyVersion)
                StatusMessage = "Không tải được lịch sử thêm mã: " + ex.Message;
        }
    }

    private bool IsCurrent(long epoch) => !_disposed && _active && epoch == Volatile.Read(ref _epoch);

    public void Dispose()
    {
        if (_disposed) return;
        Deactivate();
        _disposed = true;
        GC.SuppressFinalize(this);
    }
}

internal sealed class ManualActivationRowViewModel(ManualActivationResult result)
{
    public string Id => result.Id;
    public DateTime ReceivedUtc => result.ReceivedUtc;
    public string TimeText => result.ReceivedUtc.ToLocalTime().ToString("dd/MM/yyyy HH:mm:ss");
    public string CodeText => result.CodeContent;
    public string SourceText => result.Source switch { "Desktop" => "Nhập tay", "Handheld" => "Tay quét", _ => result.Source };
    public string OrderNoText => string.IsNullOrWhiteSpace(result.OrderNo) ? "—" : result.OrderNo;
    public string StatusText => result.Status switch
    {
        "Pass" => "Đã thêm", "Duplicate" => "Trùng mã", "InvalidStructure" => "Sai cấu trúc",
        "InvalidForOrder" => "Không thuộc PO", "NoProductionOrder" => "Chưa chạy PO",
        "Halted" => "PO đã dừng", "Paused" => "Bảo trì", "Overload" => "Quá tải",
        "Stopped" => "Đã dừng", "AuditError" => "Lỗi nhật ký", "Pending" => "Đang chờ",
        "Interrupted" => "Gián đoạn", _ => "Lỗi",
    };
    public string MessageText => result.Message;
    public string DetailText => result.AuditPersisted ? result.Message : result.Message + " · Chưa lưu nhật ký";
    public bool IsSuccess => result.Success;
    public bool IsPending => result.Status is "Pending";
    public bool IsTerminal => result.Status is not ("Pending" or "Interrupted");
    public bool IsFailure => !IsSuccess && !IsPending;
    public string AuditText => result.AuditPersisted ? "Đã lưu" : "Chưa lưu nhật ký";
}
