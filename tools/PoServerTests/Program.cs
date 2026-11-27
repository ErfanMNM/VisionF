using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using ClassLibraryMT.MesSimulator;
using ClassLibraryMT.Po;
using ClassLibraryMT.PoServer;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

if (args.Contains("--serve", StringComparer.Ordinal))
{
    await PoDemoTestHost.RunAsync(args);
    return;
}

var workDir = Path.Combine(Path.GetTempPath(), "visionf-poserver-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(workDir);
var passed = 0;
void Check(string name, bool ok, string detail = "")
{
    if (!ok) throw new Exception(name + ": " + detail);
    passed++;
    Console.WriteLine("[OK] " + name);
}

async Task AcceptAndPersistAsync(ProductionOrderService service, PoStore store, string code)
{
    var result = await service.ProcessCodeAsync(code, _ => Task.FromResult(true));
    if (result.Mutation is { } mutation)
        await store.MarkScannedBatchAsync([mutation]);
    await service.CompletePendingAsync();
}

try
{
    await CodeContentParserChecks.RunAsync(workDir, Check);
    await PoContractChecks.RunAsync(workDir, Check);
    using var fixture = new ServerFixture(Path.Combine(workDir, "main"));
    using (var health = await fixture.Http.GetAsync("api/health"))
        Check("Health endpoint ready", health.IsSuccessStatusCode);
    using (var unknown = await fixture.Http.GetAsync("unknown"))
        Check("Unknown endpoint returns 404", unknown.StatusCode == HttpStatusCode.NotFound);

    var initial = await fixture.PushAsync("PO-ONE", "https://first/01/09506000134352/21/SAME", "https://second/01/09506000134352/21/SAME", "(01)09506000134352(21)Case", "(01)09506000134352(21)case");
    Check("Import exact whole codes", initial.Status == HttpStatusCode.OK && initial.Result.Accepted == 4, initial.Body);
    Check("Shared suffix and case remain separate", (await fixture.CodesAsync("PO-ONE")).Select(code => code.CodeContent)
        .ToHashSet(StringComparer.Ordinal).SetEquals(["https://first/01/09506000134352/21/SAME", "https://second/01/09506000134352/21/SAME", "(01)09506000134352(21)Case", "(01)09506000134352(21)case"]));
    var merge = await fixture.PushAsync("po-one", "(01)09506000134352(21)Case", "(01)09506000134352(21)new-content", "(01)09506000134352(21)new-content");
    Check("Reimport same PO filters exact duplicates", merge.Status == HttpStatusCode.OK
        && merge.Result.Accepted == 1 && merge.Result.IgnoredDuplicates == 2, merge.Body);
    var one = await fixture.OrderAsync("PO-ONE");
    Check("Merge giữ identity, actual và planned độc lập", one.OrderNo == "PO-ONE" && one.TotalCodes == 5 && one.OrderQty == 4);
    var independent = await fixture.PushAsync("PO-TWO", "(01)09506000134352(21)Case", "https://first/01/09506000134352/21/SAME");
    Check("Different PO owns independent full code set", independent.Result.Accepted == 2 && independent.Result.IgnoredDuplicates == 0, independent.Body);
    var repeat = await fixture.PushAsync("PO-TWO", "(01)09506000134352(21)Case");
    Check("All existing codes report zero accepted", repeat.Status == HttpStatusCode.OK && repeat.Result.Accepted == 0
        && repeat.Result.IgnoredDuplicates == 1, repeat.Body);

    var invalidCases = new (string Body, string Media, string Query)[]
    {
        ("{\"poName\":\"PO-BAD\",\"orderQty\":1,\"codes\":[\"A\"]}", "application/json", "poName=PO-BAD&orderQty=1"),
        ("{\"csv\":\"CodeContent\\nA\"}", "application/json", "poName=PO-BAD&orderQty=1"),
        ("[\"A\"]", "application/json", "poName=PO-BAD&orderQty=1"),
        ("[{\"CodeContent\":\"A\",\"serial\":\"B\"}]", "application/json", "poName=PO-BAD&orderQty=1"),
        ("[{\"CodeContent\":1}]", "application/json", "poName=PO-BAD&orderQty=1"),
        ("CodeContent,Serial\nA,B\n", "text/csv", "poName=PO-BAD&orderQty=1"),
        ("CodeContent\nA\nB\n", "text/csv", "poName=PO-BAD&orderQty=1"),
        ("CodeContent\nA\n", "text/csv", "poName=PO-BAD&orderQty=2"),

        ("CodeContent\nA\n", "text/csv", "poName=PO-BAD&orderQty=1&serial=S1"),
        ("CodeContent\nA\n", "text/csv", "poName=PO-BAD&orderQty=1&qty=1"),
        ("CodeContent\nA\n", "text/csv", "orderQty=1"),
    };
    for (var i = 0; i < invalidCases.Length; i++)
    {
        var bad = invalidCases[i];
        var response = await fixture.SendAsync("api/po?" + bad.Query + "&gtin=09506000134352", new StringContent(bad.Body, Encoding.UTF8, bad.Media));
        Check("Invalid import rejected atomically " + i, response.Status == HttpStatusCode.BadRequest
            && fixture.Store.FindExistingDbPath("PO-BAD") is null, response.Body);
    }
    var append = await fixture.SendAsync("api/po?poName=PO-ONE&orderQty=2&gtin=09506000134352", new StringContent(
        "[{\"CodeContent\":\"PARTIAL-MUST-NOT-EXIST\"},{\"CodeContent\":null}]", Encoding.UTF8, "application/json"));
    Check("Invalid later JSON row rolls back entire append", append.Status == HttpStatusCode.BadRequest
        && (await fixture.CodesAsync("PO-ONE")).Count == 5, append.Body);

    using (var multi = Form("PO-MULTI", 2))
    {
        multi.Add(new StringContent("product"), "productName");
        multi.Add(new StringContent("CodeContent\r\n\"(01)09506000134352(21)x,y\"\r\n(01)09506000134352(21)preserve\r\n", Encoding.UTF8, "text/csv"), "file", "codes.csv");
        var response = await fixture.SendAsync("api/po", multi);
        Check("Multipart file and separate metadata accepted", response.Status == HttpStatusCode.OK && response.Result.Accepted == 2, response.Body);
        Check("Multipart giữ raw GS1 và dấu phẩy", (await fixture.CodesAsync("PO-MULTI")).Select(code => code.CodeContent)
            .ToHashSet(StringComparer.Ordinal).SetEquals(["(01)09506000134352(21)x,y", "(01)09506000134352(21)preserve"]));
    }
    using (var twoFiles = Form("PO-MULTI-BAD", 1))
    {
        twoFiles.Add(new StringContent("CodeContent\nA\n"), "file", "a.csv");
        twoFiles.Add(new StringContent("CodeContent\nB\n"), "file", "b.csv");
        Check("Two multipart files rejected", (await fixture.SendAsync("api/po", twoFiles)).Status == HttpStatusCode.BadRequest);
    }
    using (var inline = Form("PO-MULTI-BAD", 1))
    {
        inline.Add(new StringContent("[\"A\"]"), "codes");
        Check("Multipart inline codes rejected", (await fixture.SendAsync("api/po", inline)).Status == HttpStatusCode.BadRequest);
    }
    using (var traversal = new StringContent("CodeContent\nA\n", Encoding.UTF8, "text/csv"))
    {
        traversal.Headers.TryAddWithoutValidation("Content-Disposition", "attachment; filename=\"../bad.csv\"");
        Check("Filename traversal rejected", (await fixture.SendAsync("api/po?poName=PO-MULTI-BAD&orderQty=1", traversal)).Status == HttpStatusCode.BadRequest);
    }
    Check("Invalid multipart leaves no PO", fixture.Store.FindExistingDbPath("PO-MULTI-BAD") is null);

    var parallel = await Task.WhenAll(Enumerable.Range(0, 8).Select(i => fixture.PushAsync("PO-CONCURRENT", "(01)09506000134352(21)common", "(01)09506000134352(21)new-" + i)));
    Check("Concurrent same-PO merges preserve all rows", parallel.All(result => result.Status == HttpStatusCode.OK)
        && (await fixture.CodesAsync("PO-CONCURRENT")).Count == 9);
    Check("Concurrent duplicate accounting is exact", parallel.Sum(result => result.Result.Accepted) == 9
        && parallel.Sum(result => result.Result.IgnoredDuplicates) == 7);

    var lifecycle = new ProductionOrderService(new LocalOnlyPoClient(), fixture.Store, fixture.History, new PoCodeCache());
    await fixture.PushAsync("PO-RUNNING", "(01)09506000134352(21)RUN-1", "(01)09506000134352(21)RUN-2");
    Check("Imported PO can start from its database", await lifecycle.StartProductionAsync("PO-RUNNING"));
    var runningBefore = await fixture.CodesAsync("PO-RUNNING");
    Check("Running PO blocks receiving another PO", (await fixture.PushAsync("PO-BLOCKED", "(01)09506000134352(21)BLOCKED")).Status == HttpStatusCode.Conflict);
    Check("Running PO blocks same-PO append", (await fixture.PushAsync("PO-RUNNING", "(01)09506000134352(21)RUN-3")).Status == HttpStatusCode.Conflict
        && (await fixture.CodesAsync("PO-RUNNING")).Count == runningBefore.Count);
    await AcceptAndPersistAsync(lifecycle, fixture.Store, "(01)09506000134352(21)RUN-1");
    Check("Successful code has durable per-PO record", (await fixture.CodesAsync("PO-RUNNING")).Single(code => code.CodeContent == "(01)09506000134352(21)RUN-1").ScannedUtc is not null);
    await AcceptAndPersistAsync(lifecycle, fixture.Store, "(01)09506000134352(21)RUN-2");
    Check("Completed PO rejects additional file", (await fixture.PushAsync("PO-RUNNING", "(01)09506000134352(21)RUN-3")).Status == HttpStatusCode.Conflict);
    Check("Completion unlocks receiving", (await fixture.PushAsync("PO-AFTER-RUN", "(01)09506000134352(21)AFTER-1", "(01)09506000134352(21)AFTER-2")).Status == HttpStatusCode.OK);
    Check("Next PO can start", await lifecycle.StartProductionAsync("PO-AFTER-RUN"));
    await lifecycle.CancelProductionAsync();
    Check("Cancelled PO rejects additional file", (await fixture.PushAsync("PO-AFTER-RUN", "(01)09506000134352(21)AFTER-3")).Status == HttpStatusCode.Conflict);
    await fixture.PushAsync("PO-CLOSED", "(01)09506000134352(21)CLOSED-1");
    var closed = await fixture.OrderAsync("PO-CLOSED");
    await fixture.Store.SetMetaAsync(closed.OrderNo, closed.CreatedUtc, PoStore.MetaStatus, ProductionOrderStatus.Closed.ToString());
    Check("Closed PO rejects additional file", (await fixture.PushAsync("PO-CLOSED", "(01)09506000134352(21)CLOSED-2")).Status == HttpStatusCode.Conflict);

    await fixture.PushAsync("PO-PERSISTED", "(01)09506000134352(21)PERSISTED-1", "(01)09506000134352(21)PERSISTED-2");
    var persisted = await fixture.OrderAsync("PO-PERSISTED");
    await fixture.Store.SetMetaAsync(persisted.OrderNo, persisted.CreatedUtc, PoStore.MetaStatus, ProductionOrderStatus.Running.ToString());
    Check("Persisted unfinished PO blocks imports without active service", (await fixture.PushAsync("PO-PERSISTED-BLOCK", "(01)09506000134352(21)NO")).Status == HttpStatusCode.Conflict);
    await fixture.Store.MarkCancelledAsync(persisted.OrderNo, persisted.CreatedUtc);
    await fixture.History.StartRunAsync("HISTORY-ONLY", 1, DateTime.UtcNow);
    Check("History-only unfinished run blocks imports", (await fixture.PushAsync("PO-HISTORY-BLOCK", "(01)09506000134352(21)NO")).Status == HttpStatusCode.Conflict);
    await fixture.History.CancelRunAsync("HISTORY-ONLY");
    Check("Cancelling stale unfinished history unlocks imports", (await fixture.PushAsync("PO-HISTORY-CLEARED", "(01)09506000134352(21)YES")).Status == HttpStatusCode.OK);

    // Inject a real SQLite failure after a valid first row; append must not leak code/meta updates.
    await fixture.PushAsync("PO-ATOMIC", "(01)09506000134352(21)OLD");
    var atomic = await fixture.OrderAsync("PO-ATOMIC");
    await ExecuteSqlAsync(fixture.Store.GetDbPath(atomic.OrderNo, atomic.CreatedUtc),
        "CREATE TRIGGER reject_bad BEFORE INSERT ON PoCodes WHEN NEW.Code = '(01)09506000134352(21)BAD' BEGIN SELECT RAISE(ABORT, 'test reject'); END;");
    var failedAppend = await fixture.PushAsync("PO-ATOMIC", "(01)09506000134352(21)NEW", "(01)09506000134352(21)BAD");
    var atomicAfter = await fixture.OrderAsync("PO-ATOMIC");
    Check("Database failure rolls back all rows and quantity", failedAppend.Status == HttpStatusCode.InternalServerError
        && atomicAfter.TotalCodes == 1 && atomicAfter.OrderQty == 1 && (await fixture.CodesAsync("PO-ATOMIC")).Single().CodeContent == "(01)09506000134352(21)OLD", failedAppend.Body);
    await ExecuteSqlAsync(fixture.Store.GetDbPath(atomic.OrderNo, atomic.CreatedUtc), "DROP TRIGGER reject_bad;");
    Check("Server continues after failed import", (await fixture.PushAsync("PO-ATOMIC", "(01)09506000134352(21)NEW")).Status == HttpStatusCode.OK);

    using (var secured = new ServerFixture(Path.Combine(workDir, "secure"), "local-test-key", maxBodyMB: 1, maxFileMB: 1))
    {
        using var noAuth = new HttpClient { BaseAddress = secured.Http.BaseAddress };
        using var denied = await noAuth.PostAsync("api/po?poName=NOAUTH&orderQty=1", new StringContent("CodeContent\nA\n", Encoding.UTF8, "text/csv"));
        Check("API key required before import", denied.StatusCode == HttpStatusCode.Unauthorized && secured.Store.FindExistingDbPath("NOAUTH") is null);
        var huge = await secured.SendAsync("api/po?poName=TOO-LARGE&orderQty=1",
            new StringContent("CodeContent\n" + new string('X', 1024 * 1024 + 20), Encoding.UTF8, "text/csv"));
        Check("Body/file size limit rejects complete request", huge.Status == HttpStatusCode.RequestEntityTooLarge
            && secured.Store.FindExistingDbPath("TOO-LARGE") is null, huge.Body);
        Check("Authenticated valid upload works", (await secured.PushAsync("AUTH", "(01)09506000134352(21)A")).Status == HttpStatusCode.OK);
    }

    // Stop must drain in-flight notification; it must not claim a running handler is stopped.
    using (var draining = new ServerFixture(Path.Combine(workDir, "drain")))
    using (var release = new ManualResetEventSlim())
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        draining.Server.PoReceived += (_, _) => { entered.TrySetResult(); release.Wait(); };
        var sending = draining.PushAsync("PO-DRAIN", "(01)09506000134352(21)DRAIN-1");
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            using var deadline = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
            var cancelled = false;
            try { await draining.Server.StopAsync(deadline.Token); }
            catch (OperationCanceledException) { cancelled = true; }
            Check("Stop cancellation does not report handler drained", cancelled);
            var stopping = draining.Server.StopAsync();
            Check("Second Stop still waits for same handler", !stopping.IsCompleted);
            release.Set();
            await stopping.WaitAsync(TimeSpan.FromSeconds(10));
            Check("Stop drains handler and preserves committed PO", (await draining.CodesAsync("PO-DRAIN")).Count == 1);
        }
        finally
        {
            release.Set();
            await draining.Server.StopAsync();
            try { await sending; } catch (HttpRequestException) { }
        }
    }
    await fixture.Server.StopAsync();
    Check("Upload temp files are cleaned on success and rejection", !Directory.EnumerateFiles(workDir, "po-upload-*", SearchOption.AllDirectories).Any());
    Console.WriteLine($"Passed {passed} CodeContent parser, ingestion, lifecycle and atomicity checks.");
}
finally
{
    SqliteConnection.ClearAllPools();
    // Keep the unique temporary folder on failure for diagnostics; no production data touched.
}

