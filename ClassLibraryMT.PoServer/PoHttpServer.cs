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

    private readonly PoServerOptions _opt;
    private readonly PoStore _store;
    private readonly ILogger<PoHttpServer> _logger;
    private readonly PoHistoryStore _history;
    private readonly PoServerState _state = new();

    /// <summary>Handler đọc file CodeContent và metadata riêng.</summary>
    private readonly PoUploadHandler _uploadHandler;

    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _throttle;
    private readonly HttpListener? _listener = new();

    /// <summary>Các task xử lý request; chỉ vòng lặp accept thêm phần tử.</summary>
    private readonly ConcurrentBag<Task> _tasks = [];
    private Task _acceptLoop = Task.CompletedTask;
    private bool _stopping;

    /// <summary>Cờ chống dispose 2 lần.</summary>
    private bool _disposed;

    /// <summary>Cờ đã bắt đầu chưa.</summary>
    private bool _started;

    /// <summary>Chốt từ prefix đã bind, không dựa cấu hình có thể đổi sau Start.</summary>
    private bool _boundToLoopback;

    /// <summary>Lock bảo vệ cờ <see cref="_started"/>.</summary>
    private readonly Lock _startGate = new();

    /// <summary>
    /// Khởi tạo server.
    /// </summary>
    /// <param name="options">Cấu hình (section <c>PoServer</c>).</param>
    /// <param name="store">Store lưu PO — dùng chung với luồng cào PO.</param>
    /// <param name="logger">Logger kỹ thuật.</param>
    /// <param name="history">Lịch sử sản xuất để chặn PO khi còn lần chạy dở dang.</param>
    public PoHttpServer(
        IOptions<PoServerOptions> options,
        PoStore store,
        ILogger<PoHttpServer> logger,
        PoHistoryStore? history = null)
    {
        _opt = options?.Value ?? new PoServerOptions();
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _history = history ?? new PoHistoryStore(store.DbRoot);

        _throttle = new SemaphoreSlim(_opt.ResolveMaxConcurrentRequests());

        // Handler upload: stream file xuống đĩa tạm, không nạp trọn vào RAM.
        var uploadRoot = Path.Combine(
            _opt.ResolveUploadTempDirectory(),
            "visionf-po-uploads");
        _uploadHandler = new PoUploadHandler(
            _opt,
            new PoUploadStore(uploadRoot, logger),
            logger);
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
        lock (_startGate)
        {
            if (_disposed || _started || _stopping) return;
            _started = true;
            StartCore();
        }
    }

    private void StartCore()
    {
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
            _boundToLoopback = Uri.TryCreate(prefix, UriKind.Absolute, out var uri) && uri.IsLoopback;
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

        // Dọn file tạm còn sót từ lần chạy trước (app bị tắt đột ngột).
        try
        {
            _uploadHandler.CleanupStaleFiles();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Không dọn được file tạm upload cũ.");
        }

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

        _acceptLoop = Task.Run(() => AcceptLoopAsync(_cts.Token), CancellationToken.None);
    }

    /// <summary>
    /// Báo dừng lắng nghe. Dùng <see cref="StopAsync"/> nếu cần chắc chắn mọi thao tác ghi đã kết thúc.
    /// </summary>
    public void Stop()
    {
        lock (_startGate)
        {
            if (_stopping) return;
            _stopping = true;
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
            _state.SetStatus("Đang chờ các yêu cầu nhận PO kết thúc…", isError: false);
        }
    }

    /// <summary>
    /// Ngừng nhận request rồi đợi toàn bộ handler đã nhận kết thúc, kể cả rollback DB/file tạm.
    /// Hủy token chỉ hủy việc chờ và ném lỗi; tuyệt đối không báo thành công khi còn handler.
    /// </summary>
    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        Stop();
        Task accepting;
        lock (_startGate) accepting = _acceptLoop;
        await accepting.WaitAsync(cancellationToken).ConfigureAwait(false);
        // Sau accept-loop, không task nào có thể thêm vào bag nữa.
        await Task.WhenAll(_tasks.ToArray()).WaitAsync(cancellationToken).ConfigureAwait(false);
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

        // Listener không có endpoint HEAD; trả status mà không ghi body HEAD.
        if (string.Equals(request.HttpMethod, "HEAD", StringComparison.Ordinal))
        {
            response.StatusCode = CheckApiKey(request) ? 404 : 401;
            response.ContentLength64 = 0;
            return;
        }

        // Chỉ route GET nguyên dạng được phục vụ; không giải mã đường dẫn thành file.
        var staticResource = ResolveDemoResource(request);
        if (staticResource is not null && IsLoopbackListener())
        {
            await WriteDemoResourceAsync(response, staticResource.Value).ConfigureAwait(false);
            return;
        }

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

        if (staticResource is not null)
        {
            await WriteDemoResourceAsync(response, staticResource.Value).ConfigureAwait(false);
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

                PoPushOutcome outcome;
                try
                {
                    outcome = await HandlePushAsync(request, timeoutCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    outcome = new PoPushOutcome(408, new PoPushResponse
                    {
                        Message = "Yêu cầu nhận PO quá thời gian chờ; hãy kiểm tra trạng thái trước khi gửi lại.",
                    });
                }

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

    private bool IsLoopbackListener() => _boundToLoopback;

    private static (string Name, string ContentType)? ResolveDemoResource(HttpListenerRequest request)
    {
        if (!string.Equals(request.HttpMethod, "GET", StringComparison.Ordinal)) return null;
        // RawUrl ngăn alias percent-encode, dot-segment hoặc backslash vượt route cố định.
        var rawPath = (request.RawUrl ?? string.Empty).Split('?', 2)[0];
        return rawPath switch
        {
            "/po-demo/" => ("ClassLibraryMT.PoServer.PoDemo.index.html", "text/html; charset=utf-8"),
            "/po-demo/validation.js" => ("ClassLibraryMT.PoServer.PoDemo.validation.js", "text/javascript; charset=utf-8"),
            _ => null,
        };
    }

    private static async Task WriteDemoResourceAsync(HttpListenerResponse response, (string Name, string ContentType) resource)
    {
        await using var stream = typeof(PoHttpServer).Assembly.GetManifestResourceStream(resource.Name)
            ?? throw new InvalidOperationException("Thiếu tài nguyên demo được đóng gói: " + resource.Name);
        response.StatusCode = 200;
        response.ContentType = resource.ContentType;
        response.ContentLength64 = stream.Length;
        response.Headers["X-Content-Type-Options"] = "nosniff";
        response.Headers["Cache-Control"] = "no-store";
        await stream.CopyToAsync(response.OutputStream).ConfigureAwait(false);
    }

    /// <summary>
    /// Xử lý body của <c>POST /api/po</c>: đọc, kiểm tra và lưu PO.
    ///
    /// <para>
    /// Việc đọc nội dung (upload file CodeContent CSV/JSON/XLSX) do
    /// <see cref="PoUploadHandler"/> đảm nhiệm — file được stream xuống đĩa
    /// tạm, không nạp trọn vào RAM.
    /// </para>
    /// </summary>
    private async Task<PoPushOutcome> HandlePushAsync(
        HttpListenerRequest request,
        CancellationToken ct)
    {
        using var mutationLock = await _store.AcquireMutationLockAsync(ct).ConfigureAwait(false);

        var running = await GetRunningOrderNoAsync(ct).ConfigureAwait(false);
        if (running is not null)
        {
            return new PoPushOutcome(409, new PoPushResponse
            {
                Success = false,
                Message = $"Không thể nhận PO khi PO '{running}' đang sản xuất dở dang. Hãy hoàn tất hoặc huỷ PO đang chạy trước.",
                PoName = string.Empty,
            });
        }

        var content = await _uploadHandler
            .ReadContentAsync(request, ct)
            .ConfigureAwait(false);

        if (!content.IsSuccess || content.Content is null)
        {
            return new PoPushOutcome(
                content.StatusCode,
                new PoPushResponse
                {
                    Success = false,
                    Message = content.Message,
                });
        }

        var payload = content.Content.Metadata;

        return await SavePoAsync(payload, content.Content.RawCodes, ct).ConfigureAwait(false);
    }

    private async Task<string?> GetRunningOrderNoAsync(CancellationToken ct)
    {
        return await _store.GetUnfinishedOrderNoAsync(_history, ct).ConfigureAwait(false);
    }


    /// <summary>
    /// Kiểm tra + lưu PO nhận từ MES.
    /// </summary>
    private async Task<PoPushOutcome> SavePoAsync(
        PoPushRequest payload,
        IReadOnlyList<string> rawCodes,
        CancellationToken ct)
    {
        var poName = (payload.OrderNo ?? string.Empty).Trim();

        if (poName.Length == 0)
        {
            return new PoPushOutcome(
                400,
                new PoPushResponse
                {
                    Success = false,
                    Message = "Thiếu trường orderNo (LOT/tên lệnh sản xuất).",
                });
        }

        if (poName.Length > MaxPoNameLength)
        {
            return new PoPushOutcome(
                400,
                new PoPushResponse
                {
                    Success = false,
                    Message = $"orderNo quá dài (tối đa {MaxPoNameLength} ký tự).",
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
                    OrderNo = poName,
                    PlannedCount = payload.OrderQty,
                });
        }

        if (!PoCodeParser.TryNormalizeGtin(payload.Gtin, out var gtin))
        {
            return new PoPushOutcome(422, new PoPushResponse
            {
                Success = false,
                Message = "Trường gtin phải là đúng một GTIN hợp lệ (8, 12, 13 hoặc 14 chữ số có checksum).",
                PoName = poName,
                OrderNo = poName,
                PlannedCount = payload.OrderQty,
            });
        }

        // Kiểm tra schema và số record gốc trước khi lọc từng mã hoặc ghi DB.
        if (rawCodes.Count == 0 || rawCodes.Any(string.IsNullOrWhiteSpace))
            return new PoPushOutcome(400, new PoPushResponse
            {
                    Message = "File phải chứa các giá trị CodeContent không rỗng.", PoName = poName,
                    OrderNo = poName, PlannedCount = payload.OrderQty,
            });
        if (rawCodes.Count != payload.OrderQty)
            return new PoPushOutcome(400, new PoPushResponse
            {
                Message = $"Số dòng CodeContent ({rawCodes.Count}) phải bằng planned orderQty ({payload.OrderQty}) trước lọc.",
                PoName = poName, OrderNo = poName, PlannedCount = payload.OrderQty,
            });
        var localOrders = await _store.ListLocalOrdersAsync(ct, strict: true).ConfigureAwait(false);
        var existingOrder = localOrders.FirstOrDefault(order =>
            string.Equals(order.OrderNo, poName, StringComparison.OrdinalIgnoreCase));

        if (existingOrder?.IsFinished == true)
        {
            return new PoPushOutcome(
                409,
                new PoPushResponse
                {
                    Success = false,
                    Message = $"PO '{poName}' đã kết thúc (hoàn tất, huỷ hoặc đóng); không thể nhận thêm mã.",
                    PoName = poName, OrderNo = poName, PlannedCount = payload.OrderQty,
                });
        }

        var existingPath = _store.FindExistingDbPath(poName);
        if (existingPath is not null && existingOrder is null)
        {
            return new PoPushOutcome(409, new PoPushResponse
            {
                Message = "Tên PO sau chuẩn hoá trùng với file PO khác; hãy dùng tên PO khác.",
                PoName = poName, OrderNo = poName, PlannedCount = payload.OrderQty,
            });
        }

        // Mỗi PO sở hữu tập mã độc lập, so sánh toàn bộ CodeContent bằng ordinal.
        var existingCodes = existingOrder is null
            ? new HashSet<string>(StringComparer.Ordinal)
            : (await _store.GetCodesAsync(existingOrder.OrderNo, existingOrder.CreatedUtc, ct).ConfigureAwait(false))
                .Select(code => code.CodeContent).ToHashSet(StringComparer.Ordinal);
        // Store suy GTIN legacy chỉ khi toàn bộ mã cũ chứng minh cùng định danh.
        if (existingOrder is not null
            && ((!PoCodeParser.TryNormalizeGtin(existingOrder.Gtin, out var storedGtin)
                    || !string.Equals(storedGtin, gtin, StringComparison.Ordinal))
                || existingCodes.Any(raw => !PoCodeParser.TryGetGtin(raw, out var parsedGtin)
                    || !string.Equals(parsedGtin, gtin, StringComparison.Ordinal))))
        {
            return new PoPushOutcome(422, new PoPushResponse
            {
                Message = $"PO '{poName}' đã gắn GTIN khác hoặc chứa mã legacy không xác minh được; không thể trộn GTIN.",
                PoName = poName, OrderNo = poName, PlannedCount = payload.OrderQty,
            });
        }
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var codes = new List<PoCode>(rawCodes.Count);
        var duplicateCount = 0;
        var duplicateCodes = new List<string>();
        var invalidCodes = new List<PoRejectedCode>();
        foreach (var raw in rawCodes)
        {
            ct.ThrowIfCancellationRequested();
            if (!PoCodeParser.TryGetGtin(raw, out var codeGtin))
            {
                invalidCodes.Add(new PoRejectedCode { Code = raw, Reason = "invalidStructure" });
                continue;
            }
            if (!string.Equals(codeGtin, gtin, StringComparison.Ordinal))
            {
                invalidCodes.Add(new PoRejectedCode { Code = raw, Reason = "gtinMismatch" });
                continue;
            }
            if (!seen.Add(raw) || existingCodes.Contains(raw))
            {
                duplicateCount++;
                duplicateCodes.Add(raw);
                continue;
            }
            codes.Add(new PoCode { CodeContent = raw });
        }
        var onlyDuplicates = codes.Count == 0 && duplicateCount > 0 && invalidCodes.Count == 0;
        var growPlannedOnly = onlyDuplicates && existingOrder is not null
            && payload.OrderQty > existingOrder.OrderQty;
        if (codes.Count == 0 && !growPlannedOnly)
        {
            var duplicateMessage = onlyDuplicates
                ? $"Không có mã mới cho PO {poName}; đã bỏ qua {duplicateCount} mã trùng."
                : $"PO {poName} không có mã hợp lệ để nhận; loại {invalidCodes.Count} mã sai và {duplicateCount} mã trùng.";
            return new PoPushOutcome(onlyDuplicates ? 200 : 422, new PoPushResponse
            {
                Success = onlyDuplicates,
                Message = duplicateMessage,
                PoName = poName,
                OrderNo = poName,
                Accepted = 0,
                AcceptedCount = 0,
                PlannedCount = payload.OrderQty,
                IgnoredDuplicates = duplicateCount,
                InvalidCodes = invalidCodes,
                DuplicateCodes = duplicateCodes,
            });
        }

        // ---- Trường mở rộng ----
        var additional = existingOrder is null
            ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(existingOrder.Additional, StringComparer.OrdinalIgnoreCase);
        // Toàn duplicate chỉ tăng planned max, giữ các metadata mô tả và mã/scan cũ.
        if (!growPlannedOnly)
        {
            additional.Remove("gtin");
            additional.Remove("serial");
            additional["gtin"] = gtin;
            AddIfNotEmpty(additional, "batchCode", payload.BatchCode);
            AddIfNotEmpty(additional, "productName", payload.ProductName);
            AddIfNotEmpty(additional, "customer", payload.Customer);
        }

        var order = new ProductionOrder
        {
            OrderNo = existingOrder?.OrderNo ?? poName,
            OrderQty = payload.OrderQty,
            Gtin = gtin,
            CreatedUtc = existingOrder?.CreatedUtc ?? DateTime.UtcNow,
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
                    OrderNo = poName,
                    PlannedCount = payload.OrderQty,
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
                    OrderNo = poName,
                    PlannedCount = payload.OrderQty,
                });
        }

        if (codes.Count > 0) _state.IncrementReceived();

        _logger.LogInformation(
            "Đã nhận PO {PoName} từ MES: thêm {Count} mã mới, bỏ qua {Duplicates} mã trùng.",
            poName, codes.Count, duplicateCount);

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
                Message = $"Đã nhận {codes.Count}/{payload.OrderQty} mã từ file cho PO {poName}; loại {invalidCodes.Count} mã sai và {duplicateCount} mã trùng.",
                PoName = poName,
                OrderNo = poName,
                Accepted = codes.Count,
                AcceptedCount = codes.Count,
                PlannedCount = payload.OrderQty,
                IgnoredDuplicates = duplicateCount,
                InvalidCodes = invalidCodes,
                DuplicateCodes = duplicateCodes,
            });
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
        lock (_startGate)
        {
            if (_disposed) return;
            _disposed = true;
        }

        // Không dispose semaphore/CTS khi handler vẫn dùng chúng hoặc đang ghi DB.
        StopAsync().GetAwaiter().GetResult();

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
