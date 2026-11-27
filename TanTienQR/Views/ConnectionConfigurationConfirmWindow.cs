using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;

namespace TanTienQR.Views;

/// <summary>Xác nhận đúng snapshot trước khi lưu và kết nối lại thiết bị.</summary>
public sealed partial class ConnectionConfigurationConfirmWindow : Window
{
    private const string Introduction = "Lưu và áp dụng các thay đổi sau?";
    private const string ConnectionNotice = "Kết nối thiết bị sẽ tạm ngắt để chuyển cấu hình. Ứng dụng và phiên đăng nhập vẫn được giữ; thiết bị sẽ tự kết nối lại. Chỉ xác nhận khi dây chuyền có thể tạm dừng nhận mã.";
    private const string MissingMessage = "Không có thay đổi cấu hình để áp dụng.";

    public ConnectionConfigurationConfirmWindow() : this(MissingMessage, canApply: false) { }

    public ConnectionConfigurationConfirmWindow(string message) : this(message, canApply: true) { }

    private ConnectionConfigurationConfirmWindow(string message, bool canApply)
    {
        AvaloniaXamlLoader.Load(this);
        var cancel = this.FindControl<Button>("CancelConfigurationButton")!;
        var apply = this.FindControl<Button>("ApplyConfigurationButton")!;
        apply.IsEnabled = canApply;
        cancel.Click += (_, _) => Close(false);
        apply.Click += (_, _) => Close(true);

        // Focus an toàn khi mở; Enter không mặc nhiên xác nhận thay đổi thiết bị.
        Opened += (_, _) => cancel.Focus(NavigationMethod.Unspecified);
        KeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            e.Handled = true;
            Close(false);
        };

        ShowMessage(message);
    }

    private void ShowMessage(string message)
    {
        // Chỉ tách envelope hiện có của VM. Giá trị chứa dấu phân cách được giữ
        // nguyên; chuỗi mới/lạ hoặc nhiều dòng không rõ nghĩa dùng bản nguyên văn.
        var normalized = message.Replace("\r\n", "\n", StringComparison.Ordinal);
        var prefix = Introduction + "\n\n";
        var separator = "\n\n" + ConnectionNotice;
        var noticeIndex = normalized.LastIndexOf(separator, StringComparison.Ordinal);
        if (normalized.StartsWith(prefix, StringComparison.Ordinal) && noticeIndex >= prefix.Length)
        {
            var changes = normalized[prefix.Length..noticeIndex].Split('\n');
            var remaining = normalized[(noticeIndex + separator.Length)..];
            if (changes.All(IsDeviceChange)
                && (remaining.Length == 0 || remaining.StartsWith("\n\nLưu ý: ", StringComparison.Ordinal)))
            {
                this.FindControl<TextBlock>("ConfirmationIntroduction")!.Text = Introduction;
                this.FindControl<ItemsControl>("ConfigurationChanges")!.ItemsSource = changes.Select(line =>
                {
                    var device = line.StartsWith("Camera · ", StringComparison.Ordinal) ? "Camera" : "PLC";
                    return new ConfigurationChangeDisplay(device, line[(device.Length + 3)..]);
                }).ToArray();
                this.FindControl<TextBlock>("ConfigurationChangeCount")!.Text = $"{changes.Length} thay đổi";
                this.FindControl<TextBlock>("ConnectionNoticeText")!.Text = ConnectionNotice;
                if (remaining.Length > 0)
                {
                    this.FindControl<Border>("DevelopmentNoteCard")!.IsVisible = true;
                    this.FindControl<TextBlock>("DevelopmentNoteText")!.Text = remaining[2..];
                }
                return;
            }
        }

        this.FindControl<Border>("ChangesCard")!.IsVisible = false;
        this.FindControl<Border>("ConnectionNoticeCard")!.IsVisible = false;
        this.FindControl<Border>("OriginalMessageCard")!.IsVisible = true;
        this.FindControl<TextBlock>("OriginalMessageText")!.Text = message;
    }

    private static bool IsDeviceChange(string line) =>
        line.StartsWith("Camera · ", StringComparison.Ordinal)
        || line.StartsWith("PLC · ", StringComparison.Ordinal);
}

/// <summary>Chỉ tách nhãn thiết bị; giữ trọn tên trường và hai giá trị để đọc.</summary>
public sealed record ConfigurationChangeDisplay(string Device, string Description);
