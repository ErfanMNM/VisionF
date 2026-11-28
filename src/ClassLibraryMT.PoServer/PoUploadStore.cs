using System.Globalization;
using Microsoft.Extensions.Logging;

namespace ClassLibraryMT.PoServer;

/// <summary>
/// Loại file mã mà server chấp nhận từ MES.
/// </summary>
public enum PoUploadFileKind
{
    /// <summary>Không xác định / không được hỗ trợ.</summary>
    Unknown = 0,

    /// <summary>File CSV (<c>.csv</c>).</summary>
    Csv,

    /// <summary>File JSON (<c>.json</c>).</summary>
    Json,

    /// <summary>File Excel OOXML (<c>.xlsx</c>).</summary>
    Xlsx,

    /// <summary>File Excel nhị phân cũ (<c>.xls</c>).</summary>
    Xls,
}

/// <summary>
/// Thông tin về một file tải lên đã được stream xuống đĩa tạm.
///
/// <para>
/// Server KHÔNG giữ nội dung file trong RAM — file được ghi tăng dần vào
/// file tạm, sau đó đọc lại bằng streaming. Đối tượng này giữ đường dẫn
/// file tạm và metadata; <see cref="Dispose"/> sẽ xoá file tạm.
/// </para>
/// </summary>
public sealed class PoUploadedFile : IDisposable
{
    private int _disposed;

    /// <summary>Khởi tạo thông tin file tải lên.</summary>
    /// <param name="fileName">Tên file gốc (đã làm sạch).</param>
    /// <param name="tempPath">Đường dẫn file tạm trên đĩa.</param>
    /// <param name="sizeBytes">Kích thước file (byte).</param>
    /// <param name="kind">Loại file.</param>
    public PoUploadedFile(
        string fileName,
        string tempPath,
        long sizeBytes,
        PoUploadFileKind kind)
    {
        FileName = fileName;
        TempPath = tempPath;
        SizeBytes = sizeBytes;
        Kind = kind;
    }

    /// <summary>Tên file gốc do MES gửi (đã loại bỏ đường dẫn / ký tự lạ).</summary>
    public string FileName { get; }

    /// <summary>Đường dẫn file tạm chứa nội dung đã stream xuống đĩa.</summary>
    public string TempPath { get; }

    /// <summary>Kích thước file (byte).</summary>
    public long SizeBytes { get; }

    /// <summary>Loại file đã xác định.</summary>
    public PoUploadFileKind Kind { get; }

    /// <summary>True nếu file đã bị xoá / dispose.</summary>
    public bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    /// <summary>Xoá file tạm (không ném lỗi nếu đã xoá).</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try
        {
            if (!string.IsNullOrEmpty(TempPath) && File.Exists(TempPath))
            {
                File.Delete(TempPath);
            }
        }
        catch (IOException)
        {
            // Bỏ qua: file tạm sẽ được dọn khi khởi động server.
        }
        catch (UnauthorizedAccessException)
        {
            // Bỏ qua.
        }
    }
}

/// <summary>
/// Quản lý thư mục file tạm cho các file MES tải lên.
///
/// <para>
/// <b>Vì sao cần temp file?</b> File tải lên có thể tới hàng trăm MB. Nếu
/// đọc hết vào RAM sẽ làm app bị "phình" bộ nhớ và có thể bị OOM trên máy
/// công nghiệp. Thay vào đó, body được ghi tăng dần (streaming) vào file tạm,
/// rồi parser đọc lại theo luồng (streaming) — mọi lúc RAM chỉ tăng O(1).
/// </para>
/// </summary>
public sealed class PoUploadStore
{
    private readonly string _root;
    private readonly ILogger _logger;

