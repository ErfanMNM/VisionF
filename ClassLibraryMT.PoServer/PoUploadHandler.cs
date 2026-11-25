using System.Globalization;
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace ClassLibraryMT.PoServer;

/// <summary>Metadata đi riêng và nội dung CodeContent đọc từ một file tải lên.</summary>
public sealed class PoPushContent
{
    public PoPushRequest Metadata { get; init; } = new();
    public List<string> RawCodes { get; init; } = [];
    public string? SourceFileName { get; init; }
    public string Source { get; init; } = "file";
}

/// <summary>
/// Chỉ nhận file CSV/JSON/XLSX có bảng một cột CodeContent. Metadata đi qua
/// query string hoặc các field multipart; không nhận envelope codes/csv.
/// </summary>
public sealed class PoUploadHandler
{
    private readonly PoServerOptions _opt;
    private readonly PoUploadStore _uploadStore;
    private readonly ILogger _logger;

    public PoUploadHandler(PoServerOptions options, PoUploadStore uploadStore, ILogger logger)
    {
        _opt = options ?? throw new ArgumentNullException(nameof(options));
        _uploadStore = uploadStore ?? throw new ArgumentNullException(nameof(uploadStore));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<PoContentResult> ReadContentAsync(HttpListenerRequest request, CancellationToken ct)
    {
        try
        {
            if (request.ContentLength64 > _opt.MaxBodyBytes)
                return PoContentResult.Fail(413, BodyTooLargeMessage());

            var mediaType = (request.ContentType ?? string.Empty).Split(';')[0].Trim();
            if (mediaType.Equals("multipart/form-data", StringComparison.OrdinalIgnoreCase))
                return await ReadMultipartAsync(request, ct).ConfigureAwait(false);

            var fileName = GetUploadFileName(request);
            if (string.IsNullOrWhiteSpace(fileName))
            {
                fileName = mediaType.ToLowerInvariant() switch
                {
                    "text/csv" or "application/csv" => "codes.csv",
                    "application/json" => "codes.json",
                    "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet" => "codes.xlsx",
                    _ => null,
                };
            }
            if (fileName is null)
                return PoContentResult.Fail(415, "Cần tải một file .csv, .json hoặc .xlsx chứa duy nhất cột CodeContent.");

            return await ReadRawFileUploadAsync(request, fileName, ct).ConfigureAwait(false);
        }
        catch (PoFileFormatException ex) { return PoContentResult.Fail(400, ex.Message); }
        catch (JsonException ex) { return PoContentResult.Fail(400, "File JSON không hợp lệ: " + ex.Message); }
        catch (BodyTooLargeException) { return PoContentResult.Fail(413, BodyTooLargeMessage()); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        { return PoContentResult.Fail(408, "Yêu cầu bị hủy do quá thời gian chờ."); }
        catch (IOException ex)
        {
            _logger.LogWarning(ex, "Không nhận được file PO hoàn chỉnh.");
            return PoContentResult.Fail(400, "Không nhận được file PO hoàn chỉnh; PO chưa được lưu.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Lỗi khi nhận file PO.");
            return PoContentResult.Fail(500, "Lỗi khi đọc file PO: " + ex.Message);
        }
    }

    private async Task<PoContentResult> ReadRawFileUploadAsync(
        HttpListenerRequest request, string rawFileName, CancellationToken ct)
    {
        var (fileName, kind) = ValidateFileName(rawFileName);
        var metadata = BuildMetadataFromQuery(request, out _);
        var maxBytes = Math.Min(_opt.MaxFileBytes, _opt.MaxBodyBytes);
        if (request.ContentLength64 > maxBytes)
            return PoContentResult.Fail(413, BodyTooLargeMessage());

        var tempPath = _uploadStore.CreateTempPath(Path.GetExtension(fileName));
        try
        {
            await using (var output = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 64 * 1024, useAsync: true))
            {
                var written = await CopyStreamAsync(request.InputStream, output, maxBytes, ct).ConfigureAwait(false);
                if (written == 0) return PoContentResult.Fail(400, "File tải lên rỗng.");
                if (request.ContentLength64 >= 0 && request.ContentLength64 != written)
                    return PoContentResult.Fail(400, "File tải lên chưa đầy đủ; PO chưa được lưu.");
            }
            return await ParseContentAsync(metadata, fileName, kind, tempPath, ct).ConfigureAwait(false);
        }
        finally { TryDeleteTempFile(tempPath); }
    }

    private async Task<PoContentResult> ReadMultipartAsync(HttpListenerRequest request, CancellationToken ct)
    {
        var boundary = PoMultipartHeader.GetBoundary(request.ContentType);
        if (string.IsNullOrWhiteSpace(boundary))
            return PoContentResult.Fail(400, "multipart/form-data thiếu boundary.");
        var metadata = BuildMetadataFromQuery(request, out var fields);
        var multipartTemp = _uploadStore.CreateTempPath(".multipart");
        string? fileTemp = null;
        string? fileName = null;
        var kind = PoUploadFileKind.Unknown;
        try
        {
            await using (var output = new FileStream(multipartTemp, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 64 * 1024, useAsync: true))
            {
                var written = await CopyStreamAsync(request.InputStream, output, _opt.MaxBodyBytes, ct).ConfigureAwait(false);
                if (request.ContentLength64 >= 0 && request.ContentLength64 != written)
                    return PoContentResult.Fail(400, "Nội dung multipart chưa đầy đủ; PO chưa được lưu.");
            }
            await using var input = new FileStream(multipartTemp, FileMode.Open, FileAccess.Read,
                FileShare.Read, 64 * 1024, useAsync: true);
            await foreach (var part in PoMultipartReader.EnumeratePartsAsync(input, boundary, ct).ConfigureAwait(false))
            {
                var name = PoMultipartHeader.GetDispositionParameter(part.HeaderText, "name");
                var uploadedName = PoMultipartHeader.GetDispositionParameter(part.HeaderText, "filename");
                if (uploadedName is not null)
                {
                    if (fileTemp is not null)
                        return PoContentResult.Fail(400, "Mỗi yêu cầu chỉ được chứa một file CodeContent.");
                    (fileName, kind) = ValidateFileName(uploadedName);
                    fileTemp = _uploadStore.CreateTempPath(Path.GetExtension(fileName));
                    await using var output = new FileStream(fileTemp, FileMode.CreateNew, FileAccess.Write,
                        FileShare.None, 64 * 1024, useAsync: true);
                    await PoMultipartPartReader.CopyContentToAsync(input, part, output, _opt.MaxFileBytes, ct).ConfigureAwait(false);
                }
                else
                {
                    var field = CanonicalMetadataField(name);
                    if (!fields.Add(field))
                        return PoContentResult.Fail(400, $"Metadata '{field}' xuất hiện nhiều lần.");
                    // Metadata ngắn; không cho phép field thay thế file hoặc chiếm RAM không giới hạn.
                    if (part.ContentLength > 64 * 1024)
                        return PoContentResult.Fail(400, $"Metadata '{field}' quá dài.");
                    var value = await PoMultipartPartReader.ReadContentAsTextAsync(input, part, ct).ConfigureAwait(false);
                    ApplyMetadataField(metadata, field, value);
                }
            }
            if (fileTemp is null || fileName is null)
                return PoContentResult.Fail(400, "multipart/form-data phải chứa đúng một file .csv, .json hoặc .xlsx.");
            return await ParseContentAsync(metadata, fileName, kind, fileTemp, ct).ConfigureAwait(false);
        }
        finally
        {
            if (fileTemp is not null) TryDeleteTempFile(fileTemp);
            TryDeleteTempFile(multipartTemp);
        }
    }

    private async Task<PoContentResult> ParseContentAsync(PoPushRequest metadata, string fileName,
        PoUploadFileKind kind, string tempPath, CancellationToken ct)
    {
        var codes = await ParseFileAsync(tempPath, kind, ct).ConfigureAwait(false);
        if (codes.Count == 0)
            return PoContentResult.Fail(400, "File CodeContent không có dòng dữ liệu.");
        _logger.LogInformation("Nhận file {FileName}: {Count} dòng CodeContent.", fileName, codes.Count);
        return PoContentResult.Ok(new PoPushContent
        {
            Metadata = metadata, RawCodes = codes.ToList(), SourceFileName = fileName, Source = "file " + fileName,
        });
    }

    private static (string FileName, PoUploadFileKind Kind) ValidateFileName(string rawFileName)
    {
        if (!PoUploadStore.IsSaneFileName(rawFileName, out var error))
            throw new PoFileFormatException("Tên file không hợp lệ: " + error);
        var fileName = PoUploadStore.SanitizeFileName(rawFileName);
        var kind = PoUploadStore.DetectKind(fileName);
        if (kind is not (PoUploadFileKind.Csv or PoUploadFileKind.Json or PoUploadFileKind.Xlsx))
            throw new PoFileFormatException("Chỉ chấp nhận file .csv, .json hoặc .xlsx chứa duy nhất cột CodeContent; .xls cũ không được hỗ trợ.");
        return (fileName, kind);
    }

    public void CleanupStaleFiles() => _uploadStore.CleanupStaleFiles();

    public static async Task<IReadOnlyList<string>> ParseFileAsync(string path, PoUploadFileKind kind, CancellationToken ct)
        => kind switch
        {
            PoUploadFileKind.Csv => await PoCodeFileParser.ReadCsvAsync(path, ct).ConfigureAwait(false),
            PoUploadFileKind.Json => await PoCodeFileParser.ReadJsonAsync(path, ct).ConfigureAwait(false),
            PoUploadFileKind.Xlsx => await PoCodeFileParser.ReadXlsxAsync(path, ct).ConfigureAwait(false),
            _ => throw new PoFileFormatException("Chỉ hỗ trợ bảng CodeContent trong .csv, .json hoặc .xlsx."),
        };

    private static PoPushRequest BuildMetadataFromQuery(HttpListenerRequest request, out HashSet<string> fields)
    {
        var metadata = new PoPushRequest();
        fields = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in request.QueryString.AllKeys)
        {
            var field = CanonicalMetadataField(name);
            var values = request.QueryString.GetValues(name);
            if (values is not { Length: 1 } || !fields.Add(field))
                throw new PoFileFormatException($"Metadata '{field}' xuất hiện nhiều lần.");
            ApplyMetadataField(metadata, field, values[0]);
        }
        return metadata;
    }

