namespace ClassLibraryMT.MultiConnect;

/// <summary>Nguồn dữ liệu đầu vào của một kênh gateway.</summary>
internal interface IInputSource
{
    /// <summary>
    /// Chạy vòng lặp đọc dữ liệu và gọi <paramref name="onFrame"/> cho mỗi frame.
    /// Tự reconnect khi mất kết nối; chỉ kết thúc khi <paramref name="ct"/> bị hủy.
    /// </summary>
    Task RunAsync(Func<string, Task> onFrame, CancellationToken ct);
}
