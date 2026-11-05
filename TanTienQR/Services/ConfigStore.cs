using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace TanTienQR.Services;

/// <summary>
/// Đọc và ghi file cấu hình <c>appsettings.json</c> trong thư mục ứng dụng.
///
/// Dùng <see cref="AppConfigJsonContext"/> (source-generated) nên không phụ
/// thuộc reflection -> an toàn với Native AOT.
///
/// Lưu ý khi ghi: luôn ghi file tạm rồi move đè, để không để lại file hỏng
/// nếu ghi lỗi giữa chừng (cùng cách làm với file license).
/// </summary>
internal sealed class ConfigStore
{
    private readonly ILogger<ConfigStore> _logger;

    public ConfigStore(ILogger<ConfigStore> logger)
    {
        _logger = logger;
    }

    /// <summary>Đường dẫn file cấu hình chính của ứng dụng.</summary>
    public static string ConfigPath => Path.Combine(AppContext.BaseDirectory, "appsettings.json");

    /// <summary>
    /// File cấu hình Development tồn tại trong thư mục ứng dụng hay không.
    ///
    /// Nếu có, nó sẽ GHI ĐÈ các giá trị trong appsettings.json khi app đọc cấu
    /// hình -> việc sửa appsettings.json sẽ không có tác dụng. UI cần cảnh báo.
    /// </summary>
    public static bool HasDevelopmentOverride =>
        File.Exists(Path.Combine(AppContext.BaseDirectory, "appsettings.Development.json"));

    /// <summary>
    /// Đọc cấu hình hiện tại từ file. Nếu file không tồn tại hoặc lỗi định dạng
    /// thì trả về snapshot mặc định (không ném exception).
    /// </summary>
    public AppConfigSnapshot Load()
    {
        var path = ConfigPath;

        if (!File.Exists(path))
        {
            _logger.LogWarning("Khong tim thay file cau hinh {Path}, dung gia tri mac dinh.", path);
            return new AppConfigSnapshot();
        }

        try
        {
            var text = File.ReadAllText(path, Encoding.UTF8);
            var snapshot = JsonSerializer.Deserialize(text, AppConfigJsonContext.Default.AppConfigSnapshot);

            if (snapshot is null)
            {
                _logger.LogWarning("File cau hinh rong, dung gia tri mac dinh.");
                return new AppConfigSnapshot();
            }

            // Bảo đảm không null phần tử con (JSON có thể viết "Camera": null).
            snapshot.Camera ??= new CameraConfig();
            snapshot.Plc ??= new PlcConfig();
            snapshot.Web ??= new WebConfig();

            return snapshot;
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "File cau hinh loi dinh dang, dung gia tri mac dinh.");
            return new AppConfigSnapshot();
        }
        catch (IOException ex)
        {
            _logger.LogError(ex, "Khong doc duoc file cau hinh, dung gia tri mac dinh.");
            return new AppConfigSnapshot();
        }
    }

    /// <summary>
    /// Ghi cấu hình xuống file. Trả về true nếu ghi thành công.
    /// </summary>
    public bool Save(AppConfigSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var path = ConfigPath;

        try
        {
            var json = JsonSerializer.Serialize(
                snapshot,
                AppConfigJsonContext.Default.AppConfigSnapshot);

            // Ghi file tạm trước rồi move đè: nếu ghi lỗi giữa chừng thì
            // appsettings.json cũ vẫn còn nguyên.
            var temp = path + ".tmp";
            File.WriteAllText(temp, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.Move(temp, path, overwrite: true);

            _logger.LogInformation("Da luu cau hinh vao {Path}", path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            _logger.LogError(ex, "Khong ghi duoc file cau hinh: {Path}", path);
            return false;
        }
    }
}