    private static string CanonicalMetadataField(string? name) => (name ?? string.Empty).Trim().ToLowerInvariant() switch
    {
        "poname" or "po" => "poName",
        "orderqty" or "qty" => "orderQty",
        "batchcode" or "batch" => "batchCode",
        "productname" or "product" => "productName",
        "customer" => "customer",
        _ => throw new PoFileFormatException($"Metadata '{name}' không được hỗ trợ. Mã chỉ được cung cấp trong file một cột CodeContent; không nhận GTIN/serial/codes/csv."),
    };

    private static void ApplyMetadataField(PoPushRequest metadata, string field, string value)
    {
        switch (field)
        {
            case "poName": metadata.PoName = value; break;
            case "orderQty":
                if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var qty) || qty <= 0)
                    throw new PoFileFormatException("orderQty phải là số nguyên lớn hơn 0.");
                metadata.OrderQty = qty;
                break;
            case "batchCode": metadata.BatchCode = value; break;
            case "productName": metadata.ProductName = value; break;
            case "customer": metadata.Customer = value; break;
        }
    }

    private static string? GetUploadFileName(HttpListenerRequest request)
    {
        var name = PoMultipartHeader.GetDispositionParameter(request.Headers["Content-Disposition"], "filename");
        return name ?? request.Headers["X-Filename"];
    }

    private static async Task<long> CopyStreamAsync(Stream input, Stream output, int maxBytes, CancellationToken ct)
    {
        var buffer = new byte[81920];
        long total = 0;
        while (true)
        {
            var read = await input.ReadAsync(buffer, ct).ConfigureAwait(false);
            if (read == 0) break;
            total += read;
            if (total > maxBytes) throw new BodyTooLargeException();
            await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
        }
        await output.FlushAsync(ct).ConfigureAwait(false);
        return total;
    }

    private static void TryDeleteTempFile(string path)
    {
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private string BodyTooLargeMessage() =>
        $"Dữ liệu vượt quá giới hạn body/file ({_opt.MaxBodyMB}/{_opt.MaxFileMB} MB).";
}

public sealed class PoContentResult
{
    private PoContentResult(bool ok, PoPushContent? content, int statusCode, string message)
    { IsSuccess = ok; Content = content; StatusCode = statusCode; Message = message; }
    public bool IsSuccess { get; }
    public PoPushContent? Content { get; }
    public int StatusCode { get; }
    public string Message { get; }
    public static PoContentResult Ok(PoPushContent content) => new(true, content, 200, string.Empty);
    public static PoContentResult Fail(int statusCode, string message) => new(false, null, statusCode, message);
}
