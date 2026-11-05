using System.Threading.Channels;
using TanTienQR.Models;

namespace TanTienQR.Services;

/// <summary>
/// Kênh truyền kết quả quét mã từ luồng nền (camera worker) sang luồng UI.
///
/// Worker chạy trong <c>BackgroundService</c> nên không được chạm trực tiếp vào
/// <c>ObservableCollection</c> của ViewModel (sẽ lỗi cross-thread).
/// Worker ghi vào đây, ViewModel đọc và dispatch sang luồng Avalonia.
/// </summary>
internal sealed class ScanFeed
{
    private readonly Channel<ScanRecord> _channel = Channel.CreateUnbounded<ScanRecord>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });

    /// <summary>
    /// Đẩy một bản ghi kết quả quét mã. Không chặn luồng gọi.
    /// </summary>
    public void Publish(ScanRecord record) => _channel.Writer.TryWrite(record);

    /// <summary>
    /// Đọc các bản ghi đã đẩy tới. Trả về false khi kênh đã đóng.
    /// </summary>
    public IAsyncEnumerable<ScanRecord> ReadAllAsync(CancellationToken ct) =>
        _channel.Reader.ReadAllAsync(ct);

    /// <summary>
    /// Đóng kênh (khi app tắt).
    /// </summary>
    public void Complete() => _channel.Writer.TryComplete();
}
