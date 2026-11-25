using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using TanTienQR.ViewModels;

namespace TanTienQR.Views;

/// <summary>
/// Ruột màn hình đăng nhập (branding + wizard 2 bước).
///
/// Được nhúng ở 2 nơi:
/// - <see cref="LoginShellWindow"/>: wrapper cũ, giữ cho trình xem trước / audit.
/// - <see cref="MainWindow"/>: OVERLAY phủ lên cửa sổ chính — không tạo /
///   đóng cửa sổ riêng nên bấm "Đăng nhập" không còn giật (1 frame trống,
///   chuyển FullScreen 2 lần).
///
/// Control tự focus ô nhập đúng bước hiện tại và tự bỏ đăng ký sự kiện khi
/// bị tháo khỏi cây hình ảnh (overlay đóng -> đăng nhập lại).
/// </summary>
public partial class LoginView : UserControl
{
    /// <summary>
    /// ViewModel đăng nhập đang gắn (dùng để hủy đăng ký sự kiện).
    /// </summary>
    private LoginViewModel? _boundVm;

    private Window? _ownerWindow;

    public LoginView()
    {
        InitializeComponent();

        DataContextChanged += OnDataContextChanged;
        AttachedToVisualTree += OnAttachedToVisualTree;
        DetachedFromVisualTree += OnDetachedFromVisualTree;
        Loaded += (_, _) => FocusStepInput();
    }

    /// <summary>VM đăng nhập đang gắn (null nếu chưa có DataContext).</summary>
    private LoginViewModel? Vm => DataContext as LoginViewModel;

    /// <summary>Đăng ký / hủy đăng ký theo dõi đổi DataContext.</summary>
    /// <param name="sender">Control.</param>
    /// <param name="e">Tham số sự kiện.</param>
    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        BindViewModel();
        FocusStepInput();
    }

    private void BindViewModel()
    {
        if (_boundVm is not null)
        {
            _boundVm.StepChanged -= OnStepChanged;
            _boundVm.PropertyChanged -= OnViewModelPropertyChanged;
            _boundVm = null;
        }

        _boundVm = Vm;
        if (_boundVm is not null)
        {
            _boundVm.StepChanged += OnStepChanged;
            _boundVm.PropertyChanged += OnViewModelPropertyChanged;
        }
    }

    /// <summary>Vào cây hình ảnh -> focus ô nhập của bước hiện tại.</summary>
    /// <param name="sender">Control.</param>
    /// <param name="e">Tham số sự kiện.</param>
    private void OnAttachedToVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        BindViewModel();
        BindOwnerWindow(TopLevel.GetTopLevel(this) as Window);
        FocusStepInput();
    }

    private void BindOwnerWindow(Window? window)
    {
        if (_ownerWindow is not null)
        {
            _ownerWindow.Opened -= OnOwnerReady;
            _ownerWindow.Activated -= OnOwnerReady;
        }

        _ownerWindow = window;
        if (_ownerWindow is not null)
        {
            _ownerWindow.Opened += OnOwnerReady;
            _ownerWindow.Activated += OnOwnerReady;
        }
    }

    private void OnOwnerReady(object? sender, EventArgs e) => FocusStepInput();

    /// <summary>Rời cây hình ảnh -> hủy đăng ký sự kiện (tránh giữ tham chiếu).</summary>
    /// <param name="sender">Control.</param>
    /// <param name="e">Tham số sự kiện.</param>
    private void OnDetachedFromVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        BindOwnerWindow(null);
        if (_boundVm is not null)
        {
            _boundVm.StepChanged -= OnStepChanged;
            _boundVm.PropertyChanged -= OnViewModelPropertyChanged;
            _boundVm = null;
        }
    }

    /// <summary>
    /// Overlay vừa hiện lại (sau khi đăng xuất) -> focus lại ô nhập.
    /// </summary>
    /// <param name="change">Thay đổi thuộc tính.</param>
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == IsVisibleProperty && IsVisible)
        {
            FocusStepInput();
        }
    }

    /// <summary>Chuyển bước wizard -> focus ô nhập tương ứng.</summary>
    /// <param name="sender">VM.</param>
    /// <param name="e">Tham số sự kiện.</param>
    private void OnStepChanged(object? sender, EventArgs e) => FocusStepInput();

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (this.FindControl<ComboBox>("AccountSelector")?.IsDropDownOpen == true)
            return;

        if (e.PropertyName == nameof(LoginViewModel.SelectedAccount)
            || (e.PropertyName == nameof(LoginViewModel.IsLoadingAccounts) && Vm?.IsLoadingAccounts == false))
            FocusStepInput();
    }

    private void OnAccountDropDownClosed(object? sender, EventArgs e)
    {
        // Giữ focus trong danh sách khi dùng phím mũi tên, chỉ chuyển sang
        // mật khẩu sau khi đã chốt một tài khoản và đóng danh sách.
        if (Vm?.IsSignInStep == true && Vm.SelectedAccount is not null)
            FocusStepInput();
    }

    /// <summary>
    /// Focus bộ chọn tài khoản, mật khẩu sau khi chọn, hoặc mật khẩu hiện tại ở bước 2.
    ///
    /// Focus được ĐĂNG KÝ lại ở lượt dispatcher kế tiếp: khi control vừa
    /// được attach vào cây hình ảnh thì cửa sổ cha có thể chưa sẵn sàng
    /// nhận focus -> gọi <c>Focus()</c> ngay sẽ không có tác dụng.
    /// </summary>
    private void FocusStepInput()
    {
        var vm = Vm;
        if (vm is null)
        {
            return;
        }

        // Đợi các callback kích hoạt cửa sổ hoàn tất thiết lập focus scope.
        Dispatcher.UIThread.Post(ApplyFocus, DispatcherPriority.Background);
    }

    /// <summary>Thực hiện focus ô nhập (sau khi dispatcher đã ổn định).</summary>
    private void ApplyFocus()
    {
        var vm = Vm;
        if (vm is null || !IsEffectivelyVisible || _ownerWindow?.IsActive == false)
        {
            return;
        }

        // Một callback focus có thể đã được xếp lịch trước khi mở danh sách.
        if (vm.IsSignInStep && this.FindControl<ComboBox>("AccountSelector")?.IsDropDownOpen == true)
            return;

        Control? target = vm.IsChangePasswordStep
            ? this.FindControl<TextBox>("CurrentBox")
            : vm.SelectedAccount is not null
                ? this.FindControl<TextBox>("LoginPasswordBox")
                : this.FindControl<ComboBox>("AccountSelector");

        if (target is null)
        {
            return;
        }

        target.Focus();

        // Cửa sổ cha có thể chưa có focus (đặc biệt lúc app vừa mở) ->
        // đưa focus về cửa sổ trước để ô nhập nhận được focus.
        var top = TopLevel.GetTopLevel(this);
        if (top is not null && !ReferenceEquals(top.FocusManager?.GetFocusedElement(), target))
        {
            top.Focus();
            target.Focus();
        }
    }
}
