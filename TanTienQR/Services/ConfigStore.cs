using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using TanTienQR.Services.Config;

namespace TanTienQR.Services;

/// <summary>
/// Đọc và ghi file cấu hình <c>appsettings.json</c> trong thư mục ứng dụng.
///
/// Dùng <see cref="AppConfigJsonContext"/> (source-generated) nên không phụ
/// thuộc reflection -> an toàn với Native AOT.
///
/// Lưu ý khi ghi: luôn ghi file tạm rồi move đè, để không để lại file hỏng
/// nếu ghi lỗi giữa chừng (cùng cách làm với file license).
///
/// Khi ghi, CÁC KEY LẠ (không nằm trong model cấu hình) vẫn được giữ nguyên:
/// file cũ được parse thành <see cref="JsonObject"/> rồi merge các giá trị
/// mới theo đường dẫn <c>Camera:Ip</c> ... vào đó. Nhờ vậy các section do
/// thư viện ngoài hoặc phiên bản mới hơn thêm vào không bị mất.
///
/// Thư mục gốc có thể ghi đè qua DI (mặc định là thư mục ứng dụng). Nhờ vậy
/// công cụ kiểm thử headless có thể trỏ sang thư mục sandbox mà không chạm
/// vào cấu hình thật.
/// </summary>
internal sealed class ConfigStore
{
    private readonly ILogger<ConfigStore> _logger;

    /// <summary>Thư mục gốc chứa file cấu hình.</summary>
    private readonly string _baseDirectory;

    public ConfigStore(ILogger<ConfigStore> logger)
        : this(logger, AppContext.BaseDirectory)
    {
    }

    /// <summary>
    /// Tạo store với thư mục gốc tùy chỉnh (dùng cho sandbox / kiểm thử).
    /// </summary>
    /// <param name="logger">Logger.</param>
    /// <param name="baseDirectory">Thư mục chứa appsettings.json.</param>
    public ConfigStore(ILogger<ConfigStore> logger, string baseDirectory)
    {
        _logger = logger;
        _baseDirectory = string.IsNullOrWhiteSpace(baseDirectory)
            ? AppContext.BaseDirectory
            : baseDirectory;
    }

    /// <summary>Đường dẫn file cấu hình chính của ứng dụng (thư mục thực thi).</summary>
    public static string ConfigPath => Path.Combine(AppContext.BaseDirectory, "appsettings.json");

    /// <summary>Đường dẫn file cấu hình mà store này đọc/ghi.</summary>
    public string ConfigFilePath => Path.Combine(_baseDirectory, "appsettings.json");

    /// <summary>
    /// True nếu có file <c>appsettings.Development.json</c> trong thư mục gốc
    /// (file này sẽ ghi đè cấu hình chính khi app khởi động).
    /// </summary>
    public bool HasDevelopmentOverride =>
        File.Exists(Path.Combine(_baseDirectory, "appsettings.Development.json"));

    /// <summary>
    /// Đọc cấu hình hiện tại từ file. Nếu file không tồn tại hoặc lỗi định dạng
    /// thì trả về snapshot mặc định (không ném exception).
    /// </summary>
    public AppConfigSnapshot Load()
    {
        var path = ConfigFilePath;

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

            Normalize(snapshot);

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
    /// Đọc cấu hình kèm dạng phẳng (key "Camera:Ip" -&gt; giá trị) và
    /// JsonObject gốc. Dùng khi cần biết các key lạ trong file.
    /// </summary>
    /// <returns>Kết quả đọc cấu hình.</returns>
    public ConfigLoadResult LoadDetailed()
    {
        var path = ConfigFilePath;
        var flat = new Dictionary<string, string?>(StringComparer.Ordinal);
        JsonObject? raw = null;
        AppConfigSnapshot snapshot;

        if (!File.Exists(path))
        {
            _logger.LogWarning("Khong tim thay file cau hinh {Path}, dung gia tri mac dinh.", path);
            snapshot = new AppConfigSnapshot();
            Normalize(snapshot);
            return new ConfigLoadResult(snapshot, flat, raw);
        }

        try
        {
            var text = File.ReadAllText(path, Encoding.UTF8);

            // JsonObject giữ lại mọi key lạ để lúc lưu không bị mất.
            try
            {
                raw = JsonNode.Parse(text) as JsonObject;
            }
            catch (JsonException)
            {
                raw = null;
            }

            var parsed = JsonSerializer.Deserialize(text, AppConfigJsonContext.Default.AppConfigSnapshot);
            snapshot = parsed ?? new AppConfigSnapshot();
            Normalize(snapshot);

            // Dựng bản phẳng từ JsonObject gốc (giữ nguyên giá trị thô).
            if (raw is not null)
            {
                Flatten(raw, null, flat);
            }
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "File cau hinh loi dinh dang, dung gia tri mac dinh.");
            snapshot = new AppConfigSnapshot();
            Normalize(snapshot);
        }
        catch (IOException ex)
        {
            _logger.LogError(ex, "Khong doc duoc file cau hinh, dung gia tri mac dinh.");
            snapshot = new AppConfigSnapshot();
            Normalize(snapshot);
        }

        return new ConfigLoadResult(snapshot, flat, raw);
    }

