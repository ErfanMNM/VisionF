using System.Globalization;
using ClassLibraryMT.Plc;
using CommunityToolkit.Mvvm.ComponentModel;

namespace TanTienQR.ViewModels;

/// <summary>
/// ViewModel cho bàn phím số nhỏ dùng trong bảng ô nhớ PLC.
///
/// Bàn phím này CHỈ dùng cho ô nhập giá trị kiểu Short / Int32. Nó thay thế
/// bàn phím ảo chữ toàn cục của ứng dụng, nên giữ rất gọn:
/// 4 hàng x 4 cột (0-9, ±, C, OK) — vừa đủ cho một ô nhập số.
///
/// Mọi thao tác đều ghi thẳng vào <see cref="InputValue"/> của dòng đang gõ
/// (qua <see cref="PlcParamRowViewModel.NumericValue"/>), giá trị luôn được
/// giới hạn trong khoảng hợp lệ của kiểu dữ liệu.
/// </summary>
internal sealed partial class PlcValueKeypadViewModel : ObservableObject
{
    private PlcParamRowViewModel? _row;

    /// <summary>
    /// Sự kiện báo "bàn phím đã đóng xong" — bắn ra sau khi giá trị được
    /// chốt thành công (nút OK) hoặc bị huỷ.
    ///
    /// Bàn phím số nằm TRONG panel "Ô NHỚ PLC" nên việc ẩn/hiện panel được
    /// quyết định bởi <c>PlcSettingsViewModel.IsKeypadOpen</c> (dựa trên
    /// <c>KeypadRow</c>), KHÔNG phải bởi <see cref="Row"/> của bàn phím.
    ///
    /// Vì vậy khi bàn phím tự đóng (nút OK), nó phải báo ngược lại cho
    /// ViewModel trang để ViewModel trang xoá <c>KeypadRow</c> — nếu không,
    /// panel bàn phím vẫn hiện (đây chính là lỗi "nhấn OK không đóng popup").
    /// </summary>
    public event EventHandler? Closed;

    /// <summary>
    /// Dòng đang gõ giá trị (null = bàn phím không hoạt động).
    /// </summary>
    public PlcParamRowViewModel? Row
    {
        get => _row;
        set
        {
            if (ReferenceEquals(_row, value))
            {
                return;
            }

            if (_row is not null)
            {
                _row.PropertyChanged -= OnRowPropertyChanged;
            }

            if (SetProperty(ref _row, value))
            {
                if (_row is not null)
                {
                    _row.PropertyChanged += OnRowPropertyChanged;
                }

                OnPropertyChanged(nameof(IsActive));
                OnPropertyChanged(nameof(DisplayValue));
                OnPropertyChanged(nameof(DisplayHint));
                OnPropertyChanged(nameof(CanIncrement));
                OnPropertyChanged(nameof(CanDecrement));
            }
        }
    }

