using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using TanTienQR.Views;

internal static class ConnectionConfirmationChecks
{
    private const string Intro = "Lưu và áp dụng các thay đổi sau?";
    private const string Notice = "Kết nối thiết bị sẽ tạm ngắt để chuyển cấu hình. Ứng dụng và phiên đăng nhập vẫn được giữ; thiết bị sẽ tự kết nối lại. Chỉ xác nhận khi dây chuyền có thể tạm dừng nhận mã.";
    private const string Development = "Lưu ý: cấu hình Development có thể ghi đè các giá trị này ở lần khởi động sau.";

    public static void Run(Action<string, bool, string> check, string output)
    {
        Console.WriteLine("\n[Hộp thoại] Xác nhận cấu hình Camera / PLC");
        Directory.CreateDirectory(output);
        var owner = new Window { Width = 1000, Height = 820 };
        owner.Show();
        Dispatcher.UIThread.RunJobs();
        try
        {
            var shortChanges = new[]
            {
                "Camera · Địa chỉ IP: 192.168.1.10 → 192.168.1.20",
                "PLC · Cổng kết nối: 9600 → 9601",
            };
            var shortMessage = Message(shortChanges, Development);
            var dialog = Open(shortMessage);
            Check("Hai dòng thay đổi giữ đủ nội dung và nhãn", Rows(dialog)
                .Select(row => row.Device + " · " + row.Description).SequenceEqual(shortChanges));
            Check("Cảnh báo và Development tách riêng, giữ nguyên văn",
                dialog.FindControl<TextBlock>("ConnectionNoticeText")!.Text == Notice
                && dialog.FindControl<TextBlock>("DevelopmentNoteText")!.Text == Development);
            var apply = dialog.FindControl<Button>("ApplyConfigurationButton")!;
            var cancel = dialog.FindControl<Button>("CancelConfigurationButton")!;
            Check("Nút dùng primary/soft, vùng bấm 44px, áp dụng không IsDefault",
                apply.Classes.Contains("primary") && cancel.Classes.Contains("soft")
                && apply.Bounds.Height >= 44 && cancel.Bounds.Height >= 44 && !apply.IsDefault && cancel.IsCancel);
            Check("Nút chính nhận màu Accent từ App.axaml", apply.Background is ISolidColorBrush brush
                && brush.Color == Color.Parse("#1A56DB"));
            Capture(dialog, "confirmation-short.png");
            dialog.KeyPress(Key.Tab, RawInputModifiers.None, PhysicalKey.Tab, "\t");
            dialog.KeyRelease(Key.Tab, RawInputModifiers.None, PhysicalKey.Tab, "\t");
            Pump(dialog);
            Check("Tab thật chuyển focus tới nút áp dụng và hiện focus-visible",
                ReferenceEquals(dialog.FocusManager?.GetFocusedElement(), apply)
                && apply.Classes.Contains(":focus-visible"));
            Capture(dialog, "confirmation-keyboard-focus.png");
            dialog.Close(false);

            dialog = Open(Message(new[] { "Camera · Port TCP: 9000 → 9001" }));
            Check("Một thay đổi không Development vẫn tách đúng nội dung",
                Rows(dialog).Single().Description == "Port TCP: 9000 → 9001"
                && !dialog.FindControl<Border>("DevelopmentNoteCard")!.IsVisible);
            Capture(dialog, "confirmation-single-camera.png");
            dialog.Close(false);

            var longChanges = Enumerable.Range(1, 24).Select(index =>
                $"{(index % 2 == 1 ? "Camera" : "PLC")} · Thông số kết nối {index:00}: "
                + $"tuyến hiện tại {index}: D100 → tuyến mới {index}: D200 · giữ nguyên toàn bộ giá trị").ToArray();
            var longNote = Development + "\n\n" + string.Join(" ", Enumerable.Repeat(
                "Kiểm tra cấu hình phát triển trước lần khởi động tiếp theo.", 20)) + " KẾT THÚC GHI CHÚ.";
            dialog = Open(Message(longChanges, longNote));
            Check("Danh sách dài giữ đủ 24 dòng, kể cả dấu phân cách trong giá trị", Rows(dialog)
                .Select(row => row.Device + " · " + row.Description).SequenceEqual(longChanges));
            Check("Ghi chú dài không bị cắt hoặc lược nội dung",
                dialog.FindControl<TextBlock>("DevelopmentNoteText")!.Text == longNote);
            var scroll = dialog.FindControl<ScrollViewer>("ConfigurationDetailsScroll")!;
            Check("Nội dung dài có vùng cuộn, cửa sổ không vượt MaxHeight",
                scroll.Extent.Height > scroll.Viewport.Height && dialog.Bounds.Height <= dialog.MaxHeight);
            Check("Nội dung dài không tạo vùng cuộn ngang",
                scroll.HorizontalScrollBarVisibility == Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled
                && scroll.Extent.Width <= scroll.Viewport.Width + 1);
            Capture(dialog, "confirmation-long-top.png");
            var lastDescription = Rows(dialog).Last().Description;
            var lastRow = dialog.GetVisualDescendants().OfType<TextBlock>()
                .Single(block => block.Text == lastDescription);
            lastRow.BringIntoView();
            Pump(dialog);
            Check("Cuộn tới dòng thay đổi cuối được", InsideScroll(lastRow, scroll));
            Capture(dialog, "confirmation-long-last-change.png");
            scroll.ScrollToEnd();
            Pump(dialog);
            var note = dialog.FindControl<TextBlock>("DevelopmentNoteText")!;
            var noteBottom = note.TranslatePoint(new Point(0, note.Bounds.Height), scroll);
            Check("Cuộn tới cuối ghi chú dài được", scroll.Offset.Y > 0 && noteBottom is { } bottom
                && bottom.Y <= scroll.Bounds.Height + 1 && bottom.Y > 0);
            Check("Footer và nút áp dụng vẫn hiện khi cuộn", InsideWindow(dialog.FindControl<Button>("ApplyConfigurationButton")!, dialog));
            Capture(dialog, "confirmation-long-bottom.png");
            dialog.Close(false);

            foreach (var unusual in new[]
            {
                "Thông điệp mới: a:b → c · d\r\n\r\n" + new string('X', 1800) + "\nDÒNG CUỐI.",
                Message(new[] { "Camera · Địa chỉ: a:b → c\n\nGiá trị nhiều dòng · → :" }, Development),
                "",
            })
            {
                dialog = Open(unusual);
                Check("Message lạ/nhiều dòng giữ nguyên văn trong fallback",
                    dialog.FindControl<Border>("OriginalMessageCard")!.IsVisible
                    && dialog.FindControl<TextBlock>("OriginalMessageText")!.Text == unusual);
                dialog.Close(false);
            }

            CheckResult("Bấm Hủy trả false", "CancelConfigurationButton", false);
            CheckResult("Bấm Lưu và áp dụng trả true", "ApplyConfigurationButton", true);
            dialog = new ConnectionConfigurationConfirmWindow(shortMessage);
            var closeResult = dialog.ShowDialog<bool>(owner);
            Pump(dialog);
            dialog.Close();
            Dispatcher.UIThread.RunJobs();
            Check("Đóng cửa sổ mặc định trả false", closeResult.IsCompletedSuccessfully && !closeResult.Result);
            foreach (var key in new[] { Key.Escape, Key.Enter })
            {
                dialog = new ConnectionConfigurationConfirmWindow(shortMessage);
                var result = dialog.ShowDialog<bool>(owner);
                Pump(dialog);
                var physicalKey = key == Key.Escape ? PhysicalKey.Escape : PhysicalKey.Enter;
                dialog.KeyPress(key, RawInputModifiers.None, physicalKey, key == Key.Enter ? "\r" : null);
                // Escape/Enter trên Hủy có thể đóng ngay ở KeyDown.
                if (!result.IsCompleted)
                    dialog.KeyRelease(key, RawInputModifiers.None, physicalKey, key == Key.Enter ? "\r" : null);
                Dispatcher.UIThread.RunJobs();
                Check(key == Key.Escape ? "Escape trả false" : "Enter khi mở không tự áp dụng",
                    key == Key.Escape ? result.IsCompletedSuccessfully && !result.Result
                        : !result.IsCompleted || (result.IsCompletedSuccessfully && !result.Result));
                if (!result.IsCompleted) dialog.Close(false);
            }

            ConnectionConfigurationConfirmWindow Open(string message)
            {
                var window = new ConnectionConfigurationConfirmWindow(message);
                _ = window.ShowDialog<bool>(owner);
                Pump(window);
                return window;
            }

            void CheckResult(string label, string buttonName, bool expected)
            {
                var window = new ConnectionConfigurationConfirmWindow(shortMessage);
                var result = window.ShowDialog<bool>(owner);
                Pump(window);
                var button = window.FindControl<Button>(buttonName)!;
                var point = button.TranslatePoint(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), window)!.Value;
                window.MouseDown(point, MouseButton.Left, RawInputModifiers.None);
                window.MouseUp(point, MouseButton.Left, RawInputModifiers.None);
                Dispatcher.UIThread.RunJobs();
                Check(label, result.IsCompletedSuccessfully && result.Result == expected);
                if (!result.IsCompleted) window.Close(false);
            }
        }
        catch (Exception ex)
        {
            check("Hộp thoại tạo và render được", false, ex.ToString());
        }
        finally
        {
            owner.Close();
            Dispatcher.UIThread.RunJobs();
        }

