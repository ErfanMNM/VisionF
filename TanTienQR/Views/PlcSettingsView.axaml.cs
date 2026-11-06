using System;
using Avalonia.Controls;
using Avalonia.Interactivity;
using TanTienQR.ViewModels;

namespace TanTienQR.Views;

/// <summary>
/// Tab "Chỉnh PLC": webview xem camera + bảng tham số PLC + khung recipe.
///
/// <c>NativeWebView</c> dùng engine native của hệ điều hành (WebView2 trên
/// Windows) nên không cần nhúng Chromium — nhẹ hơn nhiều so với CefSharp.
/// </summary>
public partial class PlcSettingsView : UserControl
{
    public PlcSettingsView()
    {
        InitializeComponent();
    }

    private MainViewModel? ViewModel => DataContext as MainViewModel;

    /// <summary>
    /// Tải lại trang camera (khi trang trắng hoặc lỗi thì điều hướng lại từ đầu).
    /// </summary>
    private void OnReloadCameraClick(object? sender, RoutedEventArgs e)
    {
        var vm = ViewModel;
        if (vm is null)
        {
            return;
        }

        var uri = vm.PlcSettings.CameraMonitorUri;
        if (uri.AbsoluteUri == "about:blank")
        {
            return;
        }

        if (CameraBrowser.Source is null || CameraBrowser.Source.AbsoluteUri == "about:blank")
        {
            CameraBrowser.Navigate(uri);
        }
        else
        {
            CameraBrowser.Refresh();
        }
    }

    /// <summary>
    /// Mở trang camera trong trình duyệt mặc định của hệ điều hành.
    /// </summary>
    private async void OnOpenCameraExternalClick(object? sender, RoutedEventArgs e)
    {
        var vm = ViewModel;
        if (vm is null)
        {
            return;
        }

        var uri = vm.PlcSettings.CameraMonitorUri;
        if (uri.AbsoluteUri == "about:blank")
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
            await launcher.LaunchUriAsync(uri);
        }
        catch (Exception)
        {
            // Không có trình duyệt mặc định: bỏ qua, người dùng vẫn xem được trong khung nhúng.
        }
    }

    private void OnCameraNavigationStarted(object? sender, WebViewNavigationStartingEventArgs e)
    {
        // Không cần xử lý thêm: trạng thái đã hiển thị qua thông báo chung.
    }

    private void OnCameraNavigationCompleted(object? sender, WebViewNavigationCompletedEventArgs e)
    {
        // Không cần xử lý thêm: trạng thái đã hiển thị qua thông báo chung.
    }
}
