using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
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
using TanTienQR.Models;
using TanTienQR.Services;

// No app host/config is loaded. Every database is explicitly rooted in this unique sandbox.
var sandbox = Path.Combine(Path.GetTempPath(), "TEST-ASYNC-PO-20261127-Camera-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(sandbox);
Console.WriteLine("SANDBOX=" + sandbox);
using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(50));
var ct = timeout.Token;
var checks = 0;
void Check(bool condition, string label)
{
    if (!condition) throw new InvalidOperationException("[FAIL] " + label);
    checks++;
    Console.WriteLine("[OK] " + label);
}
static string Code(string suffix) => "/01/08934567890123/21/" + suffix;
var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
{
    ["Scans:DbPath"] = Path.Combine(sandbox, "history", "scans.db"),
    ["Scans:FlushIntervalMs"] = "100", ["Scans:BatchSize"] = "100", ["Scans:QueueCapacity"] = "100",
    ["Scans:RetentionDays"] = "0",
}).Build();
var services = new ServiceCollection();
services.AddSingleton<IConfiguration>(configuration);
services.AddScanStore(configuration);
await using var provider = services.BuildServiceProvider();
var poStore = new PoStore(Path.Combine(sandbox, "po"));
var po = new ProductionOrderService(new NoNetworkPoClient(), poStore, new PoHistoryStore(poStore.DbRoot), new PoCodeCache());
var gate = new ScanIntakeGate();
using var coordinator = new ScanPersistenceWorker(provider.GetRequiredService<IScanPersistenceStore>(), poStore, po,
    gate, provider.GetRequiredService<IOptions<ScanStoreOptions>>(), NullLogger<ScanPersistenceWorker>.Instance);
await coordinator.StartAsync(ct);
var order = new ProductionOrder
{
    OrderNo = "TEST-ASYNC-PO-20261127-CAMERA", CreatedUtc = DateTime.UtcNow, OrderQty = 5,
};
await poStore.EnsurePoDbAsync(order, new[] { "WARMUP", "LOCKED", "OVERLAP", "LOST-ACK", "FINAL" }
    .Select(suffix => new PoCode { CodeContent = Code(suffix) }).ToArray(), ct);
Check(await po.StartProductionAsync(order.OrderNo, ct), "Start sandbox-only PO after persistence startup");
using var plc = new LoopbackPlc();
await using var camera = await CameraFixture.StartAsync(po, coordinator, gate, plc, ct);
var warmup = await camera.ScanAsync(Code("WARMUP"), ct);
Check(warmup.Outcome == ScanOutcome.Pass, "Warm up real camera queue and loopback PLC");
await camera.DrainAsync(ct);

// Hold a real SQLite write transaction in the PO sandbox. The common worker may commit
// scans/outbox first, but PO mutation cannot commit until this transaction releases.
await using (var locked = new SqliteConnection(new SqliteConnectionStringBuilder
{
    DataSource = poStore.GetDbPath(order.OrderNo, order.CreatedUtc), Pooling = false,
}.ToString()))
{
    await locked.OpenAsync(ct);
    await using var transaction = (SqliteTransaction)await locked.BeginTransactionAsync(ct);
    var started = Stopwatch.GetTimestamp();
    var beforeWrites = plc.Writes.Count;
    var pass = await camera.ScanAsync(Code("LOCKED"), ct);
    var duplicate = await camera.ScanAsync(Code("LOCKED"), ct);
    var elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
    Check(pass.Outcome == ScanOutcome.Pass && pass.PlcWritten, "Locked PO SQLite does not block camera Pass/PLC ACK");
    Check(duplicate.Outcome == ScanOutcome.Duplicate && duplicate.PlcWritten,
        "Duplicate is rejected from RAM before the 100 ms persistence flush");
    Check(elapsed < 100, "Pass plus immediate duplicate finish before 100 ms target tick");
    Check(po.ValidateCode(Code("LOCKED")) == PoCodeValidation.Duplicate, "RAM consumes accepted full code while DB remains locked");
    var pendingDrain = camera.DrainAsync(ct);
    await Task.Delay(300, ct);
    Check(!pendingDrain.IsCompleted, "Camera lifecycle drain includes pending PO/history persistence");
    Check(plc.Writes.Skip(beforeWrites).Count(w => w.Address == 12 && w.Value == 1) == 1,
        "One accepted attempt produces exactly one PLC Pass");
    Console.WriteLine($"LATENCY locked_sqlite camera_to_plc_call_ms={pass.ProcessingTimeMs:F3} camera_to_feed_after_ack_ms={camera.Measurements[^2].AckMs:F3} pass_and_duplicate_ms={elapsed:F3} sqlite_lock_held_ms={Stopwatch.GetElapsedTime(started).TotalMilliseconds:F3}");
    await transaction.RollbackAsync(ct);
    await pendingDrain.WaitAsync(TimeSpan.FromSeconds(8), ct);
}
var lockedRecord = (await provider.GetRequiredService<IScanRecordQuery>().GetAsync(
    new ScanRecordFilter { CodeContentEquals = Code("LOCKED"), Status = ScanStatus.Pass },
    cancellationToken: ct)).Items.Single();
