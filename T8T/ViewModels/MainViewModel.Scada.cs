using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using Avalonia;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using T8T.Models;

namespace T8T.ViewModels;

/// <summary>
/// Mô phỏng dữ liệu SCADA theo thời gian thực. Toàn bộ số liệu ở đây là dữ liệu giả lập;
/// khi nối PLC/API thật, thay phần UpdateMachines/RefreshMetrics bằng nguồn dữ liệu thật.
/// </summary>
public partial class MainViewModel
{
    private const double ChartPlotHeight = 150;
    private const double ChartLabelRowHeight = 22;
    private const double ChartHeadroom = 1.25;
    private const int MaxAlarms = 8;
    private const double OeeTarget = 85;
    private const double DefectLimit = 2.0;
    private const double PowerBaselineKw = 160;

    // Công suất danh định (kW) theo thứ tự máy trong BuildMachines.
    private static readonly double[] MachinePowerKw = [18, 95, 40, 12, 25];

    private static readonly (string Source, string Message)[] RandomInfoEvents =
    [
        ("M1 · Cấp liệu", "Silo hạt nhựa còn dưới 25%"),
        ("M4 · Kiểm tra QC", "Hoàn tất lô kiểm tra 500 sản phẩm"),
        ("M5 · Đóng gói", "Cuộn nhãn sắp hết, cần thay trong 15 phút"),
        ("Hệ thống", "Đồng bộ dữ liệu ca làm việc thành công"),
    ];

    private readonly Random _random = new();
    private DispatcherTimer? _timer;
    private int _tickCount;
    private double _pace = 1;
    private double _defectRate = 1.8;
    private long _bucketKey;

    public ObservableCollection<MachineViewModel> Machines { get; } = [];
    public ObservableCollection<AlarmViewModel> Alarms { get; } = [];
    public ObservableCollection<ChartBarViewModel> ChartBars { get; } = [];

    public System.Collections.Generic.IReadOnlyList<KpiViewModel> Kpis { get; } =
    [
        new KpiViewModel("Sản lượng", "sp"),
        new KpiViewModel("OEE", "%"),
        new KpiViewModel("Tỷ lệ lỗi", "%"),
        new KpiViewModel("Điện năng", "kW"),
    ];

    /// <summary>Vị trí đường mục tiêu trên biểu đồ (cố định vì thang đo luôn là 125% mục tiêu).</summary>
    public Thickness TargetLineMargin { get; } =
        new(0, 0, 0, ChartLabelRowHeight + ChartPlotHeight / ChartHeadroom);

    public double ChartAreaHeight => ChartPlotHeight + ChartLabelRowHeight + 20;

    // ---------- Khoảng thời gian biểu đồ ----------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsShiftRange))]
    [NotifyPropertyChangedFor(nameof(IsDayRange))]
    [NotifyPropertyChangedFor(nameof(IsWeekRange))]
    public partial TimeRange SelectedRange { get; set; } = TimeRange.Shift;

    public bool IsShiftRange => SelectedRange == TimeRange.Shift;
    public bool IsDayRange => SelectedRange == TimeRange.Day;
    public bool IsWeekRange => SelectedRange == TimeRange.Week;

