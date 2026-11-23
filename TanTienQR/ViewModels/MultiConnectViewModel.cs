using System.Collections.ObjectModel;
using Avalonia.Threading;
using ClassLibraryMT.MultiConnect;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Options;

namespace TanTienQR.ViewModels;

/// <summary>
/// ViewModel cho tab MultiConnect: lấy các dòng dữ liệu vào/ra từ gateway theo
/// định kỳ rồi giữ 50 dòng gần nhất cho mỗi bên (mới nhất ở đầu danh sách).
/// </summary>
internal sealed partial class MultiConnectViewModel : ViewModelBase
{
    private const int MaxRows = 50;

    private readonly MultiConnectGateway _gateway;
    private readonly DispatcherTimer _timer;
    private long _lastTrafficId;

    public MultiConnectViewModel(
        MultiConnectGateway gateway,
        IOptions<MultiConnectOptions> options)
    {
        _gateway = gateway;
        IsGatewayEnabled = options.Value.Enabled;

        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Normal, OnTick);
        _timer.Start();
    }

    /// <summary>50 frame đi vào gần nhất (từ thiết bị sang gateway).</summary>
    public ObservableCollection<MultiConnectTrafficItem> InputRows { get; } = new();

    /// <summary>50 frame đi ra gần nhất (gateway phát cho các client TCP).</summary>
    public ObservableCollection<MultiConnectTrafficItem> OutputRows { get; } = new();

    [ObservableProperty] private bool isGatewayEnabled;
    [ObservableProperty] private string statusText = "Đang chờ dữ liệu…";

    public int InputCount => InputRows.Count;
    public int OutputCount => OutputRows.Count;
    public bool HasInputRows => InputRows.Count > 0;
    public bool HasOutputRows => OutputRows.Count > 0;

    [RelayCommand]
    private void Clear()
    {
        InputRows.Clear();
        OutputRows.Clear();
        OnPropertyChanged(nameof(InputCount));
        OnPropertyChanged(nameof(OutputCount));
        OnPropertyChanged(nameof(HasInputRows));
        OnPropertyChanged(nameof(HasOutputRows));
    }

    private void OnTick(object? sender, EventArgs e)
    {
        var items = _gateway.GetTrafficAfter(_lastTrafficId);
        if (items.Count > 0)
        {
            foreach (var item in items)
            {
                _lastTrafficId = Math.Max(_lastTrafficId, item.Id);
                var rows = item.IsOutput ? OutputRows : InputRows;
                rows.Insert(0, item);
                if (rows.Count > MaxRows)
                {
                    rows.RemoveAt(rows.Count - 1);
                }
            }

            OnPropertyChanged(nameof(InputCount));
            OnPropertyChanged(nameof(OutputCount));
            OnPropertyChanged(nameof(HasInputRows));
            OnPropertyChanged(nameof(HasOutputRows));
        }

        var statuses = _gateway.Statuses;
        if (!IsGatewayEnabled)
        {
            StatusText = "Gateway đang tắt — đặt MultiConnect:Enabled = true để nhận dữ liệu.";
            return;
        }

        if (statuses.Count == 0)
        {
            StatusText = "Gateway đã bật nhưng chưa có kênh nào.";
            return;
        }

        var running = statuses.Count(s => s.State == "Running");
        var frames = statuses.Sum(s => s.FramesReceived);
        StatusText = $"{running}/{statuses.Count} kênh chạy · {frames} frame đã nhận · "
                     + $"đệm {InputRows.Count}/{OutputRows.Count} dòng";
    }
}
