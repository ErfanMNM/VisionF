using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace TanTienQR.Services.PrinterRemote;

/// <summary>Giữ COM trong helper riêng; ứng dụng AOT chỉ trao đổi qua pipe và HWND.</summary>
public sealed partial class WindowsPrinterRemoteSession(PrinterRemoteOptions options) : IPrinterRemoteSession, IDisposable
{
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly SemaphoreSlim _writes = new(1, 1);
    private nint _parent;
    private bool _visible;
    private bool _disposed;
    private NamedPipeServerStream? _pipe;
    private StreamWriter? _writer;
    private Process? _process;
    private CancellationTokenSource? _sessionCts;
    private TaskCompletionSource<bool>? _connected;
    private Task? _readTask;
    private readonly object _disposeGate = new();
    private Task? _disposeTask;
    public bool IsSupported => OperatingSystem.IsWindows();
    public bool HasSurface => _parent != 0;
    public PrinterRemoteSnapshot Snapshot { get; private set; } = new(PrinterRemoteState.Disconnected, "Chưa kết nối màn hình máy in.");
    public event Action<PrinterRemoteSnapshot>? Changed;

    public void AttachSurface(nint handle) { _parent = handle; Changed?.Invoke(Snapshot); }
    public void DetachSurface(nint handle)
    {
        if (_parent != handle) return;
        _parent = 0;
        SetSurfaceVisible(false);
        _ = DisconnectAsync();
    }

    public void SetSurfaceVisible(bool visible)
    {
        if (_visible == visible) return;
        _visible = visible;
        if (_writer is not null) _ = SendVisibilityAsync(visible);
    }

    private async Task SendVisibilityAsync(bool visible)
    {
        try { await SendAsync(new() { Kind = "visibility", Visible = visible }, CancellationToken.None).ConfigureAwait(false); }
        catch (Exception) { /* Phiên đang được đóng; HWND cha đã ẩn ngay tại UI. */ }
    }

