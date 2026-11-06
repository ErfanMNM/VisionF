using System;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using TanTienQR.ViewModels;

namespace TanTienQR.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    // Kéo di chuyển cửa sổ từ thanh tiêu đề tự vẽ (bỏ qua khi bấm vào nút).
    private void OnHeaderPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Source is Control src && (src is Button || src.FindAncestorOfType<Button>() is not null))
            return;

        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;

        // Double-click thanh tiêu đề: phóng to / khôi phục.
        if (e.ClickCount == 2)
        {
            ToggleMaxRestore();
            return;
        }

        BeginMoveDrag(e);
    }

    // Bật/tắt menu cửa sổ (popup hamburger).
    private void OnWindowMenuClick(object? sender, RoutedEventArgs e)
    {
        if (this.FindControl<Avalonia.Controls.Primitives.Popup>("WindowMenuPopup") is { } popup)
            popup.IsOpen = !popup.IsOpen;
    }

    private void CloseWindowMenu()
    {
        if (this.FindControl<Avalonia.Controls.Primitives.Popup>("WindowMenuPopup") is { } popup)
            popup.IsOpen = false;
    }

    private void OnMinimizeClick(object? sender, RoutedEventArgs e)
    {
        CloseWindowMenu();
        WindowState = WindowState.Minimized;
    }

    private void OnMaxRestoreClick(object? sender, RoutedEventArgs e)
    {
        CloseWindowMenu();
        ToggleMaxRestore();
    }

    private void ToggleMaxRestore()
    {
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    }

    private void OnShutdownClick(object? sender, RoutedEventArgs e)
    {
        CloseWindowMenu();
        if (DataContext is MainViewModel vm && vm.ShutdownAppCommand.CanExecute(null))
            vm.ShutdownAppCommand.Execute(null);
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
            // Nếu người dùng bấm "Đăng xuất" thì chỉ dừng tạm (app còn chạy để
            // đăng nhập lại) — chỉ dispose khi cửa sổ đóng hẳn (thoát app).
            if (vm.IsLogoutRequested)
            {
                vm.Stop();
            }
            else
            {
                vm.Dispose();
            }
        }

        base.OnClosed(e);
    }
}
