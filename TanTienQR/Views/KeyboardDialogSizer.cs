using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia.Controls;
using TanTienQR.ViewModels;

namespace TanTienQR.Views;

/// <summary>
/// Tiện ích dùng chung cho các hộp thoại con có bàn phím ảo.
///
/// Bàn phím ảo nằm ở hàng riêng (layout "push") nên khi mở, hộp thoại cần
/// thêm chiều cao để vừa bàn phím mà form vẫn nhìn thấy. Hộp thoại nhỏ
/// (400-560px) thường không đủ chỗ, nên ta tự giãn chiều cao cửa sổ theo
/// chiều cao vùng làm việc (WorkingArea) của màn hình.
///
/// Cách dùng: gọi <see cref="Attach"/> trong <c>OnOpened</c> và
/// <see cref="Detach"/> trong <c>OnClosed</c>.
/// </summary>
internal static class KeyboardDialogSizer
{
    /// <summary>
    /// Chiều cao dự trữ cho bàn phím ảo (px logic) khi CHƯA bật hàng phím
    /// tiếng Việt.
    ///
    /// Bàn phím gồm: thanh trên (~36) + ô nhập mirror (44) + khoảng đệm (~24)
    /// + các hàng phím (42+48+48+48+44) + khoảng đệm các hàng (~25) ≈ 370px.
    /// </summary>
    private const double KeyboardHeight = 370;

    /// <summary>
    /// Chiều cao thêm khi BẬT hàng phím tiếng Việt.
    ///
    /// Hàng này gồm 6 hàng phím (tiêu đề nguyên âm + 5 dấu) cao 30px,
    /// khoảng đệm (~15) và viền/padding (~10) ≈ 205px.
    /// </summary>
    private const double VietnameseRowHeight = 205;

    /// <summary>
    /// Khoảng đệm thêm cho bàn phím + nút bấm khi cửa sổ được giãn.
    /// </summary>
    private const double ExtraPadding = 12;

    /// <summary>
    /// Cửa sổ hộp thoại đang được theo dõi, gắn theo ViewModel bàn phím.
    ///
    /// Dùng bảng tra yếu để không giữ tham chiếu tới cửa sổ đã đóng.
    /// </summary>
    private static readonly ConditionalWeakTable<VirtualKeyboardViewModel, Window> _windows = [];

    /// <summary>
    /// Gắn theo dõi trạng thái bàn phím để tự giãn / thu cửa sổ.
    /// </summary>
    /// <param name="window">Cửa sổ hộp thoại.</param>
    /// <param name="keyboard">ViewModel bàn phím (null = không làm gì).</param>
    public static void Attach(Window window, VirtualKeyboardViewModel? keyboard)
    {
        if (keyboard is null)
        {
            return;
        }

        // Lưu chiều cao gốc để biết cần trả về bao nhiêu khi đóng bàn phím.
        if (window.Tag is not double)
        {
            window.Tag = window.Height;
        }

        _windows.Remove(keyboard);
        _windows.Add(keyboard, window);

        keyboard.PropertyChanged += OnKeyboardPropertyChanged;
        ApplyHeight(window, keyboard.IsVisible, keyboard.IsVietnameseKeysVisible);
    }

    /// <summary>
    /// Gỡ theo dõi (gọi khi cửa sổ đóng).
    /// </summary>
    /// <param name="keyboard">ViewModel bàn phím.</param>
    public static void Detach(VirtualKeyboardViewModel? keyboard)
    {
        if (keyboard is null)
        {
            return;
        }

        keyboard.PropertyChanged -= OnKeyboardPropertyChanged;
        _windows.Remove(keyboard);
    }

    /// <summary>
    /// Bàn phím đổi trạng thái -> giãn / thu cửa sổ.
    /// </summary>
    /// <param name="sender">ViewModel bàn phím.</param>
    /// <param name="e">Tham số sự kiện.</param>
    private static void OnKeyboardPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (nameof(VirtualKeyboardViewModel.IsVisible)
            or nameof(VirtualKeyboardViewModel.IsVietnameseKeysVisible)))
        {
            return;
        }

        if (sender is not VirtualKeyboardViewModel keyboard)
        {
            return;
        }

        // Cửa sổ hộp thoại đã đăng ký với ViewModel bàn phím này.
        if (!_windows.TryGetValue(keyboard, out var window))
        {
            return;
        }

        ApplyHeight(window, keyboard.IsVisible, keyboard.IsVietnameseKeysVisible);
    }

    /// <summary>
    /// Áp dụng chiều cao cho cửa sổ: thêm bàn phím khi mở, trừ lại khi đóng.
    /// </summary>
    /// <param name="window">Cửa sổ cần chỉnh.</param>
    /// <param name="keyboardVisible">Bàn phím đang mở hay không.</param>
    /// <param name="vietnameseVisible">Hàng phím tiếng Việt đang bật hay không.</param>
    public static void ApplyHeight(Window window, bool keyboardVisible, bool vietnameseVisible = false)
    {
        // Chiều cao gốc của cửa sổ (không kể bàn phím).
        var baseHeight = window.Tag is double tagged ? tagged : window.Height;

        if (!keyboardVisible)
        {
            window.Height = baseHeight;
            window.MaxHeight = double.PositiveInfinity;
            return;
        }

        var target = baseHeight + KeyboardHeight + ExtraPadding;

        // Hàng phím tiếng Việt (mặc định ẩn) -> thêm chiều cao khi bật.
        if (vietnameseVisible)
        {
            target += VietnameseRowHeight;
        }

        // Không cho vượt quá vùng làm việc của màn hình.
        var workingArea = GetWorkingArea(window);
        if (workingArea > 0 && target > workingArea)
        {
            target = workingArea;
        }

        window.Height = target;
        window.MaxHeight = workingArea > 0 ? workingArea : double.PositiveInfinity;
    }

    /// <summary>
    /// Lấy chiều cao vùng làm việc của màn hình đang hiển thị cửa sổ.
    /// </summary>
    /// <param name="window">Cửa sổ.</param>
    /// <returns>Chiều cao vùng làm việc (px), 0 nếu không xác định.</returns>
    private static double GetWorkingArea(Window window)
    {
        try
        {
            // Screens là thuộc tính instance của TopLevel.
            var screen = window.Screens.ScreenFromWindow(window);
            return screen?.WorkingArea.Height ?? 0;
        }
        catch (Exception)
        {
            // Một số nền tảng không hỗ trợ truy vấn màn hình -> bỏ qua.
            return 0;
        }
    }
}
