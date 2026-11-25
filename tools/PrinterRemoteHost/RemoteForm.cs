using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using TanTienQR.Services.PrinterRemote;

namespace TanTien.PrinterRemoteHost;

internal sealed partial class RemoteForm : Form
{
    private readonly Process _parent;
    private readonly HostChannel? _channel;
    private readonly System.Windows.Forms.Timer _timer;
    private RdpControl? _rdp;
    private nint _parentWindow;
    private bool _requestedVisible, _connected, _stopping, _faulted;
    private DateTime _deadline;
    private int _remoteWidth = 1920, _remoteHeight = 1080;

    public RemoteForm(Process parent, HostChannel? channel)
    {
        _parent = parent; _channel = channel;
        FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false;
        TopLevel = false; AutoScaleMode = AutoScaleMode.None;
        BackColor = Color.Black;
        _timer = new() { Interval = 100 };
        _timer.Tick += (_, _) => Poll();
        _timer.Start();
        ClientSizeChanged += (_, _) => FitDesktop();
    }

    public void Receive(PrinterRemoteMessage message)
    {
        if (_stopping) return;
        try
        {
            switch (message.Kind)
            {
                case "connect": Configure(message); break;
                case "visibility": _requestedVisible = message.Visible; UpdateVisibility(); break;
                case "disconnect": Stop(); break;
            }
        }
        catch (Exception) { Fail("Backend", "Không khởi tạo được client RDP. Kiểm tra thành phần Remote Desktop của Windows."); }
        finally { message.Password = null; }
    }

    private void Configure(PrinterRemoteMessage message)
    {
        if (_rdp is not null || message.ParentHandle == 0 || string.IsNullOrWhiteSpace(message.Host) || string.IsNullOrWhiteSpace(message.Username))
            throw new InvalidOperationException();
        _parentWindow = (nint)message.ParentHandle;
        GetWindowThreadProcessId(_parentWindow, out var owner);
        if (owner != _parent.Id) throw new InvalidOperationException("HWND không thuộc tiến trình cha.");
        AttachWindow(_parentWindow);
        CreateRdp();
        dynamic client = _rdp!.Client;
        client.Server = message.Host;
        client.UserName = message.Username;
        dynamic advanced = client.AdvancedSettings7;
        advanced.RDPPort = message.Port;
        advanced.EnableCredSspSupport = true;
        advanced.AuthenticationLevel = 1;
        advanced.EnableAutoReconnect = false;
        advanced.ClearTextPassword = message.Password ?? "";
        _requestedVisible = message.Visible;
        _deadline = DateTime.UtcNow.AddSeconds(message.TimeoutSeconds);
        _channel?.Send("Connecting", "Đang kết nối màn hình máy in…");
        client.Connect();
    }

    private void CreateRdp()
    {
        _rdp = new RdpControl();
        ((ISupportInitialize)_rdp).BeginInit();
        Controls.Add(_rdp);
        ((ISupportInitialize)_rdp).EndInit();
        _rdp.CreateControl();
        dynamic client = _rdp.Client;
        client.DesktopWidth = 1920; client.DesktopHeight = 1080;
        dynamic advanced = client.AdvancedSettings7;
        advanced.SmartSizing = true;
        advanced.RedirectClipboard = false;
        advanced.RedirectDrives = false;
        advanced.RedirectPrinters = false;
        _rdp.Observe(2, (Action)(() =>
        {
            _connected = true; _faulted = false; _deadline = default;
            _channel?.Send("Connected", "Đã kết nối màn hình máy in."); UpdateVisibility();
        }));
        _rdp.Observe(4, (Action<int>)OnDisconnected);
        _rdp.Observe(10, (Action<int>)(_ => Fail("Backend", "Client RDP gặp lỗi. Hãy ngắt rồi kết nối lại.")));
        _rdp.Observe(12, (Action<int, int>)((width, height) =>
        {
            if (width <= 0 || height <= 0) return;
            _remoteWidth = width; _remoteHeight = height; FitDesktop();
        }));
        _rdp.Observe(22, (Action<int>)(reason =>
        {
            if (reason is 0 or 1 or -1 or -6 or -1073741715 or -1073741714 or -1073741276)
                Fail("Authentication", "Máy in từ chối đăng nhập. Kiểm tra tài khoản, mật khẩu và quyền remote.");
        }));
        FitDesktop();
    }

    private void OnDisconnected(int reason)
    {
        _connected = false; _deadline = default; UpdateVisibility();
        if (_stopping || _faulted) return;
        // Mã chung chưa đủ phân biệt lỗi TLS, chứng thư và lỗi phía máy in.
        var detail = GetDisconnectDetail(reason);
        var failure = ClassifyDisconnect(reason);
        if (failure == "None") _channel?.Send("Disconnected", "Phiên màn hình máy in đã kết thúc.");
        else Fail(failure, failure switch
        {
            "Authentication" => "Máy in từ chối đăng nhập. Kiểm tra tài khoản, mật khẩu và quyền remote.",
            "Certificate" => "Không xác minh được chứng thư RDP của máy in. Cần cấu hình chứng thư tin cậy trên Windows.",
            "Network" => "Mất kết nối mạng tới màn hình máy in.",
            "Timeout" => "Hết thời gian kết nối màn hình máy in.",
            _ => $"Client RDP đã ngắt kết nối (mã {reason}). {detail}",
        });
    }

