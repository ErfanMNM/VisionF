using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace ClassLibraryMT.Checkee;

/// <summary>
/// Client gọi Checkee Middleware API.
///
/// <para>
/// Theo spec "Checkee Middleware API Integration Specification v1.1.1":
/// <list type="bullet">
/// <item>Lấy token: <c>GET {HOST}/onboarding/token</c> với header
/// <c>x-client-id</c> + <c>x-api-key</c> (KHÔNG dùng Bearer).</item>
/// <item>Gọi API nghiệp vụ: header <c>Authorization: Bearer accessToken=&lt;JWT&gt;</c>
/// + <c>x-api-key</c>.</item>
/// <item>Kích hoạt mã TXNG: <c>POST {HOST}/middleware-api/CTEs/active</c> với
/// body JSON.</item>
/// </list>
/// </para>
///
/// <para>
/// Dùng <see cref="CheckeeJsonContext"/> (source-generated) nên tương thích
/// Native AOT — không dùng reflection của <c>JsonSerializer</c>.
/// </para>
/// </summary>
public sealed class CheckeeClient : IDisposable
{
    /// <summary>Đường dẫn lấy access token.</summary>
    private const string TokenPath = "onboarding/token";

    /// <summary>Đường dẫn kích hoạt mã TXNG (CTE).</summary>
    private const string ActivatePath = "middleware-api/CTEs/active";

    private readonly HttpClient _http;
    private readonly CheckeeOptions _options;
    private readonly bool _ownsHttp;

    /// <summary>Access token đang cache (null = chưa lấy).</summary>
    private string? _accessToken;

    /// <summary>
    /// Chống nhiều luồng (timer 1s + gửi lại thủ công) cùng lấy token.
    /// </summary>
    private readonly SemaphoreSlim _tokenLock = new(1, 1);

