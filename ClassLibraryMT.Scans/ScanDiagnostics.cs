namespace ClassLibraryMT.Scans;

/// <summary>
/// Ghi thông báo lỗi ra stderr cho worker SQLite.
///
/// Worker nằm trong chuỗi ghi log nên không thể phụ thuộc <c>ILogger</c> (sẽ
/// gây vòng phụ thuộc vòng tròn trong DI). Vì vậy lỗi ghi ra stderr trực tiếp.
/// </summary>
internal static class ScanDiagnostics
{
    /// <summary>Ghi một lỗi ra stderr.</summary>
    /// <param name="message">Thông điệp.</param>
    /// <param name="exception">Exception kèm theo (nếu có).</param>
    public static void Error(string message, Exception? exception = null)
    {
        var text = exception is null
            ? $"[ClassLibraryMT.Scans] {message}"
            : $"[ClassLibraryMT.Scans] {message} | {exception}";

        Console.Error.WriteLine(text);
    }

    /// <summary>Ghi một thông tin ra stderr.</summary>
    /// <param name="message">Thông điệp.</param>
    public static void Info(string message) =>
        Console.Error.WriteLine($"[ClassLibraryMT.Scans] {message}");
}
