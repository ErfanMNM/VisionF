namespace ClassLibraryMT.Scans;

/// <summary>
/// Cài đặt <see cref="IScanRecordQuery"/> đọc trực tiếp từ SQLite.
///
/// Mỗi lần truy vấn mở connection riêng (WAL cho phép đọc song song với
/// worker đang ghi) nên không cần lock với writer.
/// </summary>
internal sealed class ScanRecordQuery : IScanRecordQuery
{
    private readonly ScanStore _store;

    public ScanRecordQuery(ScanStore store)
    {
        _store = store;
    }

    public Task<PagedScanResult<ScanRecordEntity>> GetAsync(
        ScanRecordFilter? filter = null,
        int page = 1,
        int pageSize = 200,
        CancellationToken cancellationToken = default) =>
        _store.QueryAsync(filter, page, pageSize, cancellationToken);

    public Task<Dictionary<string, long>> CountByStatusAsync(
        CancellationToken cancellationToken = default) =>
        _store.CountByStatusAsync(cancellationToken);
}
