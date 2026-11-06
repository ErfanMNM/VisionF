using ClassLibraryMT.Plc;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace TanTienQR.ViewModels;

/// <summary>
/// ViewModel cho hộp thoại tạo / sửa tham số PLC tùy chỉnh.
/// </summary>
internal sealed partial class PlcParamEditViewModel : ViewModelBase
{
    private readonly IPlcSettingsService _plc;

    /// <summary>Tham số đang sửa (null = đang tạo mới).</summary>
    private readonly PlcParameter? _editing;

    /// <summary>
    /// Khởi tạo view model.
    /// </summary>
    /// <param name="plc">Service quản lý tham số PLC.</param>
    /// <param name="editing">Tham số đang sửa (null = tạo mới).</param>
    public PlcParamEditViewModel(IPlcSettingsService plc, PlcParameter? editing = null)
    {
        _plc = plc;
        _editing = editing;

        if (_editing is not null)
        {
            Name = _editing.Name;
            Address = _editing.Address;
            ValueType = _editing.ValueType;
            AccessMode = _editing.AccessMode;
            Description = _editing.Description ?? string.Empty;
            SortOrder = _editing.SortOrder;
        }
    }

    /// <summary>True nếu đang tạo tham số mới.</summary>
    public bool IsNew => _editing is null;

    /// <summary>
    /// Bàn phím ảo cho máy cảm ứng (singleton dùng chung toàn ứng dụng).
    /// </summary>
    public VirtualKeyboardViewModel? Keyboard => Controls.VirtualKeyboardHost.ViewModel;

    /// <summary>Tiêu đề hộp thoại.</summary>
    public string Title => IsNew ? "Thêm tham số PLC" : "Sửa tham số PLC";

    /// <summary>Tên tham số (bắt buộc, duy nhất).</summary>
    [ObservableProperty]
    public partial string Name { get; set; } = string.Empty;

    /// <summary>Địa chỉ ô nhớ (bắt buộc): "D12", "DM12" hoặc "D12.05".</summary>
    [ObservableProperty]
    public partial string Address { get; set; } = string.Empty;

    /// <summary>Kiểu dữ liệu.</summary>
    [ObservableProperty]
    public partial PlcValueType ValueType { get; set; } = PlcValueType.Int16;

    /// <summary>Chế độ truy cập.</summary>
    [ObservableProperty]
    public partial PlcAccessMode AccessMode { get; set; } = PlcAccessMode.ReadWrite;

    /// <summary>Mô tả (không bắt buộc).</summary>
    [ObservableProperty]
    public partial string Description { get; set; } = string.Empty;

    /// <summary>Thứ tự sắp xếp trong bảng.</summary>
    [ObservableProperty]
    public partial int SortOrder { get; set; }

    /// <summary>Thông báo lỗi.</summary>
    [ObservableProperty]
    public partial string ErrorMessage { get; set; } = string.Empty;

    /// <summary>True khi đang xử lý.</summary>
    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    /// <summary>Các kiểu dữ liệu có thể chọn.</summary>
    public IReadOnlyList<PlcValueType> AvailableValueTypes => PlcEnumNames.AllValueTypes;

    /// <summary>Các chế độ truy cập có thể chọn.</summary>
    public IReadOnlyList<PlcAccessMode> AvailableAccessModes => PlcEnumNames.AllAccessModes;

    /// <summary>True khi có thông báo lỗi.</summary>
    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);

    /// <summary>True khi nút lưu có thể bấm.</summary>
    public bool CanSubmit => !IsBusy;

    partial void OnErrorMessageChanged(string value) => OnPropertyChanged(nameof(HasError));

    partial void OnIsBusyChanged(bool value) => OnPropertyChanged(nameof(CanSubmit));

    /// <summary>
    /// Lưu tham số (tạo mới hoặc cập nhật).
    /// </summary>
    [RelayCommand]
    private async Task SaveAsync()
    {
        if (IsBusy)
        {
            return;
        }

        ErrorMessage = string.Empty;

        if (string.IsNullOrWhiteSpace(Name))
        {
            ErrorMessage = "Vui lòng nhập tên tham số.";
            return;
        }

        if (string.IsNullOrWhiteSpace(Address))
        {
            ErrorMessage = "Vui lòng nhập ô nhớ (ví dụ D12 hoặc D12.05).";
            return;
        }

        IsBusy = true;

        try
        {
            var parameter = new PlcParameter
            {
                Id = _editing?.Id ?? 0,
                Name = Name.Trim(),
                Address = Address.Trim(),
                ValueType = ValueType,
                AccessMode = AccessMode,
                Description = string.IsNullOrWhiteSpace(Description) ? null : Description.Trim(),
                SortOrder = SortOrder,
            };

            var result = await _plc.SaveParameterAsync(parameter).ConfigureAwait(true);

            if (!result.Ok)
            {
                ErrorMessage = result.Message;
                return;
            }

            OnSaved?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            ErrorMessage = "Lỗi khi lưu tham số: " + ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Sự kiện phát ra khi lưu thành công.</summary>
    public event EventHandler? OnSaved;
}
