namespace ClassLibraryMT.Plc;

/// <summary>Ket qua lenh ghi (khong tra du lieu). Kieu Hsl-like.</summary>
public sealed class PlcResult
{
    public bool IsSuccess { get; init; }
    public string Message { get; init; } = string.Empty;
    /// <summary>Ma loi: end code FINS dang hex ("0000","1101","1103","0401","0001"...), hoac "TIMEOUT", hoac "ARG".</summary>
    public string ErrorCode { get; init; } = string.Empty;
    public TimeSpan ProcessTime { get; init; }

    public static PlcResult Ok(string message, TimeSpan processTime) =>
        new() { IsSuccess = true, Message = message, ErrorCode = "0000", ProcessTime = processTime };

    public static PlcResult Fail(string errorCode, string message, TimeSpan processTime) =>
        new() { IsSuccess = false, Message = message, ErrorCode = errorCode, ProcessTime = processTime };
}

/// <summary>Ket qua lenh doc (co du lieu). Kieu Hsl-like.</summary>
public sealed class PlcResult<T>
{
    public bool IsSuccess { get; init; }
    public string Message { get; init; } = string.Empty;
    public string ErrorCode { get; init; } = string.Empty;
    public T? Value { get; init; }
    public TimeSpan ProcessTime { get; init; }

    public static PlcResult<T> Ok(T? value, string message, TimeSpan processTime) =>
        new() { IsSuccess = true, Message = message, ErrorCode = "0000", Value = value, ProcessTime = processTime };

    public static PlcResult<T> Fail(string errorCode, string message, TimeSpan processTime, T? value = default) =>
        new() { IsSuccess = false, Message = message, ErrorCode = errorCode, Value = value, ProcessTime = processTime };
}
