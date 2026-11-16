using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Reflection;
using System.Text.Json.Serialization;

namespace TanTienQR.Services.Config;

/// <summary>
/// Mô tả một trường cấu hình đã được phân giải từ attribute + PropertyInfo.
///
/// Được sinh một lần bởi <see cref="ConfigSchemaProvider"/> rồi dùng lại cho
/// cả ViewModel, validation và diff nên không cần reflection lúc chạy.
/// </summary>
public sealed class ConfigFieldSchema
{
    internal ConfigFieldSchema(
        ConfigSectionSchema section,
        PropertyInfo property,
        ConfigFieldAttribute attribute,
        string jsonPath,
        string jsonKey,
        string? parentJsonPath)
    {
        Section = section;
        Property = property;
        Attribute = attribute;
        JsonPath = jsonPath;
        JsonKey = jsonKey;
        ParentJsonPath = parentJsonPath;
        Label = attribute.Label;
        Control = ResolveControl(attribute, property);
        IsNumeric = Control == ConfigControlKind.Number;
        NumericType = IsNumeric ? Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType : null;
    }

    /// <summary>Section chứa trường này.</summary>
    public ConfigSectionSchema Section { get; }

    /// <summary>Property gốc trong model cấu hình.</summary>
    public PropertyInfo Property { get; }

    /// <summary>Attribute gốc đã dùng để sinh schema.</summary>
    public ConfigFieldAttribute Attribute { get; }

    /// <summary>
    /// Đường dẫn dạng "Camera:Ip" — khớp với key của IConfiguration và
    /// với đường dẫn trong JsonObject khi merge.
    /// </summary>
    public string JsonPath { get; }

    /// <summary>Tên key JSON của property (VD: "Ip").</summary>
    public string JsonKey { get; }

    /// <summary>Đường dẫn cha (VD: "Logging:Sqlite"), null nếu property nằm ở gốc.</summary>
    public string? ParentJsonPath { get; }

    /// <summary>Nhãn hiển thị cạnh ô nhập.</summary>
    public string Label { get; }

    /// <summary>Loại control cần dùng khi render.</summary>
    public ConfigControlKind Control { get; }

    /// <summary>True nếu control là ô nhập số.</summary>
    public bool IsNumeric { get; }

    /// <summary>Kiểu số thực tế của property (int/long/double...), null nếu không phải số.</summary>
    public Type? NumericType { get; }

    /// <summary>Giá trị nhỏ nhất (double.MinValue nếu không giới hạn).</summary>
    public double Min => Attribute.Min;

    /// <summary>Giá trị lớn nhất (double.MaxValue nếu không giới hạn).</summary>
    public double Max => Attribute.Max;

    /// <summary>Bước tăng/giảm.</summary>
    public double Increment => Attribute.Increment;

    /// <summary>Chữ gợi ý trong ô nhập.</summary>
    public string Placeholder => Attribute.Placeholder;

    /// <summary>Danh sách lựa chọn cho ComboBox.</summary>
    public IReadOnlyList<string> Options => Attribute.Options;

    /// <summary>True nếu thay đổi cần khởi động lại ứng dụng.</summary>
    public bool RequiresRestart => Attribute.RequiresRestart;

    /// <summary>Gợi ý hỗ trợ.</summary>
    public string HelpText => Attribute.HelpText;

    /// <summary>Nhóm con trong section.</summary>
    public string Group => Attribute.Group;

    /// <summary>True nếu trường bắt buộc (không được để trống).</summary>
    public bool Required => Attribute.Required;

    /// <summary>Quy tắc kiểm tra bổ sung ("IpOrHost" / "Url" / "Path").</summary>
    public string? Validation => Attribute.Validation;

    /// <summary>
    /// Đường dẫn tới trường bool quyết định trường này có được validate hay không.
    /// </summary>
    public string? SkipWhenDisabledBy => Attribute.SkipWhenDisabledBy;

    /// <summary>
    /// Đọc giá trị property từ một object cha (VD: đọc "Ip" từ CameraConfig).
    /// </summary>
    /// <param name="parent">Object chứa property.</param>
    /// <returns>Giá trị property, null nếu không đọc được.</returns>
    public object? GetValue(object? parent) =>
        parent is null ? null : Property.GetValue(parent);

