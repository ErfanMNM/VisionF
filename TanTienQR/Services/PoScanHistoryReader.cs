using ClassLibraryMT.Po;
using ClassLibraryMT.Scans;

namespace TanTienQR.Services;

/// <summary>
/// Cầu nối giữa luồng PO và DB log quét mã.
///
/// <para>
/// Cho phép <see cref="ProductionOrderService"/> kiểm tra "mã này đã từng
/// xuất hiện trong lịch sử quét của hệ thống chưa" mà không tạo phụ thuộc
/// vòng giữa <c>ClassLibraryMT.Po</c> và <c>ClassLibraryMT.Scans</c>.
/// </para>
/// </summary>
internal sealed class PoScanHistoryReader : IPoScanHistoryReader
{
    private readonly IScanRecordQuery _query;

    public PoScanHistoryReader(IScanRecordQuery query)
    {
        _query = query;
    }

    public Task<IReadOnlyList<string>> GetSeenSerialsAsync(
        string orderNo,
        CancellationToken ct = default) =>
        _query.GetSeenSerialsAsync(orderNo, ct);
}
