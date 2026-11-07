using Avalonia.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

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
///
/// Ngoài ra bàn phím có một Ô NHẬP MIRROR (ô nhập trên bàn phím) hiển thị
/// nội dung đang nhập — vì bàn phím có thể che mất ô nhập gốc, người dùng
/// cần nhìn thấy mình đang gõ gì. Ô mirror GÕ ĐƯỢC: mọi thay đổi ở ô mirror
/// đều được ghi ngược vào ô gốc, và ngược lại.
///
/// Đồng bộ hai chiều dùng cờ <see cref="_syncing"/> để tránh ping-pong vô hạn.
/// </summary>
public sealed partial class VirtualKeyboardViewModel : ObservableObject
{
    /// <summary>Ký tự mask mặc định khi hiển thị mật khẩu trên ô mirror.</summary>
    private const char DefaultMaskChar = '•';

    /// <summary>Ô đang nhập ký tự (null = bàn phím đang đóng).</summary>
    private TextBox? _target;

    /// <summary>
    /// Ô nhập MIRROR đang hoạt động trên bàn phím.
    ///
    /// Giữ tham chiếu để đọc vị trí con trỏ / vùng chọn chính xác khi
    /// người dùng gõ trực tiếp, và để đặt con trỏ sau khi bấm phím.
    /// </summary>
    private TextBox? _mirrorBox;

    /// <summary>Cửa sổ sở hữu bàn phím hiện tại (để biết cửa sổ nào hiển thị).</summary>
    private TopLevel? _owner;

    /// <summary>
    /// Cờ đồng bộ: true khi đang chủ động ghi giữa ô mirror và ô gốc.
    ///
    /// Dùng để chặn vòng lặp: ghi vào ô mirror -> ghi vào ô gốc -> ô gốc báo
    /// TextChanged -> ghi lại vào ô mirror -> ... (vô hạn).
    /// </summary>
    private bool _syncing;

    /// <summary>Vị trí con trỏ trong ô mirror.</summary>
    private int _mirrorCaret;

    /// <summary>Đầu vùng chọn trong ô mirror (-1 = không chọn).</summary>
    private int _mirrorSelectionStart = -1;

    /// <summary>Cuối vùng chọn trong ô mirror (-1 = không chọn).</summary>
    private int _mirrorSelectionEnd = -1;

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

    /// <summary>
    /// Nội dung của ô nhập mirror (ô nhập trên bàn phím).
    ///
    /// Bind hai chiều với ô nhập trong XAML. Mọi thay đổi đều được ghi
    /// ngược vào ô gốc (trừ khi đang trong lúc đồng bộ ngược lại).
    /// </summary>
    [ObservableProperty]
    private string _mirrorText = string.Empty;

    /// <summary>Ô gốc có mask mật khẩu không (có khai báo PasswordChar).</summary>
    [ObservableProperty]
    private bool _isTargetPassword;

    /// <summary>Đang hiện rõ mật khẩu (bỏ mask) hay không.</summary>
    [ObservableProperty]
    private bool _isRevealed;

    /// <summary>
    /// Hàng phím tiếng Việt (ă â ê ô đ ơ ư + các dấu) đang hiện hay không.
    ///
    /// Mặc định ẨN để bàn phím gọn hơn; người dùng bấm nút "Tiếng Việt"
    /// trên thanh trên cùng để bật/tắt hàng này.
    /// </summary>
    [ObservableProperty]
    private bool _isVietnameseKeysVisible;

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
    /// Nội dung hiển thị trên ô nhập mirror.
    ///
    /// Với ô mật khẩu chưa bật "con mắt" -> trả về chuỗi dấu mask, để không
    /// lộ mật khẩu khi đang gõ.
    /// </summary>
    public string MirrorDisplayText =>
        IsTargetPassword && !IsRevealed
            ? new string(DefaultMaskChar, MirrorText.Length)
            : MirrorText;

