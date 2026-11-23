using ClassLibraryMT.MultiConnect;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

// Kong chay rieng: doc appsettings.json ngay trong thu muc chay (CopyToOutputDirectory).
// App TanTienQR khong can biet tool nay ton tai; chi can tro Camera:Ip/Port
// vao mot ListenPort cua gateway la duoc.
var builder = Host.CreateApplicationBuilder(args);

// Host.CreateApplicationBuilder tim appsettings.json theo current directory,
// nen khi chay bang `dotnet run --project ...` tu repo root se khong thay file
// cua tool. Nap ro rang tu thu muc chay (noi file duoc copy ra output).
builder.Configuration.AddJsonFile(
    Path.Combine(AppContext.BaseDirectory, "appsettings.json"),
    optional: true,
    reloadOnChange: false);

builder.Services.AddMultiConnect(builder.Configuration);

var host = builder.Build();
var options = host.Services.GetRequiredService<IOptions<MultiConnectOptions>>().Value;

Console.WriteLine("MultiConnect Gateway");
Console.WriteLine($"Enabled = {options.Enabled}");
foreach (var channel in options.Channels)
{
    var source = channel.Type switch
    {
        "Serial" or "com" => $"COM {channel.PortName} @{channel.BaudRate}",
        "TcpClient" or "tcp" or "forward" => $"TCP {channel.Host}:{channel.Port}",
        "Udp" => $"UDP {channel.UdpListenIp}:{channel.UdpListenPort}",
        _ => channel.Type,
    };

    Console.WriteLine($"  [{channel.Name}] {source} -> TCP {channel.ListenIp}:{channel.ListenPort}"
                      + (channel.Enabled ? "" : " (disabled)"));
}
Console.WriteLine("Nhan Ctrl+C de dung.");

await host.RunAsync();
