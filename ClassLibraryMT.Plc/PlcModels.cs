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

    /// <summary>
    /// Ten hien thi khi gan vao ComboBox / TextBlock.
    ///
    /// mac dinh <see cref="object.ToString"/> se tra ve ten lop day du
    /// ("ClassLibraryMT.Plc.PlcRecipe") -> phai override de hien thi
    /// ten recipe that su cho nguoi dung.
    /// </summary>
    public override string ToString() =>
        string.IsNullOrWhiteSpace(Name) ? $"Recipe #{Id}" : Name;
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
    /// <summary>Id cua tham so da doc (dung de khop ket qua voi danh sach).</summary>
    public long ParameterId { get; init; }

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
    public static PlcReadResult Ok(
        long parameterId,
        string value,
        string message = "OK",
        TimeSpan processTime = default) =>
        new()
        {
            ParameterId = parameterId,
            IsSuccess = true,
            Value = value,
            Message = message,
            ProcessTime = processTime,
        };

    /// <summary>Tao ket qua that bai.</summary>
    public static PlcReadResult Fail(
        long parameterId,
        string errorCode,
        string message,
        TimeSpan processTime = default) =>
        new()
        {
            ParameterId = parameterId,
            IsSuccess = false,
            ErrorCode = errorCode,
            Message = message,
            ProcessTime = processTime,
        };
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
        PlcValueType.Int16 => "Short",
        PlcValueType.Int32 => "Int32",
        PlcValueType.Float => "Float",
        PlcValueType.Bool => "Bool",
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

    /// <summary>
    /// Cac kieu du lieu duoc PHEP CHON tren giao dien (Bool / Short / Int32).
    ///
    /// Chi dung danh sach nay cho ComboBox chon kieu. Cac tham so da luu trong
    /// database voi kieu khac (vd <see cref="PlcValueType.Float"/>) van hien thi
    /// va doc/ghi duoc binh thuong — chi khong cho tao them o giao dien.
    ///
    /// Gia tri enum khong thay doi de giu tuong thich du lieu cu.
    /// </summary>
    public static IReadOnlyList<PlcValueType> SupportedValueTypes { get; } =
    [
        PlcValueType.Int16,
        PlcValueType.Int32,
        PlcValueType.Bool,
    ];

    /// <summary>
    /// True neu kieu du lieu duoc phep chon tren giao dien.
    /// </summary>
    /// <param name="type">Kieu du lieu can kiem tra.</param>
    /// <returns>True neu nam trong <see cref="SupportedValueTypes"/>.</returns>
    public static bool IsSupportedValueType(PlcValueType type) =>
        SupportedValueTypes.Contains(type);

    /// <summary>Tat ca che do truy cap (dung cho ComboBox).</summary>
    public static IReadOnlyList<PlcAccessMode> AllAccessModes { get; } =
    [
        PlcAccessMode.Read,
        PlcAccessMode.Write,
        PlcAccessMode.ReadWrite,
    ];
}

/// <summary>
/// Kiem tra / chuan hoa gia tri nhap theo kieu du lieu cua tham so PLC.
///
/// Dung chung cho ca UI (validate truoc khi ghi) va service (parse khi ghi)
/// de hai noi quy tac giong nhau — tranh truong hop UI cho qua nhung service
/// lai tu choi, hoac nguoc lai.
/// </summary>
public static class PlcValueValidator
{
    /// <summary>Gia tri chuoi cho Bool khi ON.</summary>
    public const string BoolTrue = "true";

    /// <summary>Gia tri chuoi cho Bool khi OFF.</summary>
    public const string BoolFalse = "false";

    /// <summary>
    /// Gia tri nho nhat cua kieu Short (Int16).
    /// </summary>
    public const int Int16Min = short.MinValue;

    /// <summary>
    /// Gia tri lon nhat cua kieu Short (Int16).
    /// </summary>
    public const int Int16Max = short.MaxValue;

    /// <summary>
    /// Gia tri nho nhat cua kieu Int32.
    /// </summary>
    public const int Int32Min = int.MinValue;

    /// <summary>
    /// Gia tri lon nhat cua kieu Int32.
    /// </summary>
    public const int Int32Max = int.MaxValue;