Console.WriteLine($"LATENCY durable_locked_sqlite camera_to_plc_call_ms={lockedRecord.ProcessingTimeMs:F3} camera_to_plc_ack_ms={lockedRecord.PlcAckTimeMs:F3}");
Check(lockedRecord.PlcAckTimeMs >= lockedRecord.ProcessingTimeMs && lockedRecord.PlcAckTimeMs < 100,
    "Stored Stopwatch timings distinguish PLC call start from PLC ACK under SQLite lock");

// A held PLC ACK keeps the existing single-flight policy; the next frame still gets history.
plc.HoldNextAck();
camera.Inject(Code("OVERLAP"));
await plc.WaitForHeldWriteAsync(ct);
camera.Inject(Code("FINAL"));
var overload = await camera.NextAsync(ct);
Check(overload.Outcome == ScanOutcome.Overload, "Second frame during PLC command remains Overload");
plc.ReleaseAck();
Check((await camera.NextAsync(ct)).Outcome == ScanOutcome.Pass, "Original in-flight PLC command finishes normally");
await camera.WaitIdleAsync(ct);
Check((await camera.ScanAsync("NOT-GS1", ct)).Outcome == ScanOutcome.InvalidStructure, "Invalid structure stays rejected");
Check((await camera.ScanAsync("NoRead", ct)).Outcome == ScanOutcome.NoRead, "NoRead stays rejected");

plc.DropNextAck();
var uncertain = await camera.ScanAsync(Code("LOST-ACK"), ct);
Check(uncertain.Outcome == ScanOutcome.Pass && !uncertain.PlcWritten
    && po.ValidateCode(Code("LOST-ACK")) == PoCodeValidation.Duplicate,
    "PLC ACK timeout preserves consumed RAM code and separate failure result");
AppStateManager.SetPlcStatus(PLCStatus.Connected);
Check((await camera.ScanAsync(Code("LOST-ACK"), ct)).Outcome == ScanOutcome.Duplicate,
    "Lost PLC ACK never permits a second Pass for the same content");
await camera.DrainAsync(ct);
var final = await camera.ScanAsync(Code("FINAL"), ct);
Check(final.Outcome == ScanOutcome.Pass, "Final code is accepted after earlier Overload did not consume it");
await camera.DrainAsync(ct);
await po.CompletePendingAsync(ct);
Check(!po.IsProductionRunning && po.LastFinishedStatus == ProductionOrderStatus.Completed
    && po.LastFinishedScannedCount == 5, "Final PO completes only after all accepted mutations persist");