    /// <summary>
    /// Ghi cấu hình xuống file. Trả về true nếu ghi thành công.
    ///
    /// Snapshot chứa đầy đủ các section được chỉnh sửa trong trang Cấu hình.
    /// Các key lạ trong file cũ vẫn được giữ nguyên (merge theo JsonPath).
    /// </summary>
    public bool Save(AppConfigSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var path = ConfigFilePath;

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

    /// <summary>
    /// Ghi cấu hình nhưng GIỮ NGUYÊN các key lạ đang có trong file.
    ///
    /// Quy trình: đọc file cũ thành <see cref="JsonObject"/>, merge từng
    /// trường của snapshot theo đường dẫn <c>Section:Key</c> (vd "Camera:Ip"),
    /// rồi ghi lại. Các key không nằm trong model cấu hình được giữ nguyên.
    /// </summary>
    /// <param name="snapshot">Cấu hình cần lưu.</param>
    /// <returns>True nếu ghi thành công.</returns>
    public bool SaveMerged(AppConfigSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var path = ConfigFilePath;

        try
        {
            JsonObject root;

            if (File.Exists(path))
            {
                var existingText = File.ReadAllText(path, Encoding.UTF8);
                root = JsonNode.Parse(existingText) as JsonObject ?? new JsonObject();
            }
            else
            {
                root = new JsonObject();
            }

            // Merge từng field theo schema để không mất key lạ.
            // Schema đã bao gồm cả section lồng nhau (Logging:Sqlite, Logging:History).
            foreach (var section in ConfigSchemaProvider.Sections)
            {
                var sectionValue = section.ResolveOwner(snapshot);
                if (sectionValue is null)
                {
                    continue;
                }

                foreach (var field in section.Fields)
                {
                    var value = field.GetValue(sectionValue);
                    SetByPath(root, field.JsonPath, ToJsonNode(value));
                }
            }

            var json = root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });

            var temp = path + ".tmp";
            File.WriteAllText(temp, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.Move(temp, path, overwrite: true);

            _logger.LogInformation("Da luu cau hinh vao {Path} (giu key la)", path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            _logger.LogError(ex, "Khong ghi duoc file cau hinh: {Path}", path);
            return false;
        }
    }

    /// <summary>
    /// Lấy (hoặc tạo) object con theo tên trong JsonObject gốc.
    /// </summary>
    /// <param name="parent">Object cha.</param>
    /// <param name="name">Tên key.</param>
    /// <returns>JsonObject con.</returns>
    private static JsonObject GetOrAddObject(JsonObject parent, string name)
    {
        if (parent.TryGetPropertyValue(name, out var existing) && existing is JsonObject existingObj)
        {
            return existingObj;
        }

        var created = new JsonObject();
        parent[name] = created;
        return created;
    }

    /// <summary>
    /// Gán giá trị theo đường dẫn phân tách bởi dấu ':' (VD "Logging:Sqlite:Path"),
    /// tự tạo các object con nếu cần.
    /// </summary>
    /// <param name="root">JsonObject gốc.</param>
    /// <param name="path">Đường dẫn.</param>
    /// <param name="value">Giá trị (null -> xoá key).</param>
    public static void SetByPath(JsonObject root, string path, JsonNode? value)
    {
        if (root is null || string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        var parts = path.Split(':', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            return;
        }

        var current = root;
        for (var i = 0; i < parts.Length - 1; i++)
        {
            current = GetOrAddObject(current, parts[i]);
        }

        var last = parts[^1];
        if (value is null)
        {
            current.Remove(last);
            return;
        }

        current[last] = value;
    }

    /// <summary>
    /// Chuyển giá trị .NET sang JsonNode.
    /// </summary>
    /// <param name="value">Giá trị gốc.</param>
    /// <returns>JsonNode tương ứng (null nếu value null).</returns>
    public static JsonNode? ToJsonNode(object? value) => value switch
    {
        null => null,
        string s => JsonValue.Create(s),
        bool b => JsonValue.Create(b),
        int i => JsonValue.Create(i),
        long l => JsonValue.Create(l),
        double d => JsonValue.Create(d),
        float f => JsonValue.Create(f),
        decimal m => JsonValue.Create(m),
        short sh => JsonValue.Create(sh),
        byte by => JsonValue.Create(by),
        _ => JsonValue.Create(value.ToString()),
    };

    /// <summary>
    /// Duyệt JsonObject đệ quy, tạo bản phẳng key "A:B:C" -&gt; giá trị.
    /// </summary>
    /// <param name="node">Node gốc.</param>
    /// <param name="prefix">Tiền tố đường dẫn hiện tại.</param>
    /// <param name="result">Dictionary kết quả.</param>
    private static void Flatten(JsonNode? node, string? prefix, Dictionary<string, string?> result)
    {
        if (node is null)
        {
            return;
        }

        if (node is JsonObject obj)
        {
            foreach (var pair in obj)
            {
                var key = string.IsNullOrEmpty(prefix) ? pair.Key : $"{prefix}:{pair.Key}";
                Flatten(pair.Value, key, result);
            }

            return;
        }

        if (node is JsonArray arr)
        {
            var key = prefix ?? string.Empty;
            result[key] = arr.ToJsonString();
            return;
        }

        if (!string.IsNullOrEmpty(prefix))
        {
            result[prefix] = node is JsonValue value
                ? value.ToString()
                : node?.ToJsonString();
        }
    }

    /// <summary>
    /// Bảo đảm không null phần tử con (JSON có thể viết "Camera": null).
    /// </summary>
    /// <param name="snapshot">Snapshot cần chuẩn hoá.</param>
    private static void Normalize(AppConfigSnapshot snapshot)
    {
        snapshot.Camera ??= new CameraConfig();
        snapshot.Plc ??= new PlcConfig();
        snapshot.Web ??= new WebConfig();
        snapshot.Scanner ??= new ScannerConfig();
        snapshot.Po ??= new PoConfig();
        snapshot.Print ??= new PrintConfig();
        snapshot.Print.Remote ??= new PrinterRemoteConfig();
        snapshot.PoServer ??= new PoServerConfig();
        snapshot.Checkee ??= new CheckeeConfig();
        snapshot.Logging ??= new LoggingConfig();
        snapshot.Logging.Sqlite ??= new SqliteLoggingConfig();
        snapshot.Logging.History ??= new LoggingHistoryConfig();
        snapshot.Auth ??= new AuthConfig();
        snapshot.PlcSettings ??= new PlcSettingsConfig();
        snapshot.Scans ??= new ScanConfig();
        snapshot.LicensePortal ??= new LicensePortalConfig();
    }
}

/// <summary>
/// Kết quả đọc cấu hình: snapshot đã parse + bản phẳng + JsonObject gốc.
/// </summary>
/// <param name="Snapshot">Cấu hình đã parse.</param>
/// <param name="Flat">Bản phẳng key "Camera:Ip" -&gt; giá trị thô.</param>
/// <param name="Raw">JsonObject gốc (null nếu file lỗi/không tồn tại).</param>
public sealed record ConfigLoadResult(
    AppConfigSnapshot Snapshot,
    IReadOnlyDictionary<string, string?> Flat,
    JsonObject? Raw);
