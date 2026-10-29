using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using ClassLibraryMT.Communications.CodeReaderProtocol;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace QRDemoCodeBackend;

internal class Program
{
    private static async Task Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        Console.InputEncoding = Encoding.UTF8;

        var builder = Host.CreateApplicationBuilder(args);

        builder.Services.Configure<CheckeeOptions>(builder.Configuration.GetSection("Checkee"));
        builder.Services.AddHttpClient<CheckeeClient>();

        builder.Services.Configure<CameraOptions>(builder.Configuration.GetSection("Camera"));
        builder.Services.AddSingleton(sp =>
        {
            var cam = sp.GetRequiredService<IOptions<CameraOptions>>().Value;
            return new Reader(cam.Ip, cam.Port);
        });
        builder.Services.AddHostedService<CameraRDWorker>();

        using var host = builder.Build();
        await host.RunAsync();
    }
}

// ---------------------------------------------------------------- Cấu hình
internal sealed class CameraOptions
{
    public string Ip { get; set; } = "127.0.0.1"; // IP đầu đọc
    public int Port { get; set; } = 9000;         // Port đầu đọc
}

internal sealed class CheckeeOptions
{
    public string BaseUrl { get; set; } = "https://beta-api-eco.checkee.vn"; // UAT; Production: https://api-eco.checkee.vn
    public string ClientId { get; set; } = "";
    public string ApiKey { get; set; } = "";
    public string BatchCode { get; set; } = "";   // Mã lô/lệnh sản xuất đang chạy
    public string SerialType { get; set; } = "RANDOM"; // RANDOM | DECLARE | INCREASING
    public int DuplicateWindowSeconds { get; set; } = 30; // Bỏ qua mã quét lặp trong khoảng này
}

// ---------------------------------------------------------------- Model
internal sealed class ApiResponse<T>
{
    [JsonPropertyName("code")] public int Code { get; set; }
    [JsonPropertyName("status")] public bool Status { get; set; }
    [JsonPropertyName("message")] public string? Message { get; set; }
    [JsonPropertyName("data")] public T? Data { get; set; }
}

internal sealed class TokenData
{
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("token")] public string Token { get; set; } = ""; // "accessToken=<JWT>"
}

internal sealed class ActiveData
{
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("GTIN")] public string? Gtin { get; set; }
    [JsonPropertyName("batch")] public string? Batch { get; set; }
    [JsonPropertyName("quantityInBatch")] public int QuantityInBatch { get; set; }
    [JsonPropertyName("activeSerials")] public int ActiveSerials { get; set; }
}

internal sealed class ActiveRequest
{
    [JsonPropertyName("GTIN")] public string Gtin { get; set; } = "";
    [JsonPropertyName("batchCode")] public string BatchCode { get; set; } = "";
    [JsonPropertyName("type")] public string Type { get; set; } = "";
    [JsonPropertyName("serialNumbers")] public string[] SerialNumbers { get; set; } = [];
}

internal sealed record ActivateResult(bool Success, string Message, ActiveData? Data);

// ---------------------------------------------------------------- Checkee client
internal sealed class CheckeeClient
{
    private readonly HttpClient _http;
    private readonly CheckeeOptions _opt;
    private readonly ILogger<CheckeeClient> _logger;
    private readonly SemaphoreSlim _tokenLock = new(1, 1);
    private string? _token;

    public CheckeeClient(HttpClient http, IOptions<CheckeeOptions> opt, ILogger<CheckeeClient> logger)
    {
        _http = http;
        _opt = opt.Value;
        _logger = logger;
        _http.BaseAddress = new Uri(_opt.BaseUrl.TrimEnd('/') + "/");
        _http.Timeout = TimeSpan.FromSeconds(20);
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
                try { parsed = JsonSerializer.Deserialize<ApiResponse<TokenData>>(body); } catch { }
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
        var json = JsonSerializer.Serialize(payload);

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
            try { parsed = JsonSerializer.Deserialize<ApiResponse<ActiveData>>(body); } catch { /* body không phải JSON */ }

            if (res.IsSuccessStatusCode && parsed is { Status: true })
                return new ActivateResult(true, parsed.Message ?? "OK", parsed.Data);

            return new ActivateResult(false, parsed?.Message ?? $"HTTP {(int)res.StatusCode}: {body}", null);
        }

        return new ActivateResult(false, "Xác thực thất bại sau khi làm mới token", null);
    }
}

