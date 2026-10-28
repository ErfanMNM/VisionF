using System;
using Avalonia.Controls;
using Avalonia.Interactivity;
using T8T.ViewModels;

namespace T8T.Views;

public partial class WebDashboardView : UserControl
{
    public WebDashboardView()
    {
        InitializeComponent();
    }

    private MainViewModel? ViewModel => DataContext as MainViewModel;

    private void OnNavigationStarted(object? sender, WebViewNavigationStartingEventArgs e)
    {
        ViewModel?.OnWebNavigationStarted();
        UpdateHistoryButtons();
    }

    private void OnNavigationCompleted(object? sender, WebViewNavigationCompletedEventArgs e)
    {
        ViewModel?.OnWebNavigationCompleted(e.IsSuccess);
        UpdateHistoryButtons();
    }

    private void UpdateHistoryButtons()
    {
        BackButton.IsEnabled = Browser.CanGoBack;
        ForwardButton.IsEnabled = Browser.CanGoForward;
    }

    private void OnBackClick(object? sender, RoutedEventArgs e) => Browser.GoBack();

    private void OnForwardClick(object? sender, RoutedEventArgs e) => Browser.GoForward();

    private void OnReloadClick(object? sender, RoutedEventArgs e)
    {
        var vm = ViewModel;
        if (vm is null)
        {
            return;
        }

        // Sau lỗi tải hoặc khi chưa có trang nào, Refresh không đủ nên điều hướng lại từ đầu.
        if (vm.HasWebError || Browser.Source is null || Browser.Source.AbsoluteUri == "about:blank")
        {
            Browser.Navigate(new Uri(MainViewModel.DashboardUrl));
        }
        else
        {
            Browser.Refresh();
        }
    }

    private async void OnOpenExternalClick(object? sender, RoutedEventArgs e)
    {
        var launcher = TopLevel.GetTopLevel(this)?.Launcher;
        if (launcher is null)
        {
            return;
        }

        try
        {
            await launcher.LaunchUriAsync(new Uri(MainViewModel.DashboardUrl));
        }
        catch (Exception)
        {
            // Không có trình duyệt mặc định: bỏ qua, người dùng vẫn xem được trong khung nhúng.
        }
    }
}
