using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using ClassLibraryMT.Po;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ClassLibraryMT.PoServer;

/// <summary>
/// Trạng thái của HTTP server nhận PO (dùng cho UI hiển thị).
///
/// <para>
/// Các thuộc tính được ghi từ luồng listener nên UI đọc qua
/// <see cref="Status"/> / <see cref="IsError"/>; dùng
/// <see cref="Volatile"/> để đọc nhất quán.
/// </para>
/// </summary>
public sealed class PoServerState
{
    private readonly Lock _gate = new();
    private string _status = "Chưa chạy.";
    private bool _isError;
    private int _receivedCount;
    private DateTime? _startedUtc;

    /// <summary>Thông điệp trạng thái gần nhất (tiếng Việt).</summary>
    public string Status => Volatile.Read(ref _status);

    /// <summary>True nếu thông điệp hiện tại là lỗi (hiện màu đỏ).</summary>
    public bool IsError => Volatile.Read(ref _isError);

    /// <summary>Số PO đã nhận được từ lúc bật server.</summary>
    public int ReceivedCount => Volatile.Read(ref _receivedCount);

    /// <summary>Thời điểm khởi động server (UTC), null nếu chưa chạy.</summary>
    public DateTime? StartedUtc
    {
        get
        {
            lock (_gate)
            {
                return _startedUtc;
            }
        }
    }

    /// <summary>Cập nhật thông điệp trạng thái.</summary>
    public void SetStatus(string message, bool isError)
    {
        Volatile.Write(ref _status, message ?? string.Empty);
        Volatile.Write(ref _isError, isError);
    }

    /// <summary>Đánh dấu server đã khởi động.</summary>
    public void MarkStarted(DateTime utc)
    {
        lock (_gate)
        {
            _startedUtc = utc;
        }
    }

    /// <summary>Tăng bộ đếm số PO đã nhận.</summary>
    public void IncrementReceived() => Interlocked.Increment(ref _receivedCount);
}

/// <summary>
/// Kết quả xử lý một yêu cầu nhận PO (nội bộ).
/// </summary>
/// <param name="StatusCode">Mã HTTP trả về.</param>
/// <param name="Response">Nội dung phản hồi.</param>
public readonly record struct PoPushOutcome(int StatusCode, PoPushResponse Response);

/// <summary>
/// HTTP server nhận Production Order từ MES.
///
/// <para>
/// <b>Endpoint:</b>
/// <list type="bullet">
/// <item><c>POST /api/po</c> — nhận PO + tập mã.</item>
/// <item><c>GET /api/health</c> — kiểm tra server còn sống.</item>
/// </list>
/// </para>
///
/// <para>
/// <b>Nguyên tắc quan trọng:</b>
/// <list type="bullet">
/// <item>Mọi I/O chạy trên luồng nền — KHÔNG bao giờ chạm vào luồng UI.</item>
/// <item>Mỗi request được xử lý trong một task riêng, có giới hạn số
/// request đồng thời.</item>
/// <item>Body bị giới hạn cỡ (<see cref="PoServerOptions.MaxBodyMB"/>) và
/// có timeout cho mỗi request.</item>
/// <item>Mọi lỗi đều được bắt và ghi log — server KHÔNG bao giờ crash app.</item>
/// </list>
/// </para>
///
/// <para>
/// PO nhận được ghi vào <b>chính DB PO local</b> (qua <see cref="PoStore"/>)
/// nên nó xuất hiện ngay trong tab "Truy xuất Sản xuất (Local)" và trong danh
/// sách chọn PO ở tab "Thông tin sản xuất", y hệt PO cào từ server.
/// </para>
/// </summary>
public sealed class PoHttpServer : IDisposable
{
    /// <summary>Header dùng để truyền API key.</summary>
    public const string ApiKeyHeader = "x-api-key";

    /// <summary>Đường dẫn endpoint nhận PO.</summary>
    public const string PoPath = "/api/po";

    /// <summary>Đường dẫn endpoint kiểm tra sống.</summary>
    public const string HealthPath = "/api/health";

    /// <summary>Số ký tự tối đa cho mã PO (chống mã quá dài / rác).</summary>
    private const int MaxPoNameLength = 200;

    /// <summary>
    /// Số mã trùng tối đa liệt kê trong thông báo lỗi (ví dụ cho người dùng).
    /// </summary>
    private const int MaxDuplicateSamples = 5;

    private readonly PoServerOptions _opt;
    private readonly PoStore _store;
    private readonly ILogger<PoHttpServer> _logger;
    private readonly PoServerState _state = new();

    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _throttle;
    private readonly HttpListener? _listener = new();

    /// <summary>Các task đang chạy (vòng lặp accept + xử lý request).</summary>
    private readonly ConcurrentBag<Task> _tasks = [];

