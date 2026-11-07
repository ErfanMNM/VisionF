using ClassLibraryMT.Plc;
using CommunityToolkit.Mvvm.ComponentModel;

namespace TanTienQR.ViewModels;

/// <summary>
/// Dòng tham số PLC đang soạn (chưa lưu) trong bảng.
///
/// Dùng cho thao tác "nhấn +" để thêm nhanh một dòng mà không cần mở hộp thoại.
/// Mỗi dòng có thể gọn (Tên | Giá trị | Nhập) và xổ ra để sửa địa chỉ / kiểu / R-W.
/// </summary>
internal sealed partial class NewParamRowViewModel : ObservableObject
{
    /// <summary>Tên tham số (bắt buộc).</summary>
    [ObservableProperty]
    public partial string Name { get; set; }

    /// <summary>Địa chỉ ô nhớ (bắt buộc): "D12", "DM12" hoặc "D12.05".</summary>
    [ObservableProperty]
    public partial string Address { get; set; }

    /// <summary>Kiểu dữ liệu.</summary>
    [ObservableProperty]
    public partial PlcValueType ValueType { get; set; }

    /// <summary>Chế độ truy cập.</summary>
    [ObservableProperty]
    public partial PlcAccessMode AccessMode { get; set; }

    /// <summary>Mô tả (không bắt buộc).</summary>
    [ObservableProperty]
    public partial string Description { get; set; }

    /// <summary>True khi dòng đang được mở rộng (xổ chi tiết).</summary>
    [ObservableProperty]
    public partial bool IsExpanded { get; set; }

    /// <summary>Thông báo trạng thái của dòng (lỗi / thành công).</summary>
    [ObservableProperty]
    public partial string StatusText { get; set; }

    /// <summary>True khi thông báo là lỗi.</summary>
    [ObservableProperty]
    public partial bool IsError { get; set; }

    /// <summary>True khi đang lưu (disable nút).</summary>
    [ObservableProperty]
    public partial bool IsSaving { get; set; }

    /// <summary>Các kiểu dữ liệu có thể chọn.</summary>
    public IReadOnlyList<PlcValueType> AvailableValueTypes => PlcEnumNames.AllValueTypes;

    /// <summary>Các chế độ truy cập có thể chọn.</summary>
    public IReadOnlyList<PlcAccessMode> AvailableAccessModes => PlcEnumNames.AllAccessModes;

    /// <summary>Tên hiển thị kiểu dữ liệu.</summary>
    public string ValueTypeText => PlcEnumNames.ToDisplay(ValueType);

    /// <summary>Tên hiển thị chế độ truy cập.</summary>
    public string AccessModeText => PlcEnumNames.ToDisplay(AccessMode);

    /// <summary>True khi dòng đã có đủ thông tin bắt buộc để lưu.</summary>
    public bool CanSave =>
        !IsSaving
        && !string.IsNullOrWhiteSpace(Name)
        && !string.IsNullOrWhiteSpace(Address);

    /// <summary>True khi dòng đang có thông báo trạng thái.</summary>
    public bool HasStatus => !string.IsNullOrWhiteSpace(StatusText);

    partial void OnValueTypeChanged(PlcValueType value) => OnPropertyChanged(nameof(ValueTypeText));

    partial void OnAccessModeChanged(PlcAccessMode value) => OnPropertyChanged(nameof(AccessModeText));

    partial void OnNameChanged(string value) => OnPropertyChanged(nameof(CanSave));

    partial void OnAddressChanged(string value) => OnPropertyChanged(nameof(CanSave));

    partial void OnIsSavingChanged(bool value) => OnPropertyChanged(nameof(CanSave));

    partial void OnStatusTextChanged(string value) => OnPropertyChanged(nameof(HasStatus));

    /// <summary>
    /// Bật/tắt mở rộng dòng (xổ chi tiết).
    /// </summary>
    public void ToggleExpanded() => IsExpanded = !IsExpanded;

    /// <summary>
    /// Dựng tham số để lưu.
    /// </summary>
    public PlcParameter ToParameter(int sortOrder) => new()
    {
        Id = 0,
        Name = Name.Trim(),
        Address = Address.Trim(),
        ValueType = ValueType,
        AccessMode = AccessMode,
        Description = string.IsNullOrWhiteSpace(Description) ? null : Description.Trim(),
        SortOrder = sortOrder,
    };
}
