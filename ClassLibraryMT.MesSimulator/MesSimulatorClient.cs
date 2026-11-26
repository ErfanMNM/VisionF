using System.Globalization;
using System.Text;
using ClassLibraryMT.Po;
using ClassLibraryMT.PoServer;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ClassLibraryMT.MesSimulator;

/// <summary>Gửi file một cột CodeContent tới listener nhận PO.</summary>
public sealed class MesSimulatorClient
{
    private readonly HttpClient _http;
    private readonly MesSimulatorOptions _options;
    private readonly ILogger<MesSimulatorClient> _logger;

    public MesSimulatorClient(HttpClient http, IOptions<MesSimulatorOptions> options,
        ILogger<MesSimulatorClient> logger)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>Gửi những CodeContent được cung cấp rõ ràng; không tự sinh hoặc phân tích mã.</summary>
    public async Task<MesSimulatorResult> UploadTestOrderAsync(
        string poName, IReadOnlyList<string> codeContents, CancellationToken ct = default, string? gtin = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(poName);
        ArgumentNullException.ThrowIfNull(codeContents);
        if (codeContents.Count == 0 || codeContents.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("PO test phải có các CodeContent không rỗng.", nameof(codeContents));

        var csv = new StringBuilder("CodeContent\r\n");
        foreach (var code in codeContents)
        {
            // Quote mỗi ô để giữ chính xác nội dung raw khi truyền CSV.
            csv.Append('"').Append(code.Replace("\"", "\"\"", StringComparison.Ordinal)).Append("\"\r\n");
        }

        using var form = CreateForm(poName, codeContents.Count, ResolveGtin(gtin));
        form.Add(new StringContent(csv.ToString(), Encoding.UTF8, "text/csv"), "file", "codes.csv");
        return await SendMultipartAsync(poName, form, ct).ConfigureAwait(false);
    }

    /// <summary>Kiểm tra rồi gửi nguyên file CodeContent CSV/JSON/XLSX.</summary>
    public async Task<MesSimulatorResult> UploadExistingFileAsync(
        string poName, int orderQty, string filePath, CancellationToken ct = default, string? gtin = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(poName);
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        if (orderQty <= 0) throw new ArgumentOutOfRangeException(nameof(orderQty));
        var normalizedGtin = ResolveGtin(gtin);
        var fullPath = Path.GetFullPath(filePath);
        var fileName = Path.GetFileName(fullPath);
        var kind = PoUploadStore.DetectKind(fileName);
        var contentType = kind switch
        {
            PoUploadFileKind.Csv => "text/csv",
            PoUploadFileKind.Json => "application/json",
            PoUploadFileKind.Xlsx => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            _ => throw new ArgumentException("Chỉ hỗ trợ file CodeContent .csv, .json hoặc .xlsx.", nameof(filePath)),
        };

        // Giữ handle chỉ đọc xuyên suốt kiểm tra và gửi HTTP để file không bị thay đổi.
        await using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read,
            81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var codes = await PoUploadHandler.ParseFileAsync(fullPath, kind, ct).ConfigureAwait(false);
        if (codes.Count == 0 || codes.Count != orderQty)
            throw new ArgumentException($"orderQty ({orderQty}) phải bằng số record CodeContent ({codes.Count}) trước lọc mã.", nameof(orderQty));

        using var form = CreateForm(poName, orderQty, normalizedGtin);
        var file = new StreamContent(stream);
        file.Headers.ContentType = new(contentType);
        form.Add(file, "file", fileName);
        return await SendMultipartAsync(poName, form, ct).ConfigureAwait(false);
    }

    private string ResolveGtin(string? gtin)
    {
        var value = gtin ?? _options.Gtin;
        if (!PoCodeParser.TryNormalizeGtin(value, out var normalized))
            throw new ArgumentException("Cần đúng một GTIN 8/12/13/14 chữ số có checksum hợp lệ qua tham số gtin hoặc MesSimulator:Gtin.", nameof(gtin));
        return normalized;
    }

    private static MultipartFormDataContent CreateForm(string poName, int orderQty, string gtin)
    {
        var form = new MultipartFormDataContent();
        form.Add(new StringContent(poName, Encoding.UTF8), "orderNo");
        form.Add(new StringContent(orderQty.ToString(CultureInfo.InvariantCulture)), "orderQty");
        form.Add(new StringContent(gtin), "gtin");
        form.Add(new StringContent("MES simulator", Encoding.UTF8), "productName");
        return form;
    }

    private async Task<MesSimulatorResult> SendMultipartAsync(
        string poName, MultipartFormDataContent form, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, _options.ResolveEndpoint());
        if (!string.IsNullOrWhiteSpace(_options.ApiKey))
            request.Headers.TryAddWithoutValidation("x-api-key", _options.ApiKey.Trim());
        request.Content = form;
        try
        {
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseContentRead, ct)
                .ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return new MesSimulatorResult(poName, response.IsSuccessStatusCode, (int)response.StatusCode, body);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new MesSimulatorResult(poName, false, 0, "Request timeout.");
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "MES simulator không kết nối được tới {Endpoint}.", _options.ResolveEndpoint());
            return new MesSimulatorResult(poName, false, 0, ex.Message);
        }
    }
}

/// <summary>Kết quả một lần upload PO test.</summary>
public readonly record struct MesSimulatorResult(string PoName, bool Success, int StatusCode, string ResponseBody);
