namespace ClassLibraryMT.Checkee;

/// <summary>
/// Cấu hình kết nối tới Checkee Middleware API (section <c>Checkee</c>).
///
/// <para>
/// Theo tài liệu "Checkee Middleware API Integration Specification v1.1.1":
/// host UAT là <c>https://beta-api-eco.checkee.vn</c>, host Production là
/// <c>https://api-eco.checkee.vn</c>. Middleware API nằm ở
/// <c>{HOST}/middleware-api</c>, còn API lấy access token nằm ở
/// <c>{HOST}/onboarding/token</c>.
/// </para>
/// </summary>
public sealed class CheckeeOptions
{
    /// <summary>Tên section cấu hình.</summary>
    public const string SectionName = "Checkee";

    /// <summary>
    /// Base URL của host Checkee (không kèm đường dẫn API).
    /// Mặc định là host UAT.
    /// </summary>
    public string BaseUrl { get; set; } = "https://beta-api-eco.checkee.vn";

    /// <summary>
    /// API Key do Checkee cấp cho doanh nghiệp (header <c>x-api-key</c>).
    /// </summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>
    /// Client ID do Checkee cấp (header <c>x-client-id</c>) — dùng để lấy
    /// access token.
    /// </summary>
    public string ClientId { get; set; } = string.Empty;

    /// <summary>
    /// True nếu muốn worker tự động gửi mã đã quét lên Checkee.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Chu kỳ gửi (phút) — worker gom các mã chưa gửi rồi gửi lên Checkee
    /// mỗi khoảng thời gian này. Tối thiểu 1 phút.
    /// </summary>
    public int SendIntervalMinutes { get; set; } = 15;

    /// <summary>
    /// Số mã tối đa gửi trong một lần (một batch). Tối đa 500.
    /// </summary>
    public int BatchSize { get; set; } = 100;

    /// <summary>
    /// Timeout (giây) cho mỗi lần gọi API Checkee.
    /// </summary>
    public int TimeoutSeconds { get; set; } = 30;

    /// <summary>
    /// True nếu chỉ gửi những mã có kết quả quét là <c>Pass</c>.
    /// Mặc định true — chỉ mã hợp lệ mới được kích hoạt trên Checkee.
    /// </summary>
    public bool OnlyPassCodes { get; set; } = true;

    /// <summary>
    /// Mã lô / batch code gửi kèm khi kích hoạt mã (trường <c>batchCode</c>
    /// của API <c>POST /middleware-api/CTEs/active</c>).
    /// </summary>
    public string BatchCode { get; set; } = string.Empty;

    /// <summary>
    /// Loại mã TXNG gửi lên Checkee: <c>RANDOM</c>, <c>DECLARE</c> hoặc
    /// <c>INCREASING</c> (trường <c>type</c> của API kích hoạt).
    /// </summary>
    public string SerialType { get; set; } = "DECLARE";

    /// <summary>
    /// True nếu đã cấu hình đủ thông tin để gọi API Checkee.
    /// </summary>
    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(BaseUrl)
        && !string.IsNullOrWhiteSpace(ApiKey)
        && !string.IsNullOrWhiteSpace(ClientId);

    /// <summary>Base URL đã chuẩn hoá (bỏ dấu '/' cuối).</summary>
    public string ResolveBaseUrl() => BaseUrl.TrimEnd('/');

    /// <summary>Chu kỳ gửi đã chuẩn hoá (tối thiểu 1 phút).</summary>
    public TimeSpan ResolveSendInterval() =>
        TimeSpan.FromMinutes(Math.Clamp(SendIntervalMinutes, 1, 24 * 60));

    /// <summary>Timeout đã chuẩn hoá (tối thiểu 5 giây).</summary>
    public TimeSpan ResolveTimeout() =>
        TimeSpan.FromSeconds(Math.Clamp(TimeoutSeconds, 5, 600));
}
