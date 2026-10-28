using System;
using Avalonia.Controls;
using T8T.ViewModels;

namespace T8T.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);

        // Chỉ chạy vòng mô phỏng khi cửa sổ thật sự hiển thị (không chạy trong trình xem trước).
        if (DataContext is MainViewModel vm)
        {
            vm.StartSimulation();
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
