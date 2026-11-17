namespace ClassLibraryMT.PoServer;

/// <summary>
/// Cấu hình HTTP server nhận Production Order từ MES (section <c>PoServer</c>).
///
/// <para>
/// Server này là chiều NGƯỢC của luồng "cào PO": thay vì máy đi hỏi server PO
/// thì MES chủ động <b>đẩy</b> PO xuống máy qua <c>POST /api/po</c>.
/// </para>
///
/// <para>
/// Mặc định chỉ lắng nghe <c>127.0.0.1</c> (chỉ máy cục bộ truy cập được).
/// Khi muốn MES ở máy khác truy cập, đổi <see cref="BindAddress"/> thành
/// <c>0.0.0.0</c> hoặc IP của máy — khi đó trên Windows cần chạy app với
/// quyền quản trị (để đăng ký URL ACL) hoặc chạy lệnh
/// <c>netsh http add urlacl url=http://+:8085/ user=Everyone</c>.
/// </para>
/// </summary>
public sealed class PoServerOptions
{
    /// <summary>Tên section cấu hình.</summary>
    public const string SectionName = "PoServer";

    /// <summary>
    /// True nếu bật HTTP server nhận PO từ MES.
    /// Tắt thì server KHÔNG lắng nghe (tiết kiệm tài nguyên).
    /// </summary>
    public bool Enabled { get; set; } = false;

    /// <summary>
    /// Cổng TCP của HTTP server (1-65535). Mặc định 8085.
    /// </summary>
    public int Port { get; set; } = 8085;

    /// <summary>
    /// Địa chỉ IP mà server lắng nghe.
    ///
    /// <para>
    /// - <c>127.0.0.1</c> (mặc định): chỉ máy cục bộ truy cập được, KHÔNG
    ///   cần quyền admin.
    /// - <c>0.0.0.0</c> hoặc IP LAN: mọi máy trong mạng truy cập được, trên
    ///   Windows thường cần quyền admin để đăng ký URL ACL.
    /// - <c>+</c>: tương đương <c>0.0.0.0</c> (HttpListener hiểu prefix <c>+</c>).
    /// </para>
    /// </summary>
    public string BindAddress { get; set; } = "127.0.0.1";

    /// <summary>
    /// API key kiểm tra ở header <c>x-api-key</c>.
    ///
    /// <para>
    /// Rỗng (mặc định) = KHÔNG yêu cầu xác thực (tiện cho mạng nội bộ /
    /// khi mới cài đặt). Nếu đặt giá trị thì mọi request phải có header
    /// <c>x-api-key</c> khớp, sai thì trả về HTTP 401.
    /// </para>
    /// </summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>
    /// Dung lượng tối đa cho body của một request (MB). Mặc định 64 MB.
    /// Vượt quá -> trả về HTTP 413.
    /// </summary>
    public int MaxBodyMB { get; set; } = 64;

    /// <summary>
    /// Thời gian chờ tối đa cho một request (giây). Mặc định 120 giây.
    /// Dùng cho PO rất lớn (vài chục nghìn mã).
    /// </summary>
    public int RequestTimeoutSeconds { get; set; } = 120;

    /// <summary>
    /// Số request tối đa xử lý đồng thời. Mặc định 4.
    /// </summary>
    public int MaxConcurrentRequests { get; set; } = 4;

    /// <summary>
    /// True nếu <see cref="BindAddress"/> là mọi interface (0.0.0.0 hoặc +).
    /// </summary>
    public bool IsAllInterfaces
    {
        get
        {
            var addr = (BindAddress ?? string.Empty).Trim();
            return addr is "0.0.0.0" or "+" or "*" or "[::]";
        }
    }

    /// <summary>
    /// Dung lượng body tối đa (byte) — đã chuyển sang byte.
    /// </summary>
    public int MaxBodyBytes => (int)Math.Clamp(MaxBodyMB, 1, 1024) * 1024 * 1024;

    /// <summary>
    /// Thời gian chờ tối đa cho một request.
    /// </summary>
    public TimeSpan ResolveRequestTimeout() =>
        TimeSpan.FromSeconds(Math.Clamp(RequestTimeoutSeconds, 5, 3600));

    /// <summary>
    /// Số request xử lý đồng thời tối đa (ít nhất 1).
    /// </summary>
    public int ResolveMaxConcurrentRequests() => Math.Clamp(MaxConcurrentRequests, 1, 64);

    /// <summary>
    /// Dựng prefix HttpListener từ cấu hình (VD: <c>http://127.0.0.1:8085/</c>).
    /// </summary>
    public string ResolvePrefix()
    {
        var addr = string.IsNullOrWhiteSpace(BindAddress)
            ? "127.0.0.1"
            : BindAddress.Trim();

        // Người dùng có thể gõ kèm scheme / port -> chuẩn hoá về IP + port.
        if (addr.Contains("://", StringComparison.Ordinal))
        {
            addr = addr["://".Length..];
        }

        var slash = addr.IndexOf('/');
        if (slash >= 0)
        {
            addr = addr[..slash];
        }

        // Bỏ port nếu người dùng gõ kèm (VD "127.0.0.1:9000").
        var colon = addr.LastIndexOf(':');
        if (colon > 0 && int.TryParse(addr[(colon + 1)..], out _))
        {
            addr = addr[..colon];
        }

        if (addr.Length == 0)
        {
            addr = "127.0.0.1";
        }

        // Prefix dạng "+" (mọi interface) -> HttpListener dùng "+" chứ không
        // phải "0.0.0.0" (0.0.0.0 sẽ khớp cả IPv6 và gây lỗi).
        var host = addr switch
        {
            "0.0.0.0" or "*" => "+",
            _ => addr,
        };

        return $"http://{host}:{Port}/";
    }
}
