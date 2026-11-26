using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace TanTienQR.Services;

/// <summary>Always-on dedicated PDA listener; unrelated to desktop tab/scanner lifetime.</summary>
internal sealed class ManualActivationHttpServer : IHostedService, IDisposable
{
    private readonly ManualActivationService _service;
    private readonly ManualActivationOptions _options;
    private readonly ILogger<ManualActivationHttpServer> _logger;
    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _slots;
    private readonly object _gate = new();
    private readonly HashSet<Task> _requests = [];
    private Task _acceptLoop = Task.CompletedTask;
    private bool _started;
    private int _disposed;
    private volatile bool _running;
    private string _lastError = "";
    public bool IsRunning => _running;
    public bool IsListening => IsRunning;
    public event Action? StateChanged;
    public string LastError => Volatile.Read(ref _lastError);
    public string Endpoint => _options.HttpPrefix;
    public string StatusText => IsRunning ? "PDA HTTP đang nghe: " + Endpoint
        : LastError.Length > 0 ? "PDA HTTP chưa sẵn sàng: " + LastError : "PDA HTTP chưa chạy.";

    private void NotifyState()
    {
        if (StateChanged is not { } changed) return;
        foreach (Action observer in changed.GetInvocationList())
            try { observer(); } catch (Exception ex) { _logger.LogError(ex, "PDA state observer failed."); }
    }

