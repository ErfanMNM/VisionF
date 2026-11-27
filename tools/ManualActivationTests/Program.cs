using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using ClassLibraryMT.Communications.CodeReaderProtocol;
using ClassLibraryMT.Communications.Plc;
using ClassLibraryMT.DeviceStatus;
using ClassLibraryMT.Po;
using ClassLibraryMT.Scans;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TanTienQR.Services;

internal static class Program
{
    private static int _passed;
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(12);
    private const string Gtin = "09506000134352";
    private const string OtherGtin = "04006381333931";
    private static string Code(string serial) => $"/01/{Gtin}/21/{serial}";
    private static string WrongGtin => $"/01/{OtherGtin}/21/WRONG";
    private static void Check(bool value, string label)
    {
        if (!value) throw new InvalidOperationException("[FAIL] " + label);
        _passed++; Console.WriteLine("[OK] " + label);
    }

    public static async Task<int> Main(string[] args)
    {
        try
        {
            Check(PoCodeParser.IsValidGtin(Gtin) && PoCodeParser.IsValidGtin(OtherGtin), "Synthetic GTIN fixtures have valid checksum");
            await OfflineAndValidationAsync();
            await OverloadPauseCancellationAsync();
            await LifecycleBarrierAsync();
            await AuditFailuresAndRestartAsync();
            await LockedAuditDoesNotBlockCameraAsync();
            await HttpAsync(args.Contains("--serve-preview", StringComparer.Ordinal));
            Console.WriteLine($"ManualActivationTests: {_passed} checks passed; unique temp DBs, loopback HTTP only, no production/hardware.");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }

    private static async Task EventuallyAsync(Func<bool> predicate)
    {
        var watch = Stopwatch.StartNew();
        while (!predicate())
        {
            if (watch.Elapsed > Deadline) throw new TimeoutException("Expected state did not arrive.");
            await Task.Delay(5);
        }
    }

    private static async Task OfflineAndValidationAsync()
    {
        await using var f = await Fixture.CreateAsync("offline");
        await f.StartCameraAsync();
        Check(!f.Reader!.Connected && GlobalVariables.PLCStatus != PLCStatus.Connected, "Fixture camera and PLC are offline");
        var results = await Task.WhenAll(f.Manual.SubmitAsync(Code("A"), "Desktop"), f.Manual.SubmitAsync(Code("A"), "COM"),
            f.Manual.SubmitAsync(Code("A"), "PDA")).WaitAsync(Deadline);
        Check(results.Count(r => r.Success && r.Status == "Pass") == 1 && results.Count(r => r.Status == "Duplicate") == 2,
            "Concurrent Desktop/COM/PDA submission accepts exact code once with offline hardware");
        Check(results.All(r => r.AuditPersisted) && f.Counters.ResultWrites == 0 && f.Counters.ResultFailures == 0,
            "Manual outcomes audited without any PLC write or failure");
        Check((await f.Manual.SubmitAsync("NoRead", "Desktop")).Status == "InvalidStructure", "NoRead fails manual GS1 validation");
        Check((await f.Manual.SubmitAsync(Code("A") + " ", "Desktop")).Status == "InvalidStructure", "Trailing whitespace is preserved and rejected, not normalized");
        Check((await f.Manual.SubmitAsync("/01/09506000134353/21/B", "Desktop")).Status == "InvalidStructure", "Bad GTIN checksum rejected");
        Check((await f.Manual.SubmitAsync(WrongGtin, "Desktop")).Status == "InvalidForOrder", "Wrong PO GTIN rejected even when full code exists in dictionary");
        Check((await f.Manual.SubmitAsync(Code("a"), "Desktop")).Status == "InvalidForOrder", "Exact dictionary identity is case sensitive");
        Check((await f.Manual.SubmitAsync(Code("MISSING"), "PDA")).Status == "InvalidForOrder", "GS1-valid absent code rejected");
        f.Cache.MarkActivated(Code("B"));
        Check((await f.Manual.SubmitAsync(Code("B"), "COM")).Status == "Duplicate", "Activated-only code cannot be accepted manually");
        f.Manual.Completed += _ => throw new IOException("Synthetic failed UI observer");
        Check((await f.Manual.SubmitAsync(Code("C"), "Desktop")).Success, "Failed completion observer cannot erase accepted result");
        await f.Camera!.DrainAsync(CancellationToken.None).WaitAsync(Deadline);
        var scans = await f.Query.GetAsync();
        var pass = scans.Items.Where(r => r.Status == ScanStatuses.Pass).ToArray();
        Check(pass.Length == 2 && pass.All(r => r.Gtin == Gtin && r.PlcResult == ScanPlcResults.Skipped),
            "Shared scans.db receives manual Pass with normalized GTIN and PLC Skipped");
        Check(await f.PoStore.CountScannedAsync(f.Order.OrderNo, f.Order.CreatedUtc) == 2 && f.Persistence.PendingCount == 0,
            "Manual mutations reach PO file through shared outbox and drain every reservation");
        Check((await f.Manual.GetRecentAsync()).Count == 10, "Dedicated SQLite history returns latest ten attempts");
        Check(File.Exists(f.Options.HistoryPath) && Path.GetFullPath(f.Options.HistoryPath) != Path.GetFullPath(f.ScanPath), "Manual audit is a separate SQLite file");
        var snapshot = f.Po.GetRunningOrderSnapshot()!; snapshot.OrderNo = "changed"; snapshot.Additional["lot"] = "changed";
        Check(f.Po.GetRunningOrderSnapshot() is { } original && original.OrderNo == f.Order.OrderNo && original.Additional["lot"] == "LOT-TEST",
            "Running PO snapshot detaches identity and metadata from mutable consumers");
    }

    private static async Task OverloadPauseCancellationAsync()
    {
        await using var f = await Fixture.CreateAsync("queue", capacity: 1);
        using var caller = new CancellationTokenSource();
        var first = f.Manual.SubmitAsync(Code("A"), "PDA", caller.Token);
        await EventuallyAsync(() => f.Manual.PendingCount == 1);
        caller.Cancel();
        Check((await f.Manual.SubmitAsync(Code("B"), "COM")).Status == "Overload" && f.Persistence.PendingCount == 1,
            "Bounded queue overload audited without reserving extra capacity or accepting RAM");
        var barrier = f.Persistence.FlushAsync();
        Check(!barrier.IsCompleted, "Persistence barrier covers the queued unpublished manual reservation");
        await f.StartCameraAsync();
        Check((await first.WaitAsync(Deadline)).Success, "Caller cancellation after admission cannot lose accepted queue item");
        await barrier.WaitAsync(Deadline);
        Check(f.Persistence.PendingCount == 0 && f.Manual.PendingCount == 0, "Queue overload and cancellation leave no dangling reservation");
        using (f.Intake.Suspend())
        {
            Check((await f.Manual.SubmitAsync(Code("B"), "Desktop")).Status == "Paused", "Pause rejects and audits intake before reservation/RAM");
            await f.Camera!.DrainAsync(CancellationToken.None).WaitAsync(Deadline);
        }
        f.Manual.StopAccepting();
        Check((await f.Manual.SubmitAsync(Code("B"), "PDA")).Status == "Stopped", "Shutdown blocks admission and audits rejection");

        await using var paused = await Fixture.CreateAsync("queued-pause");
        var queued = paused.Manual.SubmitAsync(Code("A"), "PDA");
        await EventuallyAsync(() => paused.Manual.PendingCount == 1);
        using (paused.Intake.Suspend())
        {
            await paused.StartCameraAsync();
            Check((await queued.WaitAsync(Deadline)).Status == "Pass", "Pause drains previously admitted work despite intake gate now suspended");
            await paused.Camera!.DrainAsync(CancellationToken.None).WaitAsync(Deadline);
            Check(paused.Persistence.PendingCount == 0 && paused.Po.ValidateCode(Code("A")) == PoCodeValidation.Duplicate,
                "Paused queue drains through shared persistence without lifecycle hang");
        }
        paused.Po.HoldForUpdateHealthCheck();
        Check((await paused.Manual.SubmitAsync(Code("A"), "PDA")).Status == "Halted", "Update maintenance prevents manual acceptance");
        paused.Po.LeaveUpdateMaintenance();

        await using var stopping = await Fixture.CreateAsync("shutdown");
        var pendingStop = stopping.Manual.SubmitAsync(Code("A"), "PDA");
        await EventuallyAsync(() => stopping.Manual.PendingCount == 1);
        await stopping.StartCameraAsync();
        await stopping.StopCameraAsync();
        Check((await pendingStop.WaitAsync(Deadline)).Success && stopping.Manual.PendingCount == 0,
            "Camera shutdown resolves every admitted manual operation");
        await stopping.Persistence.FlushAsync().WaitAsync(Deadline);
        Check(stopping.Persistence.PendingCount == 0, "Shutdown publishes all accepted/rejected reservation envelopes");

        await using var pressure = await Fixture.CreateAsync("pressure", persistenceCapacity: 1);
        var pressured = pressure.Manual.SubmitAsync(Code("A"), "PDA");
        await EventuallyAsync(() => pressure.Manual.PendingCount == 1);
        Check(pressure.Intake.IsSuspended, "One persistence slot suspends intake after reservation");
        await pressure.StartCameraAsync();
        Check((await pressured.WaitAsync(Deadline)).Success, "Reservation's own pressure gate cannot reject admitted valid manual code");
        await pressure.Camera!.DrainAsync(CancellationToken.None).WaitAsync(Deadline);
        Check(!pressure.Intake.IsSuspended && pressure.Persistence.PendingCount == 0, "Capacity-one pressure clears after publication and persistence");
    }

    private static async Task LifecycleBarrierAsync()
    {
        await using var f = await Fixture.CreateAsync("lifecycle");
        var request = f.Manual.SubmitAsync(Code("A"), "PDA");
        await EventuallyAsync(() => f.Manual.PendingCount == 1);
        var cancelled = f.Po.CancelProductionAsync();
        await Task.Delay(40);
        Check(!cancelled.IsCompleted, "PO lifecycle waits for already reserved manual request");
        await f.StartCameraAsync();
        Check((await request.WaitAsync(Deadline)).Status == "Halted", "Lifecycle transition rejects old queued manual code atomically");
        Check(await cancelled.WaitAsync(Deadline), "Manual publication releases PO cancellation barrier without mutex deadlock");
        Check(f.Po.GetRunningOrderSnapshot() is null && f.Persistence.PendingCount == 0, "Cancelled order has no outbound running snapshot or reservation tail");
        Check((await f.Manual.SubmitAsync(Code("A"), "PDA")).Status == "NoProductionOrder", "Manual code requires currently running PO");
    }

    private static async Task AuditFailuresAndRestartAsync()
    {
        await using var f = await Fixture.CreateAsync("audit");
        await f.StartCameraAsync();
        await f.History.GetRecentAsync(); // Ensure schema before installing deterministic SQLite triggers.
        await f.AuditSqlAsync("CREATE TRIGGER RejectNew BEFORE INSERT ON ManualActivationAudit BEGIN SELECT RAISE(ABORT,'synthetic initial audit failure'); END;");
        var noAudit = await f.Manual.SubmitAsync(Code("A"), "Desktop");
        Check(!noAudit.Success && !noAudit.AuditPersisted && noAudit.Status == "AuditError" && f.Persistence.PendingCount == 0
            && f.Po.ValidateCode(Code("A")) == PoCodeValidation.Valid,
            "Initial audit write failure fails closed before reservation/RAM acceptance");
        await f.AuditSqlAsync("DROP TRIGGER RejectNew; CREATE TRIGGER RejectFinal BEFORE UPDATE ON ManualActivationAudit WHEN NEW.Status <> 'Pending' BEGIN SELECT RAISE(ABORT,'synthetic final audit failure'); END;");
        var accepted = await f.Manual.SubmitAsync(Code("A"), "Desktop");
        Check(accepted.Success && !accepted.AuditPersisted && accepted.Status == "Pass", "Final audit failure truthfully preserves accepted Pass");
        await f.Camera!.DrainAsync(CancellationToken.None).WaitAsync(Deadline);
        Check(await f.PoStore.CountScannedAsync(f.Order.OrderNo, f.Order.CreatedUtc) == 1 && f.Persistence.PendingCount == 0,
            "Audit failure cannot strand scan/outbox/PO reservation");
        Check((await f.History.GetRecentAsync()).Single().Status == "Pending", "Failed final audit retains original durable Pending attempt");
        await f.AuditSqlAsync("DROP TRIGGER RejectFinal;");
        var reopened = new ManualActivationHistoryStore(Microsoft.Extensions.Options.Options.Create(f.Options));
        var afterRestart = (await reopened.GetRecentAsync()).Single();
        Check(afterRestart.Status == "Interrupted" && afterRestart.Id == accepted.Id && afterRestart.CodeContent == Code("A"),
            "Reopening audit marks interrupted Pending result without automatic resubmission");
        Check((await f.Manual.SubmitAsync(Code("A"), "PDA")).Status == "Duplicate", "Reconciled interrupted attempt cannot reaccept already consumed code");
    }

    private static async Task HttpAsync(bool preview)
    {
        await using var f = await Fixture.CreateAsync("http");
        await f.StartCameraAsync();
        var port = FreePort();
        f.Options.HttpPrefix = $"http://127.0.0.1:{port}/";
        using var server = new ManualActivationHttpServer(f.Manual, f.Query,
            f.Provider.GetRequiredService<IScanRecordWriter>(), Microsoft.Extensions.Options.Options.Create(f.Options),
            NullLogger<ManualActivationHttpServer>.Instance);
        var states = 0; server.StateChanged += () => states++;
        await server.StartAsync(CancellationToken.None);
        Check(server.IsListening && server.StatusText.Contains(f.Options.HttpPrefix) && states > 0, "HTTP listener binds explicit loopback with status event");
        using var client = new HttpClient { BaseAddress = new Uri(f.Options.HttpPrefix), Timeout = Deadline };
        var html = await client.GetStringAsync("manual-activation");
        Check(html.Contains("autofocus") && html.Contains("<title>Thêm mã</title>") && html.Contains("code.focus()") && html.Contains("textContent"),
            "PDA GET form uses Thêm mã, autofocus/refocus, safe text rendering");
        var lookupHtml = await client.GetStringAsync("code-lookup");
        Check(lookupHtml.Contains("<title>Tra cứu mã</title>") && lookupHtml.Contains("/api/code-lookup")
            && lookupHtml.Contains("autofocus") && lookupHtml.Contains("code.select()") && lookupHtml.Contains("textContent"),
            "PDA GET form provides mobile exact-code lookup with safe rendering and scanner refocus");
        var post = await client.PostAsync("api/manual-activation", new FormUrlEncodedContent(new Dictionary<string, string> { ["codeContent"] = Code("A") }));
        using var result = JsonDocument.Parse(await post.Content.ReadAsStringAsync());
        Check(post.IsSuccessStatusCode && result.RootElement.GetProperty("success").GetBoolean()
            && result.RootElement.GetProperty("source").GetString() == "PDA", "Form POST activates offline without opening desktop tab");
        var auditCountBeforeLookup = (await f.Manual.GetRecentAsync()).Count;
        var lookupPost = await client.PostAsync("api/code-lookup",
            new StringContent(JsonSerializer.Serialize(new { codeContent = Code("A") }), Encoding.UTF8, "application/json"));
        using var lookupResult = JsonDocument.Parse(await lookupPost.Content.ReadAsStringAsync());
        Check(lookupPost.IsSuccessStatusCode && lookupResult.RootElement.GetProperty("totalCount").GetInt64() == 1
            && lookupResult.RootElement.GetProperty("items")[0].GetProperty("codeContent").GetString() == Code("A")
            && lookupResult.RootElement.GetProperty("items")[0].GetProperty("orderNo").GetString() == f.Order.OrderNo,
            "PDA lookup returns the exact scan row and production details");
        var lookupMiss = await client.PostAsync("api/code-lookup",
            new StringContent(JsonSerializer.Serialize(new { codeContent = Code("A").ToLowerInvariant() }), Encoding.UTF8, "application/json"));
        using var lookupMissResult = JsonDocument.Parse(await lookupMiss.Content.ReadAsStringAsync());
        Check(lookupMissResult.RootElement.GetProperty("totalCount").GetInt64() == 0,
            "PDA lookup requires an exact case-sensitive code match");
        Check((await f.Manual.GetRecentAsync()).Count == auditCountBeforeLookup,
            "PDA lookup is read-only and does not add manual activation audit entries");
        var jsonPost = await client.PostAsync("api/manual-activation", new StringContent("{\"codeContent\":\"" + Code("A") + "\"}", Encoding.UTF8, "application/json"));
        using var duplicate = JsonDocument.Parse(await jsonPost.Content.ReadAsStringAsync());
        Check(duplicate.RootElement.GetProperty("status").GetString() == "Duplicate", "JSON PDA POST shares exact RAM duplicate guard");
        using var status = JsonDocument.Parse(await client.GetStringAsync("api/manual-activation/status"));
        Check(status.RootElement.GetArrayLength() == 2, "PDA status endpoint returns dedicated audit attempts");
        var huge = await client.PostAsync("api/manual-activation", new StringContent(new string('x', f.Options.MaxBodyBytes + 1), Encoding.UTF8, "application/json"));
        Check(huge.StatusCode == HttpStatusCode.RequestEntityTooLarge, "HTTP rejects oversized body before JSON/admission");
        using var foreign = new HttpRequestMessage(HttpMethod.Post, "api/manual-activation")
        { Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["codeContent"] = Code("B") }) };
        foreign.Headers.Add("Origin", "https://unrelated.invalid");
        Check((await client.SendAsync(foreign)).StatusCode == HttpStatusCode.Forbidden, "Browser POST from unrelated origin rejected");
        using var foreignLookup = new HttpRequestMessage(HttpMethod.Post, "api/code-lookup")
        { Content = new StringContent(JsonSerializer.Serialize(new { codeContent = Code("A") }), Encoding.UTF8, "application/json") };
        foreignLookup.Headers.Add("Origin", "https://unrelated.invalid");
        Check((await client.SendAsync(foreignLookup)).StatusCode == HttpStatusCode.Forbidden,
            "Browser lookup POST from unrelated origin rejected");
        if (preview)
        {
            Console.WriteLine("PDA_PREVIEW_URL=" + f.Options.HttpPrefix + "manual-activation");
            Console.WriteLine("Preview remains available for 45 seconds, then normal shutdown continues.");
            await Task.Delay(TimeSpan.FromSeconds(45));
        }
        await server.StopAsync(CancellationToken.None).WaitAsync(Deadline);
        Check(!server.IsListening && states >= 2, "HTTP stop publishes readiness change and joins handlers");
        var badOptions = new ManualActivationOptions { HttpPrefix = "invalid-prefix" };
        using var denied = new ManualActivationHttpServer(f.Manual, f.Query,
            f.Provider.GetRequiredService<IScanRecordWriter>(), Microsoft.Extensions.Options.Options.Create(badOptions),
            NullLogger<ManualActivationHttpServer>.Instance);
        await denied.StartAsync(CancellationToken.None);
        Check(!denied.IsListening && denied.LastError.Length > 0 && denied.StatusText.Contains("chưa sẵn sàng"),
            "Invalid prefix exposes HTTP readiness error without crashing application or localhost fallback");
    }

    private static async Task LockedAuditDoesNotBlockCameraAsync()
    {
        await using var f = await Fixture.CreateAsync("locked-audit");
        var manual = f.Manual.SubmitAsync(Code("A"), "PDA");
        await EventuallyAsync(() => f.Manual.PendingCount == 1);
        await using var locked = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = f.Options.HistoryPath, Pooling = false }.ToString());
        await locked.OpenAsync();
        await using var transaction = (SqliteTransaction)await locked.BeginTransactionAsync();
        await f.StartCameraAsync();
        await EventuallyAsync(() => f.Po.ScannedCount == 1);
        await f.Persistence.FlushAsync().WaitAsync(Deadline);
        Check(!manual.IsCompleted, "Audit SQLite write lock retains tracked manual completion after RAM/outbox publication");
        // Synthetic connected flag allows invalid camera frame through the camera queue.
        // The PlcWriter is disabled, so no actual PLC socket operation occurs.
        AppStateManager.SetPlcStatus(PLCStatus.Connected);
        using var deadline = new CancellationTokenSource(Deadline);
        await using var feed = f.Feed.ReadAllAsync(deadline.Token).GetAsyncEnumerator(deadline.Token);
        var callback = typeof(CameraSignalWorker).GetMethod("OnCameraEvent", BindingFlags.Instance | BindingFlags.NonPublic)!
            .CreateDelegate<Action<eReaderState, string>>(f.Camera!);
        var watch = Stopwatch.StartNew(); callback(eReaderState.Received, "NOT-GS1");
        Check(await feed.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(1))
            && feed.Current.Outcome == TanTienQR.Models.ScanOutcome.InvalidStructure && watch.ElapsedMilliseconds < 1000,
            "Camera consumer continues while manual audit finalization waits on locked SQLite");
        Check(!manual.IsCompleted && f.Counters.ResultWrites == 0, "Audit remains pending and manual/camera fixture never sends PLC");
        var drain = f.Camera!.DrainAsync(deadline.Token);
        Check(!drain.IsCompleted, "Camera lifecycle drain includes separately tracked audit finalization");
        await transaction.RollbackAsync();
        Check((await manual.WaitAsync(Deadline)).Success, "Unlocked audit finishes original accepted manual attempt");
        await drain.WaitAsync(Deadline);
        Check(f.Manual.PendingCount == 0 && f.Persistence.PendingCount == 0, "Locked audit regression leaves no queue/reservation tail");
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop(); return port;
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public required string Root { get; init; }
        public string ScanPath => Path.Combine(Root, "scans.db");
        public required ServiceProvider Provider { get; init; }
        public required ProductionOrder Order { get; init; }
        public required ProductionOrderService Po { get; init; }
        public required PoStore PoStore { get; init; }
        public required PoCodeCache Cache { get; init; }
        public required ScanPersistenceWorker Persistence { get; init; }
        public required ScanIntakeGate Intake { get; init; }
        public required ManualActivationOptions Options { get; init; }
        public required ManualActivationHistoryStore History { get; init; }
        public required ManualActivationService Manual { get; init; }
        public IScanRecordQuery Query => Provider.GetRequiredService<IScanRecordQuery>();
        public RuntimeCounters Counters { get; } = new();
        public ScanFeed Feed { get; } = new();
        public CameraSignalWorker? Camera { get; private set; }
        public Reader? Reader { get; private set; }
        private OmronPlc? _plc;
        private bool _cameraStopped;

        public static async Task<Fixture> CreateAsync(string name, int capacity = 64, int persistenceCapacity = 100)
        {
            var root = Path.Combine(Path.GetTempPath(), "VisionF-ManualActivation-" + name + "-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root); Console.WriteLine("SANDBOX=" + root);
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            { ["Scans:DbPath"] = Path.Combine(root, "scans.db"), ["Scans:QueueCapacity"] = persistenceCapacity.ToString(), ["Scans:FlushIntervalMs"] = "10" }).Build();
            var services = new ServiceCollection(); services.AddSingleton<IConfiguration>(config); services.AddScanStore(config);
            var provider = services.BuildServiceProvider(); var poStore = new PoStore(Path.Combine(root, "po"));
            var cache = new PoCodeCache(); var po = new ProductionOrderService(new NoNetworkPoClient(), poStore, new PoHistoryStore(poStore.DbRoot), cache);
            var intake = new ScanIntakeGate();
            var persistence = new ScanPersistenceWorker(provider.GetRequiredService<IScanPersistenceStore>(), poStore, po, intake,
                provider.GetRequiredService<IOptions<ScanStoreOptions>>(), NullLogger<ScanPersistenceWorker>.Instance);
            await persistence.StartAsync(CancellationToken.None);
            var order = new ProductionOrder { OrderNo = "TEST-MANUAL-" + name, OrderQty = 100, CreatedUtc = DateTime.UtcNow,
                Gtin = Gtin, Additional = new() { ["lot"] = "LOT-TEST" } };
            var codes = new[] { Code("A"), Code("B"), Code("C"), WrongGtin }.Concat(Enumerable.Range(0, 12).Select(i => Code("SPARE" + i)));
            await poStore.EnsurePoDbAsync(order, codes.Select(c => new PoCode { CodeContent = c }).ToArray());
            if (!await po.StartProductionAsync(order.OrderNo)) throw new InvalidOperationException("Synthetic PO did not start.");
            var options = new ManualActivationOptions { HistoryPath = Path.Combine(root, "manual-audit.db"), QueueCapacity = capacity };
            var history = new ManualActivationHistoryStore(Microsoft.Extensions.Options.Options.Create(options));
            var manual = new ManualActivationService(history, persistence, po, intake, Microsoft.Extensions.Options.Options.Create(options), NullLogger<ManualActivationService>.Instance);
            return new Fixture { Root = root, Provider = provider, PoStore = poStore, Po = po, Cache = cache, Order = order,
                Intake = intake, Persistence = persistence, Options = options, History = history, Manual = manual };
        }

        public async Task StartCameraAsync()
        {
            Reader = new Reader("127.0.0.1", FreePort(), TimeSpan.FromSeconds(1));
            _plc = new OmronPlc("127.0.0.1", FreePort(), 1, 2, 100, 0);
            var writer = new PlcWriter(_plc, Microsoft.Extensions.Options.Options.Create(new PlcOptions { Enabled = false }), Counters, Intake, NullLogger<PlcWriter>.Instance);
            Camera = new CameraSignalWorker(Reader, writer, Feed, Persistence,
                Microsoft.Extensions.Options.Options.Create(new CameraOptions()), Counters, Po, Intake, NullLogger<CameraSignalWorker>.Instance, Manual);
            await Camera.StartAsync(CancellationToken.None);
        }

        public async Task StopCameraAsync()
        {
            if (Camera is null || _cameraStopped) return;
            using var timeout = new CancellationTokenSource(Deadline);
            await Camera.StopAsync(timeout.Token); _cameraStopped = true;
        }

        public async Task AuditSqlAsync(string sql)
        {
            await using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Options.HistoryPath, Pooling = false }.ToString());
            await db.OpenAsync(); await using var command = db.CreateCommand(); command.CommandText = sql; await command.ExecuteNonQueryAsync();
        }

        public async ValueTask DisposeAsync()
        {
            await StopCameraAsync(); Camera?.Dispose(); Reader?.Disconnect(); _plc?.Dispose();
            using var timeout = new CancellationTokenSource(Deadline);
            await Persistence.StopAsync(timeout.Token); Persistence.Dispose(); await Provider.DisposeAsync();
            // Leave the unique temp sandbox for independent inspection; never clean any application DB.
        }
    }

    private sealed class NoNetworkPoClient : IPoClient
    {
        public Task<IReadOnlyList<ProductionOrder>> GetOrdersAsync(bool includeCompleted = false, CancellationToken ct = default) => throw new InvalidOperationException("Network forbidden in manual sandbox");
        public Task<IReadOnlyList<PoCode>> GetCodesAsync(string orderNo, CancellationToken ct = default) => throw new InvalidOperationException("Network forbidden in manual sandbox");
        public Task<ProductionOrder?> CreateOrderAsync(string orderNo, int orderQty, CancellationToken ct = default) => throw new InvalidOperationException("Network forbidden in manual sandbox");
    }
}
