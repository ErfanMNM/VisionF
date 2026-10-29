using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using ClassLibraryMT.Communications.CodeReaderProtocol;
using ClassLibraryMT.Licensing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace QRDemoCodeBackend;

internal class Program
{
    private static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        Console.InputEncoding = Encoding.UTF8;

        if (args.Any(a => a.Equals("--show-hwid", StringComparison.OrdinalIgnoreCase)))
        {
            Console.WriteLine("HWID (gui ma nay cho nha cung cap de lay file license.lic):");
            Console.WriteLine("  " + HardwareId.GetGrouped());
            return 0;
        }

        // ---- Check license truoc khi chay service ----
        var licPath = LicenseArg(args) ?? LicenseValidator.DefaultLicensePath;
        var lic = LicenseValidator.Validate(licPath);
        if (!lic.Ok)
        {
            Console.WriteLine("GIAY PHEP KHONG HOP LE: " + lic.Message);
            Console.WriteLine("Ma may (HWID) cua may nay:");
            Console.WriteLine("  " + HardwareId.GetGrouped());
            Console.WriteLine("Chep file license.lic vao thu muc chua file .exe roi chay lai,");
            Console.WriteLine("hoac chay: DemoQR.exe --license <duong-dan-license.lic>");
            try
            {
                Console.Write("Nhan phim bat ky de thoat...");
                Console.ReadKey(true);
                Console.WriteLine();
            }
            catch { }
            return 3;
        }

        Console.WriteLine($"License OK (het han {lic.Payload!.ExpiresUtc:yyyy-MM-dd}, con {lic.DaysLeft} ngay).");
        if (lic.DaysLeft <= 30)
            Console.WriteLine("CANH BAO: license sap het han, lien he nha cung cap de gia han.");

        var builder = Host.CreateApplicationBuilder(args);

        // Doc cau hinh thu cong (tuong thich Native AOT, khong dung reflection binding).
        var cfg = builder.Configuration;
        var camera = new CameraOptions
        {
            Ip = cfg.GetValue<string>("Camera:Ip") ?? "127.0.0.1",
            Port = cfg.GetValue<int?>("Camera:Port") ?? 9000,
        };
        var checkee = new CheckeeOptions
        {
            BaseUrl = cfg.GetValue<string>("Checkee:BaseUrl") ?? "https://beta-api-eco.checkee.vn",
            ClientId = cfg.GetValue<string>("Checkee:ClientId") ?? "",
            ApiKey = cfg.GetValue<string>("Checkee:ApiKey") ?? "",
            BatchCode = cfg.GetValue<string>("Checkee:BatchCode") ?? "",
            SerialType = cfg.GetValue<string>("Checkee:SerialType") ?? "RANDOM",
            DuplicateWindowSeconds = cfg.GetValue<int?>("Checkee:DuplicateWindowSeconds") ?? 30,
        };

