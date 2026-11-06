using System.Collections.ObjectModel;
using System.Globalization;
using TanTienQR.Models;

namespace TanTienQR.ViewModels;

/// <summary>
/// Bảng lịch sử mã quét và danh sách sự kiện gần đây.
///
/// Phần này chạy hoàn toàn trên luồng UI (được dispatch từ
/// <see cref="MainViewModel.ConsumeFeedAsync"/>).
/// </summary>
internal partial class MainViewModel
{
    /// <summary>Số bản ghi giữ lại trong bảng (bản ghi mới nhất đứng đầu).</summary>
    private const int MaxScanRows = 200;

    /// <summary>Số sự kiện giữ lại.</summary>
    private const int MaxEventRows = 8;

    /// <summary>Lịch sử mã quét, mới nhất đứng đầu.</summary>
    public ObservableCollection<ScanRowViewModel> Scans { get; } = [];

    /// <summary>Sự kiện gần đây, mới nhất đứng đầu.</summary>
    public ObservableCollection<ScanEvent> Events { get; } = [];

    /// <summary>Thêm một bản ghi quét mã vào bảng và cập nhật chỉ số.</summary>
    private void AddScan(ScanRecord record)
    {
        Scans.Insert(0, new ScanRowViewModel(record));
        while (Scans.Count > MaxScanRows)
            Scans.RemoveAt(Scans.Count - 1);

        TotalScans++;
        if (record.Outcome == ScanOutcome.Pass)
            PassCount++;
        else
            FailCount++;

        PassRate = TotalScans == 0 ? 0 : Math.Round(PassCount * 100.0 / TotalScans, 1);

        LastScanText = record.Serial ?? record.Raw;
        LastScanTimeText = record.TimestampUtc.ToLocalTime().ToString("HH:mm:ss.fff", ViCulture);

        switch (record.Outcome)
        {
            case ScanOutcome.Pass:
                AddEvent($"Mã hợp lệ · {record.Serial}", EventLevel.Info);
                break;
            case ScanOutcome.NoRead:
                AddEvent("Camera không đọc được mã (NoRead)", EventLevel.Warning);
                break;
            case ScanOutcome.Duplicate:
                AddEvent($"Mã trùng với lần quét gần đây · {record.Serial}", EventLevel.Warning);
                break;
            case ScanOutcome.Overload:
                AddEvent(
                    $"Hệ thống đang xử lý mã trước, bỏ qua mã mới · {record.Raw}",
                    EventLevel.Warning);
                break;
            default:
                AddEvent($"Mã sai định dạng · {record.Raw}", EventLevel.Warning);
                break;
        }

        // Mã bị từ chối do quá tải thì KHÔNG ghi PLC -> không phải lỗi PLC.
        if (!record.PlcWritten && record.Outcome != ScanOutcome.Overload)
            AddEvent("Ghi kết quả xuống PLC thất bại", EventLevel.Critical);
    }

    private void AddEvent(string message, EventLevel level)
    {
        Events.Insert(0, new ScanEvent(message, level, DateTime.Now));
        while (Events.Count > MaxEventRows)
            Events.RemoveAt(Events.Count - 1);
    }
}

/// <summary>Mức độ của một sự kiện trong danh sách sự kiện gần đây.</summary>
public enum EventLevel
{
    Info = 0,
    Warning = 1,
    Critical = 2,
}

/// <summary>Một sự kiện hiển thị trong bảng sự kiện gần đây.</summary>
/// <param name="Message">Nội dung sự kiện.</param>
/// <param name="Level">Mức độ.</param>
/// <param name="Timestamp">Thời điểm xảy ra.</param>
public sealed record ScanEvent(string Message, EventLevel Level, DateTime Timestamp)
{
    private static readonly CultureInfo ViCulture = CreateViCulture();

    /// <summary>Class style cho vạch màu bên trái.</summary>
    public string StripeClass => Level switch
    {
        EventLevel.Warning => "warning",
        EventLevel.Critical => "critical",
        _ => "",
    };

    /// <summary>true khi sự kiện mức cảnh báo (vạch vàng).</summary>
    public bool IsWarning => Level == EventLevel.Warning;

    /// <summary>true khi sự kiện mức nghiêm trọng (vạch đỏ).</summary>
    public bool IsCritical => Level == EventLevel.Critical;

    /// <summary>Giờ hiển thị.</summary>
    public string TimeText => Timestamp.ToString("HH:mm:ss.fff", ViCulture);

    private static CultureInfo CreateViCulture()
    {
        try
        {
            return new CultureInfo("vi-VN");
        }
        catch (CultureNotFoundException)
        {
            return CultureInfo.InvariantCulture;
        }
    }
}
