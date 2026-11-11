using System.Globalization;
using ClassLibraryMT.Plc;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace TanTienQR.ViewModels;

/// <summary>
/// Một dòng trong bảng tham số PLC tùy chỉnh.
///
/// Mỗi dòng hiển thị: tên, ô nhớ, kiểu dữ liệu, chế độ Read/Write,
/// giá trị hiện tại (đọc từ PLC) và giá trị đang chỉnh (dùng cho recipe).
///
/// Ô nhập giá trị phụ thuộc vào kiểu dữ liệu:
/// - <see cref="PlcValueType.Bool"/>: chọn ON/OFF (không gõ tay).
/// - <see cref="PlcValueType.Int16"/> / <see cref="PlcValueType.Int32"/>:
///   nhập số qua bàn phím số nhỏ kèm nút +/-.
/// </summary>
internal sealed partial class PlcParamRowViewModel : ObservableObject
{
    private string _currentValue = string.Empty;
    private string _statusText = string.Empty;
    private bool _isError;
    private bool _isExpanded;

    /// <summary>
    /// Khởi tạo dòng từ tham số trong database.
    /// </summary>
    /// <param name="parameter">Tham số gốc.</param>
    public PlcParamRowViewModel(PlcParameter parameter)
    {
        Parameter = parameter;

        Name = parameter.Name;
        Address = parameter.Address;
        ValueType = parameter.ValueType;
        AccessMode = parameter.AccessMode;
        Description = parameter.Description ?? string.Empty;
    }

    /// <summary>Tham số gốc trong database.</summary>
    public PlcParameter Parameter { get; }

    /// <summary>Id tham số.</summary>
    public long Id => Parameter.Id;

    /// <summary>Tên tham số.</summary>
    public string Name { get; }

    /// <summary>Địa chỉ ô nhớ.</summary>
    public string Address { get; }

    /// <summary>Kiểu dữ liệu.</summary>
    public PlcValueType ValueType { get; }

    /// <summary>Chế độ truy cập.</summary>
    public PlcAccessMode AccessMode { get; }

    /// <summary>Mô tả.</summary>
    public string Description { get; }

    /// <summary>Tên hiển thị kiểu dữ liệu.</summary>
    public string ValueTypeText => PlcEnumNames.ToDisplay(ValueType);

    /// <summary>Tên hiển thị chế độ truy cập.</summary>
    public string AccessModeText => PlcEnumNames.ToDisplay(AccessMode);

    /// <summary>True nếu tham số được phép đọc.</summary>
    public bool CanRead => Parameter.CanRead;

    /// <summary>True nếu tham số được phép ghi.</summary>
    public bool CanWrite => Parameter.CanWrite;

    /// <summary>
    /// True nếu dòng này đang được chọn (dùng cho thanh thao tác dưới bảng).
    /// </summary>
    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    /// <summary>Giá trị hiện tại đọc từ PLC.</summary>
    public string CurrentValue
    {
        get => _currentValue;
        set => SetProperty(ref _currentValue, value);
    }

    /// <summary>
    /// Giá trị người dùng đang nhập (dùng cho recipe / ghi tay).
    ///
    /// Với kiểu Bool, giá trị luôn ở dạng "true" / "false" — khớp với định
    /// dạng chuẩn hoá của <c>PlcSettingsService</c> khi ghi xuống PLC.
    /// </summary>
    [ObservableProperty]
    public partial string InputValue { get; set; } = string.Empty;

    /// <summary>Thông báo trạng thái của dòng (lỗi / thành công).</summary>
    public string StatusText
    {
        get => _statusText;
        set => SetProperty(ref _statusText, value);
    }

    /// <summary>True nếu thông báo là lỗi.</summary>
    public bool IsError
    {
        get => _isError;
        set => SetProperty(ref _isError, value);
    }

    /// <summary>True nếu dòng đang có giá trị (dùng để ẩn placeholder).</summary>
    public bool HasCurrentValue => !string.IsNullOrWhiteSpace(CurrentValue);

    /// <summary>True nếu ô nhập đang có nội dung.</summary>
    public bool HasInputValue => !string.IsNullOrWhiteSpace(InputValue);

    /// <summary>True khi dòng đang được mở rộng để xem chi tiết (ô nhớ / kiểu / R-W).</summary>
    public bool IsExpanded
    {
        get => _isExpanded;
        set => SetProperty(ref _isExpanded, value);
    }

    /// <summary>
    /// True nếu kiểu dữ liệu là Bool -> dùng bộ chọn ON/OFF thay vì ô nhập số.
    /// </summary>
    public bool IsBoolType => ValueType == PlcValueType.Bool;

    /// <summary>
    /// True nếu kiểu dữ liệu là số nguyên -> dùng ô nhập số + bàn phím số nhỏ.
    /// </summary>
    public bool IsNumericType => ValueType is PlcValueType.Int16 or PlcValueType.Int32;