    /// <summary>
    /// Gán giá trị vào property của object cha.
    /// </summary>
    /// <param name="parent">Object chứa property.</param>
    /// <param name="value">Giá trị mới (đã đúng kiểu).</param>
    public void SetValue(object? parent, object? value)
    {
        if (parent is null)
        {
            return;
        }

        Property.SetValue(parent, value);
    }

    /// <summary>
    /// Suy luận control khi attribute không chỉ định (Control = Auto).
    /// </summary>
    private static ConfigControlKind ResolveControl(ConfigFieldAttribute attribute, PropertyInfo property)
    {
        if (attribute.Control != ConfigControlKind.Auto)
        {
            return attribute.Control;
        }

        // Có danh sách lựa chọn -> ComboBox.
        if (attribute.Options.Length > 0)
        {
            return ConfigControlKind.Combo;
        }

        var type = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;

        if (type == typeof(bool))
        {
            return ConfigControlKind.Bool;
        }

        if (type == typeof(string))
        {
            return ConfigControlKind.Text;
        }

        if (type == typeof(byte)
            || type == typeof(sbyte)
            || type == typeof(short)
            || type == typeof(ushort)
            || type == typeof(int)
            || type == typeof(uint)
            || type == typeof(long)
            || type == typeof(ulong)
            || type == typeof(float)
            || type == typeof(double)
            || type == typeof(decimal))
        {
            return ConfigControlKind.Number;
        }

        return ConfigControlKind.Text;
    }
}

/// <summary>
/// Mô tả một section (nhóm) của cấu hình.
///
/// Section có thể nằm ở gốc (VD: "Camera") hoặc lồng trong section khác
/// (VD: "Logging:Sqlite"). Đường dẫn đầy đủ nằm ở <see cref="JsonPath"/>.
/// </summary>
public sealed class ConfigSectionSchema
{
    internal ConfigSectionSchema(
        ConfigSectionAttribute attribute,
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties
            | DynamicallyAccessedMemberTypes.PublicParameterlessConstructor)]
        Type modelType,
        IReadOnlyList<PropertyInfo> propertyChain,
        string jsonPath)
    {
        Attribute = attribute;
        ModelType = modelType;
        PropertyChain = propertyChain;
        JsonPath = jsonPath;
        Title = string.IsNullOrWhiteSpace(attribute.Title) ? attribute.Key : attribute.Title;
        Fields = Array.Empty<ConfigFieldSchema>();
    }

    /// <summary>Attribute gốc.</summary>
    public ConfigSectionAttribute Attribute { get; }

    /// <summary>Kiểu model của section (VD: CameraConfig).</summary>
    [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties
        | DynamicallyAccessedMemberTypes.PublicParameterlessConstructor)]
    public Type ModelType { get; }

    /// <summary>
    /// Chuỗi property dẫn từ snapshot gốc tới object chứa section này.
    ///
    /// Ví dụ:
    ///   - section gốc "Camera"       -> [Camera]
    ///   - section lồng "Logging:Sqlite" -> [Logging, Sqlite]
    /// </summary>
    public IReadOnlyList<PropertyInfo> PropertyChain { get; }

    /// <summary>Key gốc trong appsettings.json (VD: "Camera").</summary>
    public string Key => Attribute.Key;

    /// <summary>Tiêu đề hiển thị trên card.</summary>
    public string Title { get; }

    /// <summary>Mô tả ngắn.</summary>
    public string Description => Attribute.Description;

    /// <summary>Tên icon (không có "Icon." prefix).</summary>
    public string Icon => Attribute.Icon;

    /// <summary>Thứ tự hiển thị.</summary>
    public int Order => Attribute.Order;

    /// <summary>Cột hiển thị (0 = trái, 1 = phải, -1 = tự động).</summary>
    public int Column => Attribute.Column;

    /// <summary>Đường dẫn gốc của section (VD: "Logging:Sqlite").</summary>
    public string JsonPath { get; }

    /// <summary>Các trường thuộc section (đã sắp xếp theo Order).</summary>
    public IReadOnlyList<ConfigFieldSchema> Fields { get; internal set; }

    /// <summary>
    /// Đọc object chứa section này từ một snapshot.
    /// </summary>
    /// <param name="snapshot">Snapshot gốc.</param>
    /// <returns>Object chứa section, null nếu không lấy được.</returns>
    public object? ResolveOwner(AppConfigSnapshot snapshot)
    {
        object? current = snapshot;

        foreach (var property in PropertyChain)
        {
            if (current is null)
            {
                return null;
            }

            current = property.GetValue(current);
        }

        return current;
    }
}

