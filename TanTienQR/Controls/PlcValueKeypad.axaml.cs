using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Interactivity;
using TanTienQR.ViewModels;

namespace TanTienQR.Controls;

/// <summary>
/// Bàn phím số nhỏ cho ô nhập giá trị PLC (Short / Int32).
///
/// Control này KHÔNG tự quản lý dòng đang gõ — nó nhận dòng từ
/// <see cref="PlcValueKeypadViewModel.Row"/> (do ViewModel trang "Chỉnh PLC"
/// gán) rồi ghi thẳng giá trị vào dòng đó.
///
/// Các phím có <c>Focusable=False</c> để bấm phím không cướp focus khỏi ô
/// nhập — nếu không, bàn phím sẽ tự đóng sau mỗi lần bấm.
/// </summary>
public partial class PlcValueKeypad : UserControl
{
    /// <summary>ViewModel đang gắn (để hủy đăng ký khi đổi DataContext).</summary>
    private PlcValueKeypadViewModel? _boundVm;

    /// <summary>Khởi tạo control.</summary>
    public PlcValueKeypad()
    {
        InitializeComponent();

        DataContextChanged += OnDataContextChanged;
    }

    /// <summary>ViewModel của bàn phím (lấy từ DataContext của control).</summary>
    private PlcValueKeypadViewModel? Vm => DataContext as PlcValueKeypadViewModel;

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
    }

    /// <summary>
    /// VM đổi trạng thái -> bàn phím chỉ cần cập nhật khi dòng đang gõ đổi.
    ///
    /// Giá trị hiển thị, nhãn gợi ý và trạng thái nút +/- đều được VM tự báo
    /// qua <c>OnPropertyChanged</c> nên binding trong XAML tự cập nhật.
    /// </summary>
    /// <param name="sender">VM.</param>
    /// <param name="e">Tham số sự kiện.</param>
    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Giữ handler để hủy đăng ký đúng cách khi đổi DataContext.
        _ = e;
    }

    /// <summary>Bấm một phím số (0-9).</summary>
    /// <param name="sender">Nút phím.</param>
    /// <param name="e">Tham số sự kiện.</param>
    private void OnDigitClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string digit })
        {
            return;
        }

        Vm?.InputDigit(digit);
    }

    /// <summary>Bấm phím "C" (xoá trắng).</summary>
    /// <param name="sender">Nút phím.</param>
    /// <param name="e">Tham số sự kiện.</param>
    private void OnClearClick(object? sender, RoutedEventArgs e) => Vm?.Clear();

    /// <summary>Bấm phím "±" (đảo dấu).</summary>
    /// <param name="sender">Nút phím.</param>
    /// <param name="e">Tham số sự kiện.</param>
    private void OnSignClick(object? sender, RoutedEventArgs e) => Vm?.ToggleSign();

    /// <summary>Bấm nút "+1" (tăng giá trị lên 1).</summary>
    /// <param name="sender">Nút phím.</param>
    /// <param name="e">Tham số sự kiện.</param>
    private void OnIncrementClick(object? sender, RoutedEventArgs e) => Vm?.Increment();

    /// <summary>Bấm nút "-1" (giảm giá trị đi 1).</summary>
    /// <param name="sender">Nút phím.</param>
    /// <param name="e">Tham số sự kiện.</param>
    private void OnDecrementClick(object? sender, RoutedEventArgs e) => Vm?.Decrement();

    /// <summary>Bấm nút "OK" (chốt giá trị và đóng bàn phím).</summary>
    /// <param name="sender">Nút phím.</param>
    /// <param name="e">Tham số sự kiện.</param>
    private void OnCommitClick(object? sender, RoutedEventArgs e) => Vm?.Commit();
}