    /// <summary>True khi ô mirror có nội dung (dùng để ẩn placeholder).</summary>
    public bool HasMirrorText => !string.IsNullOrEmpty(MirrorText);

    /// <summary>
    /// True khi nên hiện nút "con mắt" (ô gốc là ô mật khẩu).
    /// </summary>
    public bool CanReveal => IsTargetPassword;

    /// <summary>
    /// True khi ô mirror hiển thị bản rõ (ô thường, hoặc mật khẩu đã bật
    /// nút "con mắt").
    /// </summary>
    public bool IsMirrorPlainVisible => !IsTargetPassword || IsRevealed;

    /// <summary>
    /// True khi ô mirror hiển thị bản mask (ô mật khẩu, chưa bật "con mắt").
    /// </summary>
    public bool IsMirrorMaskVisible => IsTargetPassword && !IsRevealed;

    /// <summary>
    /// True khi hàng phím tiếng Việt cần hiển thị.
    ///
    /// Hàng này chỉ có ý nghĩa với bàn phím chữ (Alpha) — bàn phím số không
    /// dùng tới các phím này.
    /// </summary>
    public bool IsVietnameseRowVisible => IsAlphaLayout && IsVietnameseKeysVisible;

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

        // Đóng ô cũ trước (để gỡ handler TextChanged của ô cũ).
        if (!ReferenceEquals(_target, target))
        {
            DetachTargetHandler();
        }

        _target = target;
        _owner = owner;
        OnPropertyChanged(nameof(Owner));
        Layout = layout;
        IsShiftOn = false;
        TargetHint = hint ?? string.Empty;

        // Đọc trạng thái mask của ô gốc.
        // PasswordChar mặc định là '\0' (không mask) -> khác '\0' nghĩa là ô mật khẩu.
        IsTargetPassword = target.PasswordChar != '\0';

        // Nếu ô gốc đang bật RevealPassword (người dùng bấm con mắt ở ô gốc)
        // -> ô mirror cũng hiện rõ cho nhất quán.
        IsRevealed = target.RevealPassword;

        // Đồng bộ nội dung hiện tại của ô gốc sang ô mirror.
        _syncing = true;
        try
        {
            MirrorText = target.Text ?? string.Empty;
        }
        finally
        {
            _syncing = false;
        }

        // Con trỏ: đặt ở cuối nội dung (giống khi bấm vào ô nhập).
        _mirrorCaret = MirrorText.Length;
        _mirrorSelectionStart = -1;
        _mirrorSelectionEnd = -1;

