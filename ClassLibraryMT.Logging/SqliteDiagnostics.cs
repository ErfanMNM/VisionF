namespace ClassLibraryMT.Logging;

/// <summary>
/// Ghi chẩn đoán nội bộ của writer ra stderr.
///
/// Cố ý KHÔNG dùng <c>ILogger</c> ở đây: writer là một phần của
/// <c>ILoggerProvider</c>, nên nếu nó phụ thuộc vào <c>ILogger</c> sẽ tạo
/// vòng phụ thuộc vòng tròn trong DI:
/// <code>
/// SqliteLogWriter -> ILogger&lt;SqliteLogWriter&gt; -> ILoggerFactory
///   -> IEnumerable&lt;ILoggerProvider&gt; -> SqliteLoggerProvider -> SqliteLogWriter
/// </code>
/// Ghi thẳng ra stderr là an toàn và không đệ quy vô hạn.
/// </summary>
internal static class SqliteDiagnostics
{
    /// <summary>
    /// Ghi một thông báo lỗi ra stderr.
    /// </summary>
    /// <param name="message">Nội dung lỗi.</param>
    /// <param name="exception">Exception gốc (nếu có).</param>
    public static void Error(string message, Exception? exception = null)
    {
        try
        {
            Console.Error.WriteLine($"[ClassLibraryMT.Logging] {message}");

            if (exception is not null)
            {
                Console.Error.WriteLine(exception);
            }
        }
        catch (IOException)
        {
            // stderr đã bị đóng -> không làm được gì thêm.
        }
        catch (ObjectDisposedException)
        {
            // stderr đã bị dispose -> bỏ qua.
        }
    }
}