    public ManualActivationHttpServer(ManualActivationService service, IOptions<ManualActivationOptions> options,
        ILogger<ManualActivationHttpServer> logger)
    {
        _service = service; _options = options.Value; _logger = logger;
        _slots = new SemaphoreSlim(Math.Clamp(_options.MaxConcurrentRequests, 1, 256));
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (_started) return Task.CompletedTask;
            _started = true;
            try
            {
                _listener.Prefixes.Add(_options.HttpPrefix);
                _listener.Start(); _running = true;
                NotifyState();
                _acceptLoop = AcceptAsync();
            }
            catch (Exception ex)
            {
                Volatile.Write(ref _lastError, ex.Message);
                _logger.LogError(ex, "Không mở được PDA HTTP {Prefix}; kiểm tra URL ACL/port.", _options.HttpPrefix);
                NotifyState();
            }
        }
        return Task.CompletedTask;
    }

    private async Task AcceptAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var context = await _listener.GetContextAsync().WaitAsync(_stop.Token).ConfigureAwait(false);
                if (!_slots.Wait(0))
                {
                    context.Response.StatusCode = 429; context.Response.Close(); continue;
                }
                var task = HandleAsync(context);
                lock (_gate) _requests.Add(task);
                _ = ObserveAsync(task);
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (HttpListenerException) when (_stop.IsCancellationRequested) { }
        catch (ObjectDisposedException) when (_stop.IsCancellationRequested) { }
        catch (Exception ex)
        {
            Volatile.Write(ref _lastError, ex.Message); _logger.LogError(ex, "PDA HTTP listener stopped unexpectedly.");
        }
        finally { _running = false; NotifyState(); }
    }

    private async Task ObserveAsync(Task task)
    {
        try { await task.ConfigureAwait(false); }
        catch (Exception ex) { _logger.LogError(ex, "PDA request failed."); }
        finally { lock (_gate) _requests.Remove(task); _slots.Release(); }
    }

    private async Task HandleAsync(HttpListenerContext context)
    {
        var request = context.Request; var response = context.Response;
        response.Headers["Cache-Control"] = "no-store";
        response.Headers["X-Content-Type-Options"] = "nosniff";
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
        deadline.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(_options.RequestTimeoutSeconds, 1, 120)));
        try
        {
            var path = request.Url?.AbsolutePath ?? "";
            if (request.HttpMethod == "GET" && path is "/" or "/manual-activation")
            {
                response.ContentType = "text/html; charset=utf-8";
                await WriteAsync(response, Encoding.UTF8.GetBytes(FormHtml), deadline.Token).ConfigureAwait(false);
                return;
            }
            if (request.HttpMethod == "GET" && path == "/api/manual-activation/status")
            {
                var recent = await _service.GetRecentAsync(10, deadline.Token).ConfigureAwait(false);
                await WriteJsonAsync(response, writer =>
                {
                    writer.WriteStartArray(); foreach (var result in recent) WriteResult(writer, result); writer.WriteEndArray();
                }, deadline.Token).ConfigureAwait(false);
                return;
            }
            if (request.HttpMethod != "POST" || path != "/api/manual-activation")
            { response.StatusCode = 404; return; }
            // Browser callers must originate from this listener. Native PDA clients may omit Origin.
            if (request.Headers["Origin"] is { Length: > 0 } origin
                && (!Uri.TryCreate(origin, UriKind.Absolute, out var originUri) || request.Url is not { } url
                    || !string.Equals(originUri.Scheme, url.Scheme, StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(originUri.Host, url.Host, StringComparison.OrdinalIgnoreCase) || originUri.Port != url.Port))
            { response.StatusCode = 403; return; }
            var bodyLimit = Math.Clamp(_options.MaxBodyBytes, 256, 1024 * 1024);
            if (request.ContentLength64 > bodyLimit) { response.StatusCode = 413; return; }
            var payload = await ReadBodyAsync(request.InputStream, bodyLimit, deadline.Token).ConfigureAwait(false);
            string code, source;
            var mediaType = request.ContentType?.Split(';')[0].Trim();
            if (string.Equals(mediaType, "application/json", StringComparison.OrdinalIgnoreCase))
            {
                using var document = JsonDocument.Parse(payload);
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("codeContent", out var value)
                    || value.ValueKind != JsonValueKind.String) { response.StatusCode = 400; return; }
                code = value.GetString() ?? "";
                source = root.TryGetProperty("source", out var sourceValue) && sourceValue.ValueKind == JsonValueKind.String
                    ? sourceValue.GetString() ?? "PDA" : "PDA";
            }
            else if (string.Equals(mediaType, "application/x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase))
            {
                var fields = Encoding.UTF8.GetString(payload).Split('&').Select(pair => pair.Split('=', 2))
                    .GroupBy(pair => WebUtility.UrlDecode(pair[0]), StringComparer.Ordinal)
                    .ToDictionary(group => group.Key, group => WebUtility.UrlDecode(group.First().Length > 1 ? group.First()[1] : ""), StringComparer.Ordinal);
                code = fields.GetValueOrDefault("codeContent", ""); source = fields.GetValueOrDefault("source", "PDA");
            }
            else { response.StatusCode = 415; return; }
            var outcome = await _service.SubmitAsync(code, source, deadline.Token).ConfigureAwait(false);
            response.StatusCode = outcome.Status == "Overload" ? 429 : outcome.Status is "Stopped" or "AuditError" ? 503 : 200;
            // Admission is independent of request cancellation. Response may be unwritable if PDA left;
            // the completed result is still in the dedicated audit and scans/outbox remain published.
            await WriteJsonAsync(response, writer => WriteResult(writer, outcome), deadline.Token).ConfigureAwait(false);
        }
        catch (BodyTooLargeException) { response.StatusCode = 413; }
        catch (JsonException) { response.StatusCode = 400; }
        catch (OperationCanceledException) { try { response.StatusCode = 408; } catch (ObjectDisposedException) { } }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "PDA HTTP request failed.");
            try { response.StatusCode = 500; } catch (ObjectDisposedException) { }
        }
        finally { try { response.Close(); } catch (Exception ex) { _logger.LogDebug(ex, "PDA connection already closed."); } }
    }

    private static async Task<byte[]> ReadBodyAsync(Stream input, int limit, CancellationToken ct)
    {
        using var output = new MemoryStream(); var buffer = new byte[Math.Min(limit + 1, 4096)];
        while (true)
        {
            var count = await input.ReadAsync(buffer, ct).ConfigureAwait(false);
            if (count == 0) break;
            if (output.Length + count > limit) throw new BodyTooLargeException();
            output.Write(buffer, 0, count);
        }
        return output.ToArray();
    }

    private static async Task WriteJsonAsync(HttpListenerResponse response, Action<Utf8JsonWriter> write, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer)) { write(writer); writer.Flush(); }
        response.ContentType = "application/json; charset=utf-8";
        await WriteAsync(response, buffer.ToArray(), ct).ConfigureAwait(false);
    }

    private static async Task WriteAsync(HttpListenerResponse response, byte[] bytes, CancellationToken ct)
    {
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes, ct).ConfigureAwait(false);
    }

    private static void WriteResult(Utf8JsonWriter writer, ManualActivationResult result)
    {
        writer.WriteStartObject();
        writer.WriteString("id", result.Id); writer.WriteString("codeContent", result.CodeContent);
        writer.WriteString("source", result.Source); writer.WriteString("status", result.Status);
        writer.WriteString("message", result.Message); writer.WriteString("orderNo", result.OrderNo);
        writer.WriteString("receivedUtc", result.ReceivedUtc); writer.WriteBoolean("success", result.Success);
        writer.WriteBoolean("auditPersisted", result.AuditPersisted); writer.WriteEndObject();
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _stop.Cancel(); _running = false;
        NotifyState();
        if (_listener.IsListening) _listener.Stop();
        await _acceptLoop.WaitAsync(cancellationToken).ConfigureAwait(false);
        Task[] requests; lock (_gate) requests = _requests.ToArray();
        await Task.WhenAll(requests).WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _stop.Cancel(); _listener.Close();
        // Requests can still be unwinding after a host shutdown timeout. Their token/semaphore
        // remain valid until normal GC instead of being disposed underneath those continuations.
    }

    private sealed class BodyTooLargeException : Exception { }

    internal const string FormHtml = """
        <!doctype html><html lang="vi"><head><meta charset="utf-8">
        <meta name="viewport" content="width=device-width, initial-scale=1"><title>Thêm mã</title>
        <style>body{font:18px system-ui;max-width:38rem;margin:2rem auto;padding:0 1rem;color:#162b3d;background:#f5f7fa}h1{font-size:1.55rem}label{display:block;margin:1rem 0 .5rem}input,button{box-sizing:border-box;width:100%;font:inherit;padding:.8rem;border-radius:.5rem}input{border:1px solid #8498aa}button{margin-top:.8rem;border:0;color:white;background:#146b63}#result{white-space:pre-wrap;overflow-wrap:anywhere}li{margin:1rem 0;overflow-wrap:anywhere}</style>
        </head><body><h1>Thêm mã</h1><p>Quét mã GS1 của PO đang chạy. Enter để gửi.</p>
        <form id="activation" action="/api/manual-activation" method="post"><label for="codeContent">Nội dung mã</label>
        <input id="codeContent" name="codeContent" autofocus autocomplete="off" autocapitalize="off" spellcheck="false" required>
        <button id="submit" type="submit">Thêm mã</button></form><p id="result" role="status" aria-live="polite"></p>
        <h2>10 lần gần nhất</h2><ol id="history"></ol>
        <script>const form=document.getElementById('activation'),code=document.getElementById('codeContent'),button=document.getElementById('submit'),result=document.getElementById('result'),history=document.getElementById('history');
        async function recent(){try{const r=await fetch('/api/manual-activation/status');if(!r.ok)return;const rows=await r.json();history.replaceChildren();for(const row of rows){const li=document.createElement('li');li.textContent=row.status+' · '+row.codeContent+'\n'+row.message;history.appendChild(li);}}catch{}}
        form.addEventListener('submit',async e=>{e.preventDefault();if(button.disabled)return;button.disabled=true;result.textContent='Đang xử lý…';try{const r=await fetch(form.action,{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({codeContent:code.value,source:'PDA'})});const row=await r.json();result.textContent=row.status+' — '+row.message;code.value='';await recent();}catch{result.textContent='Mất kết nối. Kiểm tra 10 lần gần nhất trước khi quét lại.';}finally{button.disabled=false;code.focus();}});recent();window.addEventListener('pageshow',()=>code.focus());</script>
        </body></html>
        """;
}
