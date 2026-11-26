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

    /// <summary>
    /// True khi bảng lịch sử mã quét còn dữ liệu — dùng để ẩn/hiện
    /// empty state kiểu mẫu Dashboard 4.
    /// </summary>
    public bool HasScans => Scans.Count > 0;

    /// <summary>
    /// True khi danh sách sự kiện gần đây còn dữ liệu — dùng để ẩn/hiện
    /// dòng "Chưa có sự kiện nào." kiểu mẫu Dashboard 4.
    /// </summary>
    public bool HasEvents => Events.Count > 0;

    /// <summary>Thêm một bản ghi quét mã vào bảng và cập nhật chỉ số.</summary>
    private void AddScan(ScanRecord record)
    {
        Scans.Insert(0, new ScanRowViewModel(record));
        while (Scans.Count > MaxScanRows)
            Scans.RemoveAt(Scans.Count - 1);

        // Bảng vừa có dữ liệu -> cập nhật trạng thái empty state.
        OnPropertyChanged(nameof(HasScans));

        LastScanText = record.CodeContent;
        LastScanTimeText = record.TimestampUtc.ToLocalTime().ToString("HH:mm:ss.fff", ViCulture);
        RefreshPlcCounters();

        switch (record.Outcome)
        {
            case ScanOutcome.Pass:
                AddEvent($"Mã hợp lệ · {record.CodeContent}", EventLevel.Info);
                break;
            case ScanOutcome.NoRead:
                AddEvent("Camera không đọc được mã (NoRead)", EventLevel.Warning);
                break;
            case ScanOutcome.InvalidStructure:
                AddEvent($"Sai cấu trúc · {record.CodeContent}", EventLevel.Warning);
                break;
            case ScanOutcome.Duplicate:
                AddEvent($"Trùng mã trong PO · {record.CodeContent}", EventLevel.Warning);
                break;
            case ScanOutcome.NoProductionOrder:
                AddEvent(
                    "Chưa bắt đầu sản xuất — mã bị loại bỏ (không ghi PLC)",
                    EventLevel.Warning);
                break;
            case ScanOutcome.InvalidForOrder:
                AddEvent(
                    $"Không tồn tại trong PO đang chạy · {record.CodeContent}",
                    EventLevel.Warning);
                break;
            case ScanOutcome.Overload:
                AddEvent(
                    $"Hệ thống đang xử lý mã trước, bỏ qua mã mới · {record.CodeContent}",
                    EventLevel.Warning);
                break;
            case ScanOutcome.PlcError:
                AddEvent($"PLC chưa xác nhận Pass, mã chưa được ghi nhận PO · {record.CodeContent}", EventLevel.Critical);
                break;
            case ScanOutcome.Halted:
                AddEvent($"Máy đã dừng · {record.CodeContent}", EventLevel.Critical);
                break;
        }

        // Mã bị từ chối do quá tải / chưa bắt đầu sản xuất thì KHÔNG ghi PLC
        // -> không phải lỗi PLC.
        if (!record.PlcWritten && !record.PlcSkipped
            && record.Outcome is not (ScanOutcome.Overload or ScanOutcome.NoProductionOrder or ScanOutcome.Halted))
        {
            AddEvent("Ghi kết quả xuống PLC thất bại", EventLevel.Critical);
        }
    }

    private void AddEvent(string message, EventLevel level)
    {
        Events.Insert(0, new ScanEvent(message, level, DateTime.Now));
        while (Events.Count > MaxEventRows)
            Events.RemoveAt(Events.Count - 1);

        // Danh sách sự kiện vừa có dữ liệu -> cập nhật trạng thái empty state.
        OnPropertyChanged(nameof(HasEvents));
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