var rows = (await provider.GetRequiredService<IScanRecordQuery>().GetAsync(pageSize: 100, cancellationToken: ct)).Items;
Check(rows.Count == 10 && rows.Count(r => r.Status == ScanStatuses.Pass) == 5
    && rows.Count(r => r.Status == ScanStatuses.Duplicate) == 2
    && rows.Count(r => r.Status == ScanStatuses.Overload) == 1
    && rows.Count(r => r.Status == ScanStatuses.NoRead) == 1
    && rows.Count(r => r.Status == ScanStatuses.InvalidStructure) == 1,
    "Every accepted/rejected camera attempt has its own durable history row");
await using (var historyDb = new SqliteConnection(new SqliteConnectionStringBuilder
{
    DataSource = Path.Combine(sandbox, "history", "scans.db"), Pooling = false,
}.ToString()))
{
    await historyDb.OpenAsync(ct);
    await using var events = historyDb.CreateCommand();
    events.CommandText = "SELECT COUNT(DISTINCT EventId) FROM ScanRecords;";
    Check(Convert.ToInt32(await events.ExecuteScalarAsync(ct)) == 10,
        "History identity is unique EventId per attempt, including duplicate content");
}
Check(rows.Single(r => r.CodeContent == Code("LOST-ACK") && r.Status == ScanStatuses.Pass).PlcResult == ScanPlcResults.Failed,
    "History retains Pass decision with failed PLC ACK");
Check(await poStore.CountScannedAsync(order.OrderNo, order.CreatedUtc, ct) == 5,
    "PO SQLite contains accepted codes only");
await coordinator.StopAsync(ct);
Console.WriteLine($"RESULT={checks} checks passed; no real app configuration, PO, credentials or devices used. Sandbox retained for review.");

sealed class NoNetworkPoClient : IPoClient
{
    public Task<IReadOnlyList<ProductionOrder>> GetOrdersAsync(bool includeCompleted = false, CancellationToken ct = default) => throw new InvalidOperationException("Network forbidden in sandbox test");
    public Task<IReadOnlyList<PoCode>> GetCodesAsync(string orderNo, CancellationToken ct = default) => throw new InvalidOperationException("Network forbidden in sandbox test");
    public Task<ProductionOrder?> CreateOrderAsync(string orderNo, int orderQty, CancellationToken ct = default) => throw new InvalidOperationException("Network forbidden in sandbox test");
}

sealed class CameraFixture : IAsyncDisposable
{
    private readonly Reader _reader;
    private readonly CameraSignalWorker _worker;
    private readonly OmronPlc _plc;
    private readonly TcpListener _listener;
    private readonly TcpClient _socket;
    private readonly IAsyncEnumerator<ScanRecord> _feed;
    private readonly Action<eReaderState, string> _callback;
    private readonly Func<int> _inFlight;
    private long _injected;
    public List<(double AckMs, double ProcessingMs)> Measurements { get; } = [];
    private CameraFixture(Reader reader, CameraSignalWorker worker, OmronPlc plc, TcpListener listener,
        TcpClient socket, IAsyncEnumerator<ScanRecord> feed)
    {
        _reader = reader; _worker = worker; _plc = plc; _listener = listener; _socket = socket; _feed = feed;
        _callback = typeof(CameraSignalWorker).GetMethod("OnCameraEvent", BindingFlags.Instance | BindingFlags.NonPublic)!
            .CreateDelegate<Action<eReaderState, string>>(worker);
        var field = typeof(CameraSignalWorker).GetField("_inFlight", BindingFlags.Instance | BindingFlags.NonPublic)!;
        _inFlight = () => (int)field.GetValue(worker)!;
    }
    public static async Task<CameraFixture> StartAsync(ProductionOrderService po, ScanPersistenceWorker persistence,
        ScanIntakeGate gate, LoopbackPlc simulator, CancellationToken ct)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var reader = new Reader("127.0.0.1", port);
        var plc = new OmronPlc("127.0.0.1", simulator.Port, 1, 2, 150, 2);
        var counters = new RuntimeCounters();
        var writer = new PlcWriter(plc, Options.Create(new PlcOptions { Enabled = true, Ip = "127.0.0.1", Port = simulator.Port,
            ResultAddress = "D12", ReadyAddress = "D16", TimeoutMs = 150, Retries = 2 }), counters, gate, NullLogger<PlcWriter>.Instance);
        var feed = new ScanFeed();
        var worker = new CameraSignalWorker(reader, writer, feed, persistence,
            Options.Create(new CameraOptions { Ip = "127.0.0.1", Port = port }), counters, po, gate, NullLogger<CameraSignalWorker>.Instance);
        var enumerator = feed.ReadAllAsync(ct).GetAsyncEnumerator(ct);
        AppStateManager.SetPlcStatus(PLCStatus.Connected);
        await worker.StartAsync(ct);
        var socket = await listener.AcceptTcpClientAsync(ct);
        return new(reader, worker, plc, listener, socket, enumerator);
    }
    public void Inject(string code) { _injected = Stopwatch.GetTimestamp(); _callback(eReaderState.Received, code); }
    public async Task<ScanRecord> NextAsync(CancellationToken ct)
    {
        if (!await _feed.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5), ct)) throw new IOException("Feed closed");
        Measurements.Add((Stopwatch.GetElapsedTime(_injected).TotalMilliseconds, _feed.Current.ProcessingTimeMs));
        return _feed.Current;
    }
    public async Task<ScanRecord> ScanAsync(string code, CancellationToken ct)
    {
        await WaitIdleAsync(ct); Inject(code); var result = await NextAsync(ct); await WaitIdleAsync(ct); return result;
    }
    public async Task WaitIdleAsync(CancellationToken ct)
    {
        while (_inFlight() != 0) await Task.Delay(1, ct);
    }
    public Task DrainAsync(CancellationToken ct) => _worker.DrainAsync(ct);
    public async ValueTask DisposeAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        _reader.Disconnect(); await _worker.StopAsync(timeout.Token); _worker.Dispose();
        await _feed.DisposeAsync(); _socket.Dispose(); _listener.Stop(); _plc.Dispose();
    }
}

