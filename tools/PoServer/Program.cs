using System.Net;
using System.Text.Json;
using ClassLibraryMT.Po;

namespace PoServer;

/// <summary>
/// Server giả lập quản lý Production Order.
///
/// <para>
/// API:
/// <list type="bullet">
/// <item><c>GET  /api/po</c> — danh sách PO còn hiệu lực.</item>
/// <item><c>GET  /api/po/{orderNo}/codes</c> — tải tập mã của PO.</item>
/// <item><c>POST /api/po</c> — tạo PO mới (body: <c>orderNo</c>, <c>orderQty</c>).</item>
/// <item><c>POST /api/activate</c> — nhận mã kích hoạt (body: <c>orderNo</c>, <c>serials[]</c>).</item>
/// </list>
/// </para>
/// </summary>
internal static class Program
{
    private const string DefaultPrefix = "http://127.0.0.1:5080/";

    private static PoServerStore _store = null!;

    public static async Task Main(string[] args)
    {
        var prefix = args.Length > 0 && !string.IsNullOrWhiteSpace(args[0])
            ? args[0]
            : DefaultPrefix;

        var dbPath = Path.Combine(AppContext.BaseDirectory, "logs", "po-server.db");
        _store = new PoServerStore(dbPath);

        SeedIfEmpty(3, 100);

        using var listener = new HttpListener();
        listener.Prefixes.Add(prefix);
        listener.Start();

        Console.WriteLine("=== PoServer (server giả lập Production Order) ===");
        Console.WriteLine($"Đang lắng nghe: {prefix}");
        Console.WriteLine($"Database: {dbPath}");
        Console.WriteLine();
        Console.WriteLine("Giao diện tạo PO mẫu: mở trình duyệt tại");
        Console.WriteLine($"    {prefix}");
        Console.WriteLine();
        Console.WriteLine("Endpoints:");
        Console.WriteLine("  GET  /                      (giao diện web tạo PO mẫu)");
        Console.WriteLine("  GET  /api/po");
        Console.WriteLine("  GET  /api/po/{orderNo}/codes");
        Console.WriteLine("  POST /api/po");
        Console.WriteLine("  POST /api/activate");
        Console.WriteLine("Nhấn Ctrl+C để dừng.");
        Console.WriteLine();

        var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        try
        {
            while (!cts.IsCancellationRequested)
            {
                HttpListenerContext context;
                try
                {
                    context = await listener.GetContextAsync().ConfigureAwait(false);
                }
                catch (HttpListenerException) when (cts.IsCancellationRequested)
                {
                    break;
                }
                catch (ObjectDisposedException)
                {
                    break;
                }

                _ = Task.Run(() => HandleAsync(context), CancellationToken.None);
            }
        }
        finally
        {
            listener.Stop();
        }
    }

    /// <summary>
    /// Xử lý một request.
    /// </summary>
    private static async Task HandleAsync(HttpListenerContext context)
    {
        var request = context.Request;
        var response = context.Response;

        try
        {
            var path = request.Url?.AbsolutePath.Trim('/') ?? "";

            // Giao diện web tạo PO mẫu: GET / -> www/index.html
            if (request.HttpMethod == "GET"
                && (path.Length == 0
                    || path.Equals("index.html", StringComparison.OrdinalIgnoreCase)))
            {
                await WriteHtmlAsync(response).ConfigureAwait(false);
                return;
            }

            if (request.HttpMethod == "GET" && path.Equals("api/po", StringComparison.OrdinalIgnoreCase))
            {
                // ?includeCompleted=true -> lấy cả PO đã hoàn tất / đã đóng.
                // Mô hình local-first cần vậy để danh sách PO trên máy SCADA
                // khớp với server (PO đã xong ở server vẫn phải hiện trong bảng
                // quản lý local).
                var includeCompletedValue = request.QueryString["includeCompleted"];
                var includeCompleted = string.Equals(
                    includeCompletedValue, "true", StringComparison.OrdinalIgnoreCase)
                    || includeCompletedValue == "1";

                var orders = _store.ListOrders(includeCompleted);
                await WriteJsonAsync(response, orders, PoServerJsonContext.Default.ListProductionOrder)
                    .ConfigureAwait(false);
                return;
            }

            if (request.HttpMethod == "GET"
                && path.StartsWith("api/po/", StringComparison.OrdinalIgnoreCase)
                && path.EndsWith("/codes", StringComparison.OrdinalIgnoreCase))
            {
                var orderNo = path["api/po/".Length..^"/codes".Length];
                orderNo = Uri.UnescapeDataString(orderNo);

                var codes = _store.GetCodes(orderNo);
                if (codes.Count == 0)
                {
                    await WriteErrorAsync(response, 404, $"Không tìm thấy PO '{orderNo}'.")
                        .ConfigureAwait(false);
                    return;
                }

                _store.MarkIssued(orderNo);

                await WriteJsonAsync(response, codes, PoServerJsonContext.Default.ListPoCode)
                    .ConfigureAwait(false);
                return;
            }

            if (request.HttpMethod == "POST" && path.Equals("api/po", StringComparison.OrdinalIgnoreCase))
            {
                var payload = await ReadJsonAsync(
                    request, PoServerJsonContext.Default.PoCreateRequest).ConfigureAwait(false);

                if (payload is null)
                {
                    await WriteErrorAsync(response, 400, "Body không hợp lệ.").ConfigureAwait(false);
                    return;
                }

                try
                {
                    var created = _store.CreateOrder(payload.OrderNo, payload.OrderQty);
                    await WriteJsonAsync(response, created, PoServerJsonContext.Default.ProductionOrder)
                        .ConfigureAwait(false);
                }
                catch (InvalidOperationException ex)
                {
                    await WriteErrorAsync(response, 409, ex.Message).ConfigureAwait(false);
                }

                return;
            }

            if (request.HttpMethod == "POST" && path.Equals("api/activate", StringComparison.OrdinalIgnoreCase))
            {
                var payload = await ReadJsonAsync(
                    request, PoServerJsonContext.Default.PoActivateRequest).ConfigureAwait(false);

                if (payload is null)
                {
                    await WriteErrorAsync(response, 400, "Body không hợp lệ.").ConfigureAwait(false);
                    return;
                }

                var activated = _store.Activate(payload.OrderNo, payload.Serials);
                Console.WriteLine(
                    $"[{DateTime.Now:HH:mm:ss}] Kích hoạt PO {payload.OrderNo}: {activated}/{payload.Serials.Count} mã.");

                var result = new PoActivateResult
                {
                    Ok = true,
                    Activated = activated,
                    Message = $"Đã kích hoạt {activated} mã.",
                };

                await WriteJsonAsync(response, result, PoServerJsonContext.Default.PoActivateResult)
                    .ConfigureAwait(false);
                return;
            }

            await WriteErrorAsync(response, 404, "Endpoint không tồn tại.").ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Lỗi xử lý request: {ex.Message}");
            try
            {
                await WriteErrorAsync(response, 500, ex.Message).ConfigureAwait(false);
            }
            catch
            {
                // Response đã gửi / đã đóng -> bỏ qua.
            }
        }
        finally
        {
            try
            {
                response.Close();
            }
            catch
            {
                // Bỏ qua.
            }
        }
    }

