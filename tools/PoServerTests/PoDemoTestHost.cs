using System.Net;
using System.Net.Sockets;
using ClassLibraryMT.Po;
using ClassLibraryMT.PoServer;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

internal static class PoDemoTestHost
{
    // Key công khai chỉ dành cho host thử nghiệm này; không đọc key cấu hình ứng dụng.
    private const string TestApiKey = "po-demo-local-test-key";

    public static async Task RunAsync(string[] args)
    {
        var portIndex = Array.IndexOf(args, "--port");
        var port = 0;
        if (portIndex >= 0 && (portIndex + 1 >= args.Length
            || !int.TryParse(args[portIndex + 1], out port) || port is < 1 or > 65535))
            throw new ArgumentException("--port phải là số nguyên từ 1 đến 65535.");
        if (port == 0)
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
        }
        var root = Path.Combine(Path.GetTempPath(), "visionf-po-demo-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var store = new PoStore(Path.Combine(root, "po"));
        using var server = new PoHttpServer(Options.Create(new PoServerOptions
        {
            Enabled = true, BindAddress = "127.0.0.1", Port = port,
            ApiKey = TestApiKey, UploadTempDirectory = root,
            MaxBodyMB = 64, MaxFileMB = 64, RequestTimeoutSeconds = 60,
        }), store, NullLogger<PoHttpServer>.Instance);
        server.Start();
        if (server.State.IsError) throw new InvalidOperationException(server.State.Status);
        Console.WriteLine("Host PoHttpServer thật phục vụ UI embedded: " + server.Prefix + "po-demo/");
        Console.WriteLine("Store thử nghiệm riêng: " + root);
        Console.WriteLine("Key công khai dành riêng cho test: " + TestApiKey);
        Console.WriteLine("Nhấn Ctrl+C để dừng; không dùng dữ liệu hoặc cấu hình production.");
        using var stop = new CancellationTokenSource();
        ConsoleCancelEventHandler cancel = (_, e) => { e.Cancel = true; stop.Cancel(); };
        Console.CancelKeyPress += cancel;
        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, stop.Token);
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        finally
        {
            Console.CancelKeyPress -= cancel;
            await server.StopAsync();
        }
    }
}
