using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using TanTienQR.ViewModels;

namespace TanTienQR.Controls;

/// <summary>
/// Bàn phím ảo cho máy cảm ứng.
///
/// Hiển thị ở đáy cửa sổ khi người dùng chạm vào một ô nhập. Có 2 bố cục:
/// - <b>Alpha</b>: QWERTY + hàng phụ phím tổ hợp tiếng Việt (ă ê ô đ + dấu).
/// - <b>Numeric</b>: bàn phím số thuần dạng máy POS.
///
/// Trên thanh trên cùng có Ô NHẬP MIRROR hiển thị nội dung đang nhập —
/// gõ trực tiếp được. Ô này tồn tại vì bàn phím có thể che mất ô nhập
/// gốc, người dùng cần nhìn thấy mình đang gõ gì.
///
/// Bàn phím ghi ký tự thẳng vào ô đang nhập qua
/// <see cref="VirtualKeyboardViewModel"/> (không dùng converter).
///
/// Vì bàn phím là singleton dùng chung, control này tự ẩn đi nếu cửa sổ
/// chứa nó không phải cửa sổ đang có ô nhập được focus (ví dụ khi mở hộp
/// thoại con thì bàn phím của cửa sổ cha phải ẩn).
/// </summary>
public partial class VirtualKeyboard : UserControl
{
    /// <summary>ViewModel đang gắn (để hủy đăng ký khi đổi DataContext).</summary>
    private VirtualKeyboardViewModel? _boundVm;

    /// <summary>Khởi tạo control.</summary>
    public VirtualKeyboard()
    {
        InitializeComponent();

        DataContextChanged += OnDataContextChanged;
        AttachedToVisualTree += OnAttachedToVisualTree;
    }

    /// <summary>ViewModel của bàn phím (lấy từ DataContext của control).</summary>
    private VirtualKeyboardViewModel? Vm => DataContext as VirtualKeyboardViewModel;

