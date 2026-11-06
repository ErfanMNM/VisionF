using Avalonia.Controls;
using CommunityToolkit.Mvvm.ComponentModel;

namespace TanTienQR.ViewModels;

/// <summary>Kiểu bàn phím ảo đang hiển thị.</summary>
public enum KeyboardLayout
{
    /// <summary>Bàn phím chữ: QWERTY + hàng phụ phím tổ hợp tiếng Việt.</summary>
    Alpha = 0,

    /// <summary>Bàn phím số thuần (bàn phím dạng máy POS).</summary>
    Numeric = 1,
}

/// <summary>
/// ViewModel cho bàn phím ảo.
///
/// Bàn phím không tự quản lý focus: nó nhận "ô đang nhập" từ
/// <see cref="TanTienQR.Services.KeyboardService"/> rồi ghi ký tự thẳng vào
/// ô đó. Cách này giữ bàn phím hoạt động đúng với mọi kiểu TextBox
/// (có/không PasswordChar, có/không binding) mà không cần converter.
/// </summary>
public sealed partial class VirtualKeyboardViewModel : ObservableObject
{
    /// <summary>Ô đang nhập ký tự (null = bàn phím đang đóng).</summary>
    private TextBox? _target;

    /// <summary>Cửa sổ sở hữu bàn phím hiện tại (để đóng khi đổi cửa sổ).</summary>
    private TopLevel? _owner;

    /// <summary>Bàn phím có đang hiển thị không.</summary>
    [ObservableProperty]
    private bool _isVisible;

    /// <summary>Kiểu bàn phím đang hiển thị.</summary>
    [ObservableProperty]
    private KeyboardLayout _layout = KeyboardLayout.Alpha;

    /// <summary>Đang bật Shift (chữ in hoa) không.</summary>
    [ObservableProperty]
    private bool _isShiftOn;

    /// <summary>Tiêu đề hiển thị trên thanh bàn phím (gợi ý ô đang nhập).</summary>
    [ObservableProperty]
    private string _targetHint = string.Empty;

    /// <summary>Ô đang nhập (readonly cho VM).</summary>
    public TextBox? Target => _target;

    /// <summary>Hiện bàn phím chữ hay bàn phím số.</summary>
    public bool IsAlphaLayout => Layout == KeyboardLayout.Alpha;

    /// <summary>Hiện bàn phím số hay bàn phím chữ.</summary>
    public bool IsNumericLayout => Layout == KeyboardLayout.Numeric;

    /// <summary>True khi bàn phím đang ở chế độ chữ (dùng cho hiển thị phụ).</summary>
    public bool IsShiftActive => IsShiftOn;

    /// <summary>
    /// Cửa sổ đang sở hữu bàn phím (null = chưa mở).
    /// Dùng để quyết định cửa sổ nào được hiển thị bàn phím.
    /// </summary>
    public TopLevel? Owner => _owner;

    /// <summary>
    /// Mở bàn phím cho một ô nhập.
    /// </summary>
    /// <param name="target">Ô TextBox đang nhận focus.</param>
    /// <param name="layout">Kiểu bàn phím cần hiển thị.</param>
    /// <param name="hint">Nhãn gợi ý (ví dụ "Tên đăng nhập").</param>
    /// <param name="owner">Cửa sổ chứa ô nhập (dùng để quyết định cửa sổ nào
    /// hiển thị bàn phím).</param>
    public void Open(
        TextBox target,
        KeyboardLayout layout,
        string? hint = null,
        TopLevel? owner = null)
    {
        ArgumentNullException.ThrowIfNull(target);

        _target = target;
        _owner = owner;
        Layout = layout;
        IsShiftOn = false;
        TargetHint = hint ?? string.Empty;
        IsVisible = true;
    }

    /// <summary>Đóng bàn phím (bỏ focus ô đang nhập).</summary>
    public void Close()
    {
        _target = null;
        _owner = null;
        IsVisible = false;
        IsShiftOn = false;
        TargetHint = string.Empty;
    }