    /// <summary>
    /// Kiem tra gia tri nhap co hop le voi kieu du lieu hay khong.
    ///
    /// - Bool: chi chap nhan cac chuoi boolean thong thuong (true/false, 1/0, on/off).
    /// - Int16: so nguyen trong khoang [-32768, 32767].
    /// - Int32: so nguyen trong khoang [-2147483648, 2147483647].
    /// - Float: so thuc hop le (chi doc/ghi tham so cu trong DB).
    ///
    /// </summary>
    /// <param name="type">Kieu du lieu cua tham so.</param>
    /// <param name="raw">Chuoi gia tri do nguoi dung nhap.</param>
    /// <param name="error">Thong diep loi khi khong hop le.</param>
    /// <returns>True neu gia tri hop le.</returns>
    public static bool TryValidate(
        PlcValueType type,
        string? raw,
        out string error)
    {
        error = string.Empty;

        var text = raw?.Trim() ?? string.Empty;
        if (text.Length == 0)
        {
            error = "Gia tri khong duoc de trong.";
            return false;
        }

        switch (type)
        {
            case PlcValueType.Bool:
                if (!TryParseBool(text, out _))
                {
                    error = $"'{text}' khong phai gia tri boolean (true/false, 1/0, on/off).";
                    return false;
                }

                return true;

            case PlcValueType.Int16:
                if (!TryParseInteger(text, out var shortValue))
                {
                    error = $"'{text}' khong phai so nguyen 16 bit.";
                    return false;
                }

                if (shortValue < Int16Min || shortValue > Int16Max)
                {
                    error = $"Gia tri {shortValue} ngoai khoang cho phep cua Short ({Int16Min}..{Int16Max}).";
                    return false;
                }

                return true;

            case PlcValueType.Int32:
                if (!TryParseInteger(text, out var intValue))
                {
                    error = $"'{text}' khong phai so nguyen 32 bit.";
                    return false;
                }

                if (intValue < Int32Min || intValue > Int32Max)
                {
                    error = $"Gia tri {intValue} ngoai khoang cho phep cua Int32 ({Int32Min}..{Int32Max}).";
                    return false;
                }

                return true;

            case PlcValueType.Float:
                if (!double.TryParse(
                        text,
                        System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture,
                        out var floatValue)
                    || double.IsNaN(floatValue)
                    || double.IsInfinity(floatValue))
                {
                    error = $"'{text}' khong phai so thuc hop le.";
                    return false;
                }

                return true;

            default:
                error = "Kieu du lieu khong ho tro.";
                return false;
        }
    }

    /// <summary>
    /// Chuan hoa gia tri nhap ve dang chuoi ma service ghi xuong PLC se parse duoc.
    ///
    /// Bool -&gt; "true" / "false" (dung dinh dang chuan hoa cua service).
    /// So nguyen -&gt; chuoi so nguyen khong dau / dau am tuy ban dau.
    /// </summary>
    /// <param name="type">Kieu du lieu cua tham so.</param>
    /// <param name="raw">Chuoi gia tri do nguoi dung nhap.</param>
    /// <returns>Gia tri da chuan hoa.</returns>
    public static string Normalize(PlcValueType type, string? raw)
    {
        var text = raw?.Trim() ?? string.Empty;

        return type switch
        {
            PlcValueType.Bool => TryParseBool(text, out var flag) ? flag ? BoolTrue : BoolFalse : text,
            PlcValueType.Int16 => TryParseInteger(text, out var s)
                ? s.ToString(System.Globalization.CultureInfo.InvariantCulture)
                : text,
            PlcValueType.Int32 => TryParseInteger(text, out var i)
                ? i.ToString(System.Globalization.CultureInfo.InvariantCulture)
                : text,
            _ => text,
        };
    }

    /// <summary>
    /// Parse chuoi thanh so nguyen (khong chap nhan phan thap phan).
    /// </summary>
    /// <param name="text">Chuoi can parse.</param>
    /// <param name="value">Ket qua parse.</param>
    /// <returns>True neu parse thanh cong.</returns>
    public static bool TryParseInteger(string text, out long value) =>
        long.TryParse(
            text,
            System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture,
            out value);

    /// <summary>
    /// Parse chuoi thanh boolean (chap nhan true/false, 1/0, on/off, yes/no, co/khong).
    /// </summary>
    /// <param name="text">Chuoi can parse.</param>
    /// <param name="value">Ket qua parse.</param>
    /// <returns>True neu parse thanh cong.</returns>
    public static bool TryParseBool(string text, out bool value)
    {
        value = false;

        switch (text.Trim().ToLowerInvariant())
        {
            case "1" or "true" or "on" or "yes" or "co":
                value = true;
                return true;
            case "0" or "false" or "off" or "no" or "khong":
                value = false;
                return true;
            default:
                return false;
        }
    }
}
