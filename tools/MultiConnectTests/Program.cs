using System.Net;
using System.Net.Sockets;
using System.Text;
using ClassLibraryMT.MultiConnect;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

// Kiem thong luong: TCP client source + UDP source -> 2 TCP server rieng,
// moi server phat frame toi nhieu client. Khong can thiet bi that.
var upstreamPort = FreePort();
var forwardOutPort = FreePort();
var udpOutPort = FreePort();
var udpSourcePort = FreePort();

using var cts = new CancellationTokenSource();
var upstream = RunUpstreamAsync(upstreamPort, cts.Token);

var settings = new Dictionary<string, string?>
{
    ["MultiConnect:Enabled"] = "true",
    ["MultiConnect:ReconnectDelayMs"] = "200",
    ["MultiConnect:MaxFrameLength"] = "512",

    ["MultiConnect:Channels:0:Name"] = "TCP forward",
    ["MultiConnect:Channels:0:Type"] = "TcpClient",
    ["MultiConnect:Channels:0:Enabled"] = "true",
    ["MultiConnect:Channels:0:ListenIp"] = "127.0.0.1",
    ["MultiConnect:Channels:0:ListenPort"] = forwardOutPort.ToString(),
    ["MultiConnect:Channels:0:Host"] = "127.0.0.1",
    ["MultiConnect:Channels:0:Port"] = upstreamPort.ToString(),

    ["MultiConnect:Channels:1:Name"] = "UDP",
    ["MultiConnect:Channels:1:Type"] = "Udp",
    ["MultiConnect:Channels:1:Enabled"] = "true",
    ["MultiConnect:Channels:1:ListenIp"] = "127.0.0.1",
    ["MultiConnect:Channels:1:ListenPort"] = udpOutPort.ToString(),
    ["MultiConnect:Channels:1:UdpListenIp"] = "127.0.0.1",
    ["MultiConnect:Channels:1:UdpListenPort"] = udpSourcePort.ToString(),
};

var builder = Host.CreateApplicationBuilder();
builder.Configuration.AddInMemoryCollection(settings);
builder.Services.AddMultiConnect(builder.Configuration);
builder.Logging.SetMinimumLevel(LogLevel.Warning);

var host = builder.Build();
await host.StartAsync();

var failed = false;
try
{
    // Ket noi 2 client vao cung mot TCP output cua kenh forward.
    using var clientA = await ConnectWithRetryAsync(forwardOutPort, cts.Token);
    using var clientB = await ConnectWithRetryAsync(forwardOutPort, cts.Token);

    var a = await ReadLineAsync(clientA.GetStream(), cts.Token);
    var b = await ReadLineAsync(clientB.GetStream(), cts.Token);
    failed |= Check("TCP fan-out client A", a, "UP");
    failed |= Check("TCP fan-out client B", b, "UP");

    // GUI frame UDP vao kenh UDP -> phat ra TCP output rieng cua kenh nay.
    using var udpClient = await ConnectWithRetryAsync(udpOutPort, cts.Token);
    using var sender = new UdpClient();
    await sender.SendAsync(Encoding.UTF8.GetBytes("UDP1\r\n"),
        new IPEndPoint(IPAddress.Loopback, udpSourcePort));
    var udpFrame = await ReadLineAsync(udpClient.GetStream(), cts.Token);
    failed |= Check("UDP -> TCP", udpFrame, "UDP1");

    // Gateway phai ghi lai ca chieu vao lan chieu ra cho tab giam sat.
    await Task.Delay(300, cts.Token);
    var traffic = host.Services.GetRequiredService<MultiConnectGateway>().GetTrafficAfter(0);
    failed |= Check("Traffic IN", traffic.Any(t => !t.IsOutput && t.Payload == "UDP1") ? "UDP1" : "", "UDP1");
    failed |= Check("Traffic OUT", traffic.Any(t => t.IsOutput && t.Payload == "UDP1") ? "UDP1" : "", "UDP1");
    Console.WriteLine(failed ? "KET QUA: THAT BAI" : "KET QUA: DAT");
}
catch (Exception ex)
{
    failed = true;
    Console.WriteLine($"KET QUA: THAT BAI - {ex.GetType().Name}: {ex.Message}");
}
finally
{
    cts.Cancel();
    await host.StopAsync();
    try { await upstream; } catch { /* upstream bi huy khi test ket thuc */ }
}

return failed ? 1 : 0;

static bool Check(string name, string actual, string expectedPrefix)
{
    var ok = actual.StartsWith(expectedPrefix, StringComparison.Ordinal);
    Console.WriteLine($"{(ok ? "PASS" : "FAIL")} {name}: '{actual}'");
    return !ok;
}

static int FreePort()
{
    var listener = new TcpListener(IPAddress.Loopback, 0);
    listener.Start();
    var port = ((IPEndPoint)listener.LocalEndpoint).Port;
    listener.Stop();
    return port;
}

static async Task RunUpstreamAsync(int port, CancellationToken ct)
{
    var listener = new TcpListener(IPAddress.Loopback, port);
    listener.Start();
    try
    {
        while (!ct.IsCancellationRequested)
        {
            using var client = await listener.AcceptTcpClientAsync(ct);
            var stream = client.GetStream();
            var payload = Encoding.UTF8.GetBytes("UP1\r\n");
            while (!ct.IsCancellationRequested && client.Connected)
            {
                try
                {
                    await stream.WriteAsync(payload, ct);
                    await stream.FlushAsync(ct);
                }
                catch (IOException) { break; }
                await Task.Delay(300, ct);
            }
        }
    }
    catch (OperationCanceledException) { }
    finally
    {
        listener.Stop();
    }
}

static async Task<TcpClient> ConnectWithRetryAsync(int port, CancellationToken ct)
{
    for (var i = 0; i < 40; i++)
    {
        try
        {
            var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, port, ct);
            return client;
        }
        catch (SocketException)
        {
            await Task.Delay(100, ct);
        }
    }

    throw new IOException($"Khong ket noi duoc cong {port}");
}

static async Task<string> ReadLineAsync(NetworkStream stream, CancellationToken parent)
{
    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(parent);
    timeout.CancelAfter(TimeSpan.FromSeconds(5));
    var buffer = new byte[1];
    var sb = new StringBuilder();
    while (!timeout.IsCancellationRequested)
    {
        var read = await stream.ReadAsync(buffer, timeout.Token);
        if (read == 0)
        {
            break;
        }

        var ch = (char)buffer[0];
        if (ch is '\r' or '\n')
        {
            if (sb.Length > 0)
            {
                return sb.ToString();
            }
            continue;
        }

        sb.Append(ch);
    }

    return sb.ToString();
}