    /// <summary>Khởi tạo kho file tạm.</summary>
    /// <param name="root">Thư mục gốc chứa file tạm (tạo nếu chưa có).</param>
    /// <param name="logger">Logger để ghi cảnh báo.</param>
    public PoUploadStore(string root, ILogger logger)
    {
        _root = root ?? throw new ArgumentNullException(nameof(root));
        _logger = logger;

        try
        {
            Directory.CreateDirectory(_root);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex, "Không tạo được thư mục file tạm cho upload: {Root}", _root);
        }
    }

    /// <summary>Thư mục gốc chứa file tạm.</summary>
    public string Root => _root;

    /// <summary>
    /// Tạo đường dẫn file tạm duy nhất cho một lần upload.
    /// </summary>
    /// <param name="extension">Phần mở rộng (bao gồm dấu chấm) — VD <c>.csv</c>.</param>
    /// <returns>Đường dẫn file tạm.</returns>
    public string CreateTempPath(string extension)
    {
        var safeExt = NormalizeExtension(extension);
        var name = $"po-upload-{Guid.NewGuid():N}{safeExt}";
        return Path.Combine(_root, name);
    }

    /// <summary>
    /// Xoá các file tạm cũ còn sót từ lần chạy trước (server bị tắt đột ngột).
    /// </summary>
    public void CleanupStaleFiles()
    {
        try
        {
            if (!Directory.Exists(_root))
            {
                return;
            }

            var cutoff = DateTime.UtcNow.AddHours(-6);
            foreach (var file in Directory.EnumerateFiles(_root, "po-upload-*"))
            {
                try
                {
                    var info = new FileInfo(file);
                    if (info.LastWriteTimeUtc < cutoff)
                    {
                        info.Delete();
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Bỏ qua từng file lỗi.
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Không dọn được file tạm cũ trong {Root}.", _root);
        }
    }

    /// <summary>
    /// Chuẩn hoá phần mở rộng (luôn bắt đầu bằng dấu chấm, chỉ chữ/số).
    /// </summary>
    private static string NormalizeExtension(string extension)
    {
        var text = (extension ?? string.Empty).Trim().ToLowerInvariant();
        if (text.Length == 0)
        {
            return string.Empty;
        }

        if (!text.StartsWith('.'))
        {
            text = "." + text;
        }

        var sb = new System.Text.StringBuilder(text.Length);
        foreach (var c in text)
        {
            if (char.IsLetterOrDigit(c) || c == '.')
            {
                sb.Append(c);
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// Xác định loại file từ tên file (chỉ dựa vào phần mở rộng).
    /// </summary>
    /// <param name="fileName">Tên file.</param>
    /// <returns>Loại file tương ứng.</returns>
    public static PoUploadFileKind DetectKind(string? fileName)
    {
        var ext = Path.GetExtension(fileName ?? string.Empty).ToLowerInvariant();

        return ext switch
        {
            ".csv" => PoUploadFileKind.Csv,
            ".json" => PoUploadFileKind.Json,
            ".xlsx" => PoUploadFileKind.Xlsx,
            ".xls" => PoUploadFileKind.Xls,
            _ => PoUploadFileKind.Unknown,
        };
    }

    /// <summary>
    /// Kiểm tra tên file có "hợp lý" hay không (chống path traversal / tên rác).
    /// </summary>
    /// <param name="fileName">Tên file raw do client gửi.</param>
    /// <param name="error">Thông điệp lỗi nếu không hợp lệ.</param>
    /// <returns>True nếu hợp lệ.</returns>
    public static bool IsSaneFileName(string? fileName, out string error)
    {
        error = string.Empty;

        var text = (fileName ?? string.Empty).Trim();

        if (text.Length == 0)
        {
            error = "Thiếu tên file.";
            return false;
        }

        if (text.Length > 200)
        {
            error = "Tên file quá dài (tối đa 200 ký tự).";
            return false;
        }

        // Ký tự điều khiển -> tên file rác.
        foreach (var c in text)
        {
            if (char.IsControl(c))
            {
                error = "Tên file chứa ký tự không hợp lệ.";
                return false;
            }
        }

        // Dấu gạch chéo ngược / xuôi -> cố truy cập ngoài thư mục.
        if (text.Contains('/') || text.Contains('\\'))
        {
            error = "Tên file không được chứa đường dẫn.";
            return false;
        }

        if (text is "." or "..")
        {
            error = "Tên file không hợp lệ.";
            return false;
        }

        // Ký tự Windows không hợp lệ trong tên file.
        foreach (var c in Path.GetInvalidFileNameChars())
        {
            if (c is '/' or '\\')
            {
                continue;
            }

            if (text.Contains(c, StringComparison.Ordinal))
            {
                error = "Tên file chứa ký tự không hợp lệ.";
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Làm sạch tên file để hiển thị / ghi log (bỏ đường dẫn, ký tự lạ).
    /// </summary>
    /// <param name="fileName">Tên file raw.</param>
    /// <returns>Tên file đã làm sạch (tối đa 200 ký tự).</returns>
    public static string SanitizeFileName(string? fileName)
    {
        var text = (fileName ?? string.Empty).Trim();

        // Cắt bỏ đường dẫn (client đôi gửi "C:\\temp\\codes.csv").
        var slash = text.LastIndexOfAny(['/', '\\']);
        if (slash >= 0 && slash < text.Length - 1)
        {
            text = text[(slash + 1)..];
        }

        var sb = new System.Text.StringBuilder(text.Length);
        foreach (var c in text)
        {
            sb.Append(char.IsControl(c) ? '_' : c);
        }

        text = sb.ToString().Trim();

        return text.Length > 200 ? text[..200] : text;
    }

    /// <summary>
    /// Định dạng dung lượng cho thông điệp lỗi (VD "64 MB").
    /// </summary>
    /// <param name="bytes">Số byte.</param>
    /// <returns>Chuỗi định dạng.</returns>
    public static string FormatSize(long bytes) =>
        (bytes / (1024.0 * 1024.0)).ToString("0.#", CultureInfo.InvariantCulture) + " MB";
}
