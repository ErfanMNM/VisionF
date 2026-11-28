using ClassLibraryMT.Configuration;
using Microsoft.Extensions.Configuration;

namespace ClassLibraryMT.MultiConnect;

/// <summary>
/// Bind cấu hình MultiConnect theo cách AOT-safe (đọc từng key string rồi tự parse),
/// không dùng <c>ConfigurationBinder.Bind</c> vì binder dùng reflection.
/// </summary>
public static class MultiConnectConfigBinder
{
    public const string SectionName = "MultiConnect";

    /// <summary>Đọc toàn bộ cấu hình gateway từ <see cref="IConfiguration"/>.</summary>
    public static void Bind(MultiConnectOptions options, IConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(config);

        var section = $"{SectionName}:";
        options.Enabled = ConfigReader.GetBool(config, section + "Enabled", options.Enabled);
        options.ReconnectDelayMs = Clamp(
            ConfigReader.GetInt(config, section + "ReconnectDelayMs", options.ReconnectDelayMs), 200, 60000, 3000);
        options.MaxFrameLength = Clamp(
            ConfigReader.GetInt(config, section + "MaxFrameLength", options.MaxFrameLength), 32, 8192, 512);

        options.Channels.Clear();
        for (var i = 0; ; i++)
        {
            var prefix = $"{section}Channels:{i}:";
            var name = ConfigReader.GetString(config, prefix + "Name", "");
            var type = ConfigReader.GetString(config, prefix + "Type", "");
            if (name.Length == 0 && type.Length == 0)
            {
                break;
            }

            var channel = new MultiConnectChannelOptions
            {
                Name = name.Length == 0 ? $"Channel {i + 1}" : name,
                Type = type.Length == 0 ? "Serial" : type,
                Enabled = ConfigReader.GetBool(config, prefix + "Enabled", true),
                ListenIp = ConfigReader.GetString(config, prefix + "ListenIp", "127.0.0.1"),
                ListenPort = ConfigReader.GetInt(config, prefix + "ListenPort", 0),

                PortName = ConfigReader.GetString(config, prefix + "PortName", "COM3"),
                BaudRate = ConfigReader.GetInt(config, prefix + "BaudRate", 9600),
                DataBits = ConfigReader.GetInt(config, prefix + "DataBits", 8),
                Parity = ConfigReader.GetString(config, prefix + "Parity", "None"),
                StopBits = ConfigReader.GetInt(config, prefix + "StopBits", 1),

                Host = ConfigReader.GetString(config, prefix + "Host", "127.0.0.1"),
                Port = ConfigReader.GetInt(config, prefix + "Port", 9000),

                UdpListenIp = ConfigReader.GetString(config, prefix + "UdpListenIp", "127.0.0.1"),
                UdpListenPort = ConfigReader.GetInt(config, prefix + "UdpListenPort", 0),
            };

            options.Channels.Add(channel);
        }
    }

    private static int Clamp(int value, int min, int max, int fallback) =>
        value <= 0 ? fallback : Math.Clamp(value, min, max);
}
