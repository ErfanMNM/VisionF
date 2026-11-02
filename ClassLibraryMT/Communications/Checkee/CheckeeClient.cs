using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ClassLibraryMT.Communications.Checkee;

// Client gọi API Checkee: lấy token (có cache) và kích hoạt serial.
public sealed class CheckeeClient
{
    // Ten HttpClient dang ky trong DI container.
    public const string HttpClientName = "Checkee";

    private readonly HttpClient _http;
    private readonly CheckeeOptions _opt;
    private readonly ILogger<CheckeeClient> _logger;
    private readonly SemaphoreSlim _tokenLock = new(1, 1);
    private string? _token;

    public CheckeeClient(
        HttpClient http,
        IOptions<CheckeeOptions> options,
        ILogger<CheckeeClient> logger)
    {
        _http = http;
        _opt = options.Value;
        _logger = logger;
    }

    private async Task<string> GetTokenAsync(bool forceRefresh, CancellationToken ct)
    {
        await _tokenLock.WaitAsync(ct);
        try
        {
            if (!forceRefresh && _token is not null) return _token;

            using var req = new HttpRequestMessage(HttpMethod.Get, "onboarding/token");
            req.Headers.Add("x-client-id", _opt.ClientId);
            req.Headers.Add("x-api-key", _opt.ApiKey);

            _logger.LogInformation("Gọi lấy token: {Url}", new Uri(_http.BaseAddress!, "onboarding/token"));

            using var res = await _http.SendAsync(req, ct);
            var body = await res.Content.ReadAsStringAsync(ct);

            ApiResponse<TokenData>? parsed = null;
            if (body.TrimStart().StartsWith('{'))
            {
                try { parsed = JsonSerializer.Deserialize(body, CheckeeJsonContext.Default.ApiResponseTokenData); } catch { }
            }

            if (parsed is not { Status: true, Data: not null })
            {
                var snippet = body.Length > 300 ? body[..300] : body;
                throw new InvalidOperationException(
                    $"Lấy token thất bại. HTTP {(int)res.StatusCode}. " +
                    $"Message: {parsed?.Message ?? "(không phải JSON)"}. Nội dung: {snippet}");
            }

            _token = parsed.Data.Token;
            _logger.LogInformation("Đã lấy access token cho: {Name}", parsed.Data.Name);
            return _token;
        }
        finally
        {
            _tokenLock.Release();
        }
    }

    public async Task<ActivateResult> ActivateAsync(string gtin, string serialNumber, CancellationToken ct)
    {
        var payload = new ActiveRequest
        {
            Gtin = gtin,
            BatchCode = _opt.BatchCode,
            Type = _opt.SerialType,
            SerialNumbers = [serialNumber]
        };
        var json = JsonSerializer.Serialize(payload, CheckeeJsonContext.Default.ActiveRequest);

        // Thử tối đa 2 lần: lần 2 sau khi làm mới token nếu bị 401
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var token = await GetTokenAsync(forceRefresh: attempt > 0, ct);

            using var req = new HttpRequestMessage(HttpMethod.Post, "middleware-api/CTEs/active")
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
            req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {token}");
            req.Headers.Add("x-api-key", _opt.ApiKey);

            using var res = await _http.SendAsync(req, ct);
            var body = await res.Content.ReadAsStringAsync(ct);

            if (res.StatusCode == HttpStatusCode.Unauthorized && attempt == 0)
            {
                _logger.LogWarning("Token hết hạn/không hợp lệ, đang làm mới...");
                continue;
            }

            ApiResponse<ActiveData>? parsed = null;
            try { parsed = JsonSerializer.Deserialize(body, CheckeeJsonContext.Default.ApiResponseActiveData); } catch { /* body không phải JSON */ }

            if (res.IsSuccessStatusCode && parsed is { Status: true })
                return new ActivateResult(true, parsed.Message ?? "OK", parsed.Data);

            return new ActivateResult(false, parsed?.Message ?? $"HTTP {(int)res.StatusCode}: {body}", null);
        }

        return new ActivateResult(false, "Xác thực thất bại sau khi làm mới token", null);
    }
}
