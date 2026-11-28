namespace ClassLibraryMT.Communications.Plc;

/// <summary>Ket qua lenh ghi (khong tra du lieu). Kieu Hsl-like.</summary>
public sealed class PlcResult
{
    /// <summary>True neu lenh thuc hien thanh cong.</summary>
    public bool IsSuccess { get; init; }

    /// <summary>Thong diep mo ta ket qua.</summary>
    public string Message { get; init; } = string.Empty;

    /// <summary>
    /// Ma loi: end code FINS dang hex ("0000", "1101", "1103", "0401"...),
    /// hoac "TIMEOUT" khi khong co response, hoac "ARG" khi sai dinh dang dia chi.
    /// </summary>
    public string ErrorCode { get; init; } = string.Empty;

    /// <summary>Thoi gian xu ly lenh (ke ca ca lan retry).</summary>
    public TimeSpan ProcessTime { get; init; }

    /// <summary>Tao ket qua thanh cong.</summary>
    public static PlcResult Ok(string message, TimeSpan processTime) =>
        new() { IsSuccess = true, Message = message, ErrorCode = "0000", ProcessTime = processTime };

    /// <summary>Tao ket qua loi.</summary>
    public static PlcResult Fail(string errorCode, string message, TimeSpan processTime) =>
        new() { IsSuccess = false, Message = message, ErrorCode = errorCode, ProcessTime = processTime };
}

/// <summary>Ket qua lenh doc (co du lieu). Kieu Hsl-like.</summary>
public sealed class PlcResult<T>
{
    /// <summary>True neu lenh thuc hien thanh cong.</summary>
    public bool IsSuccess { get; init; }

    /// <summary>Thong diep mo ta ket qua.</summary>
    public string Message { get; init; } = string.Empty;

    /// <summary>
    /// Ma loi: end code FINS dang hex ("0000", "1101", "1103", "0401"...),
    /// hoac "TIMEOUT" khi khong co response, hoac "ARG" khi sai dinh dang dia chi.
    /// </summary>
    public string ErrorCode { get; init; } = string.Empty;

    /// <summary>Gia tri doc duoc (chi co y nghia khi <see cref="IsSuccess"/> la true).</summary>
    public T? Value { get; init; }

    /// <summary>Thoi gian xu ly lenh (ke ca ca lan retry).</summary>
    public TimeSpan ProcessTime { get; init; }

    /// <summary>Tao ket qua thanh cong.</summary>
    public static PlcResult<T> Ok(T? value, string message, TimeSpan processTime) =>
        new() { IsSuccess = true, Message = message, ErrorCode = "0000", Value = value, ProcessTime = processTime };

    /// <summary>Tao ket qua loi.</summary>
    public static PlcResult<T> Fail(string errorCode, string message, TimeSpan processTime, T? value = default) =>
        new() { IsSuccess = false, Message = message, ErrorCode = errorCode, Value = value, ProcessTime = processTime };
}