    private string GetDisconnectDetail(int reason)
    {
        if (_rdp is null) return "";
        try
        {
            dynamic client = _rdp.Client;
            var extended = (int)client.ExtendedDisconnectReason;
            string description = client.GetErrorDescription((uint)reason, (uint)extended);
            return $"Mã mở rộng {extended}: {description.Trim()}";
        }
        catch (Exception) { return ""; }
    }

    internal static string ClassifyDisconnect(int reason) => reason switch
    {
        0 or 1 or 2 or 3 => "None",
        2055 or 2567 or 2823 or 3079 or 3335 or 3591 or 3847 or 4615 or 8455 => "Authentication",
        1798 or 6919 or 1542 or 1030 or 2310 or 2566 => "Certificate",
        264 or 1796 => "Timeout",
        260 or 1288 or 1540 or 520 or 516 or 2308 or 1028 or 772 => "Network",
        _ => "Backend",
    };

    private void Fail(string failure, string message)
    {
        if (_faulted || _stopping) return;
        _faulted = true; _connected = false;
        UpdateVisibility();
        _channel?.Send("Faulted", message, failure);
        try { if (_rdp is not null) _rdp.Client.Disconnect(); } catch (Exception) { }
    }

    private void Poll()
    {
        if (_stopping) return;
        try { if (_parent.HasExited) { Stop(); return; } }
        catch (InvalidOperationException) { Stop(); return; }
        if (_parentWindow != 0)
        {
            if (!IsWindow(_parentWindow)) { Stop(); return; }
            FitToParent();
        }
        if (_rdp is not null && !_connected && !_faulted && _deadline != default && DateTime.UtcNow > _deadline)
            Fail("Timeout", "Hết thời gian kết nối màn hình máy in.");
    }

    private void AttachWindow(nint parent)
    {
        Marshal.SetLastPInvokeError(0);
        var previous = SetParent(Handle, parent);
        if (previous == 0 && Marshal.GetLastPInvokeError() != 0) throw new Win32Exception(Marshal.GetLastPInvokeError());
        FitToParent();
    }

    private void FitToParent()
    {
        if (!GetClientRect(_parentWindow, out var rect)) return;
        var width = Math.Max(1, rect.Right);
        var height = Math.Max(1, rect.Bottom);
        if (Width != width || Height != height) SetWindowPos(Handle, 0, 0, 0, width, height, 0x0014);
        FitDesktop();
    }

    private void FitDesktop()
    {
        if (_rdp is null) return;
        var scale = Math.Min((double)ClientSize.Width / _remoteWidth, (double)ClientSize.Height / _remoteHeight);
        var width = Math.Max(1, (int)Math.Floor(_remoteWidth * scale));
        var height = Math.Max(1, (int)Math.Floor(_remoteHeight * scale));
        var bounds = new Rectangle((ClientSize.Width - width) / 2, (ClientSize.Height - height) / 2, width, height);
        if (_rdp.Bounds != bounds) _rdp.Bounds = bounds;
    }

    private void UpdateVisibility()
    {
        var show = _requestedVisible && _connected && !_stopping;
        SetVisibleCore(show);
        EnableWindow(Handle, show);
        var focused = GetFocus();
        if (!show && (focused == Handle || IsChild(Handle, focused))) SetFocus(0);
    }

    public void Stop()
    {
        if (_stopping) return;
        _stopping = true; _timer.Stop();
        UpdateVisibility();
        try { if (_rdp is not null) _rdp.Client.Disconnect(); } catch (Exception) { }
        Application.ExitThread();
    }

    public void TestBackend(nint parent)
    {
        _parentWindow = parent; AttachWindow(parent); CreateRdp();
        if (GetParent(Handle) != parent) throw new InvalidOperationException("Không nhúng được HWND.");
        SetWindowPos(parent, 0, 0, 0, 1280, 720, 0x0014); FitToParent();
        if (Width < 1 || Height < 1 || _rdp!.Width > ClientSize.Width || _rdp.Height > ClientSize.Height) throw new InvalidOperationException("Resize ActiveX không đúng.");
        _remoteWidth = 1024; _remoteHeight = 768; FitDesktop();
        if (_rdp.Left <= 0 || Math.Abs(_rdp.Width - _rdp.Height * 4d / 3) > 1) throw new InvalidOperationException("Letterbox sai tỷ lệ desktop.");
        _requestedVisible = false; UpdateVisibility();
        if (Visible) throw new InvalidOperationException("Surface chưa ẩn.");
        if (ClassifyDisconnect(2055) != "Authentication" || ClassifyDisconnect(516) != "Network") throw new InvalidOperationException("Phân loại lỗi không đúng.");
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _timer.Dispose(); _rdp?.Dispose(); }
        base.Dispose(disposing);
    }

    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [LibraryImport("user32.dll", SetLastError = true)] private static partial nint SetParent(nint child, nint parent);
    [LibraryImport("user32.dll")] private static partial nint GetParent(nint child);
    [LibraryImport("user32.dll")] private static partial uint GetWindowThreadProcessId(nint hwnd, out uint process);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool GetClientRect(nint hwnd, out Rect rect);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool IsWindow(nint hwnd);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool SetWindowPos(nint hwnd, nint after, int x, int y, int width, int height, uint flags);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool EnableWindow(nint hwnd, [MarshalAs(UnmanagedType.Bool)] bool enable);
    [LibraryImport("user32.dll")] private static partial nint GetFocus();
    [LibraryImport("user32.dll")] private static partial nint SetFocus(nint hwnd);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool IsChild(nint parent, nint child);
}
