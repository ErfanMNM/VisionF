namespace ClassLibraryMT.Po;

/// <summary>
/// Cache RAM của tập mã của PO <b>đang chạy</b>.
///
/// <para>
/// <b>Thiết kế:</b> dùng MỘT <see cref="Dictionary{TKey, TValue}"/> duy nhất, mỗi
/// mã chỉ chiếm 1 entry (thay vì 2 <c>HashSet</c> riêng cho "mã hợp lệ" và
/// "mã đã quét" như trước đây) → giảm khoảng một nửa dung lượng RAM.
/// </para>
///
/// <para>
/// <b>Phạm vi:</b> chỉ nạp mã của PO đang chạy. Khi PO hoàn tất / bị huỷ /
/// đổi sang PO khác thì gọi <see cref="Clear"/> để giải phóng RAM ngay —
/// cache không phình dần theo lịch sử PO như bản cũ.
/// </para>
///
/// <para>
/// <b>Giá trị lưu trữ:</b> là một byte cờ trạng thái (xem <see cref="Flags"/>)
/// nên rất gọn nhẹ.
/// </para>
///
/// <para>
/// Mọi thao tác đều an toàn khi gọi từ nhiều luồng (luồng camera đọc,
/// worker kích hoạt ghi) nên dùng lock quanh các thao tác dictionary.
/// </para>
/// </summary>
public sealed class PoCodeCache
{
    /// <summary>
    /// Cờ trạng thái của một mã trong cache.
    /// </summary>
    [Flags]
    public enum Flags : byte
    {
        /// <summary>Mã thuộc PO (chưa quét).</summary>
        None = 0,

        /// <summary>Mã đã được quét thành công (Pass).</summary>
        Scanned = 1 << 0,

        /// <summary>Mã đã được gửi kích hoạt lên server.</summary>
        Activated = 1 << 1,

        /// <summary>PLC Pass outcome is unresolved.</summary>
        PendingPass = 1 << 2,
    }

    private readonly Lock _gate = new();

    /// <summary>Key = complete CodeContent, value = persisted per-code state flags.</summary>
    private readonly Dictionary<string, Flags> _codes = new(StringComparer.Ordinal);

    /// <summary>Mã PO mà cache đang phục vụ (để kiểm tra nhầm lẫn).</summary>
    private string _orderNo = string.Empty;
    private int _scannedCount;
    private int _pendingPassCount;

    /// <summary>
    /// Nạp tập mã của PO đang chạy vào cache (ghi đè nội dung cũ).
    /// </summary>
    /// <param name="orderNo">Mã PO.</param>
    /// <param name="codes">
    /// Danh sách mã. Mỗi phần tử là cặp (khóa mã, cờ trạng thái).
    /// </param>
    public void LoadOrder(string orderNo, IEnumerable<KeyValuePair<string, Flags>> codes)
    {
        ArgumentNullException.ThrowIfNull(codes);

        var map = new Dictionary<string, Flags>(StringComparer.Ordinal);
        foreach (var (key, flags) in codes)
        {
            map[key] = flags;
        }

        lock (_gate)
        {
            _codes.Clear();
            foreach (var pair in map)
            {
                _codes[pair.Key] = pair.Value;
            }

            _orderNo = orderNo ?? string.Empty;
            _scannedCount = map.Values.Count(flags => (flags & (Flags.Scanned | Flags.Activated)) != 0);
            _pendingPassCount = map.Values.Count(flags => (flags & Flags.PendingPass) != 0);
        }
    }

    /// <summary>Mã PO mà cache đang phục vụ (rỗng nếu chưa nạp).</summary>
    public string OrderNo
    {
        get
        {
            lock (_gate)
                return _orderNo;
        }
    }

    /// <summary>Số mã trong cache (tổng số mã của PO đang chạy).</summary>
    public int ValidCount
    {
        get
        {
            lock (_gate)
                return _codes.Count;
        }
    }

    /// <summary>Số mã đã quét trong PO đang chạy.</summary>
    public int ScannedCount
    {
        get
        {
            lock (_gate)
                return _scannedCount;
        }
    }

