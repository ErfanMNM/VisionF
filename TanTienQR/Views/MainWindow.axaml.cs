using System;
using Avalonia.Controls;
using TanTienQR.ViewModels;

namespace TanTienQR.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);

        // Chỉ chạy khi cửa sổ thật sự hiển thị (không chạy trong trình xem trước).
        if (DataContext is MainViewModel vm)
        {
            vm.Start(this);
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        if (DataContext is MainViewModel vm)
        {
            vm.Dispose();
        }

        base.OnClosed(e);
    }
}