sealed record PlcWrite(int Address, short Value);
sealed class LoopbackPlc : IDisposable
{
    private readonly UdpClient _socket = new(new IPEndPoint(IPAddress.Loopback, 0));
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _worker;
    private TaskCompletionSource? _holdEntered;
    private TaskCompletionSource? _release;
    private int _drop;
    public ConcurrentQueue<PlcWrite> Writes { get; } = new();
    public int Port => ((IPEndPoint)_socket.Client.LocalEndPoint!).Port;
    public LoopbackPlc() => _worker = RunAsync();
    public void HoldNextAck() { _holdEntered = new(TaskCreationOptions.RunContinuationsAsynchronously); _release = new(TaskCreationOptions.RunContinuationsAsynchronously); }
    public Task WaitForHeldWriteAsync(CancellationToken ct) => _holdEntered!.Task.WaitAsync(ct);
    public void ReleaseAck() => _release!.TrySetResult();
    public void DropNextAck() => Interlocked.Exchange(ref _drop, 1);
    private async Task RunAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var packet = await _socket.ReceiveAsync(_stop.Token);
                var request = packet.Buffer;
                if (request.Length < 20 || request[11] != 2) throw new InvalidOperationException("Expected FINS write");
                Writes.Enqueue(new(BinaryPrimitives.ReadUInt16BigEndian(request.AsSpan(13, 2)), BinaryPrimitives.ReadInt16BigEndian(request.AsSpan(18, 2))));
                if (Interlocked.Exchange(ref _drop, 0) != 0) continue;
                if (_holdEntered is { } entered && _release is { } release)
                {
                    entered.TrySetResult(); await release.Task.WaitAsync(_stop.Token); _holdEntered = null; _release = null;
                }
                var response = new byte[14]; Array.Copy(request, response, 12); response[0] = 0xC0;
                await _socket.SendAsync(response, packet.RemoteEndPoint, _stop.Token);
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (ObjectDisposedException) when (_stop.IsCancellationRequested) { }
    }
    public void Dispose() { _stop.Cancel(); _socket.Dispose(); _worker.GetAwaiter().GetResult(); _stop.Dispose(); }
}