    public async Task ConnectAsync(string password, CancellationToken cancellationToken = default)
    {
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            await StopCoreAsync().ConfigureAwait(false);
            if (!IsSupported) { Publish(new(PrinterRemoteState.Unsupported, "Điều khiển màn hình máy in hiện hỗ trợ Windows.")); return; }
            if (_parent == 0) throw new InvalidOperationException("Khung màn hình máy in chưa sẵn sàng.");
            if (!options.Enabled) throw new InvalidOperationException("Điều khiển từ xa chưa được bật trong Cấu hình.");
            var helper = Path.Combine(AppContext.BaseDirectory, "TanTien.PrinterRemoteHost.exe");
            if (!File.Exists(helper)) helper = Path.Combine(AppContext.BaseDirectory, "PrinterRemoteHost", "TanTien.PrinterRemoteHost.exe");
            if (!File.Exists(helper)) throw new FileNotFoundException("Thiếu thành phần điều khiển màn hình máy in trong gói cài đặt.");

            Publish(new(PrinterRemoteState.Connecting, "Đang kết nối màn hình máy in…"));
            _sessionCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var token = _sessionCts.Token;
            var pipeName = "TanTien.PrinterRemote." + Guid.NewGuid().ToString("N");
            _pipe = new(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            var start = new ProcessStartInfo(helper) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetDirectoryName(helper)! };
            start.ArgumentList.Add("--pipe"); start.ArgumentList.Add(pipeName);
            start.ArgumentList.Add("--parent"); start.ArgumentList.Add(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            _process = Process.Start(start) ?? throw new IOException("Không khởi chạy được thành phần remote.");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(options.ConnectTimeoutSeconds, 3, 120)));
            await _pipe.WaitForConnectionAsync(timeout.Token).ConfigureAwait(false);
            if (!GetNamedPipeClientProcessId(_pipe.SafePipeHandle, out var clientId) || clientId != _process.Id)
                throw new UnauthorizedAccessException("Pipe remote không thuộc helper đã khởi chạy.");
            _writer = new StreamWriter(_pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
            _connected = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _readTask = ReadEventsAsync(_pipe, token);
            await SendAsync(new()
            {
                Kind = "connect", ParentHandle = _parent.ToInt64(), Host = options.Host,
                Port = options.Port, Username = options.Username, Password = password,
                TimeoutSeconds = Math.Clamp(options.ConnectTimeoutSeconds, 3, 120), Visible = _visible,
            }, timeout.Token).ConfigureAwait(false);
            await _connected.Task.WaitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            await StopCoreAsync().ConfigureAwait(false);
            Publish(new(PrinterRemoteState.Faulted, "Hết thời gian kết nối màn hình máy in.", PrinterRemoteFailure.Timeout));
        }
        catch (OperationCanceledException)
        {
            await StopCoreAsync().ConfigureAwait(false);
            Publish(new(PrinterRemoteState.Disconnected, "Đã ngắt màn hình máy in."));
        }
        catch (Exception ex)
        {
            var existing = Snapshot;
            await StopCoreAsync().ConfigureAwait(false);
            if (existing.State != PrinterRemoteState.Faulted)
                Publish(new(PrinterRemoteState.Faulted, ex is FileNotFoundException ? ex.Message : "Không mở được thành phần remote. Kiểm tra gói cài đặt và kết nối máy in.", PrinterRemoteFailure.Backend));
        }
        finally { _lifecycle.Release(); }
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        // Hủy ngay cả khi Connect đang đợi pipe hoặc handshake.
        CancelSession();
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { await StopCoreAsync().ConfigureAwait(false); Publish(new(PrinterRemoteState.Disconnected, "Đã ngắt màn hình máy in.")); }
        finally { _lifecycle.Release(); }
    }

    private async Task ReadEventsAsync(NamedPipeServerStream pipe, CancellationToken token)
    {
        try
        {
            using var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
            while (!token.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(token).ConfigureAwait(false);
                if (line is null) throw new IOException("Helper đã đóng pipe.");
                if (line.Length > 65_536) throw new InvalidDataException();
                var message = JsonSerializer.Deserialize(line, PrinterRemoteJsonContext.Default.PrinterRemoteMessage);
                if (message?.Kind != "state" || !Enum.TryParse<PrinterRemoteState>(message.State, out var state)) continue;
                Enum.TryParse<PrinterRemoteFailure>(message.Failure, out var failure);
                Publish(new(state, message.Message ?? "Trạng thái remote đã thay đổi.", failure));
                if (state == PrinterRemoteState.Connected) _connected?.TrySetResult(true);
                if (state is PrinterRemoteState.Faulted or PrinterRemoteState.Disconnected)
                    _connected?.TrySetException(new IOException("Phiên remote không kết nối được."));
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception) when (!token.IsCancellationRequested)
        {
            if (Snapshot.State != PrinterRemoteState.Faulted)
                Publish(new(PrinterRemoteState.Faulted, "Thành phần remote đã ngừng hoạt động. Có thể kết nối lại.", PrinterRemoteFailure.Backend));
            _connected?.TrySetException(new IOException("Thành phần remote đã ngừng hoạt động."));
        }
    }

    private async Task SendAsync(PrinterRemoteMessage message, CancellationToken token)
    {
        await _writes.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var writer = _writer;
            if (writer is null) return;
            var json = JsonSerializer.Serialize(message, PrinterRemoteJsonContext.Default.PrinterRemoteMessage);
            await writer.WriteLineAsync(json.AsMemory(), token).ConfigureAwait(false);
        }
        finally { message.Password = null; _writes.Release(); }
    }

    private async Task StopCoreAsync()
    {
        CancelSession();
        if (_writer is not null)
        {
            try { using var timeout = new CancellationTokenSource(500); await SendAsync(new() { Kind = "disconnect" }, timeout.Token).ConfigureAwait(false); }
            catch (Exception) { }
        }
        try { _writer?.Dispose(); } catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
        _writer = null;
        _pipe?.Dispose(); _pipe = null;
        var process = _process; _process = null;
        if (process is not null)
        {
            try
            {
                if (!process.HasExited)
                {
                    try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false); }
                    catch (TimeoutException)
                    {
                        process.Kill(entireProcessTree: true);
                        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
                    }
                }
            }
            catch (InvalidOperationException) { }
            finally { process.Dispose(); }
        }
        if (_readTask is not null) { try { await _readTask.ConfigureAwait(false); } catch (Exception) { } }
        _readTask = null; _connected = null;
        _sessionCts?.Dispose(); _sessionCts = null;
    }

    private void Publish(PrinterRemoteSnapshot snapshot) { Snapshot = snapshot; Changed?.Invoke(snapshot); }
    private void CancelSession() { try { _sessionCts?.Cancel(); } catch (ObjectDisposedException) { } }
    public ValueTask DisposeAsync()
    {
        lock (_disposeGate)
        {
            _disposed = true;
            return new(_disposeTask ??= DisconnectAsync());
        }
    }
    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static partial bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint process);
}
