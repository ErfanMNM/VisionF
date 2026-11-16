using Avalonia;
using Avalonia.Controls;

namespace TanTienQR.Views;

/// <summary>
/// Khung chứa nội dung tab theo kiểu "lazy": view chỉ được TẠO khi lần đầu
/// người dùng mở tab đó, sau đó được giữ lại (cache) để chuyển tab qua lại
/// không phải dựng lại.
///
/// Vì sao cần?
/// Màn hình trước đây nhúng CỨNG 9 UserControl trong cùng một ô Grid
/// (Info / Production / Config / History / Accounts / PlcSettings / Dev /
/// CodeLookup / ProductionOrders). Tất cả đều được dựng ngay khi MainWindow
/// mở -> frame đầu tiên nặng (WebView2 trong Production/PlcSettings, bảng
/// lịch sử, bảng service DI...). Nay chỉ <b>InfoView</b> được dựng sẵn;
/// các tab nặng còn lại dựng đúng lúc người dùng bấm vào.
///
/// Cách dùng trong XAML:
/// <code>
/// &lt;views:LazyTabHost x:Name="ProductionTab"
///                 IsVisible="{Binding IsProductionVisible}"
///                 Factory="{x:Static views:LazyTabFactories.Production}" /&gt;
/// </code>
///
/// Không dùng reflection / Activator: factory là lambda tĩnh trong
/// <see cref="LazyTabFactories"/> -> an toàn với Native AOT.
/// </summary>
public partial class LazyTabHost : ContentControl
{
    /// <summary>
    /// Hàm tạo view (lazy). Gọi đúng 1 lần, lần đầu tiên host được hiện.
    /// </summary>
    public Func<Control>? Factory { get; set; }

    /// <summary>
    /// True nếu view đã được tạo (cache).
    /// </summary>
    public bool IsCreated => Content is not null;

    /// <summary>
    /// Khi host được hiện -> tạo view (nếu chưa có) và gán vào Content.
    ///
    /// CHỈ tạo khi host thực sự hiện: các host khác cùng nằm trong Grid cha
    /// (dù IsVisible=false) vẫn được attach vào cây hình ảnh -> nếu tạo ở
    /// đây thì lại dựng sẵn cả 9 view, mất ý nghĩa của lazy.
    /// </summary>
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);

        if (IsVisible)
        {
            EnsureCreated();
        }
    }

    /// <summary>
    /// Host chuyển từ ẩn -> hiện -> tạo view (nếu chưa tạo).
    /// </summary>
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == IsVisibleProperty && IsVisible)
        {
            EnsureCreated();
        }
    }

    /// <summary>
    /// Tạo view nếu chưa tạo. Gọi tường minh từ MainViewModel khi chuyển tab
    /// để việc dựng xảy ra ĐÚNG lúc người dùng bấm tab (không phụ thuộc
    /// vòng layout của Avalonia).
    /// </summary>
    public void EnsureCreated()
    {
        if (Content is not null)
        {
            return;
        }

        var factory = Factory;
        if (factory is null)
        {
            return;
        }

        Content = factory();
    }
}
