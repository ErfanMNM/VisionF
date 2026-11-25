using System.Text.Json.Serialization;

namespace ClassLibraryMT.PoServer;

/// <summary>
/// Metadata PO đi riêng qua query string hoặc các field multipart.
/// Tập mã chỉ được đọc từ một file có duy nhất cột CodeContent.
/// </summary>
public sealed class PoPushRequest
{
    /// <summary>
    /// Tên / mã lệnh sản xuất (bắt buộc). Dùng làm khoá duy nhất —
    /// PO chưa bắt đầu được bổ sung mã mới; PO đã kết thúc bị từ chối HTTP 409.
    /// </summary>
    [JsonPropertyName("poName")]
    public string PoName { get; set; } = string.Empty;

    /// <summary>
    /// Số dòng mã hợp lệ trong lần gửi (bắt buộc, &gt; 0), kiểm tra trước lọc trùng.
    /// </summary>
    [JsonPropertyName("orderQty")]
    public int OrderQty { get; set; }

    /// <summary>Mã lô / batch code — lưu vào trường mở rộng.</summary>
    [JsonPropertyName("batchCode")]
    public string? BatchCode { get; set; }

    /// <summary>Tên sản phẩm — lưu vào trường mở rộng.</summary>
    [JsonPropertyName("productName")]
    public string? ProductName { get; set; }

    /// <summary>Tên khách hàng — lưu vào trường mở rộng.</summary>
    [JsonPropertyName("customer")]
    public string? Customer { get; set; }

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

    /// <summary>Số mã mới được lưu trong lần gửi; 0 nếu lỗi hoặc toàn bộ đã có.</summary>
    [JsonPropertyName("accepted")]
    public int Accepted { get; set; }

    /// <summary>Số mã trùng đã tự động bỏ qua.</summary>
    [JsonPropertyName("ignoredDuplicates")]
    public int IgnoredDuplicates { get; set; }
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
