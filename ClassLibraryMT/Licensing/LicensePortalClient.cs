using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClassLibraryMT.Licensing;

/// <summary>Gửi yêu cầu cấp license lên portal, không mang thông tin đăng nhập quản trị.</summary>
public sealed class LicensePortalClient(HttpClient http)
{
    public async Task<LicenseRequestResponse> SubmitAsync(
        string baseUrl,
        string hwid,
        string customer = "",
        string contact = "",
        string note = "",
        CancellationToken ct = default)
    {
        var endpoint = ResolveEndpoint(baseUrl);
        var normalizedHwid = hwid.Replace("-", "", StringComparison.Ordinal).Replace(" ", "", StringComparison.Ordinal).ToUpperInvariant();
        if (normalizedHwid.Length != 32 || normalizedHwid.Any(c => !char.IsAsciiHexDigit(c)))
            throw new ArgumentException("Mã máy phải gồm đủ 32 ký tự hex.", nameof(hwid));

        var payload = new LicenseRequestPayload
        {
            Hwid = normalizedHwid,
            Product = "TanTienQR",
            Customer = ValidateText(customer, 200, "Tên khách hàng"),
            Contact = ValidateText(contact, 200, "Thông tin liên hệ"),
            Note = ValidateText(note, 1000, "Ghi chú"),
        };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload, LicensePortalJsonContext.Default.LicenseRequestPayload);
        if (bytes.Length > 8192)
            throw new ArgumentException("Nội dung yêu cầu quá dài; hãy rút ngắn ghi chú.");

        using var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new("application/json");
        try
        {
            using var response = await http.PostAsync(endpoint, content, ct).ConfigureAwait(false);
            LicenseRequestResponse? result;
            try
            {
                result = await response.Content.ReadFromJsonAsync(
                    LicensePortalJsonContext.Default.LicenseRequestResponse, ct).ConfigureAwait(false);
            }
            catch (JsonException ex)
            {
                throw new InvalidOperationException("Portal trả dữ liệu không hợp lệ. Hãy kiểm tra URL portal.", ex);
            }

            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException(result?.Error
                    ?? $"Portal từ chối yêu cầu (HTTP {(int)response.StatusCode}).");
            if (result is not { Ok: true } || string.IsNullOrWhiteSpace(result.RequestId)
                || result.Status != "pending")
                throw new InvalidOperationException("Portal chưa xác nhận đã lưu yêu cầu cấp license.");

            return result;
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new InvalidOperationException("Portal chưa phản hồi trong thời gian chờ. Có thể gửi lại để kiểm tra yêu cầu đang chờ.", ex);
        }
        catch (HttpRequestException ex)
        {
            throw new InvalidOperationException("Không kết nối được portal. Kiểm tra URL và kết nối Internet rồi thử lại.", ex);
        }
    }

    /// <summary>Chỉ gửi HWID qua HTTPS; HTTP dành riêng cho kiểm thử localhost.</summary>
    public static Uri ResolveEndpoint(string baseUrl)
    {
        if (!Uri.TryCreate(baseUrl?.Trim(), UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttps && !(uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback))
            || uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0)
            throw new ArgumentException("Nhập URL HTTPS của License Portal (HTTP chỉ dùng với localhost).", nameof(baseUrl));
        return new Uri(uri.AbsoluteUri.TrimEnd('/') + "/api/license-requests");
    }

    private static string ValidateText(string value, int maxLength, string label)
    {
        var text = (value ?? "").Trim();
        if (text.Length > maxLength)
            throw new ArgumentException($"{label} tối đa {maxLength} ký tự.");
        return text;
    }
}

public sealed class LicenseRequestPayload
{
    [JsonPropertyName("hwid")] public string Hwid { get; set; } = "";
    [JsonPropertyName("product")] public string Product { get; set; } = "TanTienQR";
    [JsonPropertyName("customer")] public string Customer { get; set; } = "";
    [JsonPropertyName("contact")] public string Contact { get; set; } = "";
    [JsonPropertyName("note")] public string Note { get; set; } = "";
}

public sealed class LicenseRequestResponse
{
    [JsonPropertyName("ok")] public bool Ok { get; set; }
    [JsonPropertyName("requestId")] public string RequestId { get; set; } = "";
    [JsonPropertyName("status")] public string Status { get; set; } = "";
    [JsonPropertyName("message")] public string Message { get; set; } = "";
    [JsonPropertyName("error")] public string? Error { get; set; }
}

[JsonSerializable(typeof(LicenseRequestPayload))]
[JsonSerializable(typeof(LicenseRequestResponse))]
internal sealed partial class LicensePortalJsonContext : JsonSerializerContext;
