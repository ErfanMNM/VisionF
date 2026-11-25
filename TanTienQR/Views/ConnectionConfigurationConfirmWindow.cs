using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace TanTienQR.Views;

/// <summary>Xác nhận đúng snapshot trước khi lưu và kết nối lại thiết bị.</summary>
public sealed class ConnectionConfigurationConfirmWindow : Window
{
    public ConnectionConfigurationConfirmWindow(string message)
    {
        Title = "Xác nhận cấu hình Camera / PLC";
        Width = 620;
        MaxHeight = 720;
        MinHeight = 260;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var cancel = new Button { Name = "CancelConfigurationButton", Content = "Hủy", IsCancel = true, MinHeight = 44, MinWidth = 96 };
        var apply = new Button { Name = "ApplyConfigurationButton", Content = "Lưu và áp dụng", MinHeight = 44, MinWidth = 150 };
        cancel.Click += (_, _) => Close(false);
        apply.Click += (_, _) => Close(true);
        var layout = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto"), Margin = new Thickness(24), RowSpacing = 18 };
        layout.Children.Add(new TextBlock { Text = "Áp dụng thay đổi thiết bị?", FontSize = 21, FontWeight = FontWeight.Bold });
        var details = new ScrollViewer { MaxHeight = 480, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, FontSize = 13, LineHeight = 21 } };
        Grid.SetRow(details, 1);
        layout.Children.Add(details);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, HorizontalAlignment = HorizontalAlignment.Right,
            Children = { cancel, apply } };
        Grid.SetRow(actions, 2);
        layout.Children.Add(actions);
        Content = layout;
    }
}