    /// <summary>
    /// Nhận một ký tự từ bàn phím và chèn vào ô đang nhập.
    /// </summary>
    /// <param name="text">Ký tự cần nhập (có thể nhiều ký tự).</param>
    public void Input(string text)
    {
        if (_target is null || string.IsNullOrEmpty(text))
        {
            return;
        }

        // Chèn tại con trỏ: cắt phần đang bôi đen (nếu có) rồi chèn.
        var current = _target.Text ?? string.Empty;
        var caret = Math.Clamp(_target.CaretIndex, 0, current.Length);

        var start = caret;
        var end = caret;

        // Nếu đang bôi đen một đoạn -> thay thế đoạn đó.
        var selectionStart = _target.SelectionStart;
        var selectionEnd = _target.SelectionEnd;
        if (selectionStart >= 0 && selectionEnd > selectionStart)
        {
            start = Math.Clamp(selectionStart, 0, current.Length);
            end = Math.Clamp(selectionEnd, 0, current.Length);
        }

        // Shift đang bật -> viết hoa ký tự Latin (không đổi ký tự có dấu
        // tiếng Việt vì chúng đã là dạng hoa/thường riêng biệt).
        if (IsShiftOn)
        {
            text = ToUpper(text);
        }

        var updated = current[..start] + text + current[end..];

        // Chặn vượt quá MaxLength (giống hành vi khi gõ bằng bàn phím thật).
        if (_target.MaxLength > 0 && updated.Length > _target.MaxLength)
        {
            updated = updated[.._target.MaxLength];
        }

        _target.Text = updated;
        _target.CaretIndex = Math.Min(start + text.Length, updated.Length);
        _target.SelectionStart = _target.CaretIndex;
        _target.SelectionEnd = _target.CaretIndex;

        // Tự tắt Shift sau 1 ký tự (giống bàn phím di động).
        if (IsShiftOn && text.Length == 1)
        {
            IsShiftOn = false;
        }
    }

    /// <summary>Viết hoa ký tự Latin, giữ nguyên ký tự đã có dấu tiếng Việt.</summary>
    private static string ToUpper(string text)
    {
        var buffer = text.ToCharArray();
        for (var i = 0; i < buffer.Length; i++)
        {
            var c = buffer[i];

            // Ký tự có dấu tiếng Việt (dạng hoa/thường riêng) -> giữ nguyên.
            if (c is >= 'à' and <= 'ỹ' or >= 'À' and <= 'Ỹ')
            {
                continue;
            }

            buffer[i] = char.ToUpperInvariant(c);
        }

        return new string(buffer);
    }

    /// <summary>Xoá 1 ký tự trước con trỏ (phím Backspace).</summary>
    public void Backspace()
    {
        if (_target is null)
        {
            return;
        }

        var current = _target.Text ?? string.Empty;
        if (current.Length == 0)
        {
            return;
        }

        var caret = Math.Clamp(_target.CaretIndex, 0, current.Length);

        // Đang bôi đen -> xoá luôn đoạn bôi đen.
        var selectionStart = _target.SelectionStart;
        var selectionEnd = _target.SelectionEnd;
        if (selectionStart >= 0 && selectionEnd > selectionStart)
        {
            var s = Math.Clamp(selectionStart, 0, current.Length);
            var e = Math.Clamp(selectionEnd, 0, current.Length);
            _target.Text = current[..s] + current[e..];
            _target.CaretIndex = s;
            _target.SelectionStart = s;
            _target.SelectionEnd = s;
            return;
        }

        if (caret <= 0)
        {
            return;
        }

        _target.Text = current[..(caret - 1)] + current[caret..];
        _target.CaretIndex = caret - 1;
        _target.SelectionStart = caret - 1;
        _target.SelectionEnd = caret - 1;
    }

    /// <summary>Chèn khoảng trắng (phím Space).</summary>
    public void Space() => Input(" ");

    /// <summary>Xoá toàn bộ nội dung ô đang nhập.</summary>
    public void Clear()
    {
        if (_target is null)
        {
            return;
        }

        _target.Text = string.Empty;
        _target.CaretIndex = 0;
        _target.SelectionStart = 0;
        _target.SelectionEnd = 0;
    }

    /// <summary>Bật/tắt Shift.</summary>
    public void ToggleShift() => IsShiftOn = !IsShiftOn;

    /// <summary>Chuyển qua lại giữa bàn phím chữ và bàn phím số.</summary>
    public void ToggleLayout() =>
        Layout = Layout == KeyboardLayout.Alpha ? KeyboardLayout.Numeric : KeyboardLayout.Alpha;

    /// <summary>
    /// Gửi giá trị hiện tại của ô đang nhập (phím Enter).
    /// </summary>
    public void Submit()
    {
        if (_target is null)
        {
            return;
        }

        // Mô phỏng Enter: bỏ focus để binding cập nhật & form gửi đi.
        _target.Focus();
    }

    partial void OnLayoutChanged(KeyboardLayout value) =>
        OnPropertyChanged(nameof(IsAlphaLayout));

    partial void OnIsShiftOnChanged(bool value) => OnPropertyChanged(nameof(IsShiftActive));
}
