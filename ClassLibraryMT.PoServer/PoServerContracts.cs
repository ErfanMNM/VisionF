using System.Text.Json.Serialization;

namespace ClassLibraryMT.PoServer;

/// <summary>
/// Yêu cầu nhận Production Order từ MES (body của <c>POST /api/po</c>).
///
/// <para>
/// Tập mã được cung cấp theo MỘT trong hai cách:
/// <list type="bullet">
/// <item><c>codes</c> — mảng JSON các chuỗi mã.</item>
/// <item><c>csv</c> — nội dung CSV (mỗi dòng một mã, có thể có dòng tiêu đề).</item>
/// </list>
/// Nếu có cả hai thì <c>codes</c> được ưu tiên.
/// </para>
/// </summary>
public sealed class PoPushRequest
{
    /// <summary>
    /// Tên / mã lệnh sản xuất (bắt buộc). Dùng làm khoá duy nhất —
    /// trùng với PO đã có trên máy thì trả về HTTP 409.
    /// </summary>
    [JsonPropertyName("poName")]
    public string PoName { get; set; } = string.Empty;

    /// <summary>
    /// Số lượng mã của PO (bắt buộc, &gt; 0).
    /// </summary>
    [JsonPropertyName("orderQty")]
    public int OrderQty { get; set; }

    /// <summary>
    /// Danh sách mã (cách 1). Mỗi phần tử là một mã đầy đủ.
    /// </summary>
    [JsonPropertyName("codes")]
    public List<string>? Codes { get; set; }

    /// <summary>
    /// Nội dung CSV chứa tập mã (cách 2) — mỗi dòng một mã,
    /// dòng đầu có thể là tiêu đề.
    /// </summary>
    [JsonPropertyName("csv")]
    public string? Csv { get; set; }

    /// <summary>GTIN của lô (mã sản phẩm) — lưu vào trường mở rộng.</summary>
    [JsonPropertyName("gtin")]
    public string? Gtin { get; set; }

    /// <summary>Mã lô / batch code — lưu vào trường mở rộng.</summary>
    [JsonPropertyName("batchCode")]
    public string? BatchCode { get; set; }

    /// <summary>Tên sản phẩm — lưu vào trường mở rộng.</summary>
    [JsonPropertyName("productName")]
    public string? ProductName { get; set; }

    /// <summary>Tên khách hàng — lưu vào trường mở rộng.</summary>
    [JsonPropertyName("customer")]
    public string? Customer { get; set; }

    /// <summary>
    /// Các trường mở rộng bổ sung (key → value) — ghi thẳng vào
    /// <c>ProductionOrder.Additional</c> của PO.
    /// </summary>
    [JsonPropertyName("additional")]
    public Dictionary<string, string>? Additional { get; set; }
}

/// <summary>
/// Phản hồi trả về cho MES (mọi endpoint đều dùng cùng một shape).
///
/// <para>
/// Ví dụ thành công:
/// <code>
/// { "success": true, "message": "Đã nhận PO PO-001 với 120 mã.",
///   "poName": "PO-001", "accepted": 120 }
/// </code>
/// </para>
/// </summary>
public sealed class PoPushResponse
{
    /// <summary>True nếu xử lý thành công.</summary>
    [JsonPropertyName("success")]
    public bool Success { get; set; }

    /// <summary>Thông điệp tiếng Việt giải thích kết quả / lỗi.</summary>
    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;

    /// <summary>Mã PO (có thể rỗng nếu không đọc được).</summary>
    [JsonPropertyName("poName")]
    public string PoName { get; set; } = string.Empty;

    /// <summary>Số mã đã chấp nhận (0 nếu lỗi).</summary>
    [JsonPropertyName("accepted")]
    public int Accepted { get; set; }
}

/// <summary>
/// Phản hồi của endpoint kiểm tra sống (<c>GET /api/health</c>).
/// </summary>
public sealed class PoHealthResponse
{
    /// <summary>True nếu server đang chạy và sẵn sàng nhận PO.</summary>
    [JsonPropertyName("success")]
    public bool Success { get; set; }

    /// <summary>Thông điệp trạng thái.</summary>
    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;

    /// <summary>Prefix mà server đang lắng nghe (VD <c>http://127.0.0.1:8085/</c>).</summary>
    [JsonPropertyName("prefix")]
    public string Prefix { get; set; } = string.Empty;

    /// <summary>Số PO đã nhận được từ lúc bật server.</summary>
    [JsonPropertyName("received")]
    public int Received { get; set; }

    /// <summary>Thời điểm khởi động server (UTC).</summary>
    [JsonPropertyName("startedUtc")]
    public DateTime StartedUtc { get; set; }
}
