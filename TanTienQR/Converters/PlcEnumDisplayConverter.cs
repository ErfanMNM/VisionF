using System;
using System.Globalization;
using Avalonia.Data;
using Avalonia.Data.Converters;
using ClassLibraryMT.Plc;

namespace TanTienQR.Converters;

/// <summary>
/// Chuyển kiểu dữ liệu / chế độ truy cập của PLC sang tên hiển thị.
///
/// ComboBox trong bảng ô nhớ và hộp thoại sửa cần hiện "Short" thay vì
/// "Int16", "Chỉ đọc" thay vì "Read"… nên dùng converter này thay vì để
/// ComboBox tự hiện tên enum.
///
/// Kiểu dữ liệu đọc được (<see cref="PlcValueType"/>) dùng
/// <see cref="PlcEnumNames.ToDisplay(PlcValueType)"/>; chế độ truy cập đọc
/// được (<see cref="PlcAccessMode"/>) dùng
/// <see cref="PlcEnumNames.ToDisplay(PlcAccessMode)"/>.
/// </summary>
public sealed class PlcEnumDisplayConverter : IValueConverter
{
    /// <summary>
    /// Chuyển giá trị enum sang chuỗi hiển thị.
    /// </summary>
    /// <param name="value">Giá trị cần hiển thị.</param>
    /// <param name="targetType">Kiểu đích (bỏ qua).</param>
    /// <param name="parameter">Tham số bổ sung (bỏ qua).</param>
    /// <param name="culture">Văn hoá (bỏ qua).</param>
    /// <returns>Chuỗi hiển thị tương ứng.</returns>
    public object? Convert(
        object? value,
        Type targetType,
        object? parameter,
        CultureInfo culture)
    {
        return value switch
        {
            PlcValueType type => PlcEnumNames.ToDisplay(type),
            PlcAccessMode mode => PlcEnumNames.ToDisplay(mode),
            _ => value?.ToString() ?? string.Empty,
        };
    }

    /// <summary>
    /// Không hỗ trợ chiều ngược (chỉ hiển thị).
    /// </summary>
    /// <param name="value">Giá trị nguồn.</param>
    /// <param name="targetType">Kiểu đích.</param>
    /// <param name="parameter">Tham số bổ sung.</param>
    /// <param name="culture">Văn hoá.</param>
    /// <returns>Luôn trả về <see cref="BindingOperations.DoNothing"/>.</returns>
    public object? ConvertBack(
        object? value,
        Type targetType,
        object? parameter,
        CultureInfo culture) =>
        BindingOperations.DoNothing;
}
