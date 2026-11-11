using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.TextInput;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using TanTienQR.Controls;
using TanTienQR.ViewModels;

namespace TanTienQR.Services;

/// <summary>
/// Theo dõi ô nhập đang có focus để bật bàn phím ảo.
///
/// Hoạt động như sau:
/// - Gắn handler <c>GotFocus</c> / <c>LostFocus</c> (routing Bubble) lên
///   <see cref="TopLevel"/> của mỗi cửa sổ (cả cửa sổ chính, cửa sổ đăng nhập
///   và các hộp thoại con).
/// - Gắn thêm handler <c>PointerPressed</c>: khi người dùng CHẠM vào một ô
///   nhập thì mở bàn phím NGAY, kể cả khi ô đó đã có focus sẵn.
///
///   Đây là điểm mấu chốt: nhiều cửa sổ tự focus vào ô nhập đầu tiên khi
///   mở (xem <c>LoginShellWindow.FocusStepInput</c>). Nếu chỉ dựa vào
///   <c>GotFocus</c> thì lần chạm đầu tiên sẽ KHÔNG sinh sự kiện focus mới
///   (focus không đổi) -> bàn phím không mở -> người dùng phải nhấn đúp.
///   Bắt <c>PointerPressed</c> giải quyết đúng trường hợp này.
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
    /// Đánh dấu một ô nhập để bàn phím ảo toàn cục KHÔNG mở.
    ///
    /// Một số ô nhập trong ứng dụng có bàn phím riêng (vd bàn phím số nhỏ
    /// trong bảng ô nhớ PLC) — nếu bàn phím toàn cục cũng mở thì người dùng
    /// sẽ thấy hai bàn phím chồng lên nhau.
    ///
    /// Cách dùng: đặt <c>Tag="plc-keypad"</c> cho TextBox (xem
    /// <see cref="PlcKeypadTag"/>) hoặc đặt <c>Name="PlcKeypad"</c>.
    /// </summary>
    public const string PlcKeypadTag = "plc-keypad";

    /// <summary>
    /// Tên control của ô nhập dùng bàn phím riêng (cũng bị bỏ qua).
    /// </summary>
    public const string PlcKeypadName = "PlcKeypad";

    /// <summary>
    /// Kiểm tra ô nhập này có dùng bàn phím riêng hay không.
    ///
    /// </summary>
    /// <param name="textBox">Ô nhập cần kiểm tra.</param>
    /// <returns>True nếu bàn phím toàn cục không nên mở cho ô này.</returns>
    public static bool UsesOwnKeypad(TextBox textBox)
    {
        if (textBox.Tag is string tag
            && string.Equals(tag, PlcKeypadTag, StringComparison.Ordinal))
        {
            return true;
        }

        return string.Equals(textBox.Name, PlcKeypadName, StringComparison.Ordinal);
    }

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

        // Bắt thao tác CHẠM vào ô nhập -> mở bàn phím ngay (fix nhấn đúp).
        topLevel.AddHandler(
            InputElement.PointerPressedEvent,
            OnPointerPressed,
            RoutingStrategies.Bubble | RoutingStrategies.Direct);

        _hooked.Add(topLevel);
    }

    /// <summary>
    /// Gỡ handler của một cửa sổ (gọi khi cửa sổ con đóng).
    ///
    /// Cần thiết vì <see cref="DetachAll"/> chỉ chạy 1 lần lúc tắt app — nếu
    /// không gỡ ở đây thì handler của các hộp thoại đã đóng vẫn còn trong
    /// bộ nhớ (rò) và có thể bắt sự kiện nhầm.
    /// </summary>
    /// <param name="topLevel">Cửa sổ cần gỡ.</param>
    public void Detach(TopLevel topLevel)
    {
        ArgumentNullException.ThrowIfNull(topLevel);

        if (!_hooked.Remove(topLevel))
        {
            return;
        }

        topLevel.RemoveHandler(InputElement.GotFocusEvent, OnGotFocus);
        topLevel.RemoveHandler(InputElement.LostFocusEvent, OnLostFocus);
        topLevel.RemoveHandler(InputElement.PointerPressedEvent, OnPointerPressed);

        // Nếu ô đang focus thuộc cửa sổ vừa đóng -> đóng bàn phím.
        if (_focused is not null && TopLevel.GetTopLevel(_focused) == topLevel)
        {
            _focused = null;
            _keyboard.Close();
        }
    }

    /// <summary>Gỡ toàn bộ handler (khi app đóng).</summary>
    public void DetachAll()
    {
        foreach (var top in _hooked)
        {
            top.RemoveHandler(InputElement.GotFocusEvent, OnGotFocus);
            top.RemoveHandler(InputElement.LostFocusEvent, OnLostFocus);
            top.RemoveHandler(InputElement.PointerPressedEvent, OnPointerPressed);
        }

        _hooked.Clear();
        _focused = null;
        _keyboard.Close();
    }

    private void OnGotFocus(object? sender, RoutedEventArgs e)
    {
        // Bỏ qua focus nằm trong bàn phím ảo (ô mirror) — bàn phím đã mở
        // rồi, không cần mở lại cho chính nó.
        if (e.Source is Visual source && FindAncestor<VirtualKeyboard>(source) is not null)
        {
            return;
        }

        // e.Source là phần tử vừa nhận focus -> tìm TextBox trong cây.
        var textBox = FindTextBox(e.Source as Visual);
        if (textBox is null)
        {
            return;
        }

        // Ô nhập có bàn phím riêng (vd ô nhập số trong bảng ô nhớ PLC)
        // -> KHÔNG mở bàn phím toàn cục, tránh hai bàn phím chồng nhau.
        if (UsesOwnKeypad(textBox))
        {
            return;
        }

        _focused = textBox;
        OpenKeyboard(textBox, sender as TopLevel);
    }

    /// <summary>
    /// Người dùng CHẠM vào ô nhập -> mở bàn phím ngay lập tức.
    ///
    /// Xử lý trường hợp ô đã có focus sẵn (focus không đổi nên không có
    /// <c>GotFocus</c>): bàn phím vẫn phải hiện sau một lần chạm.
    /// </summary>
    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        // Bỏ qua chạm vào BÀN PHÍM ảo (nút phím, ô mirror) — nếu không sẽ
        // đóng/mở bàn phím lung tung khi người dùng gõ.
        if (e.Source is Visual source && FindAncestor<VirtualKeyboard>(source) is not null)
        {
            return;
        }

        var textBox = FindTextBox(e.Source as Visual);
        if (textBox is null)
        {
            return;
        }

        // Ô nhập có bàn phím riêng -> bàn phím nhỏ của ô đó lo phần nhập,
        // không mở thêm bàn phím toàn cục.
        if (UsesOwnKeypad(textBox))
        {
            return;
        }

        // Đã mở đúng ô này rồi -> không cần mở lại (giữ nguyên layout và
        // trạng thái Shift đang bật).
        if (ReferenceEquals(_focused, textBox) && _keyboard.IsVisible)
        {
            return;
        }

        _focused = textBox;
        OpenKeyboard(textBox, sender as TopLevel);
    }

    /// <summary>
    /// Mở bàn phím cho ô nhập (dùng chung cho GotFocus và PointerPressed).
    /// </summary>
    private void OpenKeyboard(TextBox textBox, TopLevel? owner)
    {
        _keyboard.Open(
            textBox,
            ResolveLayout(textBox),
            BuildHint(textBox),
            owner ?? TopLevel.GetTopLevel(textBox));
    }

    private void OnLostFocus(object? sender, RoutedEventArgs e)
    {
        // e.Source là phần tử vừa MẤT focus -> tìm TextBox trong cây.
        var textBox = FindTextBox(e.Source as Visual);
        if (textBox is null)
        {
            return;
        }

        // Chỉ xử lý ô nhập đang là target của bàn phím (bỏ qua ô mirror —
        // mirror là TextBox nhưng không phải target).
        if (!ReferenceEquals(_focused, textBox))
        {
            return;
        }

        // Focus MỚI chưa ổn định tại thời điểm LostFocus: Avalonia raise
        // LostFocus TRƯỚC khi focus thực sự chuyển sang phần tử mới. Nếu
        // kiểm tra ngay, chuyển focus sang Ô MIRROR sẽ bị hiểu nhầm là
        // "rời khỏi ô nhập" -> đóng bàn phím -> ô mirror biến mất giữa chừng.
        //
        // Vì vậy đợi tới lượt dispatcher kế tiếp rồi mới quyết định.
        Dispatcher.UIThread.Post(
            () => CloseIfFocusLeft(sender as TopLevel, textBox),
            DispatcherPriority.Input);
    }

    /// <summary>
    /// Kiểm tra focus sau khi đã ổn định: nếu focus đã rời hẳn khỏi ô nhập
    /// (và không nằm trong bàn phím ảo) thì đóng bàn phím.
    /// </summary>
    /// <param name="topLevel">Cửa sổ chứa ô nhập.</param>
    /// <param name="textBox">Ô nhập vừa mất focus.</param>
    private void CloseIfFocusLeft(TopLevel? topLevel, TextBox textBox)
    {
        // Trong lúc chờ, bàn phím có thể đã đóng/chuyển ô -> không làm gì.
        if (!ReferenceEquals(_focused, textBox))
        {
            return;
        }

        var newFocus = topLevel?.FocusManager?.GetFocusedElement();

        // Focus vẫn nằm bên trong cùng ô nhập (ví dụ bấm nút hiện/ẩn mật
        // khẩu nằm trong TextBox) -> giữ bàn phím.
        if (newFocus is Visual v && textBox.IsVisualAncestorOf(v))
        {
            return;
        }

        // Focus chuyển vào Ô NHẬP MIRROR của bàn phím -> GIỮ bàn phím mở.
        // Ô mirror là nơi người dùng gõ trực tiếp, đóng bàn phím lúc này
        // sẽ khiến ô mirror biến mất giữa chừng.
        if (newFocus is Visual focusVisual
            && FindAncestor<VirtualKeyboard>(focusVisual) is not null)
        {
            return;
        }

        _focused = null;
        _keyboard.Close();
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

    /// <summary>Tìm ancestor kiểu T (đi ngược cây Visual).</summary>
    private static T? FindAncestor<T>(Visual? visual)
        where T : Visual
    {
        while (visual is not null)
        {
            if (visual is T match)
            {
                return match;
            }

            visual = visual.GetVisualParent();
        }

        return null;
    }
}
