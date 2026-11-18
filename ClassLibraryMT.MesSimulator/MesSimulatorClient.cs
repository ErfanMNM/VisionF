using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ClassLibraryMT.MesSimulator;

/// <summary>Client mô phỏng MES gửi PO dạng multipart CSV.</summary>
public sealed class MesSimulatorClient
{
    private readonly HttpClient _http;
    private readonly MesSimulatorOptions _options;
    private readonly ILogger<MesSimulatorClient> _logger;

    public MesSimulatorClient(
        HttpClient http,
        IOptions<MesSimulatorOptions> options,
        ILogger<MesSimulatorClient> logger)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>Gửi một PO test và trả về HTTP status cùng nội dung phản hồi.</summary>
    public async Task<MesSimulatorResult> UploadTestOrderAsync(
        string poName,
        IReadOnlyList<string> serials,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(poName);
        ArgumentNullException.ThrowIfNull(serials);
        if (serials.Count == 0)
            throw new ArgumentException("PO test phải có ít nhất một mã.", nameof(serials));

        var csv = new StringBuilder();
        csv.AppendLine("serial");
        foreach (var serial in serials)
            csv.AppendLine(serial);

        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(poName, Encoding.UTF8), "poName");
        form.Add(new StringContent(serials.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)), "orderQty");
        form.Add(new StringContent(_options.Gtin ?? string.Empty), "gtin");
        form.Add(new StringContent("MES simulator"), "productName");

        var file = new StringContent(csv.ToString(), Encoding.UTF8, "text/csv");
        form.Add(file, "file", poName + ".csv");
        return await SendMultipartAsync(poName, form, ct).ConfigureAwait(false);
    }

    /// <summary>Upload file PO có sẵn dạng CSV, JSON hoặc Excel.</summary>
    public async Task<MesSimulatorResult> UploadExistingFileAsync(
        string poName,
        int orderQty,
        string filePath,
        string? gtin = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(poName);
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        if (orderQty <= 0)
            throw new ArgumentOutOfRangeException(nameof(orderQty), "Số lượng PO phải lớn hơn 0.");

        var fullPath = Path.GetFullPath(filePath);
        var fileName = Path.GetFileName(fullPath);
        var contentType = Path.GetExtension(fileName).ToLowerInvariant() switch
        {
            ".csv" => "text/csv",
            ".json" => "application/json",
            ".xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            ".xls" => "application/vnd.ms-excel",
            _ => throw new ArgumentException(
                "Chỉ hỗ trợ file .csv, .json, .xlsx hoặc .xls.", nameof(filePath)),
        };

        await using var stream = new FileStream(
            fullPath, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);

        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(poName, Encoding.UTF8), "poName");
        form.Add(new StringContent(orderQty.ToString(System.Globalization.CultureInfo.InvariantCulture)), "orderQty");
        form.Add(new StringContent(gtin ?? string.Empty, Encoding.UTF8), "gtin");
        form.Add(new StringContent("MES simulator custom file", Encoding.UTF8), "productName");

        var file = new StreamContent(stream);
        file.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);
        form.Add(file, "file", fileName);
        return await SendMultipartAsync(poName, form, ct).ConfigureAwait(false);
    }

    private async Task<MesSimulatorResult> SendMultipartAsync(
        string poName,
        MultipartFormDataContent form,
        CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, _options.ResolveEndpoint());
        if (!string.IsNullOrWhiteSpace(_options.ApiKey))
            request.Headers.TryAddWithoutValidation("x-api-key", _options.ApiKey.Trim());
        request.Content = form;

        try
        {
            using var response = await _http.SendAsync(
                request,
                HttpCompletionOption.ResponseContentRead,
                ct).ConfigureAwait(false);
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
public readonly record struct MesSimulatorResult(
    string PoName,
    bool Success,
    int StatusCode,
    string ResponseBody);
