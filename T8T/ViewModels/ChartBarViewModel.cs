using System;
using CommunityToolkit.Mvvm.ComponentModel;

namespace T8T.ViewModels;

/// <summary>Một cột trên biểu đồ sản lượng.</summary>
public partial class ChartBarViewModel : ObservableObject
{
    public ChartBarViewModel(string label, bool isLive)
    {
        Label = label;
        IsLive = isLive;
    }

    public string Label { get; }

    /// <summary>Cột của khoảng thời gian hiện tại, đang tăng dần.</summary>
    public bool IsLive { get; }

    [ObservableProperty]
    public partial double Value { get; set; }

    [ObservableProperty]
    public partial double Height { get; set; }

    [ObservableProperty]
    public partial bool IsBelowTarget { get; set; }

    public string ValueText => Math.Round(Value).ToString("0");

    partial void OnValueChanged(double value) => OnPropertyChanged(nameof(ValueText));

    public void Update(double value, double chartMax, double chartHeight, double target)
    {
        Value = value;
        Height = Math.Clamp(value / chartMax, 0, 1) * chartHeight;
        IsBelowTarget = !IsLive && value < target * 0.95;
    }
}
