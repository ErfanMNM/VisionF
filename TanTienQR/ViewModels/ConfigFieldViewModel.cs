using System;
using System.Collections.Generic;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using TanTienQR.Services.Config;

namespace TanTienQR.ViewModels;

/// <summary>
/// ViewModel của MỘT trường cấu hình (được sinh động từ schema).
///
/// Giữ 2 giá trị:
///  - <see cref="Value"/>        — giá trị người dùng đang nhập (object?).
///  - <see cref="OriginalValue"/> — giá trị lúc nạp form (mốc so sánh dirty).
///
/// Mọi thay đổi đều báo qua <see cref="ValueChanged"/> để ViewModel cha
/// tính lại trạng thái "chưa lưu" / "cần restart".
///
/// Lớp này KHÔNG sealed: các lớp vỏ (TextField / NumberField / ...) kế thừa
/// để Avalonia 12 chọn đúng DataTemplate theo kiểu (Avalonia 12 đã bỏ
/// <c>DataTemplateSelector</c>).
/// </summary>
public partial class ConfigFieldViewModel : ObservableObject
{
    private readonly ConfigFieldSchema _schema;

    /// <summary>Giá trị đang nhập (object theo kiểu property gốc).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TextValue))]
    [NotifyPropertyChangedFor(nameof(IsDirty))]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private object? _value;

    /// <summary>Giá trị lúc nạp form (mốc so sánh).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDirty))]
    private object? _originalValue;

    /// <summary>Thông báo lỗi hiển thị dưới ô nhập (null = không lỗi).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private string? _error;

    /// <summary>Cờ nội bộ: đang nạp dữ liệu, không báo thay đổi ra ngoài.</summary>
    private bool _suppressChangeNotification;

    public ConfigFieldViewModel(ConfigFieldSchema schema, object? value)
    {
        _schema = schema;
        _value = value;
        _originalValue = value;
    }

    /// <summary>Sự kiện khi giá trị thay đổi (dùng để ViewModel cha cập nhật trạng thái).</summary>
    public event EventHandler? ValueChanged;

    /// <summary>Schema của trường này.</summary>
    public ConfigFieldSchema Schema => _schema;

    /// <summary>Nhãn hiển thị.</summary>
    public string Label => _schema.Label;

    /// <summary>Đường dẫn JSON (VD: "Camera:Ip").</summary>
    public string JsonPath => _schema.JsonPath;

    /// <summary>Loại control.</summary>
    public ConfigControlKind Control => _schema.Control;

    /// <summary>Danh sách lựa chọn cho ComboBox.</summary>
    public IReadOnlyList<string> Options => _schema.Options;

    /// <summary>Chữ gợi ý trong ô nhập.</summary>
    public string Placeholder => _schema.Placeholder;

    /// <summary>Gợi ý hỗ trợ.</summary>
    public string HelpText => _schema.HelpText;

    /// <summary>True nếu thay đổi cần khởi động lại ứng dụng.</summary>
    public bool RequiresRestart => _schema.RequiresRestart;

    /// <summary>Giá trị nhỏ nhất (cho NumericUpDown).</summary>
    public double Minimum => _schema.Min;

    /// <summary>Giá trị lớn nhất (cho NumericUpDown).</summary>
    public double Maximum => _schema.Max;

    /// <summary>Bước tăng/giảm (cho NumericUpDown).</summary>
    public double Increment => _schema.Increment;

    /// <summary>True nếu trường là ô nhập số.</summary>
    public bool IsNumeric => _schema.IsNumeric;

    /// <summary>True nếu trường là hộp tick.</summary>
    public bool IsBoolean => _schema.Control == ConfigControlKind.Bool;

    /// <summary>True nếu trường là ComboBox.</summary>
    public bool IsCombo => _schema.Control == ConfigControlKind.Combo;

    /// <summary>True nếu trường là ô nhập có che ký tự.</summary>
    public bool IsPassword => _schema.Control == ConfigControlKind.Password;

    /// <summary>True nếu trường là ô nhập đường dẫn.</summary>
    public bool IsPath => _schema.Control == ConfigControlKind.Path;

    /// <summary>Giá trị dạng chuỗi (dùng cho TextBox / ComboBox).</summary>
    public string TextValue
    {
        get => Value switch
        {
            null => string.Empty,
            string s => s,
            bool b => b ? "true" : "false",
            IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
            _ => Value.ToString() ?? string.Empty,
        };
        set
        {
            if (_schema.Control == ConfigControlKind.Bool)
            {
                Value = ParseBool(value);
                return;
            }

            Value = value;
        }
    }

    /// <summary>True nếu giá trị hiện tại khác giá trị gốc.</summary>
    public bool IsDirty => !ValuesEqual(Value, OriginalValue);

    /// <summary>True nếu trường đang có lỗi.</summary>
    public bool HasError => !string.IsNullOrWhiteSpace(Error);

