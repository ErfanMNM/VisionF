using TanTienQR.Services.Config;

namespace TanTienQR.ViewModels;

/// <summary>
/// Kiểu "vỏ" cho trường cấu hình dạng TextBox.
///
/// Avalonia 12 đã bỏ <c>DataTemplateSelector</c>, nên để chọn đúng template
/// theo loại control, mỗi loại dùng một kiểu riêng. XAML khai báo
/// <c>DataTemplate DataType="vm:TextField"</c> và Avalonia tự chọn theo
/// kiểu thực tế của đối tượng.
///
/// Các lớp này kế thừa <see cref="ConfigFieldViewModel"/> nên binding 2 chiều
/// vẫn hoạt động bình thường.
/// </summary>
public sealed class TextField : ConfigFieldViewModel
{
    public TextField(ConfigFieldSchema schema, object? value)
        : base(schema, value)
    {
    }
}

/// <summary>Kiểu "vỏ" cho trường cấu hình dạng ô nhập số (NumericUpDown).</summary>
public sealed class NumberField : ConfigFieldViewModel
{
    public NumberField(ConfigFieldSchema schema, object? value)
        : base(schema, value)
    {
    }
}

/// <summary>Kiểu "vỏ" cho trường cấu hình dạng hộp tick (CheckBox).</summary>
public sealed class BoolField : ConfigFieldViewModel
{
    public BoolField(ConfigFieldSchema schema, object? value)
        : base(schema, value)
    {
    }
}

/// <summary>Kiểu "vỏ" cho trường cấu hình dạng danh sách chọn (ComboBox).</summary>
public sealed class ComboField : ConfigFieldViewModel
{
    public ComboField(ConfigFieldSchema schema, object? value)
        : base(schema, value)
    {
    }
}

/// <summary>Kiểu "vỏ" cho trường cấu hình dạng ô nhập mật khẩu.</summary>
public sealed class PasswordField : ConfigFieldViewModel
{
    public PasswordField(ConfigFieldSchema schema, object? value)
        : base(schema, value)
    {
    }
}
