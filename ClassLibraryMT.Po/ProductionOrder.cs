namespace ClassLibraryMT.Po;

/// <summary>
/// Thông tin cơ bản của một lệnh sản xuất (Production Order — PO).
///
/// <para>
/// Cấu trúc cố tình giữ tối giản: <see cref="OrderNo"/> và
/// <see cref="OrderQty"/> là hai trường BẮT BUỘC, mọi trường mở rộng
/// sau này được gom vào <see cref="Additional"/> (dictionary JSON) nên không
/// phải đổi schema / model khi bổ sung thông tin (ví dụ: mã khách hàng,
/// ngày giao, ca sản xuất...).
/// </para>
/// </summary>
public sealed class ProductionOrder
{
    /// <summary>
    /// Mã lệnh sản xuất — khóa chính, duy nhất, không phân biệt hoa thường.
    /// </summary>
    public string OrderNo { get; set; } = "";

    /// <summary>
    /// Số lượng mã của đơn (số mã được phép sản xuất trong PO này).
    /// </summary>
    public int OrderQty { get; set; }

    /// <summary>Thời điểm tạo PO trên server (UTC).</summary>
    public DateTime CreatedUtc { get; set; }

    /// <summary>
    /// Trường mở rộng tùy chọn (JSON object).
    ///
    /// Cho phép server gửi thêm thông tin mà không cần sửa client:
    /// ví dụ <c>{"customer":"ABC","line":"L01"}</c>.
    /// </summary>
    public Dictionary<string, string> Additional { get; set; } = [];
}
