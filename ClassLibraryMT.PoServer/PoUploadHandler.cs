using System.Globalization;
using System.Net;
using System.Text;
using Microsoft.Extensions.Logging;

namespace ClassLibraryMT.PoServer;

/// <summary>
/// Kết quả đọc nội dung request <c>POST /api/po</c>: tập mã lấy từ file
/// tải lên (CSV / JSON / Excel) hoặc từ body JSON thuần.
/// </summary>
public sealed class PoPushContent
{
    /// <summary>Metadata của request (poName, orderQty, ...).</summary>
    public PoPushRequest Metadata { get; init; } = new();

    /// <summary>Danh sách mã thô đọc được (chưa de-dup, chưa kiểm tra trùng).</summary>
    public List<string> RawCodes { get; init; } = new();

    /// <summary>
    /// Tên file nguồn (nếu request là upload file) — dùng để log / thông báo.
    /// </summary>
    public string? SourceFileName { get; init; }

    /// <summary>
    /// Mô tả nguồn tập mã (VD "file codes.csv", "mã JSON") — dùng cho thông báo.
    /// </summary>
    public string Source { get; init; } = "codes";
}

/// <summary>
/// Xử lý nội dung request nhận PO, bao gồm <b>upload file thật</b> từ MES.
///
/// <para>
/// Ba cách MES gửi tập mã:
/// <list type="bullet">
/// <item>Body JSON thuần (<c>codes</c> / <c>csv</c>) — không có file.</item>
/// <item>Upload file thuần: <c>Content-Type: text/csv</c> hoặc
/// <c>application/json</c> kèm <c>filename</c> trong header, metadata đi kèm
/// qua query string.</item>
/// <item>Upload <c>multipart/form-data</c> với field file.</item>
/// </list>
/// </para>
///
/// <para>
/// <b>Yêu cầu kỹ thuật:</b>
/// <list type="bullet">
/// <item>Mọi thao tác async / không block luồng UI.</item>
/// <item>File được ghi tăng dần xuống file tạm (streaming) — không nạp trọn
/// file vào RAM.</item>
/// <item>Giới hạn cỡ file theo <see cref="PoServerOptions.MaxFileMB"/> và kiểm
/// tra phần mở rộng + tên file.</item>
/// <item>Không dùng reflection (AOT-safe).</item>
/// </list>
/// </para>
/// </summary>
public sealed class PoUploadHandler
{
    private readonly PoServerOptions _opt;
    private readonly PoUploadStore _uploadStore;
    private readonly ILogger _logger;

