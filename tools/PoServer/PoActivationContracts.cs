namespace PoServer;

// Legacy demo-server protocol only. The production app has no activation client.
/// <summary>
/// Yêu cầu kích hoạt một tập mã của một PO.
/// </summary>
public sealed class PoActivateRequest
{
    /// <summary>Mã lệnh sản xuất.</summary>
    public string OrderNo { get; set; } = "";

    /// <summary>Complete CodeContent values to activate.</summary>
    public List<string> CodeContents { get; set; } = [];

    /// <summary>Legacy API compatibility alias.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    [Obsolete("Use CodeContents.")]
    public List<string> Serials { get => CodeContents; set => CodeContents = value ?? []; }
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