/// <summary>
/// Quét model cấu hình một lần và sinh "schema" mô tả toàn bộ section + field.
///
/// Schema được cache tĩnh (lazy) nên chỉ phản xạ một lần trong vòng đời app.
/// Dùng cho:
///  - sinh form động trong <c>ConfigView.axaml</c>,
///  - validate theo schema,
///  - phát hiện thay đổi cần restart.
///
/// Hỗ trợ section lồng nhau: một property của section cũng có thể mang
/// <c>[ConfigSection]</c> (VD: LoggingConfig.Sqlite -> SqliteLoggingConfig).
///
/// Toàn bộ thuộc tính dùng để phản xạ đều được khai báo
/// <see cref="DynamicallyAccessedMembersAttribute"/> để an toàn với Native AOT.
/// </summary>
public static class ConfigSchemaProvider
{
    private static readonly Lazy<IReadOnlyList<ConfigSectionSchema>> LazySections =
        new(BuildSections, isThreadSafe: true);

    /// <summary>
    /// Toàn bộ section của cấu hình, đã sắp xếp theo Order rồi theo tên key.
    /// </summary>
    public static IReadOnlyList<ConfigSectionSchema> Sections => LazySections.Value;

    /// <summary>
    /// Toàn bộ trường của mọi section (phẳng), theo đúng thứ tự hiển thị.
    /// </summary>
    public static IReadOnlyList<ConfigFieldSchema> AllFields =>
        Sections.SelectMany(s => s.Fields).ToList();

    /// <summary>
    /// Tìm section theo key gốc (VD: "Camera").
    /// </summary>
    /// <param name="key">Key cần tìm.</param>
    /// <returns>Section tương ứng, null nếu không có.</returns>
    public static ConfigSectionSchema? FindSection(string key) =>
        Sections.FirstOrDefault(s => string.Equals(s.Key, key, StringComparison.Ordinal));

    /// <summary>
    /// Tìm section theo ĐƯỜNG DẪN đầy đủ (VD: "Logging:Sqlite").
    /// </summary>
    /// <param name="jsonPath">Đường dẫn cần tìm.</param>
    /// <returns>Section tương ứng, null nếu không có.</returns>
    public static ConfigSectionSchema? FindSectionByPath(string jsonPath) =>
        Sections.FirstOrDefault(s => string.Equals(s.JsonPath, jsonPath, StringComparison.Ordinal));

    /// <summary>
    /// Tìm trường theo đường dẫn đầy đủ (VD: "Camera:Ip").
    /// </summary>
    /// <param name="jsonPath">Đường dẫn cần tìm.</param>
    /// <returns>Field tương ứng, null nếu không có.</returns>
    public static ConfigFieldSchema? FindField(string jsonPath) =>
        AllFields.FirstOrDefault(f => string.Equals(f.JsonPath, jsonPath, StringComparison.Ordinal));

    /// <summary>
    /// Dựng lại schema từ đầu (dùng cho test / hot reload trong dev).
    /// </summary>
    /// <returns>Danh sách section mới.</returns>
    public static IReadOnlyList<ConfigSectionSchema> Rebuild() => BuildSections();

