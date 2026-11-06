namespace ClassLibraryMT.Plc;

/// <summary>
/// Che do truy cap cua mot tham so PLC (o nho vung D).
///
/// Luu trong database duoi dang <c>int</c> de sua enum sau nay khong pha du lieu.
/// </summary>
public enum PlcAccessMode
{
    /// <summary>Chi doc duoc, khong ghi xuong PLC.</summary>
    Read = 0,

    /// <summary>Chi ghi duoc, khong doc gia tri hien tai.</summary>
    Write = 1,

    /// <summary>Doc va ghi deu duoc.</summary>
    ReadWrite = 2,
}

/// <summary>
/// Kieu du lieu cua gia tri trong o nho PLC.
///
/// Chi giu cac kieu an toan voi API <c>OmronPlc</c> hien co.
/// </summary>
public enum PlcValueType
{
    /// <summary>So nguyen 16 bit (1 word) — <c>short</c>.</summary>
    Int16 = 0,

    /// <summary>So nguyen 32 bit (2 word) — <c>int</c>.</summary>
    Int32 = 1,

    /// <summary>So thuc 32 bit (2 word) — <c>float</c>.</summary>
    Float = 2,

    /// <summary>Bit trong word — dia chi dang <c>D12.05</c>.</summary>
    Bool = 3,
}

/// <summary>
/// Mot tham so PLC do nguoi dung tu dinh nghia (Name / O nho / loai Read-Write).
/// </summary>
public sealed class PlcParameter
{
    /// <summary>Khoa chinh.</summary>
    public long Id { get; set; }

    /// <summary>Ten tham so, duy nhat — vi du "Delay Reject".</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Dia chi o nho trong vung D — vi du "D12" hoac "D12.05".</summary>
    public string Address { get; set; } = string.Empty;

    /// <summary>Kieu du lieu cua gia tri.</summary>
    public PlcValueType ValueType { get; set; }

    /// <summary>Che do truy cap (chi doc / chi ghi / doc va ghi).</summary>
    public PlcAccessMode AccessMode { get; set; }

    /// <summary>Mo ta tu chon (khong bat buoc).</summary>
    public string? Description { get; set; }

    /// <summary>Thu tu sap xep trong bang.</summary>
    public int SortOrder { get; set; }

    /// <summary>Thoi diem cap nhat gan nhat (UTC).</summary>
    public DateTime UpdatedUtc { get; set; }

    /// <summary>True neu tham so nay duoc phep doc.</summary>
    public bool CanRead =>
        AccessMode is PlcAccessMode.Read or PlcAccessMode.ReadWrite;

    /// <summary>True neu tham so nay duoc phep ghi.</summary>
    public bool CanWrite =>
        AccessMode is PlcAccessMode.Write or PlcAccessMode.ReadWrite;
}

/// <summary>
/// Mot bộ giá trị tham so PLC (recipe) để lưu và áp dụng nhanh cho máy.
/// </summary>
public sealed class PlcRecipe
{
    /// <summary>Khoa chinh.</summary>
    public long Id { get; set; }

    /// <summary>Ten recipe, duy nhat.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>True neu la recipe mac dinh (khong duoc xoa).</summary>
    public bool IsDefault { get; set; }

    /// <summary>Thoi diem tao (UTC).</summary>
    public DateTime CreatedUtc { get; set; }

    /// <summary>Thoi diem cap nhat gan nhat (UTC).</summary>
    public DateTime UpdatedUtc { get; set; }
}

/// <summary>
/// Ket qua cua mot thao tac tra ve (them / sua / xoa / ap dung).
/// </summary>
/// <param name="Ok">True neu thao tac thanh cong.</param>
/// <param name="Message">Thong diep mo ta ket qua.</param>
public readonly record struct Result(bool Ok, string Message)
{
    /// <summary>Tao ket qua thanh cong.</summary>
    public static Result Success(string message = "OK") => new(true, message);

    /// <summary>Tao ket qua that bai.</summary>
    public static Result Fail(string message) => new(false, message);
}

