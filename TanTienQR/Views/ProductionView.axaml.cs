using System;
using Avalonia.Controls;
using Avalonia.Interactivity;
using TanTienQR.ViewModels;

namespace TanTienQR.Views;

/// <summary>
/// Tab Sản xuất: nhúng trang web Checkee bằng <c>NativeWebView</c>.
///
/// <c>NativeWebView</c> dùng engine native của hệ điều hành (WebView2 trên
/// Windows) nên không cần nhúng Chromium — nhẹ hơn nhiều so với CefSharp.
/// </summary>
public partial class ProductionView : UserControl
{
    public ProductionView()
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
            Browser.Navigate(new Uri(vm.WebUrl));
        }
        else
        {
            Browser.Refresh();
        }
    }

    private async void OnOpenExternalClick(object? sender, RoutedEventArgs e)
    {
        var vm = ViewModel;
        if (vm is null)
        {
            return;
        }

        var launcher = TopLevel.GetTopLevel(this)?.Launcher;
        if (launcher is null)
        {
            return;
        }

        try
        {
            await launcher.LaunchUriAsync(new Uri(vm.WebUrl));
        }
        catch (Exception)
        {
            // Không có trình duyệt mặc định: bỏ qua, người dùng vẫn xem được trong khung nhúng.
        }
    }
}