    /// <summary>
    /// Gán giá trị mới từ code (nạp form / reset) mà không báo thay đổi ra ngoài.
    /// </summary>
    /// <param name="value">Giá trị mới.</param>
    /// <param name="resetOriginal">True nếu đồng thời cập nhật giá trị gốc.</param>
    public void SetValueSilently(object? value, bool resetOriginal)
    {
        _suppressChangeNotification = true;
        try
        {
            Value = value;
            if (resetOriginal)
            {
                OriginalValue = value;
            }
        }
        finally
        {
            _suppressChangeNotification = false;
        }
    }

    /// <summary>
    /// Đặt thông báo lỗi cho trường (dùng sau khi validate).
    /// </summary>
    /// <param name="message">Nội dung lỗi, null để xoá lỗi.</param>
    public void SetError(string? message) => Error = message;

    /// <summary>
    /// Chuyển đổi giá trị string từ UI thành đúng kiểu của property gốc.
    /// </summary>
    /// <param name="text">Chuỗi người dùng nhập.</param>
    /// <returns>Giá trị đã chuyển đổi (null nếu không hợp lệ).</returns>
    public object? ParseText(string? text)
    {
        if (!_schema.IsNumeric)
        {
            return text;
        }

        var type = _schema.NumericType ?? typeof(int);

        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        try
        {
            return Convert.ChangeType(text.Trim(), type, CultureInfo.InvariantCulture);
        }
        catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException)
        {
            return null;
        }
    }

    partial void OnValueChanged(object? value)
    {
        if (_suppressChangeNotification)
        {
            return;
        }

        ValueChanged?.Invoke(this, EventArgs.Empty);    }

    private static bool ParseBool(string? value) =>
        bool.TryParse(value, out var parsed) && parsed;

    /// <summary>
    /// So sánh 2 giá trị có kiểm tra chuỗi (bỏ khoảng trắng thừa).
    /// </summary>
    /// <param name="a">Giá trị A.</param>
    /// <param name="b">Giá trị B.</param>
    /// <returns>True nếu tương đương.</returns>
    private static bool ValuesEqual(object? a, object? b)
    {
        if (a is null && b is null)
        {
            return true;
        }

        if (a is null || b is null)
        {
            return false;
        }

        if (a is string sa && b is string sb)
        {
            return string.Equals(sa.Trim(), sb.Trim(), StringComparison.Ordinal);
        }

        return a.Equals(b);
    }
}

/// <summary>
/// ViewModel của MỘT section (nhóm) cấu hình.
/// </summary>
    public sealed partial class ConfigSectionViewModel : ObservableObject
{
    public ConfigSectionViewModel(ConfigSectionSchema schema)
    {
        Schema = schema;
        Title = schema.Title;
        Description = schema.Description;
        Icon = schema.Icon;
        Order = schema.Order;
        Column = schema.Column;
        Key = schema.Key;
    }

    /// <summary>Schema của section.</summary>
    public ConfigSectionSchema Schema { get; }

    /// <summary>Tiêu đề card.</summary>
    public string Title { get; }

    /// <summary>Mô tả ngắn.</summary>
    public string Description { get; }

    /// <summary>Tên icon (không có prefix "Icon.").</summary>
    public string Icon { get; }

    /// <summary>Thứ tự hiển thị.</summary>
    public int Order { get; }

    /// <summary>Cột hiển thị (0 = trái, 1 = phải).</summary>
    public int Column { get; }

    /// <summary>Key gốc trong JSON.</summary>
    public string Key { get; }

    /// <summary>True nếu section có mô tả để hiện.</summary>
    public bool HasDescription => !string.IsNullOrWhiteSpace(Description);

    /// <summary>True nếu section có icon để hiện.</summary>
    public bool HasIcon => !string.IsNullOrWhiteSpace(Icon) && IconGeometry is not null;

    /// <summary>True nếu section này cần hiện ghi chú D12/D16 (section PLC).</summary>
    public bool ShowMemoryNote => string.Equals(Key, "Plc", StringComparison.Ordinal);

    /// <summary>
    /// Geometry của icon (VD: Icon.Camera). Trả về null nếu không tìm thấy.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasIcon))]
    private Avalonia.Media.Geometry? _iconGeometry;

    /// <summary>
    /// Thử nạp lại geometry của icon từ ResourceDictionary.
    ///
    /// Icon là static resource nên thường đã sẵn sàng khi View được dựng;
    /// hàm này cho phép gọi lại sau khi ứng dụng nạp xong resource
    /// (tránh trường hợp icon bị trống lúc bố cục dựng lần đầu).
    /// </summary>
    public void RefreshIcon()
    {
        if (string.IsNullOrWhiteSpace(Icon))
        {
            IconGeometry = null;
            return;
        }

        var app = Avalonia.Application.Current;
        if (app is null)
        {
            return;
        }

        try
        {
            if (app.TryGetResource("Icon." + Icon, app.RequestedThemeVariant, out var resource)
                && resource is Avalonia.Media.Geometry geometry)
            {
                IconGeometry = geometry;
            }
        }
        catch (Exception)
        {
            // Resource chưa nạp xong -> bỏ qua (icon không bắt buộc).
        }
    }

    /// <summary>Các trường thuộc section.</summary>
    public System.Collections.ObjectModel.ObservableCollection<ConfigFieldViewModel> Fields { get; } = new();
}