    /// <summary>Cờ chống dispose 2 lần.</summary>
    private bool _disposed;

    /// <summary>Cờ đã bắt đầu chưa.</summary>
    private bool _started;

    /// <summary>Lock bảo vệ cờ <see cref="_started"/>.</summary>
    private readonly Lock _startGate = new();

    /// <summary>
    /// Khởi tạo server.
    /// </summary>
    /// <param name="options">Cấu hình (section <c>PoServer</c>).</param>
    /// <param name="store">Store lưu PO — dùng chung với luồng cào PO.</param>
    /// <param name="logger">Logger kỹ thuật.</param>
    public PoHttpServer(
        IOptions<PoServerOptions> options,
        PoStore store,
        ILogger<PoHttpServer> logger)
    {
        _opt = options?.Value ?? new PoServerOptions();
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        _throttle = new SemaphoreSlim(_opt.ResolveMaxConcurrentRequests());
    }

    /// <summary>
    /// Event phát khi nhận được PO mới từ MES.
    ///
    /// <para>
    /// Tham số là mã PO vừa nhận. UI dùng event này để tải lại danh sách PO
    /// (không cần bấm "Tải PO từ server").
    /// </para>
    /// </summary>
    public event EventHandler<string>? PoReceived;

    /// <summary>Trạng thái server (để UI hiển thị).</summary>
    public PoServerState State => _state;

    /// <summary>Prefix server đang lắng nghe (VD <c>http://127.0.0.1:8085/</c>).</summary>
    public string Prefix => _opt.ResolvePrefix();

    /// <summary>
    /// Bắt đầu lắng nghe. Gọi 1 lần khi app khởi động.
    ///
    /// <para>
    /// Không chặn: mọi thao tác I/O chạy nền. Lỗi bind (port đã dùng, thiếu
    /// quyền) được ghi vào <see cref="State"/> chứ KHÔNG làm crash app.
    /// </para>
    /// </summary>
    public void Start()
    {
        if (_disposed)
        {
            return;
        }

        lock (_startGate)
        {
            if (_started)
            {
                return;
            }

            _started = true;
        }

        if (!_opt.Enabled)
        {
            _state.SetStatus("Đã tắt (PoServer:Enabled = false).", isError: false);
            _logger.LogInformation(
                "HTTP server nhận PO không chạy (PoServer:Enabled = false).");
            return;
        }

        var prefix = _opt.ResolvePrefix();

        try
        {
            _listener!.Prefixes.Add(prefix);
            _listener.Start();
        }
        catch (HttpListenerException ex)
        {
            // Port đã dùng / không có quyền đăng ký URL ACL -> báo cho UI,
            // KHÔNG crash app.
            var message = DescribeBindError(ex, prefix);
            _state.SetStatus(message, isError: true);
            _logger.LogError(ex, "Không khởi động được HTTP server nhận PO: {Message}", message);
            return;
        }
        catch (Exception ex)
        {
            var message = "Không khởi động được HTTP server nhận PO: " + ex.Message;
            _state.SetStatus(message, isError: true);
            _logger.LogError(ex, "Lỗi khởi động HTTP server nhận PO.");
            return;
        }

        var startedUtc = DateTime.UtcNow;
        _state.MarkStarted(startedUtc);
        _state.SetStatus($"Đang lắng nghe {prefix}", isError: false);

        _logger.LogInformation(
            "HTTP server nhận PO đang lắng nghe {Prefix} (POST {PoPath}, GET {HealthPath}).",
            prefix, PoPath, HealthPath);

        if (_opt.IsAllInterfaces)
        {
            _logger.LogWarning(
                "HTTP server nhận PO đang lắng nghe MỌI interface ({Prefix}). "
                + "Trên Windows cần quyền quản trị hoặc urlacl cho prefix này; "
                + "nhớ mở firewall cho cổng {Port}.",
                prefix, _opt.Port);
        }

        _tasks.Add(Task.Run(() => AcceptLoopAsync(_cts.Token), CancellationToken.None));
    }

    /// <summary>Dừng lắng nghe và giải phóng tài nguyên.</summary>
    public void Stop()
    {
        try
        {
            _cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Đã dispose rồi.
        }

        try
        {
            _listener?.Stop();
            _listener?.Close();
        }
        catch (Exception ex) when (ex is ObjectDisposedException or HttpListenerException)
        {
            // Bỏ qua: shutdown bình thường.
        }

        _state.SetStatus("Đã dừng", isError: false);
    }

    /// <summary>
    /// Vòng lặp nhận request. Chạy hoàn toàn trên luồng nền.
    /// </summary>
    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        var listener = _listener;
        if (listener is null)
        {
            return;
        }

