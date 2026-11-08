namespace ClassLibraryMT.Po;

/// <summary>
/// Cache RAM cho tập mã của các PO.
///
/// <para>
/// Cấu trúc:
/// <list type="bullet">
/// <item><c>ValidCodes[orderNo]</c> — tập mã hợp lệ (chưa quét) của PO.</item>
/// <item><c>ScannedCodes[orderNo]</c> — tập mã đã quét trong PO (dùng check trùng O(1)).</item>
/// </list>
/// </para>
///
/// <para>
/// Mọi thao tác đều an toàn khi gọi từ nhiều luồng (camera worker đọc,
/// worker kích hoạt ghi) nên dùng lock quanh các thao tác dictionary.
/// </para>
/// </summary>
public sealed class PoCodeCache
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, HashSet<string>> _valid = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, HashSet<string>> _scanned = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Thay toàn bộ tập mã hợp lệ của một PO.
    /// </summary>
    /// <param name="orderNo">Mã PO.</param>
    /// <param name="codes">Tập mã hợp lệ.</param>
    public void SetValidCodes(string orderNo, IEnumerable<string> codes)
    {
        var set = new HashSet<string>(codes, StringComparer.OrdinalIgnoreCase);
        lock (_gate)
        {
            _valid[orderNo] = set;
            _scanned.TryAdd(orderNo, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        }
    }

    /// <summary>
    /// Nạp lại tập mã đã quét của PO (dùng khi rebuild cache lúc khởi động).
    /// </summary>
    public void SetScannedCodes(string orderNo, IEnumerable<string> codes)
    {
        lock (_gate)
        {
            _scanned[orderNo] = new HashSet<string>(codes, StringComparer.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// Kiểm tra mã có thuộc tập hợp lệ của PO không.
    /// </summary>
    public bool IsValidCode(string orderNo, string code)
    {
        lock (_gate)
        {
            return _valid.TryGetValue(orderNo, out var set) && set.Contains(code);
        }
    }

    /// <summary>
    /// Kiểm tra mã đã được quét trong PO hay chưa.
    /// </summary>
    public bool IsScanned(string orderNo, string code)
    {
        lock (_gate)
        {
            return _scanned.TryGetValue(orderNo, out var set) && set.Contains(code);
        }
    }

    /// <summary>
    /// Đánh dấu mã đã quét trong PO. Trả về true nếu mã mới (chưa từng quét).
    /// </summary>
    public bool MarkScanned(string orderNo, string code)
    {
        lock (_gate)
        {
            _scanned.TryAdd(orderNo, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
            return _scanned[orderNo].Add(code);
        }
    }

    /// <summary>Số mã đã quét trong PO.</summary>
    public int ScannedCount(string orderNo)
    {
        lock (_gate)
        {
            return _scanned.TryGetValue(orderNo, out var set) ? set.Count : 0;
        }
    }

    /// <summary>Tổng số mã hợp lệ của PO.</summary>
    public int ValidCount(string orderNo)
    {
        lock (_gate)
        {
            return _valid.TryGetValue(orderNo, out var set) ? set.Count : 0;
        }
    }

    /// <summary>Xoá toàn bộ cache của một PO.</summary>
    public void Remove(string orderNo)
    {
        lock (_gate)
        {
            _valid.Remove(orderNo);
            _scanned.Remove(orderNo);
        }
    }

    /// <summary>Xoá toàn bộ cache.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _valid.Clear();
            _scanned.Clear();
        }
    }
}