        void Check(string label, bool passed) => check(label, passed, "");

        void Capture(Window window, string fileName)
        {
            Pump(window);
            using var bitmap = window.CaptureRenderedFrame()
                ?? throw new InvalidOperationException("Không lấy được frame headless của hộp thoại.");
            var path = Path.Combine(output, fileName);
            bitmap.Save(path, new PngBitmapEncoderOptions());
            Console.WriteLine("  [Ảnh] " + path);
        }
    }

    private static string Message(IEnumerable<string> changes, string? note = null) =>
        Intro + "\n\n" + string.Join("\n", changes) + "\n\n" + Notice
        + (note is null ? "" : "\n\n" + note);

    private static IEnumerable<ConfigurationChangeDisplay> Rows(Window dialog) =>
        dialog.FindControl<ItemsControl>("ConfigurationChanges")!.ItemsSource!.Cast<ConfigurationChangeDisplay>();

    private static void Pump(Window window)
    {
        for (var frame = 0; frame < 3; frame++)
        {
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
    }

    private static bool InsideScroll(Control control, ScrollViewer scroll) =>
        control.TranslatePoint(new Point(), scroll) is { } point
        && point.Y >= 0 && point.Y + control.Bounds.Height <= scroll.Bounds.Height + 1;

    private static bool InsideWindow(Control control, Window window) =>
        control.TranslatePoint(new Point(), window) is { } point
        && point.Y >= 0 && point.Y + control.Bounds.Height <= window.Bounds.Height + 1;
}
