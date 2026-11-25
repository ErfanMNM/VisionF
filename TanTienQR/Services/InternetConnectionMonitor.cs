using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace TanTienQR.Services;

/// <summary>Kết quả ICMP; không suy diễn băng thông hoặc khả năng truy cập mọi dịch vụ Internet.</summary>
public readonly record struct InternetPingSample(bool IsReachable, long? RoundtripTimeMs)
{
    public static InternetPingSample Unavailable => new(false, null);

    public int Level => IsReachable ? InternetLatencyQuality.Classify(RoundtripTimeMs) : 0;
}

/// <summary>Năm mức chất lượng dựa trên độ trễ tới cùng một đích cố định.</summary>
public static class InternetLatencyQuality
{
    public static int Classify(long? milliseconds) => milliseconds switch
    {
        null or < 0 => 0,
        <= 30 => 5,
        <= 60 => 4,
        <= 100 => 3,
        <= 200 => 2,
        _ => 1,
    };

    public static string GetLabel(int level) => level switch
    {
        5 => "Rất tốt",
        4 => "Tốt",
        3 => "Trung bình",
        2 => "Yếu",
        1 => "Rất yếu",
        _ => "Không có phản hồi",
    };
}

public interface IInternetPingProbe
{
    Task<InternetPingSample> ProbeAsync(CancellationToken cancellationToken);
}

/// <summary>Chỉ gửi ICMP tới 8.8.8.8; không khởi chạy tiến trình ping hoặc dùng DNS.</summary>
public sealed class InternetPingProbe : IInternetPingProbe
{
    public const string TargetAddress = "8.8.8.8";
    public static readonly TimeSpan Timeout = TimeSpan.FromMilliseconds(1500);
    private static readonly IPAddress Address = IPAddress.Parse(TargetAddress);
    private static readonly byte[] Payload = new byte[32];

    public async Task<InternetPingSample> ProbeAsync(CancellationToken cancellationToken)
    {
        using var ping = new Ping();
        try
        {
            var reply = await ping.SendPingAsync(Address, Timeout, Payload, null, cancellationToken)
                .ConfigureAwait(false);
            return reply.Status == IPStatus.Success
                ? new InternetPingSample(true, reply.RoundtripTime)
                : InternetPingSample.Unavailable;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is PingException or SocketException or PlatformNotSupportedException)
        {
            return InternetPingSample.Unavailable;
        }
    }
}

/// <summary>Đo nền tuần tự, không chồng request; hủy request và thời gian chờ khi app đóng.</summary>
public sealed class InternetConnectionMonitor : IDisposable
{
    private readonly object _gate = new();
    private readonly IInternetPingProbe _probe;
    private readonly TimeSpan _interval;
    private readonly CancellationTokenSource _stop = new();
    private Task? _worker;
    private bool _disposed;

    public InternetConnectionMonitor(IInternetPingProbe? probe = null, TimeSpan? interval = null)
    {
        _probe = probe ?? new InternetPingProbe();
        _interval = interval ?? TimeSpan.FromSeconds(5);
        if (_interval <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(interval));
    }

    public event Action<InternetPingSample>? SampleReceived;

    /// <summary>Dùng để theo dõi kết thúc khi hủy; Start không tạo thêm vòng lặp nếu gọi lại.</summary>
    public Task Completion
    {
        get { lock (_gate) return _worker ?? Task.CompletedTask; }
    }

    public void Start()
    {
        lock (_gate)
        {
            if (_disposed || _worker is not null) return;
            var token = _stop.Token;
            _worker = Task.Run(() => RunAsync(token));
        }
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                InternetPingSample sample;
                try
                {
                    sample = await _probe.ProbeAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception)
                {
                    // Lỗi quyền ICMP/stack mạng không được làm hỏng vòng đời ứng dụng.
                    sample = InternetPingSample.Unavailable;
                }

                if (cancellationToken.IsCancellationRequested) return;
                SampleReceived?.Invoke(sample);
                await Task.Delay(_interval, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // App đóng trong lúc chờ lần đo kế tiếp.
        }
        finally
        {
            lock (_gate)
            {
                _disposed = true;
                _stop.Dispose();
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _stop.Cancel();
            if (_worker is null) _stop.Dispose();
        }
    }
}
