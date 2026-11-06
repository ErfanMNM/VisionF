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
    /// <summary>
    /// Sức chứa tối đa của kênh.
    ///
    /// Giới hạn để không rò bộ nhớ: nếu consumer (ViewModel) bị kẹt — ví dụ
    /// UI treo hoặc task đọc bị lỗi và chết — thì kênh unbounded sẽ phình vô
    /// hạn vì không còn ai đọc. Kênh bounded + <c>DropOldest</c> đảm bảo bộ
    /// nhớ luôn ở mức giới hạn.
    /// </summary>
    private const int FeedCapacity = 1000;

    private readonly Channel<ScanRecord> _channel = Channel.CreateBounded<ScanRecord>(
        new BoundedChannelOptions(FeedCapacity)
        {
            // DropOldest: nếu UI bị chậm, bỏ bản ghi cũ thay vì nuốt hết RAM.
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
        });

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
