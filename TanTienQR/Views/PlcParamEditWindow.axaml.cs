using Avalonia.Controls;
using Avalonia.Interactivity;
using TanTienQR.ViewModels;

namespace TanTienQR.Views;

/// <summary>
/// Hộp thoại thêm / sửa tham số PLC tùy chỉnh.
/// </summary>
public partial class PlcParamEditWindow : Window
{
    /// <summary>True nếu người dùng đã lưu thành công.</summary>
    public bool IsSaved { get; private set; }

    public PlcParamEditWindow()
    {
        InitializeComponent();

        DataContextChanged += OnDataContextChanged;
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        Hook(DataContext as PlcParamEditViewModel);
    }

    protected override void OnClosed(EventArgs e)
    {
        Unhook(DataContext as PlcParamEditViewModel);
        DataContextChanged -= OnDataContextChanged;

        base.OnClosed(e);
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        Unhook(DataContext as PlcParamEditViewModel);
        Hook(DataContext as PlcParamEditViewModel);
    }

    private void Hook(PlcParamEditViewModel? vm)
    {
        if (vm is not null)
        {
            vm.OnSaved += OnSaved;
        }
    }

    private void Unhook(PlcParamEditViewModel? vm)
    {
        if (vm is not null)
        {
            vm.OnSaved -= OnSaved;
        }
    }

    private void OnSaved(object? sender, EventArgs e)
    {
        IsSaved = true;
        Close();
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e)
    {
        IsSaved = false;
        Close();
    }
}
