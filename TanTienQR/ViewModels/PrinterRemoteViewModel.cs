using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TanTienQR.Services.PrinterRemote;

namespace TanTienQR.ViewModels;

public sealed partial class PrinterRemoteViewModel : ViewModelBase, IDisposable
{
    private readonly PrinterRemoteOptions _options;
    private readonly IPrinterRemoteSession _session;
    private readonly IPrinterCredentialStore _credentials;
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _retry;
    private bool _active, _authenticated, _blocked, _manualDisconnect, _disposed, _connectBusy;
    private int _retryCount;
    private PrinterRemoteSnapshot _snapshot;

    public PrinterRemoteViewModel(PrinterRemoteOptions options, IPrinterRemoteSession session, IPrinterCredentialStore credentials)
    {
        _options = options; _session = session; _credentials = credentials;
        _snapshot = !options.Enabled ? new(PrinterRemoteState.Disabled, "Bật điều khiển từ xa trong Cấu hình → Máy in.") : session.Snapshot;
        if (!session.IsSupported) _snapshot = new(PrinterRemoteState.Unsupported, "Điều khiển màn hình máy in hiện hỗ trợ Windows.");
        _hasCredential = credentials.Exists(options.CredentialTarget);
        _session.Changed += OnSessionChanged;
    }

    public string StatusText => _snapshot.Message;
    public string EndpointText => $"{_options.Host}:{_options.Port}";
    public PrinterRemoteState State => _snapshot.State;
    public bool IsConnected => State == PrinterRemoteState.Connected;
    public bool IsConnecting => _connectBusy || State is PrinterRemoteState.Connecting or PrinterRemoteState.Reconnecting;
    public bool IsSurfaceVisible => IsConnected && _active && _authenticated && !_blocked;
    public bool CanConnect => !_disposed && _options.Enabled && _session.IsSupported && _authenticated && _active && !_blocked
        && _session.HasSurface && !IsConnected && !IsConnecting;
    public bool CanDisconnect => !_disposed && (IsConnected || IsConnecting || _retry is not null);
    public bool CanSaveCredential => !_disposed && _options.Enabled && _session.IsSupported && _authenticated && _credentials.IsSupported && !string.IsNullOrWhiteSpace(Password)
        && !string.IsNullOrWhiteSpace(_options.Username);

    [ObservableProperty] public partial bool IsExpanded { get; set; }
    [ObservableProperty] public partial string Password { get; set; } = "";
    [ObservableProperty] public partial string CredentialMessage { get; set; } = "";
    [ObservableProperty] private bool _hasCredential;
    partial void OnPasswordChanged(string value) { OnPropertyChanged(nameof(CanSaveCredential)); SaveCredentialCommand.NotifyCanExecuteChanged(); }

    [RelayCommand] private void ToggleExpanded() => IsExpanded = !IsExpanded;

    [RelayCommand(CanExecute = nameof(CanSaveCredential))]
    private void SaveCredential()
    {
        try
        {
            _credentials.Save(_options.CredentialTarget, _options.Username, Password);
            Password = "";
            HasCredential = true;
            CredentialMessage = "Đã lưu thông tin đăng nhập trong Windows Credential Manager.";
            TryAutoConnect();
        }
        catch (Exception) { CredentialMessage = "Không lưu được thông tin đăng nhập. Kiểm tra kho thông tin xác thực của Windows."; }
    }

    [RelayCommand(CanExecute = nameof(CanConnect))]
    private async Task ConnectAsync()
    {
        CancelRetry(); _retryCount = 0; _manualDisconnect = false;
        await ConnectCoreAsync(_lifetime.Token);
    }

    private async Task ConnectCoreAsync(CancellationToken token)
    {
        if (!CanConnect) return;
        if (string.IsNullOrWhiteSpace(_options.Host) || string.IsNullOrWhiteSpace(_options.Username))
        {
            Apply(new(PrinterRemoteState.Faulted, "Điền địa chỉ máy in và tài khoản remote trong Cấu hình.", PrinterRemoteFailure.Backend));
            return;
        }
        try
        {
            var password = string.IsNullOrEmpty(Password) ? _credentials.ReadPassword(_options.CredentialTarget, _options.Username) : Password;
            Password = "";
            if (string.IsNullOrEmpty(password))
            {
                CredentialMessage = "Nhập mật khẩu remote, hoặc lưu thông tin đăng nhập để tự kết nối.";
                return;
            }
            _connectBusy = true;
            NotifyState();
            await _session.ConnectAsync(password, token).ConfigureAwait(true);
        }
        catch (OperationCanceledException) { }
        catch (Exception) { Apply(new(PrinterRemoteState.Faulted, "Không kết nối được màn hình máy in.", PrinterRemoteFailure.Backend)); }
        finally { _connectBusy = false; NotifyState(); }
    }