        AttachTargetHandler();
        IsVisible = true;
    }

    /// <summary>Đóng bàn phím (bỏ focus ô đang nhập).</summary>
    public void Close()
    {
        DetachTargetHandler();

        _target = null;
        _owner = null;
        OnPropertyChanged(nameof(Owner));
        IsVisible = false;
        IsShiftOn = false;
        IsTargetPassword = false;
        IsRevealed = false;
        TargetHint = string.Empty;

        _syncing = true;
        try
        {
            MirrorText = string.Empty;
        }
        finally
        {
            _syncing = false;
        }
    }
    /// <summary>
    /// Đồng bộ ô mirror từ ô gốc.
    ///
    /// Gọi khi nội dung ô gốc thay đổi (người dùng gõ bằng bàn phím vật lý,
    /// hoặc app tự đặt lại giá trị). Không ghi ngược lại ô gốc trong lúc này.
    /// </summary>
    public void SyncMirrorFromTarget()
    {
        if (_target is null || _syncing)
        {
            return;
        }

        var text = _target.Text ?? string.Empty;

        _syncing = true;
        try
        {
            MirrorText = text;
        }
        finally
        {
            _syncing = false;
        }
    }

    /// <summary>
    /// Gắn ô nhập mirror đang hoạt động (đọc con trỏ / vùng chọn từ đây).
    ///
    /// Gọi từ code-behind khi ô mirror nhận focus.
    /// </summary>
    /// <param name="box">Ô mirror.</param>
    public void AttachMirrorBox(TextBox? box)
    {
        if (_mirrorBox is not null && !ReferenceEquals(_mirrorBox, box))
        {
            _mirrorBox.PropertyChanged -= OnMirrorBoxPropertyChanged;
        }

        _mirrorBox = box;

        if (_mirrorBox is not null)
        {
            _mirrorBox.PropertyChanged += OnMirrorBoxPropertyChanged;
            SyncMirrorCaretFromBox();
        }
    }

    /// <summary>
    /// Ô mirror đổi vị trí con trỏ / vùng chọn -> cập nhật VM để phím bàn phím
    /// (chèn/xoá) biết chỗ nào cần thao tác.
    /// </summary>
    /// <param name="sender">Ô mirror.</param>
    /// <param name="e">Tham số sự kiện.</param>
    private void OnMirrorBoxPropertyChanged(object? sender, Avalonia.AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == TextBox.CaretIndexProperty
            || e.Property == TextBox.SelectionStartProperty
            || e.Property == TextBox.SelectionEndProperty)
        {
            SyncMirrorCaretFromBox();
        }
    }

    /// <summary>Đọc con trỏ / vùng chọn hiện tại của ô mirror.</summary>
    private void SyncMirrorCaretFromBox()
    {
        if (_mirrorBox is null)
        {
            return;
        }

        _mirrorCaret = _mirrorBox.CaretIndex;
        _mirrorSelectionStart = _mirrorBox.SelectionStart;
        _mirrorSelectionEnd = _mirrorBox.SelectionEnd;
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

        // Shift đang bật -> viết hoa ký tự Latin (không đổi ký tự có dấu
        // tiếng Việt vì chúng đã là dạng hoa/thường riêng biệt).
        if (IsShiftOn)
        {
            text = ToUpper(text);
        }

        var current = MirrorText ?? string.Empty;

        // Lấy con trỏ / vùng chọn mới nhất từ ô mirror (nếu có).
        SyncMirrorCaretFromBox();

        var caret = Math.Clamp(_mirrorCaret, 0, current.Length);

        var start = caret;
        var end = caret;

        // Nếu đang bôi đen một đoạn -> thay thế đoạn đó.
        if (_mirrorSelectionStart >= 0
            && _mirrorSelectionEnd > _mirrorSelectionStart)
        {
            start = Math.Clamp(_mirrorSelectionStart, 0, current.Length);
            end = Math.Clamp(_mirrorSelectionEnd, 0, current.Length);
        }

        var updated = current[..start] + text + current[end..];

        // Chặn vượt quá MaxLength (giống hành vi khi gõ bằng bàn phím thật).
        var maxLength = _target.MaxLength;
        if (maxLength > 0 && updated.Length > maxLength)
        {
            updated = updated[..maxLength];
        }

        var newCaret = Math.Min(start + text.Length, updated.Length);
        ApplyToTarget(updated, newCaret);

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

        var current = MirrorText ?? string.Empty;
        if (current.Length == 0)
        {
            return;
        }

        // Lấy con trỏ / vùng chọn mới nhất từ ô mirror (nếu có).
        SyncMirrorCaretFromBox();

        var caret = Math.Clamp(_mirrorCaret, 0, current.Length);

        // Đang bôi đen -> xoá luôn đoạn bôi đen.
        if (_mirrorSelectionStart >= 0
            && _mirrorSelectionEnd > _mirrorSelectionStart)
        {
            var s = Math.Clamp(_mirrorSelectionStart, 0, current.Length);
            var e = Math.Clamp(_mirrorSelectionEnd, 0, current.Length);
            ApplyToTarget(current[..s] + current[e..], s);
            return;
        }

        if (caret <= 0)
        {
            return;
        }

        ApplyToTarget(current[..(caret - 1)] + current[caret..], caret - 1);
    }

    /// <summary>Chèn khoảng trắng (phím Space).</summary>
    public void Space() => Input(" ");

    /// <summary>Xoá toàn bộ nội dung ô đang nhập.</summary>
    [RelayCommand]
    public void Clear()
    {
        if (_target is null)
        {
            return;
        }

        ApplyToTarget(string.Empty, 0);
    }

    /// <summary>Bật/tắt hàng phím tiếng Việt (ă â ê ô đ ơ ư + các dấu).</summary>
    [RelayCommand]
    public void ToggleVietnameseKeys() => IsVietnameseKeysVisible = !IsVietnameseKeysVisible;

    /// <summary>Bật/tắt Shift.</summary>
    public void ToggleShift() => IsShiftOn = !IsShiftOn;

    /// <summary>Chuyển qua lại giữa bàn phím chữ và bàn phím số.</summary>
    public void ToggleLayout() =>
        Layout = Layout == KeyboardLayout.Alpha ? KeyboardLayout.Numeric : KeyboardLayout.Alpha;

    /// <summary>
    /// Bật/tắt hiện rõ mật khẩu (nút "con mắt" trên ô nhập mirror).
    /// </summary>
    [RelayCommand]
    public void ToggleReveal()
    {
        IsRevealed = !IsRevealed;

        // Đồng bộ với ô gốc (nếu ô gốc có hỗ trợ RevealPassword).
        if (_target is not null)
        {
            _target.RevealPassword = IsRevealed;
        }
    }

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

    // ---------- Đồng bộ hai chiều ----------

    /// <summary>
    /// Ghi nội dung (và vị trí con trỏ) xuống ô gốc.
    ///
    /// Đây là ĐIỂM DUY NHẤT dùng để ghi vào ô gốc — cả phím bàn phím lẫn
    /// việc gõ trực tiếp vào ô mirror đều đi qua đây, nên không bao giờ lệch
    /// dữ liệu giữa 2 ô.
    /// </summary>
    /// <param name="text">Nội dung mới.</param>
    /// <param name="caret">Vị trí con trỏ sau khi cập nhật.</param>
    private void ApplyToTarget(string text, int caret)
    {
        if (_target is null)
        {
            return;
        }

        _syncing = true;
        try
        {
            // Ô gốc có binding -> gán Text đẩy ngược vào ViewModel gốc.
            _target.Text = text;

            var position = Math.Min(caret, text.Length);
            _target.CaretIndex = position;
            _target.SelectionStart = position;
            _target.SelectionEnd = position;

            // Cập nhật ô mirror để UI hiển thị đúng.
            MirrorText = text;

            _mirrorCaret = position;
            _mirrorSelectionStart = position;
            _mirrorSelectionEnd = position;

            // Đặt con trỏ của ô mirror về đúng chỗ.
            if (_mirrorBox is not null)
            {
                _mirrorBox.CaretIndex = position;
                _mirrorBox.SelectionStart = position;
                _mirrorBox.SelectionEnd = position;
            }
        }
        finally
        {
            _syncing = false;
        }
    }

    /// <summary>
    /// Người dùng gõ trực tiếp vào ô mirror -> ghi ngược vào ô gốc.
    ///
    /// Được gọi từ code-behind (event <c>TextChanged</c> của ô mirror) thay vì
    /// dùng binding TwoWay, vì binding TwoWay gây race condition: giá trị cũ
    /// từ VM có thể ghi đè lên ký tự vừa gõ (mất ký tự đầu tiên khi mở
    /// bàn phím).
    /// </summary>
    /// <param name="value">Nội dung mới người dùng nhập vào ô mirror.</param>
    public void SetMirrorTextFromUser(string? value)
    {
        var text = value ?? string.Empty;

        _syncing = true;
        try
        {
            // Cập nhật giá trị hiển thị (binding OneWay sẽ đẩy xuống ô).
            MirrorText = text;
        }
        finally
        {
            _syncing = false;
        }

        if (_target is null)
        {
            return;
        }

        // Chặn vượt quá MaxLength của ô gốc.
        var maxLength = _target.MaxLength;
        if (maxLength > 0 && text.Length > maxLength)
        {
            text = text[..maxLength];

            _syncing = true;
            try
            {
                MirrorText = text;
            }
            finally
            {
                _syncing = false;
            }
        }

        // Con trỏ: lấy từ ô mirror nếu có, nếu không thì giữ vị trí hiện tại.
        if (_mirrorBox is not null)
        {
            _mirrorCaret = _mirrorBox.CaretIndex;
            _mirrorSelectionStart = _mirrorBox.SelectionStart;
            _mirrorSelectionEnd = _mirrorBox.SelectionEnd;
        }

        var caret = Math.Clamp(_mirrorCaret, 0, text.Length);
        _mirrorCaret = caret;

        _syncing = true;
        try
        {
            // Ô gốc có binding -> gán Text đẩy ngược vào ViewModel gốc.
            _target.Text = text;

            var position = Math.Min(caret, text.Length);
            _target.CaretIndex = position;
            _target.SelectionStart = position;
            _target.SelectionEnd = position;
        }
        finally
        {
            _syncing = false;
        }
    }

    /// <summary>
    /// Ô mirror bị người dùng gõ/sửa trực tiếp -> cập nhật hiển thị.
    ///
    /// Việc ghi ngược vào ô gốc do <see cref="SetMirrorTextFromUser"/> xử lý.
    /// </summary>
    partial void OnMirrorTextChanged(string value)
    {
        // Nội dung hiển thị cũng đổi theo (mask hay không).
        OnPropertyChanged(nameof(MirrorDisplayText));
        OnPropertyChanged(nameof(HasMirrorText));
    }

    /// <summary>Ô mirror đổi trạng thái mask -> cập nhật nút con mắt + bản hiển thị.</summary>
    partial void OnIsTargetPasswordChanged(bool value)
    {
        OnPropertyChanged(nameof(CanReveal));
        OnPropertyChanged(nameof(IsMirrorPlainVisible));
        OnPropertyChanged(nameof(IsMirrorMaskVisible));
    }

    /// <summary>Bật/tắt hiện rõ -> cập nhật nội dung hiển thị + bản hiển thị.</summary>
    partial void OnIsRevealedChanged(bool value)
    {
        OnPropertyChanged(nameof(MirrorDisplayText));
        OnPropertyChanged(nameof(IsMirrorPlainVisible));
        OnPropertyChanged(nameof(IsMirrorMaskVisible));
    }

    partial void OnLayoutChanged(KeyboardLayout value)
    {
        OnPropertyChanged(nameof(IsAlphaLayout));
        OnPropertyChanged(nameof(IsVietnameseRowVisible));
    }

    /// <summary>Bật/tắt hàng phím tiếng Việt -> cập nhật trạng thái hiển thị hàng.</summary>
    partial void OnIsVietnameseKeysVisibleChanged(bool value) =>
        OnPropertyChanged(nameof(IsVietnameseRowVisible));

    partial void OnIsShiftOnChanged(bool value) => OnPropertyChanged(nameof(IsShiftActive));

    // ---------- Handler của ô gốc ----------

    /// <summary>Gắn handler theo dõi thay đổi nội dung của ô gốc.</summary>
    private void AttachTargetHandler()
    {
        if (_target is null)
        {
            return;
        }

        _target.TextChanged -= OnTargetTextChanged;
        _target.TextChanged += OnTargetTextChanged;
    }

    /// <summary>Gỡ handler theo dõi thay đổi nội dung của ô gốc.</summary>
    private void DetachTargetHandler()
    {
        if (_target is null)
        {
            return;
        }

        _target.TextChanged -= OnTargetTextChanged;
    }

    /// <summary>
    /// Ô gốc đổi nội dung (do app tự đặt, hoặc do bàn phím vật lý gõ vào).
    /// Đồng bộ sang ô mirror.
    /// </summary>
    private void OnTargetTextChanged(object? sender, TextChangedEventArgs e)
    {
        if (_syncing)
        {
            return;
        }

        SyncMirrorFromTarget();
    }
}