static MultipartFormDataContent Form(string orderNo, int qty)
{
    var form = new MultipartFormDataContent();
    form.Add(new StringContent("09506000134352"), "gtin");
    form.Add(new StringContent(orderNo, Encoding.UTF8), "poName");
    form.Add(new StringContent(qty.ToString(System.Globalization.CultureInfo.InvariantCulture)), "orderQty");
    return form;
}

static async Task ExecuteSqlAsync(string path, string sql)
{
    await using var db = new SqliteConnection("Data Source=" + path);
    await db.OpenAsync();
    await using var command = db.CreateCommand();
    command.CommandText = sql;
    await command.ExecuteNonQueryAsync();
}

sealed class ServerFixture : IDisposable
{
    public PoStore Store { get; }
    public PoHistoryStore History { get; }
    public PoHttpServer Server { get; }
    public HttpClient Http { get; }
    public PoServerOptions Options { get; }
    public ServerFixture(string root, string apiKey = "", int maxBodyMB = 64, int maxFileMB = 64, string bindAddress = "127.0.0.1")
    {
        Directory.CreateDirectory(root);
        Store = new PoStore(Path.Combine(root, "po"));
        History = new PoHistoryStore(Store.DbRoot);
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        Options = new PoServerOptions
        {
            Enabled = true, BindAddress = bindAddress, Port = port, ApiKey = apiKey,
            MaxBodyMB = maxBodyMB, MaxFileMB = maxFileMB, UploadTempDirectory = root,
            RequestTimeoutSeconds = 30, MaxConcurrentRequests = 4,
        };
        Server = new PoHttpServer(Microsoft.Extensions.Options.Options.Create(Options), Store, NullLogger<PoHttpServer>.Instance, History);
        Server.Start();
        if (Server.State.IsError) throw new Exception(Server.State.Status);
        Http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}/"), Timeout = TimeSpan.FromSeconds(40) };
        if (apiKey.Length > 0) Http.DefaultRequestHeaders.Add("x-api-key", apiKey);
    }
    public async Task<(HttpStatusCode Status, PoPushResponse Result, string Body)> PushAsync(string orderNo, params string[] codes)
    {
        var json = "[" + string.Join(",", codes.Select(code => "{\"CodeContent\":" + JsonSerializer.Serialize(code) + "}")) + "]";
        return await SendAsync("api/po?poName=" + Uri.EscapeDataString(orderNo) + "&orderQty=" + codes.Length + "&gtin=09506000134352",
            new StringContent(json, Encoding.UTF8, "application/json"));
    }
    public async Task<(HttpStatusCode Status, PoPushResponse Result, string Body)> SendAsync(string uri, HttpContent content)
    {
        using (content)
        using (var response = await Http.PostAsync(uri, content))
        {
            var body = await response.Content.ReadAsStringAsync();
            var result = JsonSerializer.Deserialize(body, PoServerJsonContext.Default.PoPushResponse)
                ?? throw new Exception("Missing JSON response: " + body);
            return (response.StatusCode, result, body);
        }
    }
    public async Task<LocalPoInfo> OrderAsync(string orderNo) =>
        (await Store.ListLocalOrdersAsync()).Single(order => order.OrderNo == orderNo);
    public async Task<IReadOnlyList<PoCode>> CodesAsync(string orderNo)
    {
        var order = await OrderAsync(orderNo);
        return await Store.GetCodesAsync(order.OrderNo, order.CreatedUtc);
    }
    public void Dispose() { Server.StopAsync().GetAwaiter().GetResult(); Server.Dispose(); Http.Dispose(); }
}

sealed class LocalOnlyPoClient : IPoClient
{
    public Task<IReadOnlyList<ProductionOrder>> GetOrdersAsync(bool includeCompleted = false, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<ProductionOrder>>([]);
    public Task<IReadOnlyList<PoCode>> GetCodesAsync(string orderNo, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<PoCode>>([]);

    public Task<ProductionOrder?> CreateOrderAsync(string orderNo, int orderQty, CancellationToken ct = default) => Task.FromResult<ProductionOrder?>(null);
}
