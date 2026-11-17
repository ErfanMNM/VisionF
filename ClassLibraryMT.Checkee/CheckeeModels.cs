using System.Text.Json.Serialization;

namespace ClassLibraryMT.Checkee;

/// <summary>
/// Phản hồi chung của Checkee Middleware API.
///
/// <para>
/// Theo spec: <c>SuccessResponse&lt;T&gt;</c> = { code, status, message, data };
/// <c>ErrorResponse</c> = { code, status: false, message, data: null }.
/// </para>
/// </summary>
/// <typeparam name="T">Kiểu dữ liệu trong trường <c>data</c>.</typeparam>
public sealed class CheckeeResponse<T>
{
    /// <summary>Mã trả về (thường trùng HTTP status).</summary>
    [JsonPropertyName("code")]
    public int Code { get; set; }

    /// <summary>True nếu thành công.</summary>
    [JsonPropertyName("status")]
    public bool Status { get; set; }

    /// <summary>Thông điệp từ server (có thể là mã lỗi như <c>NOTFOUND_SERIAL</c>).</summary>
    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;

    /// <summary>Dữ liệu trả về (null khi lỗi).</summary>
    [JsonPropertyName("data")]
    public T? Data { get; set; }
}

/// <summary>Dữ liệu trả về của API lấy access token.</summary>
public sealed class CheckeeTokenData
{
    /// <summary>Tên doanh nghiệp / client.</summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Token dạng <c>"accessToken=&lt;JWT&gt;"</c> (đúng định dạng của spec).
    /// </summary>
    [JsonPropertyName("token")]
    public string Token { get; set; } = string.Empty;
}

/// <summary>
/// Payload gửi kích hoạt mã TXNG (CTE) — <c>POST /middleware-api/CTEs/active</c>.
///
/// <para>
/// Theo spec các trường bắt buộc: <c>GTIN</c>, <c>batchCode</c>, <c>type</c>;
/// <c>serialNumbers</c> là danh sách mã serial cần kích hoạt (hoặc dùng
/// <c>fromIndex</c>/<c>toIndex</c>).
/// </para>
/// </summary>
public sealed class CheckeeActivateRequest
{
    /// <summary>Mã định danh sản phẩm (GTIN).</summary>
    [JsonPropertyName("GTIN")]
    public string Gtin { get; set; } = string.Empty;

    /// <summary>Mã lô / lệnh sản xuất.</summary>
    [JsonPropertyName("batchCode")]
    public string BatchCode { get; set; } = string.Empty;

    /// <summary>Loại mã TXNG: <c>RANDOM</c> / <c>DECLARE</c> / <c>INCREASING</c>.</summary>
    [JsonPropertyName("type")]
    public string Type { get; set; } = "DECLARE";

    /// <summary>Danh sách mã serial cần kích hoạt.</summary>
    [JsonPropertyName("serialNumbers")]
    public List<string> SerialNumbers { get; set; } = [];
}

/// <summary>Dữ liệu trả về sau khi kích hoạt mã.</summary>
public sealed class CheckeeActiveData
{
    /// <summary>Tên sản phẩm / lô.</summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>GTIN sản phẩm.</summary>
    [JsonPropertyName("GTIN")]
    public string Gtin { get; set; } = string.Empty;

    /// <summary>Mã batch.</summary>
    [JsonPropertyName("batch")]
    public string Batch { get; set; } = string.Empty;

    /// <summary>Số mã trong lô.</summary>
    [JsonPropertyName("quantityInBatch")]
    public int QuantityInBatch { get; set; }

    /// <summary>Số mã đã kích hoạt.</summary>
    [JsonPropertyName("activeSerials")]
    public int ActiveSerials { get; set; }
}

/// <summary>Kết quả gửi mã lên Checkee (đã quy đổi sang thông điệp tiếng Việt).</summary>
public sealed class CheckeeSendResult
{
    /// <summary>True nếu server trả HTTP 200.</summary>
    public bool Ok { get; init; }

    /// <summary>Mã HTTP trả về (0 nếu không có phản hồi).</summary>
    public int StatusCode { get; init; }

    /// <summary>Thông điệp từ server (đã dịch sang tiếng Việt nếu là mã lỗi).</summary>
    public string Message { get; init; } = string.Empty;

    /// <summary>Thông điệp thô từ server (giữ nguyên để tra cứu).</summary>
    public string RawMessage { get; init; } = string.Empty;

    /// <summary>
    /// True nếu lỗi do server không phản hồi (timeout / mạng) — mã phải được
    /// giữ lại để thử lại ở chu kỳ sau.
    /// </summary>
    public bool IsTransient { get; init; }
}