/// <summary>
/// Ket qua doc mot o nho PLC.
/// </summary>
public sealed class PlcReadResult
{
    /// <summary>True neu doc thanh cong.</summary>
    public bool IsSuccess { get; init; }

    /// <summary>Thong diep mo ta ket qua.</summary>
    public string Message { get; init; } = string.Empty;

    /// <summary>Gia tri doc duoc (chuoi de hien thi).</summary>
    public string? Value { get; init; }

    /// <summary>Ma loi FINS (neu co).</summary>
    public string ErrorCode { get; init; } = string.Empty;

    /// <summary>Thoi gian xu ly lenh.</summary>
    public TimeSpan ProcessTime { get; init; }

    /// <summary>Tao ket qua thanh cong.</summary>
    public static PlcReadResult Ok(string value, string message = "OK", TimeSpan processTime = default) =>
        new() { IsSuccess = true, Value = value, Message = message, ProcessTime = processTime };

    /// <summary>Tao ket qua that bai.</summary>
    public static PlcReadResult Fail(string errorCode, string message, TimeSpan processTime = default) =>
        new() { IsSuccess = false, ErrorCode = errorCode, Message = message, ProcessTime = processTime };
}

/// <summary>
/// Ket qua ghi mot o nho PLC.
/// </summary>
public sealed class PlcWriteResult
{
    /// <summary>True neu ghi thanh cong.</summary>
    public bool IsSuccess { get; init; }

    /// <summary>Thong diep mo ta ket qua.</summary>
    public string Message { get; init; } = string.Empty;

    /// <summary>Gia tri da ghi.</summary>
    public string? Value { get; init; }

    /// <summary>Ma loi FINS (neu co).</summary>
    public string ErrorCode { get; init; } = string.Empty;

    /// <summary>Thoi gian xu ly lenh.</summary>
    public TimeSpan ProcessTime { get; init; }

    /// <summary>Tao ket qua thanh cong.</summary>
    public static PlcWriteResult Ok(string value, string message = "OK", TimeSpan processTime = default) =>
        new() { IsSuccess = true, Value = value, Message = message, ProcessTime = processTime };

    /// <summary>Tao ket qua that bai.</summary>
    public static PlcWriteResult Fail(string errorCode, string message, TimeSpan processTime = default) =>
        new() { IsSuccess = false, ErrorCode = errorCode, Message = message, ProcessTime = processTime };
}

/// <summary>
/// Ten hien thi cua cac enum, dung cho UI va nhat ky.
/// </summary>
public static class PlcEnumNames
{
    /// <summary>Ten hien thi cua che do truy cap.</summary>
    public static string ToDisplay(PlcAccessMode mode) => mode switch
    {
        PlcAccessMode.Read => "Chỉ đọc",
        PlcAccessMode.Write => "Chỉ ghi",
        PlcAccessMode.ReadWrite => "Đọc / Ghi",
        _ => "—",
    };

    /// <summary>Ten hien thi cua kieu du lieu.</summary>
    public static string ToDisplay(PlcValueType type) => type switch
    {
        PlcValueType.Int16 => "Int16",
        PlcValueType.Int32 => "Int32",
        PlcValueType.Float => "Float",
        PlcValueType.Bool => "Bool (bit)",
        _ => "—",
    };

    /// <summary>Tat ca kieu du lieu (dung cho ComboBox).</summary>
    public static IReadOnlyList<PlcValueType> AllValueTypes { get; } =
    [
        PlcValueType.Int16,
        PlcValueType.Int32,
        PlcValueType.Float,
        PlcValueType.Bool,
    ];

    /// <summary>Tat ca che do truy cap (dung cho ComboBox).</summary>
    public static IReadOnlyList<PlcAccessMode> AllAccessModes { get; } =
    [
        PlcAccessMode.Read,
        PlcAccessMode.Write,
        PlcAccessMode.ReadWrite,
    ];
}