// ---------------------------------------------------------------- Parser nội dung QR
internal static class QrParser
{
    // Link TXNG: https://.../01/{gtin}/21/{serialNumber}
    private static readonly Regex DigitalLink =
        new(@"/01/(?<gtin>\d{8,14})/21/(?<serial>[^/?#\s]+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // GS1 dạng có ngoặc: (01)08934567890123(21)ABC123
    private static readonly Regex Gs1Paren =
        new(@"\(01\)(?<gtin>\d{8,14})\(21\)(?<serial>[^\(\s]+)", RegexOptions.Compiled);

    public static bool TryParse(string raw, out string gtin, out string serial)
    {
        gtin = serial = "";
        if (string.IsNullOrWhiteSpace(raw)) return false;

        var text = raw.Trim();
        var m = DigitalLink.Match(text);
        if (!m.Success) m = Gs1Paren.Match(text);
        if (!m.Success) return false;

        gtin = m.Groups["gtin"].Value;
        serial = Uri.UnescapeDataString(m.Groups["serial"].Value);
        return true;
    }
}

// ---------------------------------------------------------------- Worker
internal sealed class CameraRDWorker : BackgroundService
{
    private readonly Reader _camera;
    private readonly CheckeeClient _checkee;
    private readonly CheckeeOptions _opt;
    private readonly ILogger<CameraRDWorker> _logger;

    // Hàng đợi để callback của camera không bị chặn bởi HTTP
    private readonly Channel<string> _queue = Channel.CreateUnbounded<string>(
        new UnboundedChannelOptions { SingleReader = true });

    private readonly Dictionary<string, DateTime> _recent = new();

    public CameraRDWorker(Reader camera, CheckeeClient checkee, IOptions<CheckeeOptions> opt,
        ILogger<CameraRDWorker> logger)
    {
        _camera = camera;
        _checkee = checkee;
        _opt = opt.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (string.IsNullOrWhiteSpace(_opt.ClientId) || string.IsNullOrWhiteSpace(_opt.ApiKey) ||
            string.IsNullOrWhiteSpace(_opt.BatchCode))
        {
            _logger.LogError("Thiếu cấu hình Checkee (ClientId / ApiKey / BatchCode) trong appsettings.json.");
            return;
        }

        _camera.ClientCallback += OnCameraEvent;
        try
        {
            await Task.WhenAll(_camera.RunAsync(stoppingToken), ProcessQueueAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            _camera.ClientCallback -= OnCameraEvent;
            _queue.Writer.TryComplete();
            _logger.LogInformation("Camera RD worker đã dừng.");
        }
    }

    private async Task ProcessQueueAsync(CancellationToken ct)
    {
        await foreach (var raw in _queue.Reader.ReadAllAsync(ct))
        {
            try
            {
                if (!QrParser.TryParse(raw, out var gtin, out var serial))
                {
                    _logger.LogWarning("QR không đúng định dạng TXNG, bỏ qua: {Data}", raw);
                    continue;
                }

                if (IsDuplicate($"{gtin}|{serial}"))
                {
                    _logger.LogInformation("Mã vừa quét gần đây, bỏ qua: {Serial}", serial);
                    continue;
                }

                var result = await _checkee.ActivateAsync(gtin, serial, ct);
                if (result.Success)
                    _logger.LogInformation(
                        "Kích hoạt OK: GTIN={Gtin} Serial={Serial} | Lô {Batch}: {Active}/{Total}",
                        gtin, serial, result.Data?.Batch, result.Data?.ActiveSerials, result.Data?.QuantityInBatch);
                else
                    _logger.LogError("Kích hoạt THẤT BẠI: GTIN={Gtin} Serial={Serial} | {Message}",
                        gtin, serial, result.Message);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi gửi kích hoạt mã: {Data}", raw);
            }
        }
    }

    private bool IsDuplicate(string key)
    {
        var now = DateTime.UtcNow;
        var window = TimeSpan.FromSeconds(_opt.DuplicateWindowSeconds);

        foreach (var k in _recent.Where(kv => now - kv.Value > window).Select(kv => kv.Key).ToList())
            _recent.Remove(k);

        if (_recent.ContainsKey(key)) return true;
        _recent[key] = now;
        return false;
    }

    private void OnCameraEvent(eReaderState state, string data)
    {
        switch (state)
        {
            case eReaderState.Connected:
                _logger.LogInformation("Camera RD đã kết nối: {Message}", data);
                break;
            case eReaderState.Disconnected:
                _logger.LogWarning("Camera RD mất kết nối: {Message}", data);
                break;
            case eReaderState.Reconnecting:
                _logger.LogInformation("Camera RD đang kết nối lại: {Message}", data);
                break;
            case eReaderState.Received:
                _logger.LogInformation("Camera RD nhận QR: {Data}", data);
                _queue.Writer.TryWrite(data);
                break;
        }
    }
}