    /// <summary>
    /// Kiểm tra mã có thuộc PO đang chạy không.
    /// </summary>
    public bool Contains(string code)
    {
        lock (_gate)
            return _codes.ContainsKey(code);
    }

    /// <summary>
    /// Kiểm tra mã đã được quét trong PO đang chạy hay chưa.
    /// </summary>
    public bool IsScanned(string code)
    {
        lock (_gate)
        {
            return _codes.TryGetValue(code, out var flags)
                && (flags & Flags.Scanned) != 0;
        }
    }

    /// <summary>
    /// Kiểm tra mã đã được gửi kích hoạt lên server hay chưa.
    /// </summary>
    public bool IsActivated(string code)
    {
        lock (_gate)
        {
            return _codes.TryGetValue(code, out var flags)
                && (flags & Flags.Activated) != 0;
        }
    }

    public bool HasPendingPass
    {
        get
        {
            lock (_gate)
                return _pendingPassCount != 0;
        }
    }

    public bool MarkPassPending(string code)
    {
        lock (_gate)
        {
            if (!_codes.TryGetValue(code, out var flags)
                || (flags & (Flags.Scanned | Flags.Activated | Flags.PendingPass)) != 0)
                return false;

            _codes[code] = flags | Flags.PendingPass;
            _pendingPassCount++;
            return true;
        }
    }

    public void ClearPassPending(string code)
    {
        lock (_gate)
        {
            if (_codes.TryGetValue(code, out var flags) && (flags & Flags.PendingPass) != 0)
            {
                _codes[code] = flags & ~Flags.PendingPass;
                _pendingPassCount--;
            }
        }
    }

    /// <summary>
    /// Đánh dấu mã đã quét. Trả về true nếu mã mới (chưa từng quét).
    /// </summary>
    public bool MarkScanned(string code)
    {
        lock (_gate)
        {
            if (!_codes.TryGetValue(code, out var flags))
            {
                return false;
            }

            if ((flags & Flags.Scanned) != 0)
            {
                return false;
            }

            _codes[code] = (flags | Flags.Scanned) & ~Flags.PendingPass;
            if ((flags & Flags.Activated) == 0)
                _scannedCount++;
            if ((flags & Flags.PendingPass) != 0)
                _pendingPassCount--;
            return true;
        }
    }

    /// <summary>
    /// Đánh dấu mã đã gửi kích hoạt lên server.
    /// </summary>
    public void MarkActivated(string code)
    {
        lock (_gate)
        {
            if (_codes.TryGetValue(code, out var flags))
            {
                _codes[code] = flags | Flags.Activated;
                if ((flags & (Flags.Scanned | Flags.Activated)) == 0)
                    _scannedCount++;
            }
        }
    }

    /// <summary>
    /// Xoá toàn bộ cache (gọi khi PO hoàn tất, bị huỷ hoặc đổi PO).
    /// </summary>
    public void Clear()
    {
        lock (_gate)
        {
            _codes.Clear();
            _orderNo = string.Empty;
            _scannedCount = 0;
            _pendingPassCount = 0;
        }
    }

    /// <summary>Validate and consume a complete code under one short RAM lock.</summary>
    public PoCodeValidation ValidateAndAccept(string orderNo, string codeContent, bool accept)
    {
        lock (_gate)
        {
            if (!string.Equals(_orderNo, orderNo, StringComparison.Ordinal))
                return PoCodeValidation.NoProductionOrder;
            if (!_codes.TryGetValue(codeContent, out var flags))
                return PoCodeValidation.InvalidForOrder;
            if ((flags & (Flags.Scanned | Flags.Activated)) != 0)
                return PoCodeValidation.Duplicate;
            if ((flags & Flags.PendingPass) != 0)
                return PoCodeValidation.Halted;
            if (accept)
            {
                _codes[codeContent] = flags | Flags.Scanned;
                _scannedCount++;
            }
            return PoCodeValidation.Valid;
        }
    }
}
