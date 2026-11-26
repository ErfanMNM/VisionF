using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using TanTienQR.ViewModels;

namespace TanTienQR.Views;

public partial class SettingsV2View : UserControl
{
    private SettingsV2ViewModel? _settings;

    public SettingsV2View()
    {
        InitializeComponent();
        Loaded += (_, _) => ScheduleSelectedNode();
        SizeChanged += (_, _) => ScheduleSelectedNode();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        ObserveSettings();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        ObserveSettings();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (_settings is not null) _settings.PropertyChanged -= OnSelectionChanged;
        _settings = null;
        base.OnDetachedFromVisualTree(e);
    }

    private void ObserveSettings()
    {
        if (_settings is not null) _settings.PropertyChanged -= OnSelectionChanged;
        _settings = (DataContext as MainViewModel)?.SettingsV2;
        if (_settings is not null) _settings.PropertyChanged += OnSelectionChanged;
        ScheduleSelectedNode();
    }

    private void OnSelectionChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SettingsV2ViewModel.SelectedSection)
            or nameof(SettingsV2ViewModel.IsOtherSettingsVisible)) ScheduleSelectedNode();
    }

    private void ScheduleSelectedNode() => Dispatcher.UIThread.Post(() =>
    {
        if (_settings is null || _settings.IsOtherSettingsVisible || !IsEffectivelyVisible) return;
        var selected = _settings.AllNodes.FirstOrDefault(node => node.IsSelected);
        var control = ReferenceEquals(selected, _settings.QrBox) ? QrBoxNode
            : this.GetVisualDescendants().OfType<Button>()
                .FirstOrDefault(button => ReferenceEquals(button.DataContext, selected));
        // BringIntoView keeps the full touch target visible at compact sizes;
        // it does not shrink labels or move the property inspector.
        control?.BringIntoView();
    }, DispatcherPriority.Loaded);
}
