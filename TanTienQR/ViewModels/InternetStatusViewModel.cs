using Avalonia.Threading;
using TanTienQR.Services;

namespace TanTienQR.ViewModels;

/// <summary>Trạng thái card Internet, cập nhật UI từ kết quả ICMP chạy nền.</summary>
public sealed class InternetStatusViewModel : ViewModelBase, IDisposable
{
    private readonly InternetConnectionMonitor _monitor;
    private readonly Action<Action> _dispatch;
    private bool _hasSample;
    private int _level;
    private long? _latency;
    private volatile bool _disposed;

    public InternetStatusViewModel() : this(new InternetConnectionMonitor()) { }

    public InternetStatusViewModel(InternetConnectionMonitor monitor, Action<Action>? dispatch = null)
    {
        ArgumentNullException.ThrowIfNull(monitor);
        _monitor = monitor;
        _dispatch = dispatch ?? (action => Dispatcher.UIThread.Post(action));
        _monitor.SampleReceived += OnSampleReceived;
    }

    public int Level => _level;
    public bool IsReachable => _level > 0;
    public bool IsGood => _level >= 4;
    public bool IsSlow => _level is >= 1 and <= 3;
    public bool IsBar1Active => _level >= 1;
    public bool IsBar2Active => _level >= 2;
    public bool IsBar3Active => _level >= 3;
    public bool IsBar4Active => _level >= 4;
    public bool IsBar5Active => _level >= 5;
    public string StatusText => !_hasSample ? "Đang kiểm tra…"
        : IsReachable ? $"{InternetLatencyQuality.GetLabel(_level)} · {_latency} ms"
        : "Không có phản hồi";
    public string DetailText => IsReachable ? $"{InternetPingProbe.TargetAddress} · Mức {_level}/5"
        : InternetPingProbe.TargetAddress;
    public string AccessibleDescription => $"Internet: {StatusText}. {DetailText}";

    public string ToolTipText => "Ping ICMP tới 8.8.8.8, tự cập nhật sau mỗi 5 giây; timeout 1,5 giây.\n"
        + "5/5 Rất tốt: ≤ 30 ms · 4/5 Tốt: 31–60 ms · 3/5 Trung bình: 61–100 ms\n"
        + "2/5 Yếu: 101–200 ms · 1/5 Rất yếu: > 200 ms.\n"
        + "Không có phản hồi có thể do mất mạng hoặc ICMP bị chặn. Đây là độ trễ, không phải tốc độ tải Mbps.";

    public void Start()
    {
        if (!_disposed) _monitor.Start();
    }

    private void OnSampleReceived(InternetPingSample sample)
    {
        if (_disposed) return;
        _dispatch(() => ApplySample(sample));
    }

    /// <summary>Áp dụng snapshot trên luồng UI; cũng cho phép preview các mức mà không gửi ping thật.</summary>
    public void ApplySample(InternetPingSample sample)
    {
        if (_disposed) return;
        _hasSample = true;
        _level = sample.Level;
        _latency = IsReachable ? sample.RoundtripTimeMs : null;
        OnPropertyChanged(nameof(Level));
        OnPropertyChanged(nameof(IsReachable));
        OnPropertyChanged(nameof(IsGood));
        OnPropertyChanged(nameof(IsSlow));
        OnPropertyChanged(nameof(IsBar1Active));
        OnPropertyChanged(nameof(IsBar2Active));
        OnPropertyChanged(nameof(IsBar3Active));
        OnPropertyChanged(nameof(IsBar4Active));
        OnPropertyChanged(nameof(IsBar5Active));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(DetailText));
        OnPropertyChanged(nameof(AccessibleDescription));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _monitor.SampleReceived -= OnSampleReceived;
        _monitor.Dispose();
    }
}
