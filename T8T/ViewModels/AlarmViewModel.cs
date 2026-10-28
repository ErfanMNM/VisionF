using System;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using T8T.Models;

namespace T8T.ViewModels;

/// <summary>Một dòng cảnh báo/sự kiện của hệ thống.</summary>
public partial class AlarmViewModel : ObservableObject
{
    public AlarmViewModel(DateTime time, string source, string message, AlarmSeverity severity, bool acknowledged)
    {
        Time = time;
        Source = source;
        Message = message;
        Severity = severity;
        IsAcknowledged = acknowledged;
    }

    public DateTime Time { get; }
    public string Source { get; }
    public string Message { get; }
    public AlarmSeverity Severity { get; }

    [ObservableProperty]
    public partial bool IsAcknowledged { get; set; }

    public bool IsCritical => Severity == AlarmSeverity.Critical;
    public bool IsWarning => Severity == AlarmSeverity.Warning;
    public bool IsInfo => Severity == AlarmSeverity.Info;

    public string MetaText => $"{Time:HH:mm:ss}  ·  {Source}";

    [RelayCommand]
    private void Acknowledge() => IsAcknowledged = true;
}
