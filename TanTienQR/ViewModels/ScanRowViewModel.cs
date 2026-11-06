using System.Globalization;
using TanTienQR.Models;

namespace TanTienQR.ViewModels;

/// <summary>
/// Một dòng trong bảng lịch sử mã quét.
///
/// Bọc <see cref="ScanRecord"/> lại để có thuộc tính hiển thị sẵn (chuỗi đã
/// format, class style cho màu) — giúp XAML không phải dùng converter.
/// </summary>
public sealed class ScanRowViewModel
{
    private static readonly CultureInfo ViCulture = CreateViCulture();

    public ScanRowViewModel(ScanRecord record)
    {
        TimeText = record.TimestampUtc.ToLocalTime().ToString("HH:mm:ss.fff", ViCulture);
        CodeText = record.Serial ?? record.Raw;
        GtinText = record.Gtin ?? "—";
        OutcomeText = record.Outcome switch
        {
            ScanOutcome.Pass => "Pass",
            ScanOutcome.NoRead => "NoRead",
            ScanOutcome.Duplicate => "Trùng mã",
            ScanOutcome.Overload => "Quá tải",
            _ => "Sai định dạng",
        };
        PlcText = record.Outcome == ScanOutcome.Overload
            ? "Bỏ qua"
            : record.PlcWritten ? "Đã ghi" : "Lỗi";
        ProcessingTimeText = $"{record.ProcessingTimeMs:F3} ms";
    }

    /// <summary>Giờ nhận mã.</summary>
    public string TimeText { get; }

    /// <summary>Mã / serial quét được.</summary>
    public string CodeText { get; }

    /// <summary>GTIN nếu có.</summary>
    public string GtinText { get; }

    /// <summary>Kết quả xử lý (Pass / NoRead / Sai định dạng / Quá tải).</summary>
    public string OutcomeText { get; }

    /// <summary>Trạng thái ghi xuống PLC.</summary>
    public string PlcText { get; }

    /// <summary>Thời gian xử lý (từ lúc nhận mã đến trước khi gửi PLC).</summary>
    public string ProcessingTimeText { get; }

    /// <summary>Class style cho badge kết quả.</summary>
    public string OutcomeBadgeClass => OutcomeText switch
    {
        "Pass" => "running",
        "NoRead" => "warning",
        "Quá tải" => "warning",
        _ => "fault",
    };

    /// <summary>true khi kết quả là Pass (badge xanh).</summary>
    public bool IsPass => OutcomeText == "Pass";

    /// <summary>true khi camera báo NoRead (badge vàng).</summary>
    public bool IsNoRead => OutcomeText == "NoRead";

    /// <summary>
    /// true khi kết quả hiển thị màu cảnh báo (badge vàng).
    ///
    /// Gộp NoRead + Quá tải vì Avalonia chỉ cho bind một property vào một
    /// class — nếu bind 2 property cùng tên "warning" thì property sau ghi đè
    /// property trước.
    /// </summary>
    public bool IsWarningOutcome => IsNoRead || IsOverload;

    /// <summary>true khi mã bị từ chối do đang xử lý mã khác (badge vàng).</summary>
    public bool IsOverload => OutcomeText == "Quá tải";

    /// <summary>true khi mã sai định dạng (badge đỏ).</summary>
    public bool IsInvalid => OutcomeText == "Sai định dạng";

    /// <summary>Class style cho trạng thái ghi PLC.</summary>
    public string PlcBadgeClass => PlcText switch
    {
        "Đã ghi" => "running",
        "Bỏ qua" => "warning",
        _ => "fault",
    };

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
