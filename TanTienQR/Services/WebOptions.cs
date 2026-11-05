namespace TanTienQR.Services;

// Cấu hình trang web nhúng (đọc từ appsettings.json, mục "Web").
internal sealed class WebOptions
{
    /// <summary>Địa chỉ trang web Checkee mở trong tab Sản xuất.</summary>
    public string Url { get; set; } = "https://estrace.checkee.vn";

    /// <summary>Bật/tắt tính năng mở web. Khi tắt, tab Sản xuất hiện thông báo.</summary>
    public bool Enabled { get; set; } = true;
}
