namespace ClassLibraryMT.Po;

/// <summary>
/// Một mã (code) thuộc về một lệnh sản xuất.
/// </summary>
public sealed class PoCode
{
    /// <summary>Mã đầy đủ (GTIN + serial, dùng để so sánh/đếm).</summary>
    public string Code { get; set; } = "";

    /// <summary>GTIN của mã (mã sản phẩm).</summary>
    public string Gtin { get; set; } = "";

    /// <summary>Serial của mã (mã serial của lô / PO).</summary>
    public string Serial { get; set; } = "";

    /// <summary>
    /// Thời điểm mã được kích hoạt trên server (UTC). Null nghĩa là chưa kích hoạt.
    /// </summary>
    public DateTime? ActivatedUtc { get; set; }
}

/// <summary>
/// Yêu cầu tạo lệnh sản xuất mới trên server.
/// </summary>
public sealed class PoCreateRequest
{
    /// <summary>Mã lệnh sản xuất.</summary>
    public string OrderNo { get; set; } = "";

    /// <summary>Số lượng mã cần sinh.</summary>
    public int OrderQty { get; set; } = 100;
}

/// <summary>
/// Yêu cầu kích hoạt một tập mã của một PO.
/// </summary>
public sealed class PoActivateRequest
{
    /// <summary>Mã lệnh sản xuất.</summary>
    public string OrderNo { get; set; } = "";

    /// <summary>Danh sách serial (hoặc mã đầy đủ) cần kích hoạt.</summary>
    public List<string> Serials { get; set; } = [];
}

/// <summary>
/// Kết quả trả về của API kích hoạt mã.
/// </summary>
public sealed class PoActivateResult
{
    /// <summary>True nếu server nhận thành công.</summary>
    public bool Ok { get; set; }

    /// <summary>Số mã đã được kích hoạt (mới).</summary>
    public int Activated { get; set; }

    /// <summary>Thông báo (tuỳ chọn).</summary>
    public string Message { get; set; } = "";
}
