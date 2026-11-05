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
        TimeText = record.TimestampUtc.ToLocalTime().ToString("HH:mm:ss", ViCulture);
        CodeText = record.Serial ?? record.Raw;
        GtinText = record.Gtin ?? "—";
        OutcomeText = record.Outcome switch
        {
            ScanOutcome.Pass => "Pass",
            ScanOutcome.NoRead => "NoRead",
            ScanOutcome.Duplicate => "Trùng mã",
            _ => "Sai định dạng",
        };
        PlcText = record.PlcWritten ? "Đã ghi" : "Lỗi";
    }

    /// <summary>Giờ nhận mã.</summary>
    public string TimeText { get; }

    /// <summary>Mã / serial quét được.</summary>
    public string CodeText { get; }

    /// <summary>GTIN nếu có.</summary>
    public string GtinText { get; }

    /// <summary>Kết quả xử lý (Pass / NoRead / Sai định dạng).</summary>
    public string OutcomeText { get; }

    /// <summary>Trạng thái ghi xuống PLC.</summary>
    public string PlcText { get; }

    /// <summary>Class style cho badge kết quả.</summary>
    public string OutcomeBadgeClass => OutcomeText switch
    {
        "Pass" => "running",
        "NoRead" => "warning",
        _ => "fault",
    };

    /// <summary>true khi kết quả là Pass (badge xanh).</summary>
    public bool IsPass => OutcomeText == "Pass";

    /// <summary>true khi camera báo NoRead (badge vàng).</summary>
    public bool IsNoRead => OutcomeText == "NoRead";

    /// <summary>true khi mã sai định dạng (badge đỏ).</summary>
    public bool IsInvalid => OutcomeText == "Sai định dạng";

    /// <summary>Class style cho trạng thái ghi PLC.</summary>
    public string PlcBadgeClass => PlcText == "Đã ghi" ? "running" : "fault";

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
