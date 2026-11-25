namespace TanTienQR.Services;

/// <summary>Metadata sản phẩm cho giao thức gửi Checkee, độc lập với định danh mã quét.</summary>
internal sealed class CheckeeOutboundOptions
{
    /// <summary>
    /// GTIN sản phẩm do người vận hành cấu hình tại Checkee:ProductGtin.
    /// Không suy ra từ CodeContent và không dùng để kiểm tra mã của PO.
    /// </summary>
    public string ProductGtin { get; set; } = string.Empty;
}
