using ClassLibraryMT.Plc;
using CommunityToolkit.Mvvm.ComponentModel;

namespace TanTienQR.ViewModels;

/// <summary>
/// Một dòng trong bảng tham số PLC tùy chỉnh.
///
/// Mỗi dòng hiển thị: tên, ô nhớ, kiểu dữ liệu, chế độ Read/Write,
/// giá trị hiện tại (đọc từ PLC) và giá trị đang chỉnh (dùng cho recipe).
/// </summary>
internal sealed partial class PlcParamRowViewModel : ObservableObject
{
    private string _currentValue = string.Empty;
    private string _inputValue = string.Empty;
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

    /// <summary>Giá trị hiện tại đọc từ PLC.</summary>
    public string CurrentValue
    {
        get => _currentValue;
        set => SetProperty(ref _currentValue, value);
    }

    /// <summary>Giá trị người dùng đang nhập (dùng cho recipe / ghi tay).</summary>
    public string InputValue
    {
        get => _inputValue;
        set => SetProperty(ref _inputValue, value);
    }

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

    /// <summary>True khi dòng đang được mở rộng để xem chi tiết (ô nhớ / kiểu / R-W).</summary>
    public bool IsExpanded
    {
        get => _isExpanded;
        set => SetProperty(ref _isExpanded, value);
    }

    /// <summary>
    /// Bật/tắt mở rộng dòng để xem chi tiết.
    /// </summary>
    public void ToggleExpanded() => IsExpanded = !IsExpanded;
}
