using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace ClassLibraryMT.MultiConnect;

/// <summary>Đăng ký MultiConnect gateway vào DI container.</summary>
public static class MultiConnectServiceCollectionExtensions
{
    /// <summary>
    /// Bind cấu hình <c>MultiConnect</c> AOT-safe rồi đăng ký gateway như một hosted service.
    /// </summary>
    public static IServiceCollection AddMultiConnect(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<MultiConnectOptions>()
            .Configure<IConfiguration>((options, config) =>
                MultiConnectConfigBinder.Bind(options, config))
            .Validate(options => !options.Enabled || options.Channels.Count > 0,
                "MultiConnect:Channels phải có ít nhất một kênh khi bật gateway.")
            .Validate(options => options.Channels.All(IsChannelValid),
                "MultiConnect: mỗi kênh cần Type hợp lệ và ListenPort từ 1 đến 65535.")
            .Validate(options => HasUniquePorts(options),
                "MultiConnect: ListenPort phải là duy nhất giữa các kênh.")
            .ValidateOnStart();

        // AddOptions chỉ đăng ký IOptions<T>; gateway nhận trực tiếp T nên cần factory này.
        services.AddSingleton(sp =>
            sp.GetRequiredService<IOptions<MultiConnectOptions>>().Value);
        services.AddSingleton<MultiConnectGateway>();
        services.AddHostedService(sp => sp.GetRequiredService<MultiConnectGateway>());
        return services;
    }

    private static bool IsChannelValid(MultiConnectChannelOptions channel)
    {
        if (channel.ListenPort is < 1 or > 65535)
        {
            return false;
        }

        return channel.Type.Trim().ToLowerInvariant() switch
        {
            "serial" or "com" => !string.IsNullOrWhiteSpace(channel.PortName)
                                 && channel.BaudRate is >= 300 and <= 921600
                                 && channel.DataBits is >= 5 and <= 8
                                 && channel.StopBits is 1 or 2,
            "tcpclient" or "tcp" or "forward" => !string.IsNullOrWhiteSpace(channel.Host)
                                                  && channel.Port is > 0 and <= 65535,
            "udp" => !string.IsNullOrWhiteSpace(channel.UdpListenIp)
                     && channel.UdpListenPort is > 0 and <= 65535,
            _ => false,
        };
    }

    private static bool HasUniquePorts(MultiConnectOptions options)
    {
        var ports = options.Channels
            .Where(c => c.Enabled)
            .Select(c => c.ListenPort)
            .ToArray();
        return ports.Length == ports.Distinct().Count();
    }
}
