using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using ClassLibraryMT.Po;
using ClassLibraryMT.PoServer;
using Microsoft.Extensions.Logging.Abstractions;

namespace PoServer;

/// <summary>Local test server for file imports, PO snapshots and exact-code activation.</summary>
public sealed class PoConsoleServer : IAsyncDisposable
{
    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _stopping = new();
    private readonly ConcurrentBag<Task> _requests = [];
    private readonly SemaphoreSlim _throttle = new(4, 4);
    private readonly PoUploadHandler _uploads;
    private Task _accept = Task.CompletedTask;
    private int _started;

    public PoConsoleServer(string prefix, string dbPath)
    {
        Store = new PoServerStore(dbPath);
        Prefix = prefix.EndsWith('/') ? prefix : prefix + "/";
        _listener.Prefixes.Add(Prefix);
        var options = new PoServerOptions();
        _uploads = new PoUploadHandler(options,
            new PoUploadStore(options.ResolveUploadTempDirectory(), NullLogger.Instance), NullLogger.Instance);
    }

    public string Prefix { get; }
    public PoServerStore Store { get; }

    public void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) != 0) return;
        _uploads.CleanupStaleFiles();
        _listener.Start();
        _accept = AcceptAsync();
    }

    private async Task AcceptAsync()
    {
        while (!_stopping.IsCancellationRequested)
        {
            HttpListenerContext context;
            try { context = await _listener.GetContextAsync().ConfigureAwait(false); }
            catch (Exception) when (_stopping.IsCancellationRequested) { break; }
            _requests.Add(HandleAsync(context));
        }
    }

    private async Task HandleAsync(HttpListenerContext context)
    {
        var response = context.Response;
        var entered = false;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_stopping.Token);
        deadline.CancelAfter(TimeSpan.FromSeconds(120));
        var ct = deadline.Token;
        try
        {
            await _throttle.WaitAsync(ct).ConfigureAwait(false);
            entered = true;
            var request = context.Request;
            var path = request.Url?.AbsolutePath.Trim('/') ?? string.Empty;
            if (request.HttpMethod == "GET" && (path.Length == 0 || path.Equals("index.html", StringComparison.OrdinalIgnoreCase)))
            {
                var html = Path.Combine(AppContext.BaseDirectory, "www", "index.html");
                if (!File.Exists(html)) { await ErrorAsync(response, 404, "Không tìm thấy giao diện."); return; }
                response.ContentType = "text/html; charset=utf-8";
                await response.OutputStream.WriteAsync(await File.ReadAllBytesAsync(html, ct), ct);
                return;
            }
            if (request.HttpMethod == "GET" && path.Equals("api/health", StringComparison.OrdinalIgnoreCase))
            {
                await JsonAsync(response, new PoActivateResult { Ok = true, Message = "PO test server ready." },
                    PoServerJsonContext.Default.PoActivateResult);
                return;
            }
            if (request.HttpMethod == "GET" && path.Equals("api/po", StringComparison.OrdinalIgnoreCase))
            {
                var all = request.QueryString["includeCompleted"] is "true" or "1";
                await JsonAsync(response, Store.ListOrders(all), PoServerJsonContext.Default.ListProductionOrder);
                return;
            }
            if (request.HttpMethod == "GET" && path.StartsWith("api/po/", StringComparison.OrdinalIgnoreCase)
                && path.EndsWith("/codes", StringComparison.OrdinalIgnoreCase))
            {
                var order = Uri.UnescapeDataString(path["api/po/".Length..^"/codes".Length]);
                var codes = Store.GetCodes(order);
                if (codes.Count == 0) { await ErrorAsync(response, 404, "Không tìm thấy PO."); return; }
                Store.MarkIssued(order);
                await JsonAsync(response, codes, PoServerJsonContext.Default.ListPoCode);
                return;
            }
            if (request.HttpMethod == "POST" && path.Equals("api/po", StringComparison.OrdinalIgnoreCase))
            {
                var content = await _uploads.ReadContentAsync(request, ct).ConfigureAwait(false);
                if (!content.IsSuccess || content.Content is null)
                {
                    await ErrorAsync(response, content.StatusCode, content.Message);
                    return;
                }
                var metadata = content.Content.Metadata;
                var additional = new Dictionary<string, string>();
                if (!string.IsNullOrWhiteSpace(metadata.BatchCode)) additional["batchCode"] = metadata.BatchCode;
                if (!string.IsNullOrWhiteSpace(metadata.ProductName)) additional["productName"] = metadata.ProductName;
                if (!string.IsNullOrWhiteSpace(metadata.Customer)) additional["customer"] = metadata.Customer;
                ct.ThrowIfCancellationRequested();
                var imported = Store.ImportOrder(metadata.PoName, metadata.OrderQty, content.Content.RawCodes, additional);
                await JsonAsync(response, imported, PoServerJsonContext.Default.PoPushResponse);
                return;
            }
            if (request.HttpMethod == "POST" && path.Equals("api/activate", StringComparison.OrdinalIgnoreCase))
            {
                var payload = await ReadActivationAsync(request, ct).ConfigureAwait(false);
                var count = Store.Activate(payload.OrderNo, payload.CodeContents);
                await JsonAsync(response, new PoActivateResult
                {
                    Ok = true, Activated = count, Message = $"Đã kích hoạt {count} mã.",
                }, PoServerJsonContext.Default.PoActivateResult);
                return;
            }
            await ErrorAsync(response, 404, "Endpoint không tồn tại.");
        }
        catch (Exception error) when (error is ArgumentException or JsonException or PoFileFormatException)
        { await TryErrorAsync(response, 400, error.Message); }
        catch (InvalidOperationException error) { await TryErrorAsync(response, 409, error.Message); }
        catch (ConsoleBodyTooLargeException) { await TryErrorAsync(response, 413, "Request vượt giới hạn 64 MB."); }
        catch (OperationCanceledException) { await TryErrorAsync(response, 408, "Request đã bị hủy."); }
        catch (Exception error)
        {
            Console.Error.WriteLine(error.Message);
            await TryErrorAsync(response, 500, "Không lưu được request: " + error.Message);
        }
        finally
        {
            if (entered) _throttle.Release();
            try { response.Close(); }
            catch (Exception error) when (error is ObjectDisposedException or HttpListenerException) { }
        }
    }

    private static async Task<PoActivateRequest> ReadActivationAsync(HttpListenerRequest request, CancellationToken ct)
    {
        if (!(request.ContentType ?? string.Empty).Split(';')[0].Equals("application/json", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Kích hoạt cần JSON orderNo/codeContents.");
        const int maximum = 64 * 1024 * 1024;
        if (request.ContentLength64 > maximum) throw new ConsoleBodyTooLargeException();
        using var bytes = new MemoryStream();
        var buffer = new byte[16 * 1024];
        while (true)
        {
            var read = await request.InputStream.ReadAsync(buffer, ct).ConfigureAwait(false);
            if (read == 0) break;
            if (bytes.Length + read > maximum) throw new ConsoleBodyTooLargeException();
            bytes.Write(buffer, 0, read);
        }
        using var document = JsonDocument.Parse(bytes.ToArray());
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("Kích hoạt cần object orderNo/codeContents.");
        var result = new PoActivateRequest();
        var fields = new HashSet<string>(StringComparer.Ordinal);
        foreach (var field in document.RootElement.EnumerateObject())
        {
            if (!fields.Add(field.Name)) throw new ArgumentException("Trường kích hoạt bị trùng.");
            if (field.Name == "orderNo" && field.Value.ValueKind == JsonValueKind.String)
                result.OrderNo = field.Value.GetString()!;
            else if (field.Name == "codeContents" && field.Value.ValueKind == JsonValueKind.Array)
            {
                foreach (var code in field.Value.EnumerateArray())
                {
                    if (code.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(code.GetString()))
                        throw new ArgumentException("codeContents chỉ chứa các chuỗi mã đầy đủ không rỗng.");
                    result.CodeContents.Add(code.GetString()!);
                }
            }
            else throw new ArgumentException("Chỉ nhận orderNo và codeContents; không nhận serials.");
        }
        if (fields.Count != 2 || string.IsNullOrWhiteSpace(result.OrderNo) || result.CodeContents.Count == 0)
            throw new ArgumentException("Thiếu orderNo hoặc codeContents.");
        return result;
    }

    private static async Task JsonAsync<T>(HttpListenerResponse response, T value, JsonTypeInfo<T> type)
    {
        response.ContentType = "application/json; charset=utf-8";
        await JsonSerializer.SerializeAsync(response.OutputStream, value, type).ConfigureAwait(false);
    }

    private static Task ErrorAsync(HttpListenerResponse response, int status, string message)
    {
        response.StatusCode = status;
        return JsonAsync(response, new PoActivateResult { Ok = false, Message = message },
            PoServerJsonContext.Default.PoActivateResult);
    }

    private static async Task TryErrorAsync(HttpListenerResponse response, int status, string message)
    {
        try { await ErrorAsync(response, status, message).ConfigureAwait(false); }
        catch (Exception error) when (error is HttpListenerException or ObjectDisposedException or IOException) { }
    }

    public async ValueTask DisposeAsync()
    {
        _stopping.Cancel();
        _listener.Close();
        await _accept.ConfigureAwait(false);
        await Task.WhenAll(_requests.ToArray()).ConfigureAwait(false);
        _stopping.Dispose();
        _throttle.Dispose();
    }

    private sealed class ConsoleBodyTooLargeException : Exception { }
}