    /// <summary>
    /// Quét <see cref="AppConfigSnapshot"/> để sinh schema (có hỗ trợ section lồng nhau).
    /// </summary>
    /// <returns>Danh sách section đã sắp xếp.</returns>
    [UnconditionalSuppressMessage("Trimming", "IL2075",
        Justification = "Các property của AppConfigSnapshot đều được khai báo qua JsonSerializable trong AppConfigJsonContext.")]
    [UnconditionalSuppressMessage("Trimming", "IL2072",
        Justification = "modelType là kiểu của property trong AppConfigSnapshot, đã khai báo qua JsonSerializable.")]
    private static IReadOnlyList<ConfigSectionSchema> BuildSections()
    {
        var sections = new List<ConfigSectionSchema>();
        var chain = new List<PropertyInfo>();

        ScanSection(typeof(AppConfigSnapshot), chain, sections, jsonPrefix: string.Empty);

        return sections
            .OrderBy(s => s.Attribute.Order)
            .ThenBy(s => s.JsonPath, StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>
    /// Quét đệ quy các section (kể cả section lồng nhau).
    /// </summary>
    /// <param name="ownerType">Kiểu chứa các section con.</param>
    /// <param name="chain">Chuỗi property dẫn tới ownerType.</param>
    /// <param name="sections">Danh sách kết quả.</param>
    /// <param name="jsonPrefix">Tiền tố đường dẫn JSON.</param>
    [UnconditionalSuppressMessage("Trimming", "IL2072",
        Justification = "modelType là kiểu của property thuộc AppConfigSnapshot hoặc section con, đều khai báo qua JsonSerializable.")]
    [UnconditionalSuppressMessage("Trimming", "IL2075",
        Justification = "modelType đã được JsonSerializable nên các property công khai được giữ lại khi trim.")]
    private static void ScanSection(
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)]
        Type ownerType,
        List<PropertyInfo> chain,
        List<ConfigSectionSchema> sections,
        string jsonPrefix)
    {
        foreach (var property in ownerType.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            var modelType = GetSectionModelType(property);
            var sectionAttribute = modelType.GetCustomAttribute<ConfigSectionAttribute>();

            // Không phải section -> bỏ qua (VD: property kiểu string lạ).
            if (sectionAttribute is null)
            {
                continue;
            }

            var jsonKey = GetJsonKey(property);
            var jsonPath = jsonPrefix.Length == 0 ? jsonKey : $"{jsonPrefix}:{jsonKey}";

            chain.Add(property);
            var section = new ConfigSectionSchema(sectionAttribute, modelType, chain.ToArray(), jsonPath);

            var fields = new List<ConfigFieldSchema>();

            foreach (var fieldProperty in modelType.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                var fieldAttribute = fieldProperty.GetCustomAttribute<ConfigFieldAttribute>();
                if (fieldAttribute is null)
                {
                    continue;
                }

                var fieldKey = GetJsonKey(fieldProperty);
                fields.Add(new ConfigFieldSchema(
                    section,
                    fieldProperty,
                    fieldAttribute,
                    $"{jsonPath}:{fieldKey}",
                    fieldKey,
                    jsonPath));
            }

            section.Fields = fields
                .OrderBy(f => f.Attribute.Order)
                .ThenBy(f => f.JsonKey, StringComparer.Ordinal)
                .ToArray();

            sections.Add(section);

            // Section lồng nhau (VD: Logging -> Sqlite / History).
            ScanSection(modelType, chain, sections, jsonPath);

            chain.RemoveAt(chain.Count - 1);
        }
    }

    /// <summary>
    /// Lấy kiểu model của một property section, kèm annotation AOT.
    /// </summary>
    /// <param name="property">Property cần lấy kiểu.</param>
    /// <returns>Kiểu model (đã được annotate cho trình cắt bỏ).</returns>
    [return: DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties
        | DynamicallyAccessedMemberTypes.PublicParameterlessConstructor)]
    [UnconditionalSuppressMessage("Trimming", "IL2073",
        Justification = "Property đến từ AppConfigSnapshot / section con — đều khai báo qua JsonSerializable nên không bị trim.")]
    private static Type GetSectionModelType(PropertyInfo property) => property.PropertyType;

    /// <summary>
    /// Lấy tên key JSON của một property (ưu tiên JsonPropertyName).
    /// </summary>
    /// <param name="property">Property cần lấy key.</param>
    /// <returns>Tên key trong file JSON.</returns>
    public static string GetJsonKey(PropertyInfo property)
    {
        var jsonName = property.GetCustomAttribute<JsonPropertyNameAttribute>();
        return jsonName?.Name ?? property.Name;
    }
}