    /// <summary>Khởi tạo handler upload.</summary>
    /// <param name="options">Cấu hình server.</param>
    /// <param name="uploadStore">Kho file tạm.</param>
    /// <param name="logger">Logger.</param>
    public PoUploadHandler(
        PoServerOptions options,
        PoUploadStore uploadStore,
        ILogger logger)
    {
        _opt = options ?? throw new ArgumentNullException(nameof(options));
        _uploadStore = uploadStore ?? throw new ArgumentNullException(nameof(uploadStore));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Đọc nội dung request và trả về tập mã.
    /// </summary>
    /// <param name="request">Request HTTP.</param>
    /// <param name="ct">Token hủy.</param>
    /// <returns>Kết quả, hoặc lỗi (không ném exception).</returns>
    public async Task<PoContentResult> ReadContentAsync(
        HttpListenerRequest request,
        CancellationToken ct)
    {
        var contentType = request.ContentType ?? string.Empty;

        if (contentType.Contains("multipart/form-data", StringComparison.OrdinalIgnoreCase))
        {
            return await ReadMultipartAsync(request, ct).ConfigureAwait(false);
        }

        // ---- Upload file thuần (text/csv, application/json, ... kèm filename) ----
        var uploadedFileName = GetUploadFileName(request);
        if (!string.IsNullOrWhiteSpace(uploadedFileName))
        {
            return await ReadRawFileUploadAsync(request, uploadedFileName, ct).ConfigureAwait(false);
        }

        // ---- Upload file thuần KHÔNG kèm filename (tương thích ngược) ----
        // MES cũ gửi Content-Type: text/csv với metadata trong query string.
        // Trường hợp này coi như file CSV với tên file mặc định.
        if (IsCsvContentType(contentType))
        {
            return await ReadRawFileUploadAsync(
                request, "codes.csv", ct).ConfigureAwait(false);
        }

        // ---- Body JSON thuần ----
        return await ReadJsonBodyAsync(request, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// True nếu Content-Type là kiểu dữ liệu thô của file CSV.
    /// </summary>
    private static bool IsCsvContentType(string contentType) =>
        contentType.Contains("text/csv", StringComparison.OrdinalIgnoreCase)
        || contentType.Contains("application/csv", StringComparison.OrdinalIgnoreCase);

    // =================================================================
    // Body JSON thuần (không upload file)
    // =================================================================

    /// <summary>Đọc body JSON thuần (<c>codes</c> hoặc <c>csv</c>).</summary>
    private async Task<PoContentResult> ReadJsonBodyAsync(
        HttpListenerRequest request,
        CancellationToken ct)
    {
        var maxBytes = _opt.MaxBodyBytes;

        if (request.ContentLength64 > maxBytes)
        {
            return PoContentResult.Fail(413, BodyTooLargeMessage());
        }

        byte[] body;
        try
        {
            body = await ReadAllBytesAsync(request, maxBytes, ct).ConfigureAwait(false);
        }
        catch (BodyTooLargeException)
        {
            return PoContentResult.Fail(413, BodyTooLargeMessage());
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return PoContentResult.Fail(408, "Yêu cầu bị hủy do quá thời gian chờ.");
        }

        if (body.Length == 0)
        {
            return PoContentResult.Fail(
                400,
                "Body rỗng — cần gửi JSON gồm poName, orderQty và codes/csv, "
                + "hoặc tải file .csv/.json/.xlsx lên.");
        }

        // ---- Content-Type application/json: phân biệt 2 trường hợp ----
        // (a) JSON OBJECT  -> yêu cầu PO thuần (poName, orderQty, codes/csv).
        // (b) JSON ARRAY   -> file danh sách mã (mảng chuỗi / mảng đối tượng).
        // Nhờ vậy MES có thể gửi file .json mà KHÔNG cần header filename.
        if (LooksLikeJsonArray(body))
        {
            return await ReadJsonCodesFileAsync(request, body, ct).ConfigureAwait(false);
        }

        PoPushRequest? payload;
        try
        {
            payload = System.Text.Json.JsonSerializer.Deserialize(
                body, PoServerJsonContext.Default.PoPushRequest);
        }
        catch (System.Text.Json.JsonException ex)
        {
            return PoContentResult.Fail(400, "Body JSON không hợp lệ: " + ex.Message);
        }

        if (payload is null)
        {
            return PoContentResult.Fail(400, "Không đọc được nội dung yêu cầu.");
        }

        var rawCodes = new List<string>();

        if (payload.Codes is { Count: > 0 })
        {
            foreach (var code in payload.Codes)
            {
                if (!string.IsNullOrWhiteSpace(code))
                {
                    rawCodes.Add(code.Trim());
                }
            }
        }

        if (rawCodes.Count == 0 && !string.IsNullOrWhiteSpace(payload.Csv))
        {
            rawCodes.AddRange(PoHttpServer.ParseCsv(payload.Csv));
        }

        return PoContentResult.Ok(
            new PoPushContent
            {
                Metadata = payload,
                RawCodes = rawCodes,
                Source = payload.Codes is { Count: > 0 } ? "codes" : "csv",
            });
    }

    /// <summary>
    /// Kiểm tra body JSON có phải MẢNG (array) hay không — dùng để phân biệt
    /// "file danh sách mã" với "yêu cầu PO thuần".
    /// </summary>
    /// <param name="body">Nội dung body.</param>
    /// <returns>True nếu body bắt đầu bằng '['.</returns>
    private static bool LooksLikeJsonArray(byte[] body)
    {
        // Bỏ BOM + khoảng trắng đầu.
        var i = 0;
        while (i < body.Length)
        {
            var b = body[i];
            if (b == 0xEF || b == 0xBB || b == 0xBF)
            {
                i++;
                continue;
            }

            if (b is 0x20 or 0x09 or 0x0A or 0x0D)
            {
                i++;
                continue;
            }

            break;
        }

        return i < body.Length && body[i] == (byte)'[';
    }

    /// <summary>
    /// Đọc body làm file danh sách mã JSON (mảng chuỗi hoặc mảng đối tượng).
    /// </summary>
    private async Task<PoContentResult> ReadJsonCodesFileAsync(
        HttpListenerRequest request,
        byte[] body,
        CancellationToken ct)
    {
        var metadata = BuildMetadataFromQuery(request);

        // Ghi ra file tạm rồi đọc lại bằng parser chuẩn (nhất quán với
        // upload file .json qua filename / multipart).
        var tempPath = _uploadStore.CreateTempPath(".json");

        try
        {
            await File.WriteAllBytesAsync(tempPath, body, ct).ConfigureAwait(false);

            var codes = await PoCodeFileParser.ReadJsonAsync(tempPath, ct)
                .ConfigureAwait(false);

            if (codes.Count == 0)
            {
                return PoContentResult.Fail(
                    400,
                    "Không tìm được mã nào hợp lệ trong file JSON.");
            }

            return PoContentResult.Ok(
                new PoPushContent
                {
                    Metadata = metadata,
                    RawCodes = codes.ToList(),
                    SourceFileName = "codes.json",
                    Source = "file codes.json",
                });
        }
        catch (System.Text.Json.JsonException ex)
        {
            return PoContentResult.Fail(400, "File JSON không hợp lệ: " + ex.Message);
        }
        catch (PoFileFormatException ex)
        {
            return PoContentResult.Fail(400, ex.Message);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return PoContentResult.Fail(408, "Yêu cầu bị hủy do quá thời gian chờ.");
        }
        finally
        {
            TryDeleteTempFile(tempPath);
        }
    }

    // =================================================================
    // Upload file thuần (Content-Type + filename, metadata trong query string)
    // =================================================================

    /// <summary>Đọc file tải lên trực tiếp (không qua multipart).</summary>
    private async Task<PoContentResult> ReadRawFileUploadAsync(
        HttpListenerRequest request,
        string rawFileName,
        CancellationToken ct)
    {
        var fileName = PoUploadStore.SanitizeFileName(rawFileName);

        // Kiểm tra tên file GỐC (chưa làm sạch) để chặn path traversal
        // (VD "..\..\evil.csv") và tên rác.
        if (!PoUploadStore.IsSaneFileName(rawFileName, out var nameError))
        {
            return PoContentResult.Fail(400, "Tên file không hợp lệ: " + nameError);
        }

        var kind = PoUploadStore.DetectKind(fileName);
        if (kind == PoUploadFileKind.Unknown)
        {
            return PoContentResult.Fail(
                400,
                $"Định dạng file không được hỗ trợ: '{fileName}'. "
                + "Chỉ chấp nhận .csv, .json, .xlsx, .xls.");
        }

        var metadata = BuildMetadataFromQuery(request);

        // ---- Stream file xuống đĩa tạm ----
        var maxFileBytes = _opt.MaxFileBytes;

        if (request.ContentLength64 > maxFileBytes)
        {
            return PoContentResult.Fail(
                413,
                $"File vượt quá dung lượng cho phép ({_opt.MaxFileMB} MB).");
        }

        var tempPath = _uploadStore.CreateTempPath(Path.GetExtension(fileName));
        long written;

        try
        {
            written = await WriteStreamToFileAsync(
                request.InputStream, tempPath, maxFileBytes, ct).ConfigureAwait(false);

            if (written == 0)
            {
                return PoContentResult.Fail(400, "File tải lên rỗng.");
            }

            var codes = await ParseFileAsync(tempPath, kind, ct).ConfigureAwait(false);

            if (codes.Count == 0)
            {
                return PoContentResult.Fail(
                    400,
                    $"Không tìm được mã nào hợp lệ trong file '{fileName}'.");
            }

            _logger.LogInformation(
                "Nhận file {FileName} ({Size} byte, {Count} mã) từ MES.",
                fileName, written, codes.Count);

            return PoContentResult.Ok(
                new PoPushContent
                {
                    Metadata = metadata,
                    RawCodes = codes.ToList(),
                    SourceFileName = fileName,
                    Source = "file " + fileName,
                });
        }
        catch (BodyTooLargeException)
        {
            return PoContentResult.Fail(
                413,
                $"File vượt quá dung lượng cho phép ({_opt.MaxFileMB} MB).");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return PoContentResult.Fail(408, "Yêu cầu bị hủy do quá thời gian chờ.");
        }
        catch (PoFileFormatException ex)
        {
            return PoContentResult.Fail(400, ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Lỗi khi xử lý file tải lên '{FileName}'.", fileName);
            return PoContentResult.Fail(500, "Lỗi khi đọc file tải lên: " + ex.Message);
        }
        finally
        {
            TryDeleteTempFile(tempPath);
        }
    }

    // =================================================================
    // multipart/form-data
    // =================================================================

    /// <summary>Đọc multipart/form-data (metadata + file).</summary>
    private async Task<PoContentResult> ReadMultipartAsync(
        HttpListenerRequest request,
        CancellationToken ct)
    {
        var maxBodyBytes = _opt.MaxBodyBytes;

        if (request.ContentLength64 > maxBodyBytes)
        {
            return PoContentResult.Fail(413, BodyTooLargeMessage());
        }

        // Stream toàn bộ multipart xuống file tạm (streaming, không nạp RAM).
        var multipartTemp = _uploadStore.CreateTempPath(".multipart");
        string boundary;

        try
        {
            boundary = PoMultipartHeader.GetBoundary(request.ContentType) ?? string.Empty;

            if (boundary.Length == 0)
            {
                return PoContentResult.Fail(
                    400, "multipart/form-data thiếu boundary trong Content-Type.");
            }

            await using (var fs = new FileStream(
                multipartTemp, FileMode.Create, FileAccess.Write, FileShare.None,
                bufferSize: 64 * 1024, useAsync: true))
            {
                await CopyStreamAsync(request.InputStream, fs, maxBodyBytes, ct)
                    .ConfigureAwait(false);
            }
        }
        catch (BodyTooLargeException)
        {
            TryDeleteTempFile(multipartTemp);
            return PoContentResult.Fail(413, BodyTooLargeMessage());
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            TryDeleteTempFile(multipartTemp);
            return PoContentResult.Fail(408, "Yêu cầu bị hủy do quá thời gian chờ.");
        }
        catch (Exception ex)
        {
            TryDeleteTempFile(multipartTemp);
            _logger.LogError(ex, "Lỗi khi nhận multipart/form-data.");
            return PoContentResult.Fail(500, "Lỗi khi nhận dữ liệu multipart: " + ex.Message);
        }

        try
        {
            return await ParseMultipartAsync(multipartTemp, boundary, ct).ConfigureAwait(false);
        }
        finally
        {
            TryDeleteTempFile(multipartTemp);
        }
    }

    /// <summary>Phân tích file multipart đã stream xuống đĩa.</summary>
    private async Task<PoContentResult> ParseMultipartAsync(
        string multipartPath,
        string boundary,
        CancellationToken ct)
    {
        var metadata = new PoPushRequest();
        string? fileName = null;
        var fileKind = PoUploadFileKind.Unknown;
        var fileTempPath = string.Empty;

        try
        {
            await using var fs = new FileStream(
                multipartPath, FileMode.Open, FileAccess.Read, FileShare.Read,
                bufferSize: 64 * 1024, useAsync: true);

            await foreach (var part in PoMultipartReader
                .EnumeratePartsAsync(fs, boundary, ct)
                .ConfigureAwait(false))
            {
                ct.ThrowIfCancellationRequested();

                var name = PoMultipartHeader.GetDispositionParameter(part.HeaderText, "name");
                var partFileName = PoMultipartHeader.GetDispositionParameter(
                    part.HeaderText, "filename");

                if (!string.IsNullOrWhiteSpace(partFileName))
                {
                    // ---- Phần file ----
                    // Kiểm tra tên file GỐC trước khi làm sạch để chặn
                    // path traversal (VD "..\..\evil.csv").
                    if (!PoUploadStore.IsSaneFileName(partFileName, out var nameError))
                    {
                        return PoContentResult.Fail(
                            400, "Tên file không hợp lệ: " + nameError);
                    }

                    fileName = PoUploadStore.SanitizeFileName(partFileName);

                    fileKind = PoUploadStore.DetectKind(fileName);
                    if (fileKind == PoUploadFileKind.Unknown)
                    {
                        return PoContentResult.Fail(
                            400,
                            $"Định dạng file không được hỗ trợ: '{fileName}'. "
                            + "Chỉ chấp nhận .csv, .json, .xlsx, .xls.");
                    }

                    // Ghi nội dung phần file xuống file tạm riêng (streaming).
                    fileTempPath = _uploadStore.CreateTempPath(Path.GetExtension(fileName));

                    await using (var outFs = new FileStream(
                        fileTempPath, FileMode.Create, FileAccess.Write, FileShare.None,
                        bufferSize: 64 * 1024, useAsync: true))
                    {
                        await PoMultipartPartReader.CopyContentToAsync(
                                fs, part, outFs, _opt.MaxFileBytes, ct)
                            .ConfigureAwait(false);
                    }

                    continue;
                }

                // ---- Phần field metadata ----
                var value = await PoMultipartPartReader
                    .ReadContentAsTextAsync(fs, part, ct)
                    .ConfigureAwait(false);
                ApplyMetadataField(metadata, name, value);
            }

            // ---- Không có file nào ----
            if (string.IsNullOrWhiteSpace(fileTempPath))
            {
                return PoContentResult.Fail(
                    400,
                    "multipart/form-data không có file. "
                    + "Cần một field file chứa .csv / .json / .xlsx / .xls.");
            }

            var codes = await ParseFileAsync(fileTempPath, fileKind, ct).ConfigureAwait(false);

            if (codes.Count == 0)
            {
                return PoContentResult.Fail(
                    400,
                    $"Không tìm được mã nào hợp lệ trong file '{fileName}'.");
            }

            _logger.LogInformation(
                "Nhận file multipart {FileName} ({Count} mã) từ MES.",
                fileName, codes.Count);

            return PoContentResult.Ok(
                new PoPushContent
                {
                    Metadata = metadata,
                    RawCodes = codes.ToList(),
                    SourceFileName = fileName,
                    Source = "file " + fileName,
                });
        }
        catch (BodyTooLargeException)
        {
            return PoContentResult.Fail(
                413,
                $"File vượt quá dung lượng cho phép ({_opt.MaxFileMB} MB).");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return PoContentResult.Fail(408, "Yêu cầu bị hủy do quá thời gian chờ.");
        }
        catch (PoFileFormatException ex)
        {
            return PoContentResult.Fail(400, ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Lỗi khi phân tích multipart/form-data.");
            return PoContentResult.Fail(500, "Lỗi khi đọc file tải lên: " + ex.Message);
        }
        finally
        {
            if (!string.IsNullOrEmpty(fileTempPath))
            {
                TryDeleteTempFile(fileTempPath);
            }
        }
    }

    // =================================================================
    // Helper
    // =================================================================

    /// <summary>
    /// Xoá các file tạm còn sót từ lần chạy trước (server bị tắt đột ngột).
    /// </summary>
    public void CleanupStaleFiles() => _uploadStore.CleanupStaleFiles();

    /// <summary>
    /// Đọc tập mã từ file tạm theo loại file (streaming, không nạp trọn RAM).
    /// </summary>
    /// <param name="path">Đường dẫn file tạm.</param>
    /// <param name="kind">Loại file.</param>
    /// <param name="ct">Token hủy.</param>
    /// <returns>Danh sách mã.</returns>
    public static async Task<IReadOnlyList<string>> ParseFileAsync(
        string path,
        PoUploadFileKind kind,
        CancellationToken ct)
    {
        return kind switch
        {
            PoUploadFileKind.Csv => await PoCodeFileParser.ReadCsvAsync(path, ct)
                .ConfigureAwait(false),
            PoUploadFileKind.Json => await PoCodeFileParser.ReadJsonAsync(path, ct)
                .ConfigureAwait(false),
            PoUploadFileKind.Xlsx or PoUploadFileKind.Xls =>
                (await PoExcelReader.ReadAsync(path, ct).ConfigureAwait(false)).Codes,
            _ => throw new PoFileFormatException(
                "Định dạng file không được hỗ trợ: " + kind + "."),
        };
    }

    /// <summary>Dựng metadata từ query string (dùng cho upload file thuần).</summary>
    private static PoPushRequest BuildMetadataFromQuery(HttpListenerRequest request)
    {
        var query = request.QueryString;
        var payload = new PoPushRequest();

        payload.PoName = query["poName"] ?? query["po"] ?? string.Empty;
        payload.Gtin = query["gtin"] ?? string.Empty;
        payload.BatchCode = query["batchCode"] ?? query["batch"] ?? string.Empty;
        payload.ProductName = query["productName"] ?? query["product"] ?? string.Empty;
        payload.Customer = query["customer"] ?? string.Empty;

        var qtyRaw = query["orderQty"] ?? query["qty"] ?? string.Empty;
        if (int.TryParse(
            qtyRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var qty))
        {
            payload.OrderQty = qty;
        }

        return payload;
    }

    /// <summary>Gán giá trị cho field metadata của multipart.</summary>
    private static void ApplyMetadataField(
        PoPushRequest payload,
        string? name,
        string value)
    {
        switch ((name ?? string.Empty).Trim().ToLowerInvariant())
        {
            case "poname":
            case "po":
                payload.PoName = value;
                break;

            case "orderqty":
            case "qty":
                if (int.TryParse(
                    value.Trim(), NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out var qty))
                {
                    payload.OrderQty = qty;
                }

                break;

            case "gtin":
                payload.Gtin = value;
                break;

            case "batchcode":
                payload.BatchCode = value;
                break;

            case "productname":
                payload.ProductName = value;
                break;

            case "customer":
                payload.Customer = value;
                break;
        }
    }

    /// <summary>
    /// Lấy tên file từ header <c>Content-Disposition</c> (dạng
    /// <c>attachment; filename="codes.csv"</c>) hoặc header <c>X-Filename</c>.
    /// </summary>
    private static string? GetUploadFileName(HttpListenerRequest request)
    {
        var disposition = request.Headers["Content-Disposition"];
        if (!string.IsNullOrWhiteSpace(disposition))
        {
            var name = PoMultipartHeader.GetDispositionParameter(disposition, "filename");
            if (!string.IsNullOrWhiteSpace(name))
            {
                return name;
            }
        }

        var xFilename = request.Headers["X-Filename"];
        if (!string.IsNullOrWhiteSpace(xFilename))
        {
            return xFilename;
        }

        return null;
    }

    /// <summary>Đọc toàn bộ body vào RAM (chỉ dùng cho JSON nhỏ).</summary>
    private static async Task<byte[]> ReadAllBytesAsync(
        HttpListenerRequest request,
        int maxBytes,
        CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        var readBuffer = new byte[81920];

        while (true)
        {
            int read;
            try
            {
                read = await request.InputStream
                    .ReadAsync(readBuffer, ct)
                    .ConfigureAwait(false);
            }
            catch (IOException)
            {
                break;
            }

            if (read <= 0)
            {
                break;
            }

            if (buffer.Length + read > maxBytes)
            {
                throw new BodyTooLargeException();
            }

            buffer.Write(readBuffer, 0, read);
        }

        return buffer.ToArray();
    }

    /// <summary>
    /// Ghi nội dung stream vào file tạm (streaming), dừng nếu vượt giới hạn.
    /// </summary>
    private static async Task<long> WriteStreamToFileAsync(
        Stream input,
        string path,
        int maxBytes,
        CancellationToken ct)
    {
        await using var fs = new FileStream(
            path, FileMode.Create, FileAccess.Write, FileShare.None,
            bufferSize: 64 * 1024, useAsync: true);

        return await CopyStreamAsync(input, fs, maxBytes, ct).ConfigureAwait(false);
    }

    /// <summary>Chép stream (giới hạn cỡ) sang stream đích.</summary>
    private static async Task<long> CopyStreamAsync(
        Stream input,
        Stream output,
        int maxBytes,
        CancellationToken ct)
    {
        var buffer = new byte[81920];
        long total = 0;

        while (true)
        {
            int read;
            try
            {
                read = await input.ReadAsync(buffer, ct).ConfigureAwait(false);
            }
            catch (IOException)
            {
                break;
            }

            if (read <= 0)
            {
                break;
            }

            total += read;

            if (total > maxBytes)
            {
                throw new BodyTooLargeException();
            }

            await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
        }

        await output.FlushAsync(ct).ConfigureAwait(false);
        return total;
    }

    /// <summary>Xoá file tạm (không ném lỗi).</summary>
    private void TryDeleteTempFile(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // Bỏ qua.
        }
        catch (UnauthorizedAccessException)
        {
            // Bỏ qua.
        }
    }

    /// <summary>Thông điệp lỗi khi body vượt giới hạn.</summary>
    private string BodyTooLargeMessage() =>
        $"Body vượt quá dung lượng cho phép ({_opt.MaxBodyMB} MB).";
}

/// <summary>
/// Kết quả đọc nội dung request: thành công hoặc lỗi (mã HTTP + thông điệp).
/// </summary>
public sealed class PoContentResult
{
    private PoContentResult(bool ok, PoPushContent? content, int statusCode, string message)
    {
        IsSuccess = ok;
        Content = content;
        StatusCode = statusCode;
        Message = message;
    }

    /// <summary>True nếu đọc thành công.</summary>
    public bool IsSuccess { get; }

    /// <summary>Nội dung đọc được (null nếu lỗi).</summary>
    public PoPushContent? Content { get; }

    /// <summary>Mã HTTP cần trả về (nếu lỗi).</summary>
    public int StatusCode { get; }

    /// <summary>Thông điệp lỗi (tiếng Việt).</summary>
    public string Message { get; }

    /// <summary>Tạo kết quả thành công.</summary>
    /// <param name="content">Nội dung đọc được.</param>
    /// <returns>Kết quả.</returns>
    public static PoContentResult Ok(PoPushContent content) =>
        new(true, content, 200, string.Empty);

    /// <summary>Tạo kết quả lỗi.</summary>
    /// <param name="statusCode">Mã HTTP.</param>
    /// <param name="message">Thông điệp lỗi.</param>
    /// <returns>Kết quả.</returns>
    public static PoContentResult Fail(int statusCode, string message) =>
        new(false, null, statusCode, message);
}
