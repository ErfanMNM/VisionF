using CommunityToolkit.Mvvm.ComponentModel;

namespace T8T.ViewModels;

/// <summary>Một thẻ chỉ số KPI ở đầu màn hình SCADA.</summary>
public partial class KpiViewModel : ObservableObject
{
    public KpiViewModel(string title, string unit)
    {
        Title = title;
        Unit = unit;
        Value = "--";
        DeltaText = string.Empty;
        IsDeltaGood = true;
    }

    public string Unit { get; }

    [ObservableProperty]
    public partial string Title { get; set; }

    [ObservableProperty]
    public partial string Value { get; set; }

    [ObservableProperty]
    public partial string DeltaText { get; set; }

    [ObservableProperty]
    public partial bool IsDeltaGood { get; set; }
}
