using System.Diagnostics;
using System.IO.Pipes;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using TanTienQR.Services.PrinterRemote;

namespace TanTien.PrinterRemoteHost;

/// <summary>Thử pipe và HWND khác tiến trình bằng loopback đóng cổng, không chạm máy in.</summary>
internal static class IpcSelfTest
{
    public static int Run()
    {
        using var parent = new Form { ShowInTaskbar = false, Width = 960, Height = 540 };
        var handle = parent.Handle;
        var test = RunAsync(handle);
        while (!test.IsCompleted) { Application.DoEvents(); Thread.Sleep(10); }
        try { test.GetAwaiter().GetResult(); Console.WriteLine("PASS: IPC riêng, HWND khác tiến trình, lỗi mạng và dọn helper."); return 0; }
        catch (Exception ex) { Console.Error.WriteLine("FAIL: " + ex.GetType().Name + ": " + ex.Message); return 1; }
    }

    private static async Task RunAsync(nint parent)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var token = timeout.Token;
        var name = "TanTien.PrinterRemote.Test." + Guid.NewGuid().ToString("N");
        using var pipe = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var path = Environment.ProcessPath!;
        var start = new ProcessStartInfo(path) { UseShellExecute = false, CreateNoWindow = true };
        if (Path.GetFileNameWithoutExtension(path).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "TanTien.PrinterRemoteHost.dll"));
        start.ArgumentList.Add("--pipe"); start.ArgumentList.Add(name);
        start.ArgumentList.Add("--parent"); start.ArgumentList.Add(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        using var process = Process.Start(start) ?? throw new IOException("Không tạo được helper thử nghiệm.");
        try
        {
            await pipe.WaitForConnectionAsync(token);
            using var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
            using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            await writer.WriteLineAsync(JsonSerializer.Serialize(new PrinterRemoteMessage
            {
                Kind = "connect", ParentHandle = parent.ToInt64(), Host = "127.0.0.1", Port = port,
                Username = "local-smoke-test", Password = "", TimeoutSeconds = 5,
            }, PrinterRemoteJsonContext.Default.PrinterRemoteMessage).AsMemory(), token);
            var receivedConnecting = false;
            while (true)
            {
                var line = await reader.ReadLineAsync(token) ?? throw new IOException("Helper kết thúc trước phản hồi.");
                var message = JsonSerializer.Deserialize(line, PrinterRemoteJsonContext.Default.PrinterRemoteMessage)!;
                if (message.State == "Connecting") receivedConnecting = true;
                if (message.State != "Faulted") continue;
                if (!receivedConnecting || message.Failure is not ("Network" or "Timeout"))
                    throw new InvalidOperationException("Không đi qua kết nối RDP hoặc phân loại sai: " + message.Failure);
                break;
            }
            await writer.WriteLineAsync(JsonSerializer.Serialize(new PrinterRemoteMessage { Kind = "disconnect" }, PrinterRemoteJsonContext.Default.PrinterRemoteMessage).AsMemory(), token);
            await process.WaitForExitAsync(token);
            if (process.ExitCode != 0) throw new InvalidOperationException("Helper kết thúc với mã lỗi.");
        }
        finally { if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); } }
    }
}
