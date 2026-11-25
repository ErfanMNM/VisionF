namespace ClassLibraryMT.PoServer;

/// <summary>Kết quả đọc bảng CodeContent của sheet đầu tiên.</summary>
public sealed class PoExcelReadResult
{
    public List<string> Codes { get; } = [];
    public int RowsRead { get; set; }
    public bool HeaderSkipped { get; set; }
}

/// <summary>Compatibility entry point; all Excel imports use the strict CodeContent table parser.</summary>
public static class PoExcelReader
{
    public static async Task<PoExcelReadResult> ReadAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!Path.GetExtension(path).Equals(".xlsx", StringComparison.OrdinalIgnoreCase))
            throw new PoFileFormatException("Chỉ hỗ trợ bảng CodeContent trong file .xlsx; .xls cũ không được hỗ trợ.");
        var codes = await PoCodeFileParser.ReadXlsxAsync(path, cancellationToken).ConfigureAwait(false);
        var result = new PoExcelReadResult { HeaderSkipped = true, RowsRead = codes.Count + 1 };
        result.Codes.AddRange(codes);
        return result;
    }
}

/// <summary>File tải lên không đúng bảng CodeContent hoặc không thể đọc được.</summary>
public sealed class PoFileFormatException : Exception
{
    public PoFileFormatException(string message) : base(message) { }
    public PoFileFormatException(string message, Exception innerException) : base(message, innerException) { }
}