        while (!ct.IsCancellationRequested)
        {
            HttpListenerContext context;

            try
            {
                context = await listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (HttpListenerException ex)
            {
                // Listener bị đóng giữa chừng (shutdown) -> kết thúc vòng lặp.
                _logger.LogDebug(ex, "HTTP server nhận PO: listener đã đóng.");
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "HTTP server nhận PO: lỗi khi chờ request.");
                continue;
            }

            // Mỗi request một task riêng -> không block vòng lặp accept.
            _tasks.Add(HandleSafelyAsync(context, ct));
        }
    }

    /// <summary>
    /// Bọc xử lý request để KHÔNG BAO GIỜ ném exception ra ngoài.
    /// </summary>
    private async Task HandleSafelyAsync(HttpListenerContext context, CancellationToken ct)
    {
        try
        {
            await HandleAsync(context, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Request bị hủy / app đang shutdown -> bỏ qua.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Lỗi không lường trước khi xử lý request nhận PO.");

            try
            {
                await WriteJsonAsync(
                    context.Response,
                    500,
                    new PoPushResponse
                    {
                        Success = false,
                        Message = "Lỗi máy chủ khi xử lý yêu cầu: " + ex.Message,
                    }).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Response đã gửi / đã đóng -> bỏ qua.
            }
        }
        finally
        {
            try
            {
                context.Response.Close();
            }
            catch (Exception)
            {
                // Bỏ qua.
            }
        }
    }

    /// <summary>
    /// Xử lý một request HTTP.
    /// </summary>
    private async Task HandleAsync(HttpListenerContext context, CancellationToken ct)
    {
        var request = context.Request;
        var response = context.Response;
        var path = request.Url?.AbsolutePath.TrimEnd('/') ?? string.Empty;

        // ---- Kiểm tra API key (nếu có cấu hình) ----
        if (!CheckApiKey(request))
        {
            await WriteJsonAsync(
                response,
                401,
                new PoPushResponse
                {
                    Success = false,
                    Message = "Thiếu hoặc sai API key (header x-api-key).",
                }).ConfigureAwait(false);
            return;
        }

        // ---- GET /api/health ----
        if (request.HttpMethod.Equals("GET", StringComparison.OrdinalIgnoreCase)
            && path.Equals(HealthPath, StringComparison.OrdinalIgnoreCase))
        {
            await WriteJsonAsync(
                response,
                200,
                new PoHealthResponse
                {
                    Success = true,
                    Message = "HTTP server nhận PO đang hoạt động.",
                    Prefix = Prefix,
                    Received = _state.ReceivedCount,
                    StartedUtc = _state.StartedUtc ?? DateTime.MinValue,
                }).ConfigureAwait(false);
            return;
        }

        // ---- POST /api/po ----
        if (request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase)
            && path.Equals(PoPath, StringComparison.OrdinalIgnoreCase))
        {
            await _throttle.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(_opt.ResolveRequestTimeout());

                var outcome = await HandlePushAsync(request, timeoutCts.Token).ConfigureAwait(false);

                await WriteJsonAsync(response, outcome.StatusCode, outcome.Response)
                    .ConfigureAwait(false);
            }
            finally
            {
                _throttle.Release();
            }

            return;
        }

        // ---- Endpoint không tồn tại / method không hợp lệ ----
        await WriteJsonAsync(
            response,
            404,
            new PoPushResponse
            {
                Success = false,
                Message = $"Endpoint không tồn tại: {request.HttpMethod} {path}. "
                    + $"Dùng POST {PoPath} hoặc GET {HealthPath}.",
            }).ConfigureAwait(false);
    }

    /// <summary>
    /// Kiểm tra API key trên header <c>x-api-key</c>.
    /// Rỗng (không cấu hình) -> luôn cho qua.
    /// </summary>
    private bool CheckApiKey(HttpListenerRequest request)
    {
        var expected = _opt.ApiKey?.Trim() ?? string.Empty;
        if (expected.Length == 0)
        {
            return true;
        }

        var provided = request.Headers[ApiKeyHeader];
        return string.Equals(provided, expected, StringComparison.Ordinal);
    }

    /// <summary>
    /// Xử lý body của <c>POST /api/po</c>: đọc, kiểm tra và lưu PO.
    /// </summary>
    private async Task<PoPushOutcome> HandlePushAsync(
        HttpListenerRequest request,
        CancellationToken ct)
    {
        // ---- Kiểm tra dung lượng body ----
        var maxBytes = _opt.MaxBodyBytes;
        if (request.ContentLength64 > maxBytes)
        {
            return new PoPushOutcome(
                413,
                new PoPushResponse
                {
                    Success = false,
                    Message = $"Body vượt quá dung lượng cho phép ({_opt.MaxBodyMB} MB).",
                });
        }

        // ---- Đọc body (bỏ qua giới hạn Content-Length vì có thể không có) ----
        byte[] body;
        try
        {
            body = await ReadBodyAsync(request, maxBytes, ct).ConfigureAwait(false);
        }
        catch (BodyTooLargeException)
        {
            return new PoPushOutcome(
                413,
                new PoPushResponse
                {
                    Success = false,
                    Message = $"Body vượt quá dung lượng cho phép ({_opt.MaxBodyMB} MB).",
                });
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return new PoPushOutcome(
                408,
                new PoPushResponse
                {
                    Success = false,
                    Message = "Yêu cầu bị hủy do quá thời gian chờ.",
                });
        }

        if (body.Length == 0)
        {
            return new PoPushOutcome(
                400,
                new PoPushResponse
                {
                    Success = false,
                    Message = "Body rỗng — cần gửi JSON gồm poName, orderQty và codes/csv.",
                });
        }

        // ---- Phân biệt JSON / CSV / multipart ----
        var contentType = request.ContentType ?? string.Empty;
        var isMultipart = contentType.Contains("multipart/form-data", StringComparison.OrdinalIgnoreCase);
        var isCsvUpload = contentType.Contains("text/csv", StringComparison.OrdinalIgnoreCase)
            || contentType.Contains("application/csv", StringComparison.OrdinalIgnoreCase)
            || contentType.Contains("application/vnd.ms-excel", StringComparison.OrdinalIgnoreCase);

        PoPushRequest? payload;

        if (isMultipart)
        {
            payload = await ParseMultipartAsync(request, body, ct).ConfigureAwait(false);
        }
        else if (isCsvUpload)
        {
            // Upload file CSV thuần: metadata đi kèm qua query string.
            payload = BuildCsvOnlyRequest(request, body);
        }
        else
        {
            try
            {
                payload = JsonSerializer.Deserialize(
                    body, PoServerJsonContext.Default.PoPushRequest);
            }
            catch (JsonException ex)
            {
                return new PoPushOutcome(
                    400,
                    new PoPushResponse
                    {
                        Success = false,
                        Message = "Body JSON không hợp lệ: " + ex.Message,
                    });
            }
        }

        if (payload is null)
        {
            return new PoPushOutcome(
                400,
                new PoPushResponse
                {
                    Success = false,
                    Message = "Không đọc được nội dung yêu cầu.",
                });
        }

        return await SavePoAsync(payload, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Đọc body của request, dừng nếu vượt quá <paramref name="maxBytes"/>.
    /// </summary>
    private static async Task<byte[]> ReadBodyAsync(
        HttpListenerRequest request,
        int maxBytes,
        CancellationToken ct)
    {
        var input = request.InputStream;

        using var buffer = new MemoryStream();
        var readBuffer = new byte[81920];

        while (true)
        {
            int read;
            try
            {
                read = await input
                    .ReadAsync(readBuffer, ct)
                    .ConfigureAwait(false);
            }
            catch (IOException)
            {
                // Client ngắt kết nối giữa chừng -> coi như body đọc được
                // đến lúc đó.
                break;
            }

            if (read <= 0)
            {
                break;
            }

            if (buffer.Length + read > maxBytes)
            {
                throw new BodyTooLargeException();
            }

            buffer.Write(readBuffer, 0, read);
        }

        return buffer.ToArray();
    }

    /// <summary>
    /// Dựng request từ file CSV thuần (text/csv) + query string.
    ///
    /// <para>
    /// Metadata đi kèm dạng query string:
    /// <c>?poName=PO-001&amp;orderQty=100&amp;gtin=089...</c>
    /// </para>
    /// </summary>
    private static PoPushRequest BuildCsvOnlyRequest(
        HttpListenerRequest request,
        byte[] body)
    {
        var query = request.QueryString;

        var poName = query["poName"] ?? query["po"] ?? string.Empty;
        var gtin = query["gtin"] ?? string.Empty;

        var orderQty = 0;
        var qtyRaw = query["orderQty"] ?? query["qty"] ?? string.Empty;
        if (int.TryParse(qtyRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var qty))
        {
            orderQty = qty;
        }

        return new PoPushRequest
        {
            PoName = poName,
            OrderQty = orderQty,
            Gtin = gtin,
            Csv = Encoding.UTF8.GetString(body),
        };
    }

    /// <summary>
    /// Đọc multipart/form-data: lấy phần file CSV + các field metadata.
    /// </summary>
    private static async Task<PoPushRequest?> ParseMultipartAsync(
        HttpListenerRequest request,
        byte[] body,
        CancellationToken ct)
    {
        var boundary = GetBoundary(request.ContentType);
        if (string.IsNullOrEmpty(boundary))
        {
            return null;
        }

        var separator = Encoding.UTF8.GetBytes("--" + boundary);
        var parts = SplitParts(body, separator);

        var payload = new PoPushRequest();
        string? csv = null;

        foreach (var part in parts)
        {
            ct.ThrowIfCancellationRequested();

            // Bỏ qua phần đầu / cuối.
            if (part.Length == 0)
            {
                continue;
            }

            var headerEnd = IndexOfSequence(part, Encoding.UTF8.GetBytes("\r\n\r\n"));
            if (headerEnd < 0)
            {
                continue;
            }

            var headerText = Encoding.UTF8.GetString(part, 0, headerEnd);
            var contentStart = headerEnd + 4;

            // Cắt phần CRLF cuối (trước boundary kế tiếp).
            var contentLength = part.Length - contentStart;
            if (contentLength >= 2)
            {
                contentLength -= 2;
            }

            if (contentLength <= 0)
            {
                continue;
            }

            var name = GetDispositionParameter(headerText, "name");
            var fileName = GetDispositionParameter(headerText, "filename");
            var content = Encoding.UTF8.GetString(part, contentStart, contentLength);

            if (!string.IsNullOrEmpty(fileName))
            {
                // Phần file -> nội dung CSV.
                csv = content;
                continue;
            }

            switch ((name ?? string.Empty).ToLowerInvariant())
            {
                case "poname":
                case "po":
                    payload.PoName = content;
                    break;
                case "orderqty":
                case "qty":
                    if (int.TryParse(
                        content.Trim(), NumberStyles.Integer,
                        CultureInfo.InvariantCulture, out var qty))
                    {
                        payload.OrderQty = qty;
                    }

                    break;
                case "gtin":
                    payload.Gtin = content;
                    break;
                case "batchcode":
                    payload.BatchCode = content;
                    break;
                case "productname":
                    payload.ProductName = content;
                    break;
                case "customer":
                    payload.Customer = content;
                    break;
            }
        }

        payload.Csv = csv;
        return payload;
    }

    /// <summary>
    /// Tách body multipart thành các phần (theo boundary).
    /// </summary>
    private static List<byte[]> SplitParts(byte[] body, byte[] separator)
    {
        var parts = new List<byte[]>();
        var index = 0;

        while (index < body.Length)
        {
            var next = IndexOfSequence(body, separator, index);
            if (next < 0)
            {
                break;
            }

            var start = next + separator.Length;

            // Bỏ CRLF ngay sau boundary.
            if (start + 1 < body.Length && body[start] == '\r' && body[start + 1] == '\n')
            {
                start += 2;
            }

            var nextSep = IndexOfSequence(body, separator, start);
            var end = nextSep < 0 ? body.Length : nextSep;

            if (end > start)
            {
                parts.Add(body[start..end]);
            }

            index = nextSep < 0 ? body.Length : nextSep;
        }

        return parts;
    }

    /// <summary>Tìm vị trí đầu tiên của một mảng byte con trong mảng byte.</summary>
    private static int IndexOfSequence(byte[] haystack, byte[] needle, int start = 0)
    {
        if (needle.Length == 0 || haystack.Length < needle.Length)
        {
            return -1;
        }

        var last = haystack.Length - needle.Length;
        for (var i = Math.Max(0, start); i <= last; i++)
        {
            var found = true;
            for (var j = 0; j < needle.Length; j++)
            {
                if (haystack[i + j] != needle[j])
                {
                    found = false;
                    break;
                }
            }

            if (found)
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>
    /// Lấy giá trị một tham số trong header <c>Content-Disposition</c> của phần
    /// multipart (VD <c>name="poName"</c>, <c>filename="codes.csv"</c>).
    /// </summary>
    private static string? GetDispositionParameter(string headerText, string parameterName)
    {
        foreach (var line in headerText.Split('\n'))
        {
            var trimmed = line.Trim();
            if (!trimmed.StartsWith("Content-Disposition", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // Tìm "name=" / "filename=" trong chuỗi tham số.
            var index = trimmed.IndexOf(parameterName + "=", StringComparison.OrdinalIgnoreCase);
            if (index < 0)
            {
                continue;
            }

            // Bỏ qua ký tự trước "=" nếu là dấu phân cách (VD "filename" khi
            // đang tìm "name" -> "e=" khớp -> phải bỏ qua).
            if (index > 0 && !char.IsWhiteSpace(trimmed[index - 1]) && trimmed[index - 1] != ';')
            {
                continue;
            }

            var valueStart = index + parameterName.Length + 1;
            if (valueStart >= trimmed.Length)
            {
                continue;
            }

            var raw = trimmed[valueStart..];

            // Giá trị có thể được bọc trong dấu nháy kép.
            if (raw.StartsWith('"'))
            {
                var closing = raw.IndexOf('"', 1);
                return closing > 0 ? raw[1..closing] : raw[1..].TrimEnd();
            }

            var end = raw.IndexOf(';');
            return (end >= 0 ? raw[..end] : raw).Trim();
        }

        return null;
    }

    /// <summary>Lấy boundary từ header Content-Type.</summary>
    private static string? GetBoundary(string? contentType)
    {
        if (string.IsNullOrEmpty(contentType))
        {
            return null;
        }

        foreach (var part in contentType.Split(';'))
        {
            var trimmed = part.Trim();
            if (!trimmed.StartsWith("boundary=", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            return trimmed["boundary=".Length..].Trim().Trim('"');
        }

        return null;
    }

    /// <summary>
    /// Kiểm tra + lưu PO nhận từ MES.
    /// </summary>
    private async Task<PoPushOutcome> SavePoAsync(
        PoPushRequest payload,
        CancellationToken ct)
    {
        var poName = (payload.PoName ?? string.Empty).Trim();

        if (poName.Length == 0)
        {
            return new PoPushOutcome(
                400,
                new PoPushResponse
                {
                    Success = false,
                    Message = "Thiếu trường poName (tên/mã lệnh sản xuất).",
                });
        }

        if (poName.Length > MaxPoNameLength)
        {
            return new PoPushOutcome(
                400,
                new PoPushResponse
                {
                    Success = false,
                    Message = $"poName quá dài (tối đa {MaxPoNameLength} ký tự).",
                });
        }

        if (payload.OrderQty <= 0)
        {
            return new PoPushOutcome(
                400,
                new PoPushResponse
                {
                    Success = false,
                    Message = "Trường orderQty phải lớn hơn 0.",
                    PoName = poName,
                });
        }

        // ---- Tập mã: ưu tiên codes, sau đó tới csv ----
        var rawCodes = CollectRawCodes(payload);
        if (rawCodes.Count == 0)
        {
            return new PoPushOutcome(
                400,
                new PoPushResponse
                {
                    Success = false,
                    Message = "Thiếu tập mã: cần gửi \"codes\" (mảng JSON) hoặc \"csv\" (nội dung CSV).",
                    PoName = poName,
                });
        }

        var fallbackGtin = (payload.Gtin ?? string.Empty).Trim();

        // Trim + bỏ rỗng + loại trùng.
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var codes = new List<PoCode>(rawCodes.Count);

        foreach (var raw in rawCodes)
        {
            if (!PoCodeParser.TryParse(raw, fallbackGtin, out var gtin, out var serial))
            {
                continue;
            }

            if (!seen.Add(serial))
            {
                continue;
            }

            codes.Add(new PoCode
            {
                Code = BuildCode(gtin, serial),
                Gtin = gtin,
                Serial = serial,
            });
        }

        if (codes.Count == 0)
        {
            return new PoPushOutcome(
                400,
                new PoPushResponse
                {
                    Success = false,
                    Message = "Không tìm được mã nào hợp lệ trong tập mã gửi lên.",
                    PoName = poName,
                });
        }

        // ---- Kiểm tra số lượng ----
        // Chấp nhận khi codes >= orderQty (server có thể gửi thừa).
        // Thiếu -> 400 để MES biết phải gửi đủ.
        if (codes.Count < payload.OrderQty)
        {
            return new PoPushOutcome(
                400,
                new PoPushResponse
                {
                    Success = false,
                    Message = $"Số mã gửi lên ({codes.Count}) ít hơn orderQty ({payload.OrderQty}).",
                    PoName = poName,
                    Accepted = codes.Count,
                });
        }

        // ---- Trùng PO đã có ----
        if (_store.FindExistingDbPath(poName) is not null)
        {
            return new PoPushOutcome(
                409,
                new PoPushResponse
                {
                    Success = false,
                    Message = $"PO '{poName}' đã tồn tại trên máy — không thể nhận lại.",
                    PoName = poName,
                });
        }

        // ---- Mã trùng với PO KHÁC đã có trên máy ----
        // So khớp theo khoá chuẩn "Gtin|Serial" — đúng cách dùng khi quét mã,
        // nên Digital Link và mã trần cùng GTIN/serial vẫn bị coi là trùng.
        try
        {
            var keys = new List<string>(codes.Count);
            foreach (var code in codes)
            {
                keys.Add(PoStore.BuildCodeKey(code.Gtin, code.Serial));
            }

            var duplicates = await _store
                .FindDuplicateCodesAsync(keys, poName, MaxDuplicateSamples, ct)
                .ConfigureAwait(false);

            if (duplicates.HasDuplicates)
            {
                var owner = string.IsNullOrEmpty(duplicates.ExistingOrderNo)
                    ? "một PO khác trên máy"
                    : $"PO '{duplicates.ExistingOrderNo}'";

                var sampleText = duplicates.SampleCodes.Count > 0
                    ? string.Join(", ", duplicates.SampleCodes.Take(MaxDuplicateSamples))
                    : "(không lấy được ví dụ)";

                return new PoPushOutcome(
                    409,
                    new PoPushResponse
                    {
                        Success = false,
                        Message =
                            $"PO bị từ chối: {duplicates.TotalDuplicates} mã đã tồn tại trong {owner} "
                            + $"(ví dụ: {sampleText}). Không thể nhận PO có mã trùng.",
                        PoName = poName,
                        Accepted = 0,
                    });
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return new PoPushOutcome(
                408,
                new PoPushResponse
                {
                    Success = false,
                    Message = "Yêu cầu bị hủy khi kiểm tra mã trùng.",
                    PoName = poName,
                });
        }
        catch (Exception ex)
        {
            // Lỗi kiểm tra mã trùng KHÔNG được chặn PO — chỉ ghi log.
            _logger.LogError(ex, "Lỗi khi kiểm tra mã trùng cho PO {PoName}.", poName);
        }

        // ---- Trường mở rộng ----
        var additional = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (payload.Additional is not null)
        {
            foreach (var (key, value) in payload.Additional)
            {
                if (string.IsNullOrWhiteSpace(key))
                {
                    continue;
                }

                additional[key.Trim()] = value ?? string.Empty;
            }
        }

        AddIfNotEmpty(additional, "gtin", payload.Gtin);
        AddIfNotEmpty(additional, "batchCode", payload.BatchCode);
        AddIfNotEmpty(additional, "productName", payload.ProductName);
        AddIfNotEmpty(additional, "customer", payload.Customer);

        var order = new ProductionOrder
        {
            OrderNo = poName,
            OrderQty = payload.OrderQty,
            CreatedUtc = DateTime.UtcNow,
            Additional = additional,
        };

        // ---- Ghi xuống DB PO local ----
        try
        {
            await _store.EnsurePoDbAsync(order, codes, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return new PoPushOutcome(
                408,
                new PoPushResponse
                {
                    Success = false,
                    Message = "Yêu cầu bị hủy khi đang lưu PO.",
                    PoName = poName,
                });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Lỗi khi lưu PO {PoName} nhận từ MES.", poName);
            return new PoPushOutcome(
                500,
                new PoPushResponse
                {
                    Success = false,
                    Message = "Lỗi khi lưu PO: " + ex.Message,
                    PoName = poName,
                });
        }

        _state.IncrementReceived();

        _logger.LogInformation(
            "Đã nhận PO {PoName} từ MES: {Count} mã (orderQty {Qty}).",
            poName, codes.Count, payload.OrderQty);

        // Báo UI tải lại danh sách PO (bắt lỗi để không làm hỏng response).
        try
        {
            PoReceived?.Invoke(this, poName);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Lỗi trong handler nhận PO của UI.");
        }

        return new PoPushOutcome(
            200,
            new PoPushResponse
            {
                Success = true,
                Message = $"Đã nhận PO {poName} với {codes.Count} mã.",
                PoName = poName,
                Accepted = codes.Count,
            });
    }

    /// <summary>
    /// Gom tập mã thô từ <c>codes</c> (ưu tiên) hoặc <c>csv</c>.
    /// </summary>
    private static List<string> CollectRawCodes(PoPushRequest payload)
    {
        var result = new List<string>();

        if (payload.Codes is { Count: > 0 })
        {
            foreach (var code in payload.Codes)
            {
                if (!string.IsNullOrWhiteSpace(code))
                {
                    result.Add(code.Trim());
                }
            }

            if (result.Count > 0)
            {
                return result;
            }
        }

        if (!string.IsNullOrWhiteSpace(payload.Csv))
        {
            foreach (var line in ParseCsv(payload.Csv))
            {
                result.Add(line);
            }
        }

        return result;
    }

    /// <summary>
    /// Tách nội dung CSV thành danh sách mã (mỗi dòng một mã).
    ///
    /// <para>
    /// Dòng tiêu đề (VD "serial", "code", "ma") ở đầu file được bỏ qua.
    /// Dòng tiêu đề được nhận diện bằng cách: là dòng đầu tiên có nội dung,
    /// <b>không chứa chữ số</b> và khớp một trong các tên cột thường gặp
    /// (serial / code / ma / gtin / product / barcode...).
    /// </para>
    /// </summary>
    internal static IReadOnlyList<string> ParseCsv(string csv)
    {
        var result = new List<string>();
        var lines = csv.Split('\n');
        var headerChecked = false;

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim('\r', ' ', '\t');

            // Bỏ BOM ở đầu file.
            if (i == 0)
            {
                line = line.TrimStart('\uFEFF');
            }

            if (line.Length == 0)
            {
                continue;
            }

            // Chỉ dòng ĐẦU TIÊN có nội dung mới được coi là tiêu đề.
            if (!headerChecked)
            {
                headerChecked = true;

                if (IsHeaderLine(line))
                {
                    continue;
                }
            }

            result.Add(line);
        }

        return result;
    }

    /// <summary>
    /// Các tên cột thường dùng trong file CSV mã (không phân biệt hoa thường).
    /// </summary>
    private static readonly string[] HeaderNames =
    [
        "serial",
        "serials",
        "serialnumber",
        "serialno",
        "code",
        "codes",
        "ma",
        "mas",
        "barcode",
        "gtin",
        "product",
        "productname",
        "customer",
        "batch",
        "batchcode",
        "lot",
    ];

    /// <summary>
    /// Đoán xem một dòng có phải dòng tiêu đề của file CSV mã hay không.
    ///
    /// <para>
    /// Điều kiện: KHÔNG chứa chữ số (mã luôn có số hoặc ký tự đặc biệt)
    /// và khớp một tên cột thường gặp.
    /// </para>
    /// </summary>
    private static bool IsHeaderLine(string line)
    {
        foreach (var c in line)
        {
            if (char.IsDigit(c))
            {
                // Có chữ số -> gần như chắc chắn là mã, không phải tiêu đề.
                return false;
            }
        }

        // Bỏ ký tự phân tách / khoảng trắng để so sánh.
        var normalized = line
            .Replace(" ", string.Empty)
            .Replace("_", string.Empty)
            .Replace("-", string.Empty)
            .Trim()
            .ToLowerInvariant();

        foreach (var name in HeaderNames)
        {
            if (normalized == name)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Thêm trường mở rộng nếu có giá trị.</summary>
    private static void AddIfNotEmpty(
        Dictionary<string, string> target,
        string key,
        string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            target[key] = value.Trim();
        }
    }

    /// <summary>
    /// Dựng mã đầy đủ để so sánh (giống cách PoServer giả lập sinh mã).
    /// </summary>
    private static string BuildCode(string gtin, string serial)
    {
        if (string.IsNullOrEmpty(gtin))
        {
            return serial;
        }

        return $"https://digital-link.txng.vn/01/{gtin}/21/{serial}";
    }

    /// <summary>
    /// Ghi JSON ra response (dùng source-generated context — AOT-safe).
    /// </summary>
    private static Task WriteJsonAsync(
        HttpListenerResponse response,
        int statusCode,
        PoPushResponse value) =>
        WriteJsonCoreAsync(
            response,
            statusCode,
            value,
            PoServerJsonContext.Default.PoPushResponse);

    /// <summary>
    /// Ghi JSON ra response (dùng source-generated context — AOT-safe).
    /// </summary>
    private static Task WriteJsonAsync(
        HttpListenerResponse response,
        int statusCode,
        PoHealthResponse value) =>
        WriteJsonCoreAsync(
            response,
            statusCode,
            value,
            PoServerJsonContext.Default.PoHealthResponse);

    /// <summary>
    /// Ghi JSON ra response bằng JsonTypeInfo đã sinh sẵn (AOT-safe).
    /// </summary>
    private static async Task WriteJsonCoreAsync<T>(
        HttpListenerResponse response,
        int statusCode,
        T value,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo)
    {
        response.StatusCode = statusCode;
        response.ContentType = "application/json; charset=utf-8";

        await JsonSerializer
            .SerializeAsync(response.OutputStream, value, typeInfo)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Dịch lỗi bind sang thông điệp tiếng Việt dễ hiểu.
    ///
    /// <para>
    /// <see cref="HttpListenerException.ErrorCode"/> trả về mã lỗi Win32:
    /// 5 = ERROR_ACCESS_DENIED (thiếu quyền URL ACL),
    /// 183 = ERROR_ALREADY_EXISTS (prefix đã được đăng ký),
    /// 10048 = ERROR_ADDR_IN_USE (cổng đang bị chiếm).
    /// </para>
    /// </summary>
    private static string DescribeBindError(HttpListenerException ex, string prefix)
    {
        var code = ex.ErrorCode;

        if (code is 5 or 13)
        {
            return "Không có quyền lắng nghe " + prefix
                + ". Trên Windows hãy chạy app với quyền quản trị, "
                + "hoặc đăng ký URL ACL: netsh http add urlacl url=" + prefix + " user=Everyone";
        }

        if (code is 183 or 10048)
        {
            return "Cổng đang lắng nghe đã bị chiếm (" + prefix + "). "
                + "Hãy đổi PoServer:Port hoặc tắt ứng dụng khác đang dùng cổng này.";
        }

        return "Không lắng nghe được " + prefix + ": " + ex.Message;
    }

    /// <summary>
    /// Giải phóng tài nguyên.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        Stop();

        try
        {
            _cts.Dispose();
        }
        catch (ObjectDisposedException)
        {
            // Bỏ qua.
        }

        _throttle.Dispose();

        GC.SuppressFinalize(this);
    }
}

/// <summary>
/// Ném khi body của request vượt quá dung lượng cho phép.
/// </summary>
internal sealed class BodyTooLargeException : Exception
{
}