    /// <summary>
    /// Ghi trang HTML giao diện tạo PO mẫu ra response.
    /// </summary>
    private static async Task WriteHtmlAsync(HttpListenerResponse response)
    {
        var htmlPath = Path.Combine(AppContext.BaseDirectory, "www", "index.html");

        if (!File.Exists(htmlPath))
        {
            // Chạy từ source (dotnet run) -> www nằm cạnh file .csproj.
            var sourceHtml = Path.Combine(
                AppContext.BaseDirectory, "..", "..", "..", "..", "www", "index.html");
            htmlPath = Path.GetFullPath(sourceHtml);
        }

        if (!File.Exists(htmlPath))
        {
            await WriteErrorAsync(
                response, 404, "Không tìm thấy file giao diện www/index.html.").ConfigureAwait(false);
            return;
        }

        response.StatusCode = 200;
        response.ContentType = "text/html; charset=utf-8";

        var bytes = await File.ReadAllBytesAsync(htmlPath).ConfigureAwait(false);
        await response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
    }

    /// <summary>
    /// Ghi JSON ra response.
    /// </summary>
    private static async Task WriteJsonAsync<T>(
        HttpListenerResponse response,
        T value,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo)
    {
        response.StatusCode = 200;
        response.ContentType = "application/json; charset=utf-8";

        await JsonSerializer.SerializeAsync(response.OutputStream, value, typeInfo).ConfigureAwait(false);
    }

    /// <summary>
    /// Ghi lỗi ra response.
    /// </summary>
    private static async Task WriteErrorAsync(HttpListenerResponse response, int status, string message)
    {
        response.StatusCode = status;
        response.ContentType = "application/json; charset=utf-8";

        var payload = JsonSerializer.SerializeToUtf8Bytes(
            new PoActivateResult { Ok = false, Message = message },
            PoServerJsonContext.Default.PoActivateResult);
        await response.OutputStream.WriteAsync(payload).ConfigureAwait(false);
    }

    /// <summary>
    /// Đọc JSON từ request body.
    /// </summary>
    private static async Task<T?> ReadJsonAsync<T>(
        HttpListenerRequest request,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo)
    {
        using var stream = request.InputStream;
        return await JsonSerializer
            .DeserializeAsync(stream, typeInfo)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Seed dữ liệu mẫu nếu server chưa có PO nào: 3 PO × 100 mã.
    /// </summary>
    private static void SeedIfEmpty(int orderCount, int codeCount)
    {
        if (_store.ListOrders(includeCompleted: true).Count > 0)
            return;

        for (var i = 1; i <= orderCount; i++)
        {
            var orderNo = $"PO{DateTime.Now:yyyyMMdd}-{i:D3}";
            _store.CreateOrder(orderNo, codeCount);
            Console.WriteLine($"Seed PO {orderNo} với {codeCount} mã.");
        }
    }
}