        builder.Services.AddSingleton(camera);
        builder.Services.AddSingleton(checkee);
        builder.Services.AddSingleton(sp =>
        {
            var cam = sp.GetRequiredService<CameraOptions>();
            return new Reader(cam.Ip, cam.Port);
        });
        builder.Services.AddSingleton(sp =>
        {
            var opt = sp.GetRequiredService<CheckeeOptions>();
            var log = sp.GetRequiredService<ILogger<CheckeeClient>>();
            var http = new HttpClient
            {
                BaseAddress = new Uri(opt.BaseUrl.TrimEnd('/') + "/"),
                Timeout = TimeSpan.FromSeconds(20),
            };
            return new CheckeeClient(http, opt, log);
        });
        builder.Services.AddSingleton<CameraRDWorker>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<CameraRDWorker>());

        using var host = builder.Build();
        await host.RunAsync();
        return 0;
    }

    private static string? LicenseArg(string[] args)
    {
        for (var i = 0; i < args.Length; i++)
            if (args[i].Equals("--license", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                return args[i + 1];
        return null;
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
    [System.Text.Json.Serialization.JsonPropertyName("code")] public int Code { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("status")] public bool Status { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("message")] public string? Message { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("data")] public T? Data { get; set; }
}

internal sealed class TokenData
{
    [System.Text.Json.Serialization.JsonPropertyName("name")] public string? Name { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("token")] public string Token { get; set; } = ""; // "accessToken=<JWT>"
}

internal sealed class ActiveData
{
    [System.Text.Json.Serialization.JsonPropertyName("name")] public string? Name { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("GTIN")] public string? Gtin { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("batch")] public string? Batch { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("quantityInBatch")] public int QuantityInBatch { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("activeSerials")] public int ActiveSerials { get; set; }
}

internal sealed class ActiveRequest
{
    [System.Text.Json.Serialization.JsonPropertyName("GTIN")] public string Gtin { get; set; } = "";
    [System.Text.Json.Serialization.JsonPropertyName("batchCode")] public string BatchCode { get; set; } = "";
    [System.Text.Json.Serialization.JsonPropertyName("type")] public string Type { get; set; } = "";
    [System.Text.Json.Serialization.JsonPropertyName("serialNumbers")] public string[] SerialNumbers { get; set; } = [];
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

    public CheckeeClient(HttpClient http, CheckeeOptions opt, ILogger<CheckeeClient> logger)
    {
        _http = http;
        _opt = opt;
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
                try { parsed = JsonSerializer.Deserialize(body, AppJsonContext.Default.ApiResponseTokenData); } catch { }
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

    public async Task<ActivateAsyncResult> ActivateAsync(string gtin, string serialNumber, CancellationToken ct)
    {
        var payload = new ActiveRequest
        {
            Gtin = gtin,
            BatchCode = _opt.BatchCode,
            Type = _opt.SerialType,
            SerialNumbers = [serialNumber]
        };
        var json = JsonSerializer.Serialize(payload, AppJsonContext.Default.ActiveRequest);

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
            try { parsed = JsonSerializer.Deserialize(body, AppJsonContext.Default.ApiResponseActiveData); } catch { /* body không phải JSON */ }

            if (res.IsSuccessStatusCode && parsed is { Status: true })
                return new ActivateAsyncResult(true, parsed.Message ?? "OK", parsed.Data);

            return new ActivateAsyncResult(false, parsed?.Message ?? $"HTTP {(int)res.StatusCode}: {body}", null);
        }

        return new ActivateAsyncResult(false, "Xác thực thất bại sau khi làm mới token", null);
    }
}

// Ten goc ActivateResult trung voi record ben duoi nen doi ten tai day de tranh nham lan.
internal sealed record ActivateAsyncResult(bool Success, string Message, ActiveData? Data);

// ---------------------------------------------------------------- Parser nội dung QR (source-generated regex, tuong thich AOT)
internal static partial class QrParser
{
    // Link TXNG: https://.../01/{gtin}/21/{serialNumber}
    [GeneratedRegex(@"/01/(?<gtin>\d{8,14})/21/(?<serial>[^/?#\s]+)", RegexOptions.IgnoreCase)]
    private static partial Regex DigitalLink();

    // GS1 dạng có ngoặc: (01)08934567890123(21)ABC123
    [GeneratedRegex(@"\(01\)(?<gtin>\d{8,14})\(21\)(?<serial>[^\(\s]+)")]
    private static partial Regex Gs1Paren();

    public static bool TryParse(string raw, out string gtin, out string serial)
    {
        gtin = serial = "";
        if (string.IsNullOrWhiteSpace(raw)) return false;

        var text = raw.Trim();
        var m = DigitalLink().Match(text);
        if (!m.Success) m = Gs1Paren().Match(text);
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

    public CameraRDWorker(Reader camera, CheckeeClient checkee, CheckeeOptions opt,
        ILogger<CameraRDWorker> logger)
    {
        _camera = camera;
        _checkee = checkee;
        _opt = opt;
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
