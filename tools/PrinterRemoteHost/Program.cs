using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using TanTienQR.Services.PrinterRemote;

namespace TanTien.PrinterRemoteHost;

internal static partial class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        if (args.Length == 1 && args[0].StartsWith("--self-test", StringComparison.Ordinal)) Console.OutputEncoding = new UTF8Encoding(false);
        if (args.Length == 1 && args[0] == "--self-test") return SelfTest();
        if (args.Length == 1 && args[0] == "--self-test-ipc") return IpcSelfTest.Run();
        if (args.Length != 4 || args[0] != "--pipe" || args[2] != "--parent" || !int.TryParse(args[3], out var parentId)) return 2;
        try
        {
            using var parent = Process.GetProcessById(parentId);
            using var pipe = new NamedPipeClientStream(".", args[1], PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            pipe.Connect(10_000);
            if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out var serverId) || serverId != parentId) return 4;
            using var channel = new HostChannel(pipe);
            using var form = new RemoteForm(parent, channel);
            _ = form.Handle;
            _ = Task.Run(async () =>
            {
                try
                {
                    using var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
                    while (await reader.ReadLineAsync() is { } line)
                    {
                        if (line.Length > 65_536) break;
                        var message = JsonSerializer.Deserialize(line, PrinterRemoteJsonContext.Default.PrinterRemoteMessage);
                        if (message is null) continue;
                        form.BeginInvoke(() => form.Receive(message));
                    }
                }
                catch (Exception) { /* Cha đóng pipe hoặc thoát; kết thúc helper cùng phiên. */ }
                finally { if (!form.IsDisposed) { try { form.BeginInvoke(form.Stop); } catch (InvalidOperationException) { } } }
            });
            Application.Run();
            return 0;
        }
        catch (Exception) { return 3; }
    }

    private static int SelfTest()
    {
        try
        {
            using var parent = new Form { ShowInTaskbar = false, Width = 960, Height = 540 };
            _ = parent.Handle;
            using var form = new RemoteForm(Process.GetCurrentProcess(), null);
            _ = form.Handle;
            form.TestBackend(parent.Handle);
            TestCredentialStore();
            Console.WriteLine("PASS: ActiveX RDP, COM settings/events, HWND child, resize, ẩn surface và Credential Manager.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("FAIL: " + ex.GetType().Name + ": " + ex.Message);
            return 1;
        }
    }

    private static void TestCredentialStore()
    {
        var target = "TanTien/Printer/Test/" + Guid.NewGuid().ToString("N");
        var store = new WindowsPrinterCredentialStore();
        try
        {
            store.Save(target, "remote-self-test", "thông tin kiểm thử tạm thời");
            if (!store.Exists(target) || store.ReadPassword(target, "remote-self-test") != "thông tin kiểm thử tạm thời"
                || store.ReadPassword(target, "wrong-user") is not null) throw new InvalidOperationException("Kho credential không khớp tài khoản.");
        }
        finally { CredDelete(target, 1, 0); }
    }

    [System.Runtime.InteropServices.LibraryImport("advapi32.dll", EntryPoint = "CredDeleteW", StringMarshalling = System.Runtime.InteropServices.StringMarshalling.Utf16)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static partial bool CredDelete(string target, uint type, uint flags);

    [System.Runtime.InteropServices.LibraryImport("kernel32.dll", SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static partial bool GetNamedPipeServerProcessId(Microsoft.Win32.SafeHandles.SafePipeHandle pipe, out uint process);
}

internal sealed class HostChannel : IDisposable
{
    private readonly StreamWriter _writer;
    private readonly object _gate = new();
    public HostChannel(Stream stream) => _writer = new(stream, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
    public void Send(string state, string message, string failure = "None")
    {
        lock (_gate)
        {
            try { _writer.WriteLine(JsonSerializer.Serialize(new PrinterRemoteMessage { Kind = "state", State = state, Failure = failure, Message = message }, PrinterRemoteJsonContext.Default.PrinterRemoteMessage)); }
            catch (IOException) { Application.Exit(); }
        }
    }
    public void Dispose() { try { _writer.Dispose(); } catch (IOException) { } }
}
