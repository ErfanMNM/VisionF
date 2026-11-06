using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.TextInput;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using TanTienQR.ViewModels;

namespace TanTienQR.Services;

/// <summary>
/// Theo dõi ô nhập đang có focus để bật bàn phím ảo.
///
/// Hoạt động như sau:
/// - Gắn handler <c>GotFocus</c> / <c>LostFocus</c> (routing Bubble) lên
///   <see cref="TopLevel"/> của mỗi cửa sổ (cả cửa sổ chính, cửa sổ đăng nhập
///   và các hộp thoại con).
/// - Khi focus rơi vào một <see cref="TextBox"/>, mở bàn phím ảo với layout
///   phù hợp: bàn phím số nếu ô đó khai báo
///   <see cref="Avalonia.Input.TextInput.TextInputOptions.ContentType"/> là
///   Digits/Number/Pin, ngược lại là bàn phím chữ.
/// - Khi focus rời khỏi ô nhập, đóng bàn phím.
///
/// Không dùng reflection -> an toàn với Native AOT.
/// </summary>
public sealed class KeyboardService
{
    private readonly VirtualKeyboardViewModel _keyboard;

    /// <summary>Các TopLevel đã gắn handler (dùng để gỡ khi dispose).</summary>
    private readonly List<TopLevel> _hooked = [];

    /// <summary>Ô đang nhận focus (null = không có).</summary>
    private TextBox? _focused;

    /// <summary>
    /// Khởi tạo service.
    /// </summary>
    /// <param name="keyboard">ViewModel của bàn phím ảo.</param>
    public KeyboardService(VirtualKeyboardViewModel keyboard)
    {
        _keyboard = keyboard;
    }

    /// <summary>ViewModel của bàn phím ảo (để bind vào XAML).</summary>
    public VirtualKeyboardViewModel Keyboard => _keyboard;

    /// <summary>
    /// Gắn theo dõi focus cho một cửa sổ.
    /// </summary>
    /// <param name="topLevel">Cửa sổ cần theo dõi.</param>
    public void Attach(TopLevel topLevel)
    {
        ArgumentNullException.ThrowIfNull(topLevel);

        if (_hooked.Contains(topLevel))
        {
            return;
        }

        topLevel.AddHandler(
            InputElement.GotFocusEvent,
            OnGotFocus,
            RoutingStrategies.Bubble | RoutingStrategies.Direct);

        topLevel.AddHandler(
            InputElement.LostFocusEvent,
            OnLostFocus,
            RoutingStrategies.Bubble | RoutingStrategies.Direct);

        _hooked.Add(topLevel);
    }

    /// <summary>Gỡ toàn bộ handler (khi app đóng).</summary>
    public void DetachAll()
    {
        foreach (var top in _hooked)
        {
            top.RemoveHandler(InputElement.GotFocusEvent, OnGotFocus);
            top.RemoveHandler(InputElement.LostFocusEvent, OnLostFocus);
        }

        _hooked.Clear();
        _focused = null;
        _keyboard.Close();
    }

    private void OnGotFocus(object? sender, RoutedEventArgs e)
    {
        // e.Source là phần tử vừa nhận focus -> tìm TextBox trong cây.
        var textBox = FindTextBox(e.Source as Visual);
        if (textBox is null)
        {
            return;
        }

        _focused = textBox;
        _keyboard.Open(
            textBox,
            ResolveLayout(textBox),
            BuildHint(textBox),
            (sender as TopLevel) ?? TopLevel.GetTopLevel(textBox));
    }

    private void OnLostFocus(object? sender, RoutedEventArgs e)
    {
        // e.Source là phần tử vừa MẤT focus -> tìm TextBox trong cây.
        var textBox = FindTextBox(e.Source as Visual);
        if (textBox is null)
        {
            return;
        }

        // Nếu focus mới vẫn nằm bên trong cùng ô nhập (ví dụ bấm vào nút
        // hiện/ẩn mật khẩu nằm trong TextBox) -> giữ bàn phím.
        var manager = (sender as TopLevel)?.FocusManager;
        var newFocus = manager?.GetFocusedElement();
        if (newFocus is Visual v && textBox.IsVisualAncestorOf(v))
        {
            return;
        }

        if (ReferenceEquals(_focused, textBox))
        {
            _focused = null;
            _keyboard.Close();
        }
    }

    /// <summary>
    /// Quyết định bàn phím nào hiển thị cho ô nhập này.
    /// </summary>
    /// <param name="textBox">Ô nhập.</param>
    /// <returns>Layout bàn phím phù hợp.</returns>
    private static KeyboardLayout ResolveLayout(TextBox textBox)
    {
        // 1) ContentType được khai báo rõ -> tin theo đó.
        var contentType = TextInputOptions.GetContentType(textBox);
        switch (contentType)
        {
            case TextInputContentType.Digits:
            case TextInputContentType.Number:
            case TextInputContentType.Pin:
                return KeyboardLayout.Numeric;
        }

        // 2) Ô chỉ nhận số: MaxLength <= 6 và toàn bộ nội dung hiện tại là số
        //    -> bàn phím số (IP, port, địa chỉ D12...).
        if (textBox.MaxLength is > 0 and <= 6 && LooksNumeric(textBox.Text))
        {
            return KeyboardLayout.Numeric;
        }

        return KeyboardLayout.Alpha;
    }

    /// <summary>Kiểm tra chuỗi có toàn số (hoặc rỗng) không.</summary>
    private static bool LooksNumeric(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return true;
        }

        foreach (var c in text)
        {
            if (!char.IsDigit(c) && c != '.' && c != '-' && c != ':')
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Tạo nhãn gợi ý cho thanh bàn phím.</summary>
    private static string BuildHint(TextBox textBox)
    {
        // Ưu tiên PlaceholderText của ô nhập.
        if (!string.IsNullOrWhiteSpace(textBox.PlaceholderText))
        {
            return textBox.PlaceholderText;
        }

        return textBox.Name is { Length: > 0 } name ? name : "Nhập liệu";
    }

    /// <summary>Tìm TextBox chứa phần tử được focus (đi ngược cây Visual).</summary>
    private static TextBox? FindTextBox(Visual? visual)
    {
        while (visual is not null)
        {
            if (visual is TextBox box)
            {
                return box;
            }

            visual = visual.GetVisualParent();
        }

        return null;
    }
}
