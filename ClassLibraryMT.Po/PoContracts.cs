namespace ClassLibraryMT.Po;

/// <summary>
/// Một mã (code) thuộc về một lệnh sản xuất.
/// </summary>
public sealed class PoCode
{
    /// <summary>Complete imported code content used as the exact PO identity.</summary>
    public string CodeContent { get; set; } = "";

    /// <summary>Legacy storage compatibility alias for <see cref="CodeContent"/>.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    [Obsolete("Use CodeContent.")]
    public string Code { get => CodeContent; set => CodeContent = value ?? string.Empty; }

    /// <summary>Legacy compatibility field; not used for PO identity.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    [Obsolete("GTIN is not part of PO code identity.")]
    public string Gtin { get; set; } = "";

    /// <summary>Legacy compatibility field; not used for PO identity.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    [Obsolete("Serial is not part of PO code identity.")]
    public string Serial { get; set; } = "";

    /// <summary>When the code was marked as scanned in its PO database.</summary>
    public DateTime? ScannedUtc { get; set; }

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

    /// <summary>GTIN duy nhất của PO.</summary>
    public string Gtin { get; set; } = "";
}

/// <summary>Atomic RAM acceptance and separate PLC outcome; persistence is completed by the app coordinator.</summary>
public sealed record PoScanResult(
    PoCodeValidation Validation,
    bool? PlcWritten,
    bool Committed,
    string OrderNo,
    bool ProductionHalted = false,
    string? Message = null)
{
    /// <summary>Captured accepted mutation for the persistence queue. Committed means RAM acceptance, not a durable SQLite commit.</summary>
    public PoScanMutation? Mutation { get; init; }
}

/// <summary>
/// Legacy compatibility exception for callers that cannot confirm durable PO state.
/// </summary>
public sealed class PoScanProcessingException : Exception
{
    public PoScanProcessingException(string message, PoScanResult result, Exception? innerException = null)
        : base(message, innerException) => Result = result;

    public PoScanResult Result { get; }
}