    /// <summary>
    /// Khởi tạo client với <see cref="HttpClient"/> bên ngoài (dùng chung).
    /// </summary>
    /// <param name="http">HTTP client dùng chung.</param>
    /// <param name="options">Cấu hình Checkee.</param>
    public CheckeeClient(HttpClient http, CheckeeOptions options)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _ownsHttp = false;
    }

    /// <summary>
    /// Khởi tạo client tự tạo <see cref="HttpClient"/>.
    /// </summary>
    /// <param name="options">Cấu hình Checkee.</param>
    public CheckeeClient(CheckeeOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));

        _http = new HttpClient
        {
            BaseAddress = new Uri(options.ResolveBaseUrl() + "/"),
            Timeout = options.ResolveTimeout(),
        };
        _ownsHttp = true;
    }

    /// <summary>
    /// Lấy access token từ Checkee (có cache trong RAM).
    ///
    /// <para>
    /// Token trả về từ server có dạng <c>"accessToken=&lt;JWT&gt;"</c> — giữ
    /// nguyên định dạng để đặt thẳng vào header <c>Authorization</c>.
    /// </para>
    /// </summary>
    /// <param name="ct">Token hủy.</param>
    /// <returns>Chuỗi token (đã kèm tiền tố <c>accessToken=</c>).</returns>
    /// <exception cref="CheckeeServerException">
    /// Khi không lấy được token (lỗi mạng, timeout hoặc HTTP != 200).
    /// </exception>
    public Task<string> GetAccessTokenAsync(CancellationToken ct = default) =>
        GetAccessTokenAsync(forceRefresh: false, ct);

    /// <summary>
    /// Lấy token, có thể ép làm mới (dùng khi API trả 401 do token hết hạn).
    /// </summary>
    private async Task<string> GetAccessTokenAsync(bool forceRefresh, CancellationToken ct)
    {
        await _tokenLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!forceRefresh && !string.IsNullOrEmpty(_accessToken))
            {
                return _accessToken;
            }

            // Bỏ token cũ trước khi lấy cái mới (tránh dùng lại token hết hạn
            // nếu lượt lấy mới thất bại).
            _accessToken = null;

            if (!_options.IsConfigured)
            {
                throw new CheckeeServerException(
                    "Chưa cấu hình đầy đủ thông tin Checkee (BaseUrl / ApiKey / ClientId). "
                    + "Vào tab Cấu hình để bổ sung.");
            }

            using var request = new HttpRequestMessage(HttpMethod.Get, TokenPath);

            // Header theo spec: x-client-id + x-api-key (KHÔNG dùng Bearer token).
            request.Headers.TryAddWithoutValidation("x-client-id", _options.ClientId);
            request.Headers.TryAddWithoutValidation("x-api-key", _options.ApiKey);

            HttpResponseMessage response;
            try
            {
                response = await _http
                    .SendAsync(request, HttpCompletionOption.ResponseContentRead, ct)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException ex)
            {
                throw new CheckeeServerException(
                    "Checkee không phản hồi (quá thời gian chờ).", ex);
            }
            catch (HttpRequestException ex)
            {
                throw new CheckeeServerException(
                    "Không kết nối được tới Checkee. Kiểm tra mạng và địa chỉ (Checkee:BaseUrl).", ex);
            }

            using (response)
            {
                var body = await ReadBodyAsync(response, ct).ConfigureAwait(false);

                if (!response.IsSuccessStatusCode)
                {
                    var message = DescribeError(response.StatusCode, body);
                    throw new CheckeeServerException(message, (int)response.StatusCode);
                }

                CheckeeResponse<CheckeeTokenData>? parsed;
                try
                {
                    parsed = await response.Content
                        .ReadFromJsonAsync(
                            CheckeeJsonContext.Default.CheckeeResponseCheckeeTokenData,
                            ct)
                        .ConfigureAwait(false);
                }
                catch (JsonException ex)
                {
                    throw new CheckeeServerException(
                        "Dữ liệu Checkee trả về không hợp lệ (không đọc được access token).", ex);
                }

                var token = parsed?.Data?.Token;
                if (string.IsNullOrWhiteSpace(token))
                {
                    var message = string.IsNullOrWhiteSpace(parsed?.Message)
                        ? "Checkee không trả về access token."
                        : $"Checkee không trả về access token: {parsed!.Message}";
                    throw new CheckeeServerException(message, (int)response.StatusCode);
                }

                _accessToken = token;
                return token;
            }
        }
        finally
        {
            _tokenLock.Release();
        }
    }

    /// <summary>
    /// Gửi kích hoạt mã TXNG lên Checkee (<c>POST /middleware-api/CTEs/active</c>).
    ///
    /// <para>
    /// Trả về <see cref="CheckeeSendResult"/> — KHÔNG ném exception khi server
    /// trả HTTP != 200, để lớp gọi có thể đánh dấu mã là "gửi lỗi" và hiển
    /// thị cho người dùng. Chỉ ném <see cref="CheckeeServerException"/> khi
    /// không có phản hồi HTTP (timeout / lỗi mạng) — trường hợp này mã phải
    /// được giữ lại để thử lại.
    /// </para>
    /// </summary>
    /// <param name="gtin">GTIN sản phẩm.</param>
    /// <param name="batchCode">Mã lô / lệnh sản xuất.</param>
    /// <param name="serialType">Loại mã TXNG (RANDOM / DECLARE / INCREASING).</param>
    /// <param name="serialNumbers">Danh sách serial hoặc URL Digital Link; serial sau /21/ được tách khi tạo payload.</param>
    /// <param name="ct">Token hủy.</param>
    /// <returns>Kết quả gửi.</returns>
    public async Task<CheckeeSendResult> ActivateAsync(
        string gtin,
        string batchCode,
        string serialType,
        IReadOnlyList<string> serialNumbers,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(serialNumbers);

        if (serialNumbers.Count == 0)
        {
            return new CheckeeSendResult
            {
                Ok = true,
                StatusCode = 200,
                Message = "Không có mã nào để gửi.",
            };
        }

        var payload = new CheckeeActivateRequest
        {
            Gtin = gtin ?? string.Empty,
            BatchCode = batchCode ?? string.Empty,
            Type = string.IsNullOrWhiteSpace(serialType) ? "DECLARE" : serialType,
            SerialNumbers = serialNumbers.Select(ExtractSerialNumber).Distinct(StringComparer.Ordinal).ToList(),
            FromIndex = 0,
            ToIndex = 0,
        };

        // Tối đa 2 lần: nếu token hết hạn (401) thì ép lấy token mới rồi gửi lại.
        for (var attempt = 0; attempt < 2; attempt++)
        {
            string token;
            try
            {
                token = await GetAccessTokenAsync(forceRefresh: attempt > 0, ct).ConfigureAwait(false);
            }
            catch (CheckeeServerException ex)
            {
                // Không lấy được token -> không gửi được. Giữ mã ở trạng thái chờ
                // nếu lỗi tạm thời (mạng / timeout).
                return new CheckeeSendResult
                {
                    Ok = false,
                    StatusCode = ex.StatusCode,
                    Message = ex.Message,
                    RawMessage = ex.Message,
                    IsTransient = ex.IsTransient,
                };
            }

            // Mỗi lần thử cần một content mới (content bị dispose cùng request).
            using var content = JsonContent.Create(
                payload,
                CheckeeJsonContext.Default.CheckeeActivateRequest);

            // Header theo spec cho API nghiệp vụ: Bearer token + x-api-key.
            // Token trả về đã có tiền tố "accessToken=" -> "Bearer accessToken=<JWT>".
            using var request = new HttpRequestMessage(HttpMethod.Post, ActivatePath)
            {
                Content = content,
            };
            request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {token}");
            request.Headers.TryAddWithoutValidation("x-api-key", _options.ApiKey);

            HttpResponseMessage response;
            try
            {
                response = await _http.SendAsync(request, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException ex)
            {
                return new CheckeeSendResult
                {
                    Ok = false,
                    StatusCode = 0,
                    Message = "Checkee không phản hồi (quá thời gian chờ). Sẽ thử gửi lại ở chu kỳ sau.",
                    RawMessage = ex.Message,
                    IsTransient = true,
                };
            }
            catch (HttpRequestException ex)
            {
                return new CheckeeSendResult
                {
                    Ok = false,
                    StatusCode = 0,
                    Message = "Không kết nối được tới Checkee. Sẽ thử gửi lại ở chu kỳ sau.",
                    RawMessage = ex.Message,
                    IsTransient = true,
                };
            }

            using (response)
            {
                if (response.StatusCode == HttpStatusCode.Unauthorized && attempt == 0)
                {
                    // Token hết hạn/không hợp lệ -> vòng sau làm mới rồi gửi lại.
                    continue;
                }

                var body = await ReadBodyAsync(response, ct).ConfigureAwait(false);

                if (!response.IsSuccessStatusCode)
                {
                    return new CheckeeSendResult
                    {
                        Ok = false,
                        StatusCode = (int)response.StatusCode,
                        Message = DescribeError(response.StatusCode, body),
                        RawMessage = body,
                        IsTransient = false,
                    };
                }

                // HTTP 200 nhưng body có status=false -> vẫn là lỗi nghiệp vụ.
                CheckeeResponse<CheckeeActiveData>? parsed = null;
                try
                {
                    parsed = await response.Content
                        .ReadFromJsonAsync(
                            CheckeeJsonContext.Default.CheckeeResponseCheckeeActiveData,
                            ct)
                        .ConfigureAwait(false);
                }
                catch (JsonException)
                {
                    // Body không đọc được -> coi như thành công (HTTP 200 đã đúng).
                }

                if (parsed is not null && !parsed.Status && !string.IsNullOrWhiteSpace(parsed.Message))
                {
                    return new CheckeeSendResult
                    {
                        Ok = false,
                        StatusCode = (int)response.StatusCode,
                        Message = DescribeError(response.StatusCode, parsed.Message),
                        RawMessage = parsed.Message,
                        IsTransient = false,
                    };
                }

                var activated = parsed?.Data?.ActiveSerials ?? payload.SerialNumbers.Count;

                return new CheckeeSendResult
                {
                    Ok = true,
                    StatusCode = (int)response.StatusCode,
                    Message = $"Đã gửi {activated} mã lên Checkee.",
                    RawMessage = parsed?.Message ?? string.Empty,
                };
            }
        }

        return new CheckeeSendResult
        {
            Ok = false,
            StatusCode = 401,
            Message = "Xác thực Checkee thất bại sau khi làm mới token.",
            RawMessage = "Unauthorized",
            IsTransient = false,
        };
    }

    /// <summary>Tách serial từ path AI 21 của URL khi gửi Checkee, không thay đổi CodeContent đã lưu.</summary>
    private static string ExtractSerialNumber(string codeContent)
    {
        if (!Uri.TryCreate(codeContent, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            return codeContent;

        var path = uri.AbsolutePath;
        var marker = path.LastIndexOf("/21/", StringComparison.Ordinal);
        if (marker < 0) return codeContent;

        var serial = path[(marker + 4)..];
        return serial.Length > 0 && !serial.Contains('/')
            ? Uri.UnescapeDataString(serial)
            : codeContent;
    }

    /// <summary>
    /// Xoá token cache (dùng khi đổi cấu hình hoặc token hết hạn).
    /// </summary>
    public void ClearToken() => _accessToken = null;

    /// <summary>
    /// Đọc nội dung phản hồi dạng chuỗi (không ném lỗi).
    /// </summary>
    private static async Task<string> ReadBodyAsync(
        HttpResponseMessage response,
        CancellationToken ct)
    {
        try
        {
            return await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            return string.Empty;
        }
    }

    /// <summary>
    /// Chuyển mã lỗi của Checkee thành thông điệp tiếng Việt dễ hiểu.
    /// </summary>
    /// <param name="statusCode">Mã HTTP trả về.</param>
    /// <param name="body">Nội dung phản hồi thô.</param>
    /// <returns>Thông điệp tiếng Việt.</returns>
    public static string DescribeError(System.Net.HttpStatusCode statusCode, string? body)
    {
        var code = (int)statusCode;

        // Thử bóc message từ body JSON {"code":400,"status":false,"message":"..."}
        var raw = ExtractMessage(body);

        var reason = raw ?? $"HTTP {code} {statusCode}";

        var translated = TranslateErrorCode(raw) ?? reason;

        return $"Checkee trả về lỗi HTTP {code}: {translated}";
    }

    /// <summary>
    /// Bóc trường <c>message</c> từ body JSON mà không dùng reflection
    /// (đọc thủ công để giữ AOT-safe).
    /// </summary>
    private static string? ExtractMessage(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("message", out var message)
                && message.ValueKind == JsonValueKind.String)
            {
                var value = message.GetString();
                return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
            }
        }
        catch (JsonException)
        {
            // Body không phải JSON -> dùng nguyên chuỗi.
        }

        var trimmed = body.Trim();
        return trimmed.Length > 300 ? trimmed[..300] : trimmed;
    }

    /// <summary>
    /// Dịch mã lỗi nghiệp vụ của Checkee sang tiếng Việt (theo mục 14 spec).
    /// </summary>
    /// <param name="code">Mã lỗi (VD: <c>NOTFOUND_SERIAL</c>).</param>
    /// <returns>Thông điệp tiếng Việt, null nếu không biết mã này.</returns>
    public static string? TranslateErrorCode(string? code)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return null;
        }

        return code.Trim().ToUpperInvariant() switch
        {
            "REQUIRED_API_KEY" => "Thiếu header x-api-key.",
            "INVALID_HEADER" => "Header không đúng kiểu dữ liệu mong đợi.",
            "INACTIVE_CLIENT" => "Doanh nghiệp/client đang bị khóa hoặc không hoạt động.",
            "CLIENT_INACTIVE" => "Client không hoạt động.",
            "CLIENT_NOT_FOUND" => "Không tìm thấy client.",
            "NOTFOUND_CLIENT" => "Không tìm thấy doanh nghiệp/client.",
            "NOT_VERIFIED_USER" => "API key không hợp lệ hoặc không thuộc doanh nghiệp.",
            "NOTFOUND_PRODUCT" => "Không tìm thấy sản phẩm trong phạm vi doanh nghiệp.",
            "NOTFOUND_SERIAL" => "Không tìm thấy dữ liệu mã TXNG.",
            "NOTFOUND_BATCH_LOT" => "Không tìm thấy lệnh sản xuất.",
            "INVALID_BATCH_LOT_STATUS" => "Trạng thái lệnh sản xuất không hợp lệ.",
            "NO_MATCHED_SERIALS" => "Kích hoạt danh sách mã thất bại.",
            "OUT_OF_QUOTA" => "Đã hết hạn mức tạo mã.",
            "OUT_OF_PRODUCT" => "Doanh nghiệp đã hết hạn mức.",
            "COUNTRY_GATE_FAILED" => "Cổng quốc gia trả dữ liệu không hợp lệ hoặc tích hợp thất bại.",
            "FAILED_SYNC_TO_COUNTRY_GATE" => "Đồng bộ dữ liệu lên cổng quốc gia thất bại.",
            _ => null,
        };
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_ownsHttp)
        {
            _http.Dispose();
        }
        _tokenLock.Dispose();
    }
}
