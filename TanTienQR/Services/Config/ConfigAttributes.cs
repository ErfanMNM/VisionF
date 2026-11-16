using System;

namespace TanTienQR.Services.Config;

/// <summary>
/// Loại control hiển thị cho một trường cấu hình.
///
/// Dùng để <c>ConfigFieldTemplateSelector</c> chọn đúng template XAML
/// (TextBox / NumericUpDown / CheckBox / ComboBox / PasswordBox).
/// </summary>
public enum ConfigControlKind
{
    /// <summary>Tự suy luận từ kiểu dữ liệu của property.</summary>
    Auto = 0,

    /// <summary>Ô nhập chữ thường.</summary>
    Text = 1,

    /// <summary>Ô nhập số (NumericUpDown).</summary>
    Number = 2,

    /// <summary>Hộp tick (CheckBox).</summary>
    Bool = 3,

    /// <summary>Ô nhập có che ký tự (PasswordChar).</summary>
    Password = 4,

    /// <summary>Danh sách chọn (ComboBox).</summary>
    Combo = 5,

    /// <summary>Ô nhập đường dẫn tập tin / thư mục.</summary>
    Path = 6,
}

/// <summary>
/// Đánh dấu một class (section) của cấu hình.
///
/// Gắn attribute này lên class con của <c>AppConfigSnapshot</c> để
/// <see cref="ConfigSchemaProvider"/> biết cần hiển thị section nào,
/// tiêu đề, thứ tự, icon và mô tả.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class ConfigSectionAttribute : Attribute
{
    /// <summary>
    /// Khởi tạo section với key tương ứng với key JSON gốc.
    /// </summary>
    /// <param name="key">Key gốc trong appsettings.json (VD: "Camera").</param>
    public ConfigSectionAttribute(string key)
    {
        Key = key ?? throw new ArgumentNullException(nameof(key));
    }

    /// <summary>
    /// Khởi tạo section rỗng — dùng khi gán Key qua named argument.
    /// </summary>
    public ConfigSectionAttribute()
    {
        Key = string.Empty;
    }

    /// <summary>Key gốc trong appsettings.json (VD: "Camera").</summary>
    public string Key { get; set; }

    /// <summary>Tiêu đề hiển thị trên card (VD: "CAMERA · ĐẦU ĐỌC MÃ").</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>Thứ tự hiển thị (nhỏ trước).</summary>
    public int Order { get; set; }

    /// <summary>Tên resource của icon (VD: "Camera" -> Icon.Camera).</summary>
    public string Icon { get; set; } = string.Empty;

    /// <summary>Mô tả ngắn hiển thị dưới tiêu đề card.</summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>
    /// Cột hiển thị trong lưới 2 cột: 0 = cột trái, 1 = cột phải.
    /// -1 = tự động xếp theo thứ tự.
    /// </summary>
    public int Column { get; set; } = -1;
}

/// <summary>
/// Đánh dấu một property của section là một trường cấu hình hiển thị được.
///
/// Mọi thuộc tính của form Cấu hình đều được mô tả bằng attribute này,
/// nhờ đó form được sinh động từ schema thay vì gán cứng trong XAML.
///
/// Ví dụ:
/// <code>
/// [ConfigField("IP camera", Order = 1, Min = 1, Max = 65535)]
/// public int Port { get; set; } = 9000;
/// </code>
/// </summary>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = false, Inherited = false)]
public sealed class ConfigFieldAttribute : Attribute
{
    /// <summary>
    /// Khởi tạo trường cấu hình với nhãn hiển thị.
    /// </summary>
    /// <param name="label">Nhãn hiển thị cạnh ô nhập.</param>
    public ConfigFieldAttribute(string label)
    {
        Label = label ?? throw new ArgumentNullException(nameof(label));
    }

    /// <summary>Nhãn hiển thị cạnh ô nhập.</summary>
    public string Label { get; }

    /// <summary>Thứ tự hiển thị trong section (nhỏ trước).</summary>
    public int Order { get; set; }

    /// <summary>Loại control cần dùng (mặc định tự suy luận từ kiểu dữ liệu).</summary>
    public ConfigControlKind Control { get; set; } = ConfigControlKind.Auto;

    /// <summary>Giá trị nhỏ nhất (chỉ áp dụng cho control Number).</summary>
    public double Min { get; set; } = double.MinValue;

    /// <summary>Giá trị lớn nhất (chỉ áp dụng cho control Number).</summary>
    public double Max { get; set; } = double.MaxValue;

    /// <summary>Bước tăng/giảm (chỉ áp dụng cho control Number).</summary>
    public double Increment { get; set; } = 1;

    /// <summary>Chữ gợi ý hiển thị trong ô nhập (VD: "VD: 192.168.1.50").</summary>
    public string Placeholder { get; set; } = string.Empty;

    /// <summary>
    /// Danh sách lựa chọn cho ComboBox. Nếu để trống, control sẽ được
    /// suy luận thành Combo khi kiểu dữ liệu là string.
    /// </summary>
    public string[] Options { get; set; } = Array.Empty<string>();

    /// <summary>
    /// True nếu thay đổi trường này BẮT BUỘC phải khởi động lại ứng dụng
    /// (vì giá trị được bind một lần lúc app khởi động).
    /// </summary>
    public bool RequiresRestart { get; set; }

    /// <summary>Gợi ý hỗ trợ hiển thị dưới ô nhập.</summary>
    public string HelpText { get; set; } = string.Empty;

    /// <summary>Nhóm con trong section (VD: "Dữ liệu", "Bảo mật").</summary>
    public string Group { get; set; } = string.Empty;

    /// <summary>
    /// Kiểm tra dữ liệu bắt buộc: trường không được để trống.
    /// Mặc định: true cho string, false cho kiểu khác.
    /// </summary>
    public bool Required { get; set; } = true;

    /// <summary>
    /// Quy tắc kiểm tra bổ sung:
    /// "IpOrHost" = phải là IPv4 hoặc hostname hợp lệ,
    /// "Url" = phải là URL http/https hợp lệ,
    /// "Path" = không được rỗng.
    /// </summary>
    public string? Validation { get; set; }

    /// <summary>
    /// Đường dẫn tới một trường bool khác trong cùng cấu hình; nếu trường đó
    /// đang FALSE thì trường này được BỎ QUA khi validate.
    ///
    /// Dùng cho các trường chỉ có ý nghĩa khi một công tắc bật
    /// (VD: "Web:Url" chỉ kiểm tra khi "Web:Enabled" = true).
    /// </summary>
    public string? SkipWhenDisabledBy { get; set; }
}
