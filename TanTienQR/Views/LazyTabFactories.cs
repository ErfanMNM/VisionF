using Avalonia.Controls;

namespace TanTienQR.Views;

/// <summary>
/// Các factory tĩnh tạo View cho từng tab.
///
/// Dùng lambda / method group tĩnh (KHÔNG reflection, KHÔNG Activator) nên
/// an toàn khi publish Native AOT — trình biên dịch AOT nhìn thấy trực tiếp
/// mọi lớp View được tham chiếu ở đây nên không bị trim mất.
///
/// XAML gọi được các factory này qua <c>x:Static</c>:
/// <code>
/// Factory="{x:Static views:LazyTabFactories.Production}"
/// </code>
/// </summary>
public static class LazyTabFactories
{
    /// <summary>Tab Thông tin (dựng sẵn ngay từ đầu).</summary>
    public static Func<Control> Info => static () => new InfoView();

    /// <summary>Tab Sản xuất (WebView2 — nặng, dựng khi mở tab).</summary>
    public static Func<Control> Production => static () => new ProductionView();

    /// <summary>Tab Cấu hình (bảng tham số lớn — dựng khi mở tab).</summary>
    public static Func<Control> Config => static () => new ConfigView();

    /// <summary>Tab Lịch sử (bảng log lớn — dựng khi mở tab).</summary>
    public static Func<Control> History => static () => new HistoryView();

    /// <summary>Tab Tài khoản (dựng khi mở tab).</summary>
    public static Func<Control> Accounts => static () => new AccountsView();

    /// <summary>Tab Chỉnh PLC (WebView2 camera + bảng ô nhớ — nặng).</summary>
    public static Func<Control> PlcSettings => static () => new PlcSettingsView();

    /// <summary>Tab Dev (danh sách service DI + log — dựng khi mở tab).</summary>
    public static Func<Control> Dev => static () => new DevView();

    /// <summary>Tab Tra cứu mã (dựng khi mở tab).</summary>
    public static Func<Control> CodeLookup => static () => new CodeLookupView();

    /// <summary>Tab Truy xuất sản xuất (dựng khi mở tab).</summary>
    public static Func<Control> ProductionOrders => static () => new ProductionOrdersView();

    public static Func<Control> Print => static () => new PrintView();
}
