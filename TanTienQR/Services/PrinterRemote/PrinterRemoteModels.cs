namespace TanTienQR.Services.PrinterRemote;

public sealed class PrinterRemoteOptions
{
    public bool Enabled { get; set; }
    public string Host { get; set; } = "";
    public int Port { get; set; } = 3389;
    public string Username { get; set; } = "";
    public string CredentialTarget { get; set; } = "TanTien/Printer/g10-1";
    public bool AutoConnect { get; set; } = true;
    public int ConnectTimeoutSeconds { get; set; } = 15;
}

public enum PrinterRemoteState { Disabled, Unsupported, Disconnected, Connecting, Connected, Reconnecting, Faulted }
public enum PrinterRemoteFailure { None, Network, Timeout, Authentication, Certificate, Backend }
public sealed record PrinterRemoteSnapshot(PrinterRemoteState State, string Message, PrinterRemoteFailure Failure = PrinterRemoteFailure.None);

public interface IPrinterRemoteSession : IAsyncDisposable
{
    bool IsSupported { get; }
    bool HasSurface { get; }
    PrinterRemoteSnapshot Snapshot { get; }
    event Action<PrinterRemoteSnapshot>? Changed;
    void AttachSurface(nint handle);
    void DetachSurface(nint handle);
    void SetSurfaceVisible(bool visible);
    Task ConnectAsync(string password, CancellationToken cancellationToken = default);
    Task DisconnectAsync(CancellationToken cancellationToken = default);
}

public interface IPrinterCredentialStore
{
    bool IsSupported { get; }
    bool Exists(string target);
    string? ReadPassword(string target, string username);
    void Save(string target, string username, string password);
}

public sealed class UnsupportedPrinterRemoteSession : IPrinterRemoteSession, IDisposable
{
    public bool IsSupported => false;
    public bool HasSurface => false;
    public PrinterRemoteSnapshot Snapshot { get; } = new(PrinterRemoteState.Unsupported, "Điều khiển màn hình máy in hiện hỗ trợ Windows.");
    public event Action<PrinterRemoteSnapshot>? Changed { add { } remove { } }
    public void AttachSurface(nint handle) { }
    public void DetachSurface(nint handle) { }
    public void SetSurfaceVisible(bool visible) { }
    public Task ConnectAsync(string password, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task DisconnectAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    public void Dispose() { }
}