    /// <summary>Đăng ký / hủy đăng ký theo dõi thay đổi trạng thái VM.</summary>
    /// <param name="sender">Control.</param>
    /// <param name="e">Tham số sự kiện.</param>
    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_boundVm is not null)
        {
            _boundVm.PropertyChanged -= OnVmPropertyChanged;
            _boundVm = null;
        }

        _boundVm = Vm;
        if (_boundVm is not null)
        {
            _boundVm.PropertyChanged += OnVmPropertyChanged;
        }

        UpdateVisibility();
    }

    /// <summary>Khi control được gắn vào cây hình ảnh -> cập nhật trạng thái hiển thị.</summary>
    /// <param name="sender">Control.</param>
    /// <param name="e">Tham số sự kiện.</param>
    private void OnAttachedToVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        UpdateVisibility();
    }

    /// <summary>VM đổi trạng thái -> cập nhật hiển thị.</summary>
    /// <param name="sender">VM.</param>
    /// <param name="e">Tham số sự kiện.</param>
    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(VirtualKeyboardViewModel.IsVisible)
            or nameof(VirtualKeyboardViewModel.Owner))
        {
            UpdateVisibility();
        }
    }

    /// <summary>
    /// Bàn phím chỉ hiện khi: VM đang bật hiển thị VÀ cửa sổ chứa control là
    /// cửa sổ sở hữu ô nhập đang focus.
    ///
    /// Bàn phím là singleton dùng chung cho MỌI cửa sổ, nên mỗi cửa sổ có
    /// một bản control riêng nhưng cùng chung một ViewModel. Control của cửa
    /// sổ cha sẽ tự ẩn khi hộp thoại con đang mở (vì <c>Owner</c> của VM
    /// lúc đó là hộp thoại con, không phải cửa sổ cha).
    ///
    /// Vì control nằm ở hàng riêng của layout "push", ẩn đi thì hàng đó co
    /// về 0 và nội dung phía trên lấy lại toàn bộ chỗ.
    /// </summary>
    private void UpdateVisibility()
    {
        var vm = Vm;
        if (vm is null || !vm.IsVisible)
        {
            IsVisible = false;
            IsHitTestVisible = false;
            return;
        }

        // Chưa gắn vào cây hình ảnh -> chưa biết cửa sổ nào -> ẩn.
        var top = TopLevel.GetTopLevel(this);
        if (top is null)
        {
            IsVisible = false;
            IsHitTestVisible = false;
            return;
        }

        // Chỉ cửa sổ đang sở hữu ô nhập mới hiện bàn phím.
        var owned = vm.Owner is not null && ReferenceEquals(vm.Owner, top);
        IsVisible = owned;
        IsHitTestVisible = owned;
    }

    /// <summary>
    /// Bấm một phím ký tự.
    /// </summary>
    /// <param name="sender">Nút phím.</param>
    /// <param name="e">Tham số sự kiện.</param>
    private void OnKeyClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Content: string key })
        {
            return;
        }

        Vm?.Input(key);
    }

    /// <summary>Bấm phím Backspace (xoá 1 ký tự).</summary>
    /// <param name="sender">Nút phím.</param>
    /// <param name="e">Tham số sự kiện.</param>
    private void OnBackspaceClick(object? sender, RoutedEventArgs e) => Vm?.Backspace();

    /// <summary>Bấm phím Space.</summary>
    /// <param name="sender">Nút phím.</param>
    /// <param name="e">Tham số sự kiện.</param>
    private void OnSpaceClick(object? sender, RoutedEventArgs e) => Vm?.Space();

    /// <summary>Bấm phím Enter (xác nhận).</summary>
    /// <param name="sender">Nút phím.</param>
    /// <param name="e">Tham số sự kiện.</param>
    private void OnEnterClick(object? sender, RoutedEventArgs e) => Vm?.Submit();

    /// <summary>Bấm phím Shift (bật/tắt chữ in hoa).</summary>
    /// <param name="sender">Nút phím.</param>
    /// <param name="e">Tham số sự kiện.</param>
    private void OnShiftClick(object? sender, RoutedEventArgs e)
    {
        Vm?.ToggleShift();

        // Đổi giao diện phím Shift để phản ánh trạng thái đang bật.
        if (ShiftButton is not null)
        {
            ShiftButton.Classes.Set("active", Vm?.IsShiftOn == true);
        }
    }

    /// <summary>Bấm phím đổi bố cục chữ / số.</summary>
    /// <param name="sender">Nút phím.</param>
    /// <param name="e">Tham số sự kiện.</param>
    private void OnLayoutClick(object? sender, RoutedEventArgs e) => Vm?.ToggleLayout();

    /// <summary>
    /// Bật/tắt hàng phím tiếng Việt.
    ///
    /// Nút dùng <c>IsChecked</c> binding OneWay nên giá trị hiển thị luôn
    /// lấy từ VM; handler này chỉ đảo trạng thái trong VM.
    /// </summary>
    /// <param name="sender">Nút bật/tắt.</param>
    /// <param name="e">Tham số sự kiện.</param>
    private void OnVietnameseToggleClick(object? sender, RoutedEventArgs e) =>
        Vm?.ToggleVietnameseKeys();

    /// <summary>Bấm phím Xoá hết.</summary>
    /// <param name="sender">Nút phím.</param>
    /// <param name="e">Tham số sự kiện.</param>
    private void OnClearClick(object? sender, RoutedEventArgs e) => Vm?.Clear();

    /// <summary>Bấm nút đóng bàn phím.</summary>
    /// <param name="sender">Nút phím.</param>
    /// <param name="e">Tham số sự kiện.</param>
    private void OnCloseClick(object? sender, RoutedEventArgs e) => Vm?.Close();

    /// <summary>
    /// Người dùng chạm vào ô mirror -> theo dõi ô này để lấy vị trí con trỏ.
    /// </summary>
    /// <param name="sender">Ô mirror.</param>
    /// <param name="e">Tham số sự kiện.</param>
    private void OnMirrorGotFocus(object? sender, FocusChangedEventArgs e)
    {
        if (sender is not TextBox box)
        {
            return;
        }

        // VM tự theo dõi con trỏ / vùng chọn của ô mirror.
        Vm?.AttachMirrorBox(box);
    }

    /// <summary>
    /// Người dùng gõ trực tiếp vào ô mirror -> ghi ngược vào ô gốc.
    ///
    /// Dùng <c>TextChanged</c> (OneWay binding) thay vì TwoWay để tránh
    /// race condition: khi binding TwoWay ghi ngược, giá trị cũ có thể ghi
    /// đè lên ký tự vừa gõ (mất ký tự đầu tiên khi mở bàn phím).
    ///
    /// Lưu ý: ô mask và ô plain cùng bind vào một giá trị nên khi người dùng
    /// gõ vào ô nào, ô kia cũng đổi -> <c>TextChanged</c> của ô kia sẽ fire
    /// với cùng nội dung -> không sao (nội dung đã bằng nhau).
    /// </summary>
    /// <param name="sender">Ô mirror.</param>
    /// <param name="e">Tham số sự kiện.</param>
    private void OnMirrorTextChanged(object? sender, TextChangedEventArgs e)
    {
        if (sender is not TextBox box)
        {
            return;
        }

        var vm = Vm;
        if (vm is null || vm.MirrorText == box.Text)
        {
            return;
        }

        vm.SetMirrorTextFromUser(box.Text);
    }}