    /// <summary>
    /// Dòng đang gõ đổi giá trị (người dùng gõ tay trong ô nhập) -> làm mới
    /// ô hiển thị và trạng thái nút +/-.
    /// </summary>
    /// <param name="sender">Dòng đang gõ.</param>
    /// <param name="e">Thông tin thay đổi.</param>
    private void OnRowPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(PlcParamRowViewModel.InputValue))
        {
            return;
        }

        OnPropertyChanged(nameof(DisplayValue));
        OnPropertyChanged(nameof(CanIncrement));
        OnPropertyChanged(nameof(CanDecrement));
    }

    /// <summary>True khi bàn phím đang gắn với một dòng.</summary>
    public bool IsActive => Row is not null;

    /// <summary>
    /// Giá trị hiển thị trên bàn phím (giá trị đang nhập của dòng).
    /// </summary>
    public string DisplayValue => Row?.InputValue ?? string.Empty;

    /// <summary>
    /// Gợi ý nhỏ trên bàn phím (tên tham số + kiểu dữ liệu).
    /// </summary>
    public string DisplayHint =>
        Row is null ? string.Empty : $"{Row.Name} · {Row.ValueTypeText}";

    /// <summary>
    /// True khi có thể tăng thêm 1 (chưa chạm trần của kiểu dữ liệu).
    /// </summary>
    public bool CanIncrement => Row is not null && Row.NumericValue < MaxValue;

    /// <summary>
    /// True khi có thể giảm thêm 1 (chưa chạm đáy của kiểu dữ liệu).
    /// </summary>
    public bool CanDecrement => Row is not null && Row.NumericValue > MinValue;

    /// <summary>Giá trị nhỏ nhất của kiểu dữ liệu đang gõ.</summary>
    private long MinValue => Row?.ValueType == PlcValueType.Int16
        ? PlcValueValidator.Int16Min
        : PlcValueValidator.Int32Min;

    /// <summary>Giá trị lớn nhất của kiểu dữ liệu đang gõ.</summary>
    private long MaxValue => Row?.ValueType == PlcValueType.Int16
        ? PlcValueValidator.Int16Max
        : PlcValueValidator.Int32Max;

    /// <summary>
    /// Nối thêm một chữ số vào giá trị đang nhập.
    /// </summary>
    /// <param name="digit">Chữ số (0-9).</param>
    public void InputDigit(string? digit)
    {
        var row = Row;
        if (row is null || string.IsNullOrEmpty(digit))
        {
            return;
        }

        foreach (var c in digit)
        {
            if (!char.IsAsciiDigit(c))
            {
                continue;
            }

            var current = row.InputValue.Trim();

            // Bỏ dấu "-" ở đầu khi nối thêm số -> tránh "-1" -> "-12".
            if (current.StartsWith('-'))
            {
                current = current[1..];
            }

            var next = current + c;

            // Giới hạn độ dài để không vượt quá số 10 chữ số của Int32.
            if (next.Length > 10)
            {
                next = next[..10];
            }

            row.InputValue = next;
        }

        ClearError(row);
        RefreshValue();
    }

    /// <summary>Xoá trắng giá trị đang nhập.</summary>
    public void Clear()
    {
        var row = Row;
        if (row is null)
        {
            return;
        }

        row.InputValue = string.Empty;
        ClearError(row);
        RefreshValue();
    }

    /// <summary>Đảo dấu giá trị đang nhập.</summary>
    public void ToggleSign()
    {
        var row = Row;
        if (row is null)
        {
            return;
        }

        var current = row.InputValue.Trim();
        if (current.Length == 0)
        {
            return;
        }

        row.InputValue = current.StartsWith('-') ? current[1..] : "-" + current;
        ClearError(row);
        RefreshValue();
    }

    /// <summary>Tăng giá trị hiện tại lên 1.</summary>
    public void Increment()
    {
        var row = Row;
        if (row is null)
        {
            return;
        }

        row.NumericValue = row.NumericValue + 1;
        ClearError(row);
        RefreshValue();
    }

    /// <summary>Giảm giá trị hiện tại đi 1.</summary>
    public void Decrement()
    {
        var row = Row;
        if (row is null)
        {
            return;
        }

        row.NumericValue = row.NumericValue - 1;
        ClearError(row);
        RefreshValue();
    }

    /// <summary>
    /// Chốt giá trị: nếu hợp lệ thì đóng bàn phím, ngược lại báo lỗi trên dòng.
    ///
    /// Khi chốt thành công, bàn phím báo <see cref="Closed"/> để ViewModel trang
    /// xoá <c>KeypadRow</c> — qua đó <c>IsKeypadOpen</c> = false và panel bàn
    /// phím thật sự biến mất khỏi màn hình.
    /// </summary>
    /// <returns>True nếu giá trị hợp lệ và bàn phím đã đóng.</returns>
    public bool Commit()
    {
        var row = Row;
        if (row is null)
        {
            return false;
        }

        if (!row.ValidateInput(out var error))
        {
            row.StatusText = error;
            row.IsError = true;
            return false;
        }

        row.InputValue = row.NormalizedInputValue;
        row.StatusText = string.Empty;
        row.IsError = false;

        Row = null;
        Closed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>Xoá thông báo lỗi trên dòng.</summary>
    /// <param name="row">Dòng cần xoá lỗi.</param>
    private static void ClearError(PlcParamRowViewModel row)
    {
        row.StatusText = string.Empty;
        row.IsError = false;
    }

    /// <summary>Cập nhật lại giá trị hiển thị + trạng thái nút +/-.</summary>
    private void RefreshValue()
    {
        OnPropertyChanged(nameof(DisplayValue));
        OnPropertyChanged(nameof(CanIncrement));
        OnPropertyChanged(nameof(CanDecrement));
    }

    /// <summary>
    /// Định dạng giá trị số để hiển thị (luôn dùng dấu chấm thập phân kiểu invariant).
    /// </summary>
    /// <param name="value">Giá trị cần định dạng.</param>
    /// <returns>Chuỗi hiển thị.</returns>
    public static string Format(long value) =>
        value.ToString(CultureInfo.InvariantCulture);
}
