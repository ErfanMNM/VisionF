using System;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using T8T.Models;

namespace T8T.ViewModels;

/// <summary>Một trạm/máy trên dây chuyền sản xuất, kèm số liệu đo giả lập.</summary>
public partial class MachineViewModel : ObservableObject
{
    private static readonly CultureInfo Vi = MainViewModel.ViCulture;

    private readonly Action<MachineViewModel> _toggle;

    public MachineViewModel(
        string code,
        string name,
        string speedUnit,
        double nominalSpeed,
        double ambientTemp,
        double baseTemp,
        double warnTemp,
        double faultTemp,
        MachineState initialState,
        Action<MachineViewModel> toggle)
    {
        Code = code;
        Name = name;
        SpeedUnit = speedUnit;
        NominalSpeed = nominalSpeed;
        AmbientTemp = ambientTemp;
        BaseTemp = baseTemp;
        WarnTemp = warnTemp;
        FaultTemp = faultTemp;
        _toggle = toggle;

        State = initialState;
        var running = initialState is MachineState.Running or MachineState.Warning;
        Speed = running ? nominalSpeed * 0.9 : 0;
        Load = running ? 68 : 0;
        Temperature = running ? baseTemp : ambientTemp;
    }

    public string Code { get; }
    public string Name { get; }
    public string SpeedUnit { get; }
    public double NominalSpeed { get; }
    public double AmbientTemp { get; }
    public double BaseTemp { get; }
    public double WarnTemp { get; }
    public double FaultTemp { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRunning))]
    [NotifyPropertyChangedFor(nameof(IsWarning))]
    [NotifyPropertyChangedFor(nameof(IsFault))]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    [NotifyPropertyChangedFor(nameof(IsActive))]
    [NotifyPropertyChangedFor(nameof(StateText))]
    [NotifyPropertyChangedFor(nameof(ActionText))]
    public partial MachineState State { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SpeedText))]
    public partial double Speed { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TemperatureText))]
    public partial double Temperature { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LoadText))]
    public partial double Load { get; set; }

    /// <summary>Đang chờ người dùng bấm lần hai để xác nhận dừng máy.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActionText))]
    public partial bool IsConfirmingStop { get; set; }

    /// <summary>Số giây còn lại của cửa sổ xác nhận dừng.</summary>
    public int ConfirmSecondsLeft { get; set; }

    public bool IsRunning => State == MachineState.Running;
    public bool IsWarning => State == MachineState.Warning;
    public bool IsFault => State == MachineState.Fault;
    public bool IsIdle => State == MachineState.Idle;
    public bool IsActive => State is MachineState.Running or MachineState.Warning;

    public string StateText => State switch
    {
        MachineState.Running => "CHẠY",
        MachineState.Warning => "CẢNH BÁO",
        MachineState.Fault => "LỖI",
        _ => "DỪNG",
    };

    public string ActionText => State switch
    {
        MachineState.Fault => "Reset lỗi",
        MachineState.Idle => "Khởi động",
        _ => IsConfirmingStop ? "Xác nhận dừng" : "Dừng máy",
    };

    public string SpeedText => $"{Speed.ToString("0.#", Vi)} {SpeedUnit}";
    public string TemperatureText => $"{Temperature.ToString("0", Vi)} °C";
    public string LoadText => $"{Load.ToString("0", Vi)}%";

    [RelayCommand]
    private void Toggle() => _toggle(this);
}