    [ObservableProperty]
    public partial string ChartTitle { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ChartTargetText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial double ChartTarget { get; set; } = 120;

    partial void OnSelectedRangeChanged(TimeRange value)
    {
        RebuildChart();
        RefreshMetrics();
    }

    [RelayCommand]
    private void SetRange(TimeRange range) => SelectedRange = range;

    // ---------- OEE ----------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OeeText))]
    [NotifyPropertyChangedFor(nameof(OeeSweep))]
    [NotifyPropertyChangedFor(nameof(IsOeeGood))]
    public partial double OeeValue { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AvailabilityText))]
    public partial double Availability { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PerformanceText))]
    public partial double Performance { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(QualityText))]
    public partial double Quality { get; set; }

    public string OeeText => OeeValue.ToString("0.0", ViCulture);
    public string AvailabilityText => $"{Availability.ToString("0.0", ViCulture)}%";
    public string PerformanceText => $"{Performance.ToString("0.0", ViCulture)}%";
    public string QualityText => $"{Quality.ToString("0.0", ViCulture)}%";
    public bool IsOeeGood => OeeValue >= OeeTarget;

    /// <summary>Góc quét (độ) của vòng cung OEE; tránh 360 để cung không bị suy biến.</summary>
    public double OeeSweep => Math.Clamp(OeeValue, 0, 99.9) / 100 * 360;

    // ---------- Tổng hợp máy & cảnh báo ----------

    [ObservableProperty]
    public partial string MachineSummaryText { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasActiveAlarms))]
    [NotifyCanExecuteChangedFor(nameof(AcknowledgeAllCommand))]
    public partial int ActiveAlarmCount { get; set; }

    [ObservableProperty]
    public partial bool HasCriticalAlarm { get; set; }

    [ObservableProperty]
    public partial bool IsAlarmListEmpty { get; set; }

    public bool HasActiveAlarms => ActiveAlarmCount > 0;

    [RelayCommand(CanExecute = nameof(HasActiveAlarms))]
    private void AcknowledgeAll()
    {
        foreach (var alarm in Alarms.Where(a => !a.IsAcknowledged))
        {
            alarm.IsAcknowledged = true;
        }

        RefreshAlarmSummary();
    }

    // ---------- Vòng lặp mô phỏng ----------

    public void StartSimulation()
    {
        if (_timer is not null)
        {
            return;
        }

        UpdateClock();
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += OnTick;
        _timer.Start();
    }

    public void StopSimulation()
    {
        if (_timer is null)
        {
            return;
        }

        _timer.Stop();
        _timer.Tick -= OnTick;
        _timer = null;
    }

    private void OnTick(object? sender, EventArgs e)
    {
        _tickCount++;
        UpdateClock();
        TickStopConfirmations();

        if (_tickCount % 2 != 0)
        {
            return;
        }

        UpdateMachines();
        UpdatePace();

        if (GetBucketKey() != _bucketKey)
        {
            RebuildChart();
        }

        if (_random.NextDouble() < 0.01)
        {
            var (source, message) = RandomInfoEvents[_random.Next(RandomInfoEvents.Length)];
            AddAlarm(source, message, AlarmSeverity.Info);
        }

        RefreshMetrics();
    }

    private void TickStopConfirmations()
    {
        foreach (var machine in Machines.Where(m => m.IsConfirmingStop))
        {
            machine.ConfirmSecondsLeft--;
            if (machine.ConfirmSecondsLeft <= 0)
            {
                machine.IsConfirmingStop = false;
            }
        }
    }

    // ---------- Máy ----------

    private void BuildMachines()
    {
        Machines.Add(new MachineViewModel("M1", "Cấp liệu", "kg/h", 420, 24, 38, 55, 70, MachineState.Running, ToggleMachine));
        Machines.Add(new MachineViewModel("M2", "Ép phun", "chu kỳ/phút", 8.5, 24, 205, 215, 235, MachineState.Running, ToggleMachine));
        Machines.Add(new MachineViewModel("M3", "Làm mát", "L/phút", 160, 24, 28, 36, 45, MachineState.Running, ToggleMachine));
        Machines.Add(new MachineViewModel("M4", "Kiểm tra QC", "sp/phút", 62, 24, 42, 58, 75, MachineState.Running, ToggleMachine));
        Machines.Add(new MachineViewModel("M5", "Đóng gói", "gói/phút", 48, 24, 40, 55, 70, MachineState.Idle, ToggleMachine));
    }

    private void ToggleMachine(MachineViewModel machine)
    {
        switch (machine.State)
        {
            case MachineState.Fault:
                machine.State = MachineState.Idle;
                machine.Temperature = Math.Min(machine.Temperature, machine.BaseTemp);
                AddAlarm($"{machine.Code} · {machine.Name}", "Đã reset lỗi, máy ở trạng thái dừng", AlarmSeverity.Info, acknowledged: true);
                break;

            case MachineState.Idle:
                machine.Load = 60;
                machine.Speed = machine.NominalSpeed * 0.9;
                machine.State = MachineState.Running;
                AddAlarm($"{machine.Code} · {machine.Name}", "Máy được khởi động theo lệnh vận hành", AlarmSeverity.Info, acknowledged: true);
                break;

            default:
                if (!machine.IsConfirmingStop)
                {
                    machine.ConfirmSecondsLeft = 4;
                    machine.IsConfirmingStop = true;
                    return;
                }

                machine.IsConfirmingStop = false;
                machine.State = MachineState.Idle;
                machine.Speed = 0;
                machine.Load = 0;
                AddAlarm($"{machine.Code} · {machine.Name}", "Máy được dừng theo lệnh vận hành", AlarmSeverity.Info, acknowledged: true);
                break;
        }

        UpdatePace();
        RefreshMetrics();
    }

    private void UpdateMachines()
    {
        foreach (var m in Machines)
        {
            if (m.State is MachineState.Fault or MachineState.Idle)
            {
                m.Speed = 0;
                m.Load = 0;
                m.Temperature = Approach(m.Temperature, m.AmbientTemp, 0.06, 0.2);
                continue;
            }

            m.Speed = m.NominalSpeed * (0.86 + _random.NextDouble() * 0.12);
            m.Load = Math.Clamp(m.Load + (_random.NextDouble() - 0.5) * 8, 45, 92);

            var noise = (m.WarnTemp - m.BaseTemp) * 0.05;
            if (m.State == MachineState.Running && _random.NextDouble() < 0.0003)
            {
                m.Temperature = m.FaultTemp + 1;
            }
            else if (m.State == MachineState.Running && _random.NextDouble() < 0.004)
            {
                m.Temperature = m.WarnTemp + 1 + _random.NextDouble() * (m.FaultTemp - m.WarnTemp) * 0.3;
            }
            else
            {
                m.Temperature = Approach(m.Temperature, m.BaseTemp, 0.08, noise);
            }

            var recoverTemp = m.BaseTemp + (m.WarnTemp - m.BaseTemp) * 0.6;
            var label = $"{m.Code} · {m.Name}";

            if (m.Temperature >= m.FaultTemp)
            {
                m.State = MachineState.Fault;
                m.Speed = 0;
                m.Load = 0;
                m.IsConfirmingStop = false;
                AddAlarm(label, $"Quá nhiệt {m.Temperature:0}°C, máy dừng khẩn cấp", AlarmSeverity.Critical);
            }
            else if (m.State == MachineState.Running && m.Temperature >= m.WarnTemp)
            {
                m.State = MachineState.Warning;
                AddAlarm(label, $"Nhiệt độ {m.Temperature:0}°C vượt ngưỡng cảnh báo {m.WarnTemp:0}°C", AlarmSeverity.Warning);
            }
            else if (m.State == MachineState.Warning && m.Temperature <= recoverTemp)
            {
                m.State = MachineState.Running;
                AddAlarm(label, "Nhiệt độ đã trở về mức bình thường", AlarmSeverity.Info, acknowledged: true);
            }
        }

        _defectRate += (1.6 - _defectRate) * 0.05 + (_random.NextDouble() - 0.5) * 0.2;
        if (Machines.Any(m => m.IsWarning))
        {
            _defectRate += 0.05;
        }

        _defectRate = Math.Clamp(_defectRate, 0.6, 4.0);
    }

    private double Approach(double current, double target, double rate, double noise)
        => current + (target - current) * rate + (_random.NextDouble() - 0.5) * noise;

    /// <summary>Hệ số tốc độ sản xuất theo số máy đang hoạt động (1 = đạt mục tiêu).</summary>
    private void UpdatePace()
    {
        var ratio = Machines.Count == 0 ? 0 : (double)Machines.Count(m => m.IsActive) / Machines.Count;
        _pace = Math.Min(1.05, ratio * 1.12);
    }

    // ---------- Cảnh báo ----------

    private void SeedAlarms()
    {
        var now = DateTime.Now;
        AddAlarm("M2 · Ép phun", "Nhiệt độ khuôn đang tiến gần ngưỡng cảnh báo", AlarmSeverity.Warning, time: now.AddMinutes(-12));
        AddAlarm("M5 · Đóng gói", "Cuộn màng bọc sắp hết", AlarmSeverity.Info, time: now.AddMinutes(-35));
        AddAlarm("Hệ thống", "Hoàn tất đổi mã sản phẩm SKU-2041", AlarmSeverity.Info, acknowledged: true, time: now.AddHours(-1));
    }

    private void AddAlarm(
        string source,
        string message,
        AlarmSeverity severity,
        bool acknowledged = false,
        DateTime? time = null)
    {
        var alarm = new AlarmViewModel(time ?? DateTime.Now, source, message, severity, acknowledged);
        alarm.PropertyChanged += OnAlarmPropertyChanged;

        // Seed theo thứ tự cũ -> mới nên chèn theo thời gian để danh sách luôn mới nhất ở trên.
        var index = 0;
        while (index < Alarms.Count && Alarms[index].Time > alarm.Time)
        {
            index++;
        }

        Alarms.Insert(index, alarm);

        while (Alarms.Count > MaxAlarms)
        {
            var removeAt = Alarms.Count - 1;
            for (var i = Alarms.Count - 1; i >= 0; i--)
            {
                if (Alarms[i].IsAcknowledged)
                {
                    removeAt = i;
                    break;
                }
            }

            Alarms[removeAt].PropertyChanged -= OnAlarmPropertyChanged;
            Alarms.RemoveAt(removeAt);
        }

        RefreshAlarmSummary();
    }

    private void OnAlarmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AlarmViewModel.IsAcknowledged))
        {
            RefreshAlarmSummary();
        }
    }

    private void RefreshAlarmSummary()
    {
        ActiveAlarmCount = Alarms.Count(a => !a.IsAcknowledged);
        HasCriticalAlarm = Alarms.Any(a => !a.IsAcknowledged && a.IsCritical);
        IsAlarmListEmpty = Alarms.Count == 0;
    }

    // ---------- Biểu đồ sản lượng ----------

    private (int Count, double Target, string Title, string TargetText) GetRangeConfig() => SelectedRange switch
    {
        TimeRange.Day => (12, 240, "Sản lượng theo 2 giờ  ·  24 giờ gần nhất", "Mục tiêu 240 sp / 2 giờ"),
        TimeRange.Week => (7, 2800, "Sản lượng theo ngày  ·  7 ngày gần nhất", "Mục tiêu 2.800 sp / ngày"),
        _ => (8, 120, "Sản lượng theo giờ  ·  8 giờ gần nhất", "Mục tiêu 120 sp / giờ"),
    };

    private long GetBucketKey()
    {
        var now = DateTime.Now;
        return SelectedRange switch
        {
            TimeRange.Day => now.Ticks / (TimeSpan.TicksPerHour * 2),
            TimeRange.Week => now.Date.Ticks,
            _ => now.Ticks / TimeSpan.TicksPerHour,
        };
    }

    /// <summary>Phần đã trôi qua (0..1) của khoảng thời gian hiện tại.</summary>
    private double GetLiveFraction()
    {
        var now = DateTime.Now;
        return SelectedRange switch
        {
            TimeRange.Day => ((now.Hour % 2) * 3600 + now.Minute * 60 + now.Second) / 7200.0,
            TimeRange.Week => now.TimeOfDay.TotalSeconds / 86400.0,
            _ => (now.Minute * 60 + now.Second) / 3600.0,
        };
    }

    private string GetBarLabel(DateTime now, int bucketsBack) => SelectedRange switch
    {
        TimeRange.Day => now.Date.AddHours(now.Hour / 2 * 2 - 2 * bucketsBack).ToString("HH:00", ViCulture),
        TimeRange.Week => now.Date.AddDays(-bucketsBack).ToString("dd/MM", ViCulture),
        _ => now.Date.AddHours(now.Hour - bucketsBack).ToString("HH:00", ViCulture),
    };

    private void RebuildChart()
    {
        var (count, target, title, targetText) = GetRangeConfig();
        var now = DateTime.Now;
        var chartMax = target * ChartHeadroom;

        ChartTitle = title;
        ChartTargetText = targetText;
        ChartTarget = target;

        ChartBars.Clear();
        for (var i = 0; i < count; i++)
        {
            var isLive = i == count - 1;
            var bar = new ChartBarViewModel(GetBarLabel(now, count - 1 - i), isLive);
            var value = isLive ? 0 : target * (0.9 + _random.NextDouble() * 0.16);
            bar.Update(value, chartMax, ChartPlotHeight, target);
            ChartBars.Add(bar);
        }

        _bucketKey = GetBucketKey();
        UpdateLiveBar();
    }

    private void UpdateLiveBar()
    {
        if (ChartBars.Count == 0)
        {
            return;
        }

        var target = ChartTarget;
        ChartBars[^1].Update(target * GetLiveFraction() * _pace, target * ChartHeadroom, ChartPlotHeight, target);
    }

    // ---------- KPI ----------

    private static string Signed(double value, string format)
        => (value >= 0 ? "+" : "-") + Math.Abs(value).ToString(format, ViCulture);

    private void RefreshMetrics()
    {
        UpdateLiveBar();

        // Sản lượng
        var total = ChartBars.Sum(b => b.Value);
        var expected = ChartTarget * (ChartBars.Count - 1 + GetLiveFraction());
        var outputDelta = expected > 0 ? (total / expected - 1) * 100 : 0;
        var output = Kpis[0];
        output.Title = SelectedRange switch
        {
            TimeRange.Day => "Sản lượng 24 giờ",
            TimeRange.Week => "Sản lượng 7 ngày",
            _ => "Sản lượng 8 giờ",
        };
        output.Value = total.ToString("N0", ViCulture);
        output.DeltaText = $"{Signed(outputDelta, "0.0")}% so với kế hoạch";
        output.IsDeltaGood = outputDelta >= -3;

        // OEE = Availability x Performance x Quality
        var activeMachines = Machines.Where(m => m.IsActive).ToList();
        Availability = Machines.Count == 0 ? 0 : 100.0 * activeMachines.Count / Machines.Count;
        Performance = activeMachines.Count == 0
            ? 0
            : Math.Min(100, 100.0 * activeMachines.Average(m => m.Speed / m.NominalSpeed));
        Quality = 100 - _defectRate;
        OeeValue = Availability / 100 * (Performance / 100) * (Quality / 100) * 100;

        var oee = Kpis[1];
        oee.Value = OeeText;
        oee.DeltaText = $"{Signed(OeeValue - OeeTarget, "0.0")} điểm so với mục tiêu {OeeTarget:0}%";
        oee.IsDeltaGood = IsOeeGood;

        var defect = Kpis[2];
        defect.Value = _defectRate.ToString("0.0", ViCulture);
        defect.DeltaText = $"Ngưỡng cho phép {DefectLimit.ToString("0.0", ViCulture)}%";
        defect.IsDeltaGood = _defectRate <= DefectLimit;

        // Điện năng
        var power = 0.0;
        for (var i = 0; i < Machines.Count && i < MachinePowerKw.Length; i++)
        {
            var m = Machines[i];
            power += m.State switch
            {
                MachineState.Running or MachineState.Warning => MachinePowerKw[i] * (0.55 + 0.45 * m.Load / 100),
                MachineState.Idle => MachinePowerKw[i] * 0.08,
                _ => MachinePowerKw[i] * 0.05,
            };
        }

        var powerDelta = (power / PowerBaselineKw - 1) * 100;
        var powerKpi = Kpis[3];
        powerKpi.Value = power.ToString("0", ViCulture);
        powerKpi.DeltaText = $"{Signed(powerDelta, "0.0")}% so với mức chuẩn";
        powerKpi.IsDeltaGood = powerDelta <= 5;

        MachineSummaryText = $"{activeMachines.Count}/{Machines.Count} máy đang hoạt động";
    }
}