    /// <summary>
    /// Giá trị Bool đang chọn (true / false) — nguồn cho bộ chọn ON/OFF.
    ///
    /// Gán giá trị này sẽ đồng bộ <see cref="InputValue"/> thành chuỗi
    /// "true" / "false" — đúng định dạng mà <c>PlcSettingsService</c> ghi xuống PLC.
    /// </summary>
    [ObservableProperty]
    public partial bool BoolValue { get; set; }

    /// <summary>
    /// Bấm nút ON -> đặt giá trị Bool là TRUE.
    ///
    /// Dùng lệnh KHÔNG có tham số (thay vì <c>IRelayCommand&lt;bool&gt;</c>) vì
    /// Avalonia không tự chuyển chuỗi "True" trong XAML thành <see cref="bool"/>,
    /// khiến <c>RelayCommand&lt;bool&gt;.CanExecute</c> ném ArgumentException lúc
    /// dựng cây điều khiển -> ứng dụng crash.
    /// </summary>
    [RelayCommand]
    public void SetBoolTrue() => ApplyBool(true);

    /// <summary>Bấm nút OFF -> đặt giá trị Bool là FALSE.</summary>
    [RelayCommand]
    public void SetBoolFalse() => ApplyBool(false);

    /// <summary>
    /// Áp dụng giá trị Bool mới và xoá thông báo trạng thái cũ trên dòng.
    /// </summary>
    /// <param name="value">True = ON, False = OFF.</param>
    private void ApplyBool(bool value)
    {
        BoolValue = value;

        StatusText = string.Empty;
        IsError = false;
    }

    /// <summary>
    /// True nếu đang chọn FALSE — dùng để tô nút OFF (nút ON dùng
    /// <see cref="BoolValue"/>).
    /// </summary>
    public bool IsBoolOff => !BoolValue;

    /// <summary>
    /// Giá trị số đang nhập (dùng cho bàn phím số nhỏ).
    ///
    /// Ô nhập rỗng được hiểu là 0. Giá trị luôn nằm trong khoang hợp lệ
    /// của kiểu dữ liệu (Short: -32768..32767, Int32: -2147483648..2147483647).
    /// </summary>
    public long NumericValue
    {
        get
        {
            if (!PlcValueValidator.TryParseInteger(InputValue, out var value))
            {
                return 0;
            }

            return ClampToType(value);
        }

        set
        {
            var clamped = ClampToType(value);
            var text = clamped.ToString(CultureInfo.InvariantCulture);

            if (!string.Equals(InputValue, text, StringComparison.Ordinal))
            {
                InputValue = text;
            }

            OnPropertyChanged();
        }
    }

    /// <summary>
    /// Bật/tắt mở rộng dòng để xem chi tiết.
    /// </summary>
    public void ToggleExpanded() => IsExpanded = !IsExpanded;

    /// <summary>
    /// Kiểm tra giá trị đang nhập có hợp lệ với kiểu dữ liệu của dòng không.
    /// </summary>
    /// <param name="error">Thông điệp lỗi nếu không hợp lệ.</param>
    /// <returns>True nếu hợp lệ.</returns>
    public bool ValidateInput(out string error) =>
        PlcValueValidator.TryValidate(ValueType, InputValue, out error);

    /// <summary>
    /// Chuẩn hoá giá trị đang nhập về đúng định dạng service ghi xuống PLC.
    /// </summary>
    /// <returns>Giá trị đã chuẩn hoá.</returns>
    public string NormalizedInputValue =>
        PlcValueValidator.Normalize(ValueType, InputValue);

    /// <summary>
    /// Giới hạn giá trị số trong khoang hợp lệ của kiểu dữ liệu.
    /// </summary>
    /// <param name="value">Giá trị cần giới hạn.</param>
    /// <returns>Giá trị đã nằm trong khoang hợp lệ.</returns>
    private long ClampToType(long value) => ValueType switch
    {
        PlcValueType.Int16 => Math.Clamp(value, PlcValueValidator.Int16Min, PlcValueValidator.Int16Max),
        PlcValueType.Int32 => Math.Clamp(value, PlcValueValidator.Int32Min, PlcValueValidator.Int32Max),
        _ => value,
    };

    /// <summary>
    /// Đổi giá trị Bool -> đồng bộ sang <see cref="InputValue"/> dưới dạng chuỗi.
    /// </summary>
    /// <param name="value">Giá trị mới.</param>
    partial void OnBoolValueChanged(bool value)
    {
        OnPropertyChanged(nameof(IsBoolOff));

        var text = value ? PlcValueValidator.BoolTrue : PlcValueValidator.BoolFalse;

        if (!string.Equals(InputValue, text, StringComparison.Ordinal))
        {
            InputValue = text;
        }
    }

    /// <summary>
    /// Đổi giá trị đang nhập -> đồng bộ ngược lại trạng thái Bool (nếu là kiểu Bool).
    /// </summary>
    /// <param name="value">Giá trị mới.</param>
    partial void OnInputValueChanged(string value)
    {
        OnPropertyChanged(nameof(HasInputValue));

        if (!IsBoolType)
        {
            return;
        }

        var flag = PlcValueValidator.TryParseBool(value, out var parsed) && parsed;

        if (BoolValue != flag)
        {
            BoolValue = flag;
        }
    }
}