    [RelayCommand(CanExecute = nameof(CanDisconnect))]
    private async Task DisconnectAsync()
    {
        _manualDisconnect = true; CancelRetry();
        await _session.DisconnectAsync().ConfigureAwait(false);
    }

    /// <summary>Được shell gọi cả khi view được cache: chặn HWND ngay trước overlay/đăng xuất.</summary>
    public void SetPresentationState(bool isActive, bool isAuthenticated, bool isOverlayBlocked)
    {
        if (_disposed) return;
        var lostAuthentication = _authenticated && !isAuthenticated;
        _active = isActive; _authenticated = isAuthenticated; _blocked = isOverlayBlocked;
        if (!isAuthenticated)
        {
            CancelRetry(); Password = "";
            if (lostAuthentication || IsConnected || IsConnecting) _ = _session.DisconnectAsync();
        }
        else if (!isActive || isOverlayBlocked) CancelRetry();
        NotifyState();
        TryAutoConnect();
    }

    public void AttachSurface(nint handle) { _session.AttachSurface(handle); NotifyState(); TryAutoConnect(); }
    public void DetachSurface(nint handle) { _session.DetachSurface(handle); NotifyState(); }

    private void OnSessionChanged(PrinterRemoteSnapshot snapshot)
    {
        if (Dispatcher.UIThread.CheckAccess()) ProcessSnapshot(snapshot);
        else Dispatcher.UIThread.Post(() => ProcessSnapshot(snapshot));
    }

    private void ProcessSnapshot(PrinterRemoteSnapshot snapshot)
    {
        if (_disposed) return;
        if (!_options.Enabled) return;
        if (snapshot.State == PrinterRemoteState.Connected && (!_authenticated || _manualDisconnect))
        {
            _session.SetSurfaceVisible(false);
            _ = _session.DisconnectAsync();
            return;
        }
        Apply(snapshot);
        if (snapshot.State == PrinterRemoteState.Connected) { _retryCount = 0; return; }
        if (snapshot.State == PrinterRemoteState.Faulted && snapshot.Failure is PrinterRemoteFailure.Network or PrinterRemoteFailure.Timeout)
            ScheduleRetry();
    }

    private void Apply(PrinterRemoteSnapshot snapshot) { _snapshot = snapshot; NotifyState(); }
    private void NotifyState()
    {
        foreach (var name in new[] { nameof(State), nameof(StatusText), nameof(IsConnected), nameof(IsConnecting), nameof(IsSurfaceVisible), nameof(CanConnect), nameof(CanDisconnect), nameof(CanSaveCredential) }) OnPropertyChanged(name);
        ConnectCommand.NotifyCanExecuteChanged(); DisconnectCommand.NotifyCanExecuteChanged(); SaveCredentialCommand.NotifyCanExecuteChanged();
        _session.SetSurfaceVisible(IsSurfaceVisible);
    }

    private void TryAutoConnect()
    {
        if (_options.AutoConnect && !_manualDisconnect && HasCredential && CanConnect && State is PrinterRemoteState.Disconnected)
            _ = ConnectCoreAsync(_lifetime.Token);
    }

    private void ScheduleRetry()
    {
        if (!_options.AutoConnect || _manualDisconnect || !_active || !_authenticated || _blocked || !HasCredential || _retry is not null || _retryCount >= 3) return;
        var cts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _retry = cts;
        _retryCount++;
        _ = RetryAsync(cts, _retryCount);
    }

    private async Task RetryAsync(CancellationTokenSource cts, int attempt)
    {
        try
        {
            Apply(new(PrinterRemoteState.Reconnecting, $"Mất kết nối màn hình. Đang chờ thử lại ({attempt}/3)…"));
            await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt)), cts.Token).ConfigureAwait(true);
            if (_retry == cts) _retry = null;
            Apply(new(PrinterRemoteState.Disconnected, "Đang thử kết nối lại màn hình…"));
            await ConnectCoreAsync(cts.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException) { }
        finally { if (_retry == cts) _retry = null; cts.Dispose(); }
    }

    private void CancelRetry()
    {
        var retry = _retry; _retry = null; retry?.Cancel();
        if (State == PrinterRemoteState.Reconnecting) _snapshot = new(PrinterRemoteState.Disconnected, "Đã tạm dừng kết nối lại màn hình.");
    }

    public async Task ShutdownAsync()
    {
        _manualDisconnect = true; _authenticated = false; CancelRetry(); Password = "";
        NotifyState();
        await _session.DisconnectAsync().ConfigureAwait(false);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; _session.Changed -= OnSessionChanged;
        CancelRetry(); _lifetime.Cancel(); Password = "";
        _session.SetSurfaceVisible(false);
        _ = _session.DisposeAsync();
        _lifetime.Dispose();
    }
}
