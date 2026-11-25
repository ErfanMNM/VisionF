using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using TanTienQR.ViewModels;

namespace TanTienQR.Views;

public partial class PrintView : UserControl
{
    private PrinterRemoteViewModel? _remote;
    private bool? _lastNarrow;
    private bool? _lastExpanded;

    public PrintView()
    {
        InitializeComponent();
        WorkArea.SizeChanged += (_, _) => UpdateLayoutMode();
        RemoteScreen.SizeChanged += (_, _) => UpdatePlaceholder();
        DataContextChanged += (_, _) => ObserveRemote();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        ObserveRemote();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (_remote is not null)
            _remote.PropertyChanged -= OnRemotePropertyChanged;
        _remote = null;
        base.OnDetachedFromVisualTree(e);
    }

    private void ObserveRemote()
    {
        var remote = (DataContext as MainViewModel)?.Print.Remote;
        if (!ReferenceEquals(remote, _remote))
        {
            if (_remote is not null)
                _remote.PropertyChanged -= OnRemotePropertyChanged;
            _remote = remote;
            if (_remote is not null)
                _remote.PropertyChanged += OnRemotePropertyChanged;
        }

        UpdateLayoutMode();
    }

    private void OnRemotePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(PrinterRemoteViewModel.IsExpanded) or null or "")
            UpdateLayoutMode();
    }

    private void UpdateLayoutMode()
    {
        var expanded = _remote?.IsExpanded == true;
        var narrow = WorkArea.Bounds.Width < 1000;
        if (_lastNarrow == narrow && _lastExpanded == expanded)
            return;

        _lastNarrow = narrow;
        _lastExpanded = expanded;
        OperationPanel.IsVisible = !expanded;

        // Đổi hàng/cột trên cùng cây giao diện để giữ nguyên HWND và phiên RDP.
        if (expanded)
        {
            WorkArea.ColumnDefinitions = new ColumnDefinitions("*");
            WorkArea.RowDefinitions = new RowDefinitions("*");
            WorkArea.ColumnSpacing = 0;
            WorkArea.RowSpacing = 0;
            Grid.SetColumn(RemoteCard, 0);
            Grid.SetRow(RemoteCard, 0);
            Grid.SetColumn(OperationPanel, 0);
            Grid.SetRow(OperationPanel, 0);
        }
        else if (narrow)
        {
            WorkArea.ColumnDefinitions = new ColumnDefinitions("*");
            WorkArea.RowDefinitions = new RowDefinitions("2*,*");
            WorkArea.ColumnSpacing = 0;
            WorkArea.RowSpacing = 12;
            Grid.SetColumn(RemoteCard, 0);
            Grid.SetRow(RemoteCard, 0);
            Grid.SetColumn(OperationPanel, 0);
            Grid.SetRow(OperationPanel, 1);
        }
        else
        {
            WorkArea.ColumnDefinitions = new ColumnDefinitions("*,316");
            WorkArea.RowDefinitions = new RowDefinitions("*");
            WorkArea.ColumnSpacing = 14;
            WorkArea.RowSpacing = 0;
            Grid.SetColumn(RemoteCard, 0);
            Grid.SetRow(RemoteCard, 0);
            Grid.SetColumn(OperationPanel, 1);
            Grid.SetRow(OperationPanel, 0);
        }
    }

    private void UpdatePlaceholder()
    {
        // Trạng thái đầy đủ vẫn ở thanh dưới màn hình khi bàn phím ảo làm khung thấp.
        var height = RemoteScreen.Bounds.Height;
        PlaceholderArea.Margin = new Thickness(Math.Min(20, height / 8));
        PlaceholderIcon.IsVisible = height >= 150;
        PlaceholderTitle.IsVisible = height >= 100;
        PlaceholderStatus.IsVisible = height >= 64;
    }

    private void OnOpenCredentials(object? sender, RoutedEventArgs e)
    {
        if (_remote is not null)
            _remote.IsExpanded = false;
        CredentialExpander.IsExpanded = true;
        Dispatcher.UIThread.Post(() =>
        {
            PasswordInput.BringIntoView();
            PasswordInput.Focus();
        }, DispatcherPriority.Loaded);
    }
}
