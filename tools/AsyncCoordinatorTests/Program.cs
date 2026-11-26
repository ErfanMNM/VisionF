using System.Diagnostics;
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

    public static async Task<int> Main()
    {
        try
        {
            await ReservationBarrierAndSnapshotAsync();
            await CapacityAndRecoveryAsync();
            await RetryAfterPoCommitAsync();
            await RestartAfterPoCommitAsync();
            await CorruptCapturedPoBlocksStartupAsync();
            await PersistentFaultWakesQueuedBarriersAsync();
            await ConcurrentAdmissionFaultRecoveryAsync();
            await HousekeepingFailurePreservesQueuedScanAsync();
            await LifecycleBarriersAsync();
            await ShutdownWaitsForPersistenceAsync();
            await ShutdownTimeoutReportsFailureAsync();
            Console.WriteLine($"AsyncCoordinatorTests: {_passed} checks passed; sandbox only, no hardware/network/app startup.");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }

    private static void Check(bool condition, string label)
    {
        if (!condition) throw new InvalidOperationException(label);
        Interlocked.Increment(ref _passed);
        Console.WriteLine("[OK] " + label);
    }

    private static async Task Eventually(Func<bool> condition)
    {
        var watch = Stopwatch.StartNew();
        while (!condition())
        {
            if (watch.Elapsed > Deadline) throw new TimeoutException("Expected coordinator state did not arrive.");
            await Task.Delay(10);
        }
    }

    private static async Task Faults(Task task, string label)
    {
        try { await task.WaitAsync(Deadline); }
        catch (IOException) { Check(true, label); return; }
        throw new InvalidOperationException(label + " (operation did not fault)");
    }

    private static async Task ReservationBarrierAndSnapshotAsync()
    {
        await using var f = await Fixture.CreateAsync("reservation");
        var reservation = f.Worker.TryReserve()!;
        var flush = f.Worker.FlushAsync();
        await Task.Delay(35);
        Check(!flush.IsCompleted && f.Worker.PendingCount == 1,
            "Flush waits for a prior reservation whose PLC result has not published");
        var record = f.Record("original\u001dCode ", ScanStatuses.NoRead);
        reservation.Publish(record);
        record.CodeContent = "caller-mutated";
        record.OrderNo = "caller-mutated";
        await flush.WaitAsync(Deadline);
        var history = await f.Query.GetAsync();
        Check(history.TotalCount == 1 && history.Items.Single().CodeContent == "original\u001dCode "
            && history.Items.Single().OrderNo == f.Order.OrderNo,
            "Published reservation owns an immutable copy of its history and PO identity");
        Check(f.Worker.PendingCount == 0 && f.Store.PersistCalls > 0,
            "Barrier covers local batch plus PO outbox completion before freeing reservation");
    }

    private static async Task CapacityAndRecoveryAsync()
    {
        await using (var f = await Fixture.CreateAsync("capacity-one", capacity: 1))
        {
            var reservation = f.Worker.TryReserve();
            Check(reservation is not null && f.Intake.IsSuspended && f.Worker.TryReserve() is null,
                "Capacity one reserves RAM before PLC and blocks additional admission");
            reservation!.Publish(f.Record("no-read", ScanStatuses.NoRead));
            await f.Worker.FlushAsync().WaitAsync(Deadline);
            Check(!f.Intake.IsSuspended && f.Worker.PendingCount == 0,
                "Capacity one pressure lease releases after its only reservation is persisted");
        }
        await using (var f = await Fixture.CreateAsync("near-full", capacity: 20))
        {
            var reservations = Enumerable.Range(0, 19).Select(_ => f.Worker.TryReserve()!).ToArray();
            Check(reservations.All(r => r is not null) && f.Intake.IsSuspended,
                "Near-full queue stops intake while retaining room for admitted scan outcome");
            for (var i = 0; i < reservations.Length; i++)
                reservations[i].Publish(f.Record("attempt-" + i, ScanStatuses.Overload));
            await f.Worker.FlushAsync().WaitAsync(Deadline);
            Check(!f.Intake.IsSuspended && f.Worker.PendingCount == 0
                && (await f.Query.GetAsync()).TotalCount == 19,
                "Batch drains every overload attempt and releases near-full pressure");
        }
    }

    private static async Task RetryAfterPoCommitAsync()
    {
        await using var f = await Fixture.CreateAsync("complete-retry");
        f.Store.CompleteFailures = 1;
        f.Store.CompleteRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var reservation = f.Worker.TryReserve()!;
        var accepted = await f.Po.ProcessCodeAsync("scan", _ => Task.FromResult(true),
            receivedUtc: f.ScanUtc, eventId: reservation.EventId);
        reservation.Publish(f.Record("scan", ScanStatuses.Pass), accepted.Mutation);
        var barrier = f.Worker.FlushAsync();
        await f.Store.CompleteEntered.Task.WaitAsync(Deadline);
        Check(await f.PoStore.CountScannedAsync(f.Order.OrderNo, f.Order.CreatedUtc) == 1
            && (await f.RawStore.GetPendingAsync()).Count == 1,
            "PO commit is durable while outbox-completion stage is held");
        Check((await f.Send.GetPendingAsync()).Count == 0,
            "Checkee eligibility remains false after PO commit until outbox completion commits");
        f.Store.CompleteRelease.TrySetResult();
        await Faults(barrier, "Injected CompleteAsync failure wakes waiting barrier with error");
        await Eventually(() => f.Worker.IsHealthy && f.Worker.PendingCount == 0);
        await f.Worker.FlushAsync().WaitAsync(Deadline);
        var history = await f.Query.GetAsync();
        Check(f.Store.CompleteCalls >= 2 && history.TotalCount == 1
            && (await f.RawStore.GetPendingAsync()).Count == 0 && (await f.Send.GetPendingAsync()).Count == 1,
            "Transient completion failure retries one event without duplicate history and then enables Checkee");
        var code = (await f.PoStore.GetCodesAsync(f.Order.OrderNo, f.Order.CreatedUtc)).Single(c => c.CodeContent == "scan");
        Check(code.ScannedUtc == f.ScanUtc && f.Po.ValidateCode("scan") == PoCodeValidation.Duplicate,
            "Retry retains original received UTC and consumed RAM identity");
    }

    private static async Task RestartAfterPoCommitAsync()
    {
        var root = Fixture.NewRoot("restart");
        ScanPersistenceEnvelope envelope;
        await using (var before = await Fixture.CreateAsync("restart", root: root, startWorker: false))
        {
            var accepted = await before.Po.ProcessCodeAsync("scan", _ => Task.FromResult(true),
                receivedUtc: before.ScanUtc, eventId: "restart-durable-event");
            envelope = new(accepted.Mutation!.EventId, before.Record("scan", ScanStatuses.Pass),
                before.Order.CreatedUtc, true);
            await before.RawStore.PersistBatchAsync([envelope]);
            await before.PoStore.MarkScannedBatchAsync([accepted.Mutation]);
            Check((await before.RawStore.GetPendingAsync()).Count == 1 && (await before.Send.GetPendingAsync()).Count == 0,
                "Simulated process boundary after PO commit leaves durable outbox and blocks Checkee");
        }
        await using var replay = await Fixture.CreateAsync("restart", root: root, initialize: false);
        Check(replay.Po.IsProductionRunning && replay.Po.ValidateCode("scan") == PoCodeValidation.Duplicate
            && (await replay.RawStore.GetPendingAsync()).Count == 0,
            "Startup replays pending PO mutation before loading RAM and accepting duplicate checks");
        var pending = (await replay.Send.GetPendingAsync()).Single();
        await replay.Send.MarkSentAsync(pending.Id, "HTTP stub acknowledged");
        await replay.RawStore.PersistBatchAsync([envelope]);
        await replay.RawStore.CompleteAsync([envelope.EventId]);
        var rows = await replay.Query.GetAsync();
        Check(rows.TotalCount == 1 && rows.Items.Single().SendStatus == "Sent"
            && (await replay.Send.GetPendingAsync()).Count == 0,
            "Replay keeps history primary key and existing Checkee Sent acknowledgement");
    }

    private static async Task CorruptCapturedPoBlocksStartupAsync()
    {
        var root = Fixture.NewRoot("missing-code");
        await using (var before = await Fixture.CreateAsync("missing-code", root: root, startWorker: false))
        {
            var accepted = await before.Po.ProcessCodeAsync("scan", _ => Task.FromResult(true),
                receivedUtc: before.ScanUtc, eventId: "missing-code-event");
            await before.RawStore.PersistBatchAsync([new(accepted.Mutation!.EventId,
                before.Record("scan", ScanStatuses.Pass), before.Order.CreatedUtc, true)]);
            // This is a synthetic fixture's private PO DB; never enumerate app DBs.
            await before.ExecutePoAsync("DELETE FROM PoCodes WHERE Code = 'scan';");
        }
        await using var replay = await Fixture.CreateAsync("missing-code", root: root, initialize: false, startWorker: false);
        await Faults(replay.Worker.StartAsync(CancellationToken.None), "Startup refuses a missing captured PO code instead of acknowledging zero updates");
        Check(replay.Intake.IsSuspended && !replay.Worker.IsHealthy && replay.Po.CurrentOrder is null,
            "Replay failure leaves camera/Ready gate suspended before loading any running PO");
        Check((await replay.RawStore.GetPendingAsync()).Count == 1 && (await replay.Send.GetPendingAsync()).Count == 0
            && (await replay.Query.GetAsync()).TotalCount == 1,
            "Replay failure retains durable intent and history without premature Checkee eligibility");
        await Faults(replay.Worker.FlushAsync(), "Failed startup makes persistence barrier fail promptly");
    }

    private static async Task PersistentFaultWakesQueuedBarriersAsync()
    {
        await using var f = await Fixture.CreateAsync("persistent-fault");
        f.Store.PersistRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Store.PersistAlwaysFails = true;
        var reservation = f.Worker.TryReserve()!;
        reservation.Publish(f.Record("invalid", ScanStatuses.InvalidStructure));
        var flush = f.Worker.FlushAsync();
        await f.Store.PersistEntered.Task.WaitAsync(Deadline);
        var another = f.Worker.FlushAsync();
        var watch = Stopwatch.StartNew();
        f.Store.PersistRelease.TrySetResult();
        await Faults(flush, "Database fault wakes current persistence barrier");
        await Faults(another, "Database fault also wakes barrier queued behind active batch");
        Check(watch.Elapsed < TimeSpan.FromSeconds(2) && f.Intake.IsSuspended && f.Worker.TryReserve() is null,
            "Persistent writer failure blocks intake and reports queued barriers without waiting for retry loop");
        f.Store.PersistAlwaysFails = false;
        await Eventually(() => f.Worker.IsHealthy && f.Worker.PendingCount == 0);
        await f.Worker.FlushAsync().WaitAsync(Deadline);
        Check(!f.Intake.IsSuspended && (await f.Query.GetAsync()).TotalCount == 1,
            "Recovery retains failed RAM batch and reopens intake only after durable retry");
    }

    private static async Task LifecycleBarriersAsync()
    {
        foreach (var complete in new[] { false, true })
        {
            await using var f = await Fixture.CreateAsync(complete ? "manual-complete" : "cancel-pending");
            var reservation = f.Worker.TryReserve()!;
            var result = await f.Po.ProcessCodeAsync("scan", _ => Task.FromResult(true),
                receivedUtc: f.ScanUtc, eventId: reservation.EventId);
            var lifecycle = complete ? f.Po.CompleteProductionAsync() : f.Po.CancelProductionAsync();
            await Task.Delay(35);
            Check(!lifecycle.IsCompleted && f.Po.ValidateCode("spare") == PoCodeValidation.Halted
                && f.Po.TotalCount == 2,
                $"{(complete ? "Manual completion" : "Cancel")} closes acceptance while waiting for unpublished reserved mutation");
            reservation.Publish(f.Record("scan", ScanStatuses.Pass), result.Mutation);
            Check(await lifecycle.WaitAsync(Deadline) && f.Po.CurrentOrder is null
                && f.Po.TotalCount == 0 && f.Po.LastFinishedScannedCount == 1,
                $"{(complete ? "Manual completion" : "Cancel")} drains PO and scan history before clearing active RAM");
            Check((await f.Query.GetAsync()).TotalCount == 1 && (await f.RawStore.GetPendingAsync()).Count == 0,
                "Lifecycle barrier includes both databases and eligibility acknowledgement");
            if (!complete)
            {
                var next = new ProductionOrder { OrderNo = f.Order.OrderNo + "-NEXT", OrderQty = 2, CreatedUtc = f.Order.CreatedUtc };
                await f.PoStore.EnsurePoDbAsync(next, [new() { CodeContent = "scan" }, new() { CodeContent = "spare" }]);
                Check(await f.Po.StartProductionAsync(next.OrderNo) && f.Po.ValidateCode("scan") == PoCodeValidation.Valid,
                    "Switch after drained cancel preserves captured source PO and new PO eligibility");
            }
        }
        await using var final = await Fixture.CreateAsync("final-auto", codes: ["scan"]);
        var last = final.Worker.TryReserve()!;
        var accepted = await final.Po.ProcessCodeAsync("scan", _ => Task.FromResult(true),
            receivedUtc: final.ScanUtc, eventId: last.EventId);
        await Task.Delay(35);
        Check(final.Po.IsCompletionPending && final.Po.TotalCount == 1 && final.Po.IsProductionRunning,
            "Final accepted RAM code keeps PO/cache until its reserved history and mutation publish");
        last.Publish(final.Record("scan", ScanStatuses.Pass), accepted.Mutation);
        await final.Worker.FlushAsync().WaitAsync(Deadline);
        await Eventually(() => !final.Po.IsProductionRunning);
        Check(final.Po.LastFinishedScannedCount == 1 && final.Po.LastFinishedTotalCount == 1
            && final.Po.LastFinishedStatus == ProductionOrderStatus.Completed,
            "Auto completion follows durable final batch and records exact final count");
    }

    private static async Task HousekeepingFailurePreservesQueuedScanAsync()
    {
        await using var f = await Fixture.CreateAsync("prune-fault", startWorker: false,
            retentionDays: 36500, pruneIntervalMs: 20);
        f.Store.PruneFailures = 1;
        f.Store.PruneRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await f.Worker.StartAsync(CancellationToken.None);
        f.Started = true;
        await f.Store.PruneEntered.Task.WaitAsync(Deadline);
        var reservation = f.Worker.TryReserve()!;
        var accepted = await f.Po.ProcessCodeAsync("scan", _ => Task.FromResult(true),
            receivedUtc: f.ScanUtc, eventId: reservation.EventId);
        reservation.Publish(f.Record("scan", ScanStatuses.Pass), accepted.Mutation);
        var flush = f.Worker.FlushAsync();
        Check(!flush.IsCompleted && f.Worker.PendingCount == 1,
            "Accepted reservation queues while retention housekeeping is paused");
        f.Store.PruneRelease.TrySetResult();
        await flush.WaitAsync(Deadline);
        Check(f.Store.InjectedPruneFailures == 1 && f.Worker.IsHealthy && !f.Intake.IsSuspended,
            "Retention failure is isolated and coordinator remains alive to drain accepted scans");
        Check((await f.Query.GetAsync()).TotalCount == 1 && (await f.RawStore.GetPendingAsync()).Count == 0
            && await f.PoStore.CountScannedAsync(f.Order.OrderNo, f.Order.CreatedUtc) == 1,
            "Scan admitted during failed prune still persists history, PO mutation and eligibility acknowledgement");
    }

    private static async Task ConcurrentAdmissionFaultRecoveryAsync()
    {
        await using var f = await Fixture.CreateAsync("concurrent-locks", capacity: 2);
        f.Store.PersistFailures = 1;
        var admitted = 0;
        var dispatched = 0;
        // Dispatch takes the intake lock and TryReserve takes the coordinator
        // lock. The worker concurrently releases pressure and fault leases;
        // reversing those two locks would deadlock this regression.
        var producers = Enumerable.Range(0, 4).Select(producer => Task.Run(async () =>
        {
            for (var i = 0; i < 400; i++)
            {
                f.Intake.Dispatch(() =>
                {
                    Interlocked.Increment(ref dispatched);
                    if (f.Worker.TryReserve() is not { } reservation) return;
                    Interlocked.Increment(ref admitted);
                    reservation.Publish(f.Record($"stress-{producer}-{i}", ScanStatuses.Overload));
                });
                await Task.Delay(2);
            }
        })).ToArray();
        await Task.WhenAll(producers).WaitAsync(Deadline);
        await Eventually(() => f.Worker.IsHealthy && f.Worker.PendingCount == 0);
        await f.Worker.FlushAsync().WaitAsync(Deadline);
        Check(f.Store.InjectedPersistFailures == 1 && admitted >= 2 && dispatched >= admitted,
            "Concurrent Dispatch/TryReserve runs across actual writer fault, retry and pressure recovery");
        Check(!f.Intake.IsSuspended && (await f.Query.GetAsync()).TotalCount == admitted,
            "Four concurrent producers finish 1,600 callback attempts without ABBA deadlock or dropping admitted outcomes");
    }

    private static async Task ShutdownWaitsForPersistenceAsync()
    {
        await using var f = await Fixture.CreateAsync("shutdown");
        f.Store.PersistRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var reservation = f.Worker.TryReserve()!;
        var result = await f.Po.ProcessCodeAsync("scan", _ => Task.FromResult(true),
            receivedUtc: f.ScanUtc, eventId: reservation.EventId);
        reservation.Publish(f.Record("scan", ScanStatuses.Pass), result.Mutation);
        var stop = f.Worker.StopAsync(CancellationToken.None);
        await f.Store.PersistEntered.Task.WaitAsync(Deadline);
        Check(!stop.IsCompleted && f.Intake.IsSuspended, "Shutdown keeps intake suspended and waits for held database flush");
        f.Store.PersistRelease.TrySetResult();
        await stop.WaitAsync(Deadline);
        f.Stopped = true;
        Check(f.Worker.PendingCount == 0 && f.Worker.TryReserve() is null
            && (await f.RawStore.GetPendingAsync()).Count == 0
            && await f.PoStore.CountScannedAsync(f.Order.OrderNo, f.Order.CreatedUtc) == 1
            && (await f.Query.GetAsync()).TotalCount == 1,
            "Shutdown returns only after accepted scan and PO commit and writer admission closes");
        f.Worker.Dispose();
        f.Worker.Dispose();
        Check(true, "Duplicate disposal through hosted-service and writer aliases is harmless");
    }

    private static async Task ShutdownTimeoutReportsFailureAsync()
    {
        await using var f = await Fixture.CreateAsync("shutdown-timeout");
        var reservation = f.Worker.TryReserve()!;
        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        var timedOut = false;
        try { await f.Worker.StopAsync(timeout.Token).WaitAsync(Deadline); }
        catch (OperationCanceledException) { timedOut = true; }
        Check(timedOut && !reservation.IsPublished && f.Worker.PendingCount == 1,
            "Shutdown timeout reports cancellation while an earlier unpublished reservation is still pending");
        reservation.Publish(f.Record("late-no-read", ScanStatuses.NoRead));
        await f.Worker.FlushAsync().WaitAsync(Deadline);
        await f.Worker.StopAsync(CancellationToken.None).WaitAsync(Deadline);
        f.Stopped = true;
        Check(f.Worker.PendingCount == 0 && (await f.Query.GetAsync()).TotalCount == 1,
            "A later successful shutdown drains the retained reservation instead of falsely losing it on timeout");
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public required string Root { get; init; }
        public required ServiceProvider Provider { get; init; }
        public required IScanPersistenceStore RawStore { get; init; }
        public required FaultStore Store { get; init; }
        public required ScanPersistenceWorker Worker { get; init; }
        public required ScanIntakeGate Intake { get; init; }
        public required ProductionOrderService Po { get; init; }
        public required PoStore PoStore { get; init; }
        public required ProductionOrder Order { get; init; }
        public bool Started { get; set; }
        public bool Stopped { get; set; }
        public DateTime ScanUtc => new(2026, 11, 27, 1, 2, 3, DateTimeKind.Utc);
        public IScanRecordQuery Query => Provider.GetRequiredService<IScanRecordQuery>();
        public IScanSendStatusStore Send => Provider.GetRequiredService<IScanSendStatusStore>();
        public static string NewRoot(string name) => Path.Combine(Path.GetTempPath(),
            "TEST-ASYNC-PO-20261127-Coordinator-" + name + "-" + Guid.NewGuid().ToString("N"));

        public static async Task<Fixture> CreateAsync(string name, int capacity = 16,
            string? root = null, bool initialize = true, bool startWorker = true, string[]? codes = null,
            int retentionDays = 0, int pruneIntervalMs = 60000)
        {
            root ??= NewRoot(name);
            Directory.CreateDirectory(root);
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Scans:DbPath"] = Path.Combine(root, "history", "scans.db"),
                ["Scans:RetentionDays"] = retentionDays.ToString(),
                ["Scans:PruneIntervalMs"] = pruneIntervalMs.ToString(),
                ["Scans:FlushIntervalMs"] = "100",
                ["Scans:BatchSize"] = "100",
                ["Scans:QueueCapacity"] = capacity.ToString(),
            }).Build();
            var services = new ServiceCollection();
            services.AddSingleton<IConfiguration>(configuration);
            services.AddScanStore(configuration);
            var provider = services.BuildServiceProvider();
            var raw = provider.GetRequiredService<IScanPersistenceStore>();
            var store = new FaultStore(raw);
            var poStore = new PoStore(Path.Combine(root, "po"));
            var po = new ProductionOrderService(new FakePoClient(), poStore, new PoHistoryStore(poStore.DbRoot), new PoCodeCache());
            var intake = new ScanIntakeGate();
            var worker = new ScanPersistenceWorker(store, poStore, po, intake,
                provider.GetRequiredService<IOptions<ScanStoreOptions>>(), NullLogger<ScanPersistenceWorker>.Instance);
            codes ??= ["scan", "spare"];
            var order = new ProductionOrder
            {
                OrderNo = "TEST-ASYNC-PO-20261127-" + name,
                CreatedUtc = new(2026, 11, 27, 0, 0, 0, DateTimeKind.Utc), OrderQty = codes.Length,
            };
            if (initialize)
            {
                await poStore.EnsurePoDbAsync(order, codes.Select(c => new PoCode { CodeContent = c }).ToArray());
                Check(await po.StartProductionAsync(order.OrderNo), "Synthetic fixture starts from explicit sandbox PO: " + name);
            }
            var fixture = new Fixture { Root = root, Provider = provider, RawStore = raw, Store = store,
                Po = po, PoStore = poStore, Intake = intake, Worker = worker, Order = order };
            if (startWorker)
            {
                await worker.StartAsync(CancellationToken.None);
                fixture.Started = true;
            }
            return fixture;
        }

        public ScanRecordEntity Record(string code, string status) => new()
        {
            ReceivedUtc = ScanUtc, CodeContent = code, Raw = code,
            OrderNo = Order.OrderNo, Status = status, PlcResult = ScanPlcResults.Ok,
        };

        public async Task ExecutePoAsync(string sql)
        {
            var path = Path.GetFullPath(PoStore.GetDbPath(Order.OrderNo, Order.CreatedUtc));
            if (!path.StartsWith(Path.GetFullPath(Root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Fixture DB escaped its sandbox.");
            await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = path, Mode = SqliteOpenMode.ReadWrite }.ToString());
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            await command.ExecuteNonQueryAsync();
        }

        public async ValueTask DisposeAsync()
        {
            if (Started && !Stopped && Worker.IsHealthy)
            {
                using var timeout = new CancellationTokenSource(Deadline);
                await Worker.StopAsync(timeout.Token);
            }
            Worker.Dispose();
            await Provider.DisposeAsync();
            // Keep the unique sandbox for inspection; never clean an app directory.
        }
    }

    private sealed class FaultStore(IScanPersistenceStore inner) : IScanPersistenceStore
    {
        public volatile bool PersistAlwaysFails;
        public int PersistFailures;
        public int InjectedPersistFailures;
        public int CompleteFailures;
        public int PersistCalls;
        public int CompleteCalls;
        public int PruneFailures;
        public int InjectedPruneFailures;
        public TaskCompletionSource? PersistRelease;
        public TaskCompletionSource? CompleteRelease;
        public TaskCompletionSource? PruneRelease;
        public TaskCompletionSource PersistEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource CompleteEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource PruneEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task PersistBatchAsync(IReadOnlyList<ScanPersistenceEnvelope> batch, CancellationToken ct = default)
        {
            Interlocked.Increment(ref PersistCalls);
            PersistEntered.TrySetResult();
            if (PersistRelease is { } release) await release.Task.WaitAsync(ct);
            if (PersistAlwaysFails) throw new IOException("Synthetic persistence failure");
            if (Interlocked.Decrement(ref PersistFailures) >= 0)
            {
                Interlocked.Increment(ref InjectedPersistFailures);
                throw new IOException("Synthetic transient persistence failure");
            }
            await inner.PersistBatchAsync(batch, ct);
        }

        public async Task CompleteAsync(IReadOnlyList<string> eventIds, CancellationToken ct = default)
        {
            Interlocked.Increment(ref CompleteCalls);
            CompleteEntered.TrySetResult();
            if (CompleteRelease is { } release) await release.Task.WaitAsync(ct);
            if (Interlocked.Decrement(ref CompleteFailures) >= 0) throw new IOException("Synthetic outbox acknowledgement failure");
            await inner.CompleteAsync(eventIds, ct);
        }

        public Task<IReadOnlyList<ScanPersistenceEnvelope>> GetPendingAsync(CancellationToken ct = default) => inner.GetPendingAsync(ct);
        public Task VerifyHealthyAsync(CancellationToken ct = default) => inner.VerifyHealthyAsync(ct);
        public async Task<int> PruneAsync(DateTime cutoff, CancellationToken ct = default)
        {
            PruneEntered.TrySetResult();
            if (PruneRelease is { } release) await release.Task.WaitAsync(ct);
            if (Interlocked.Decrement(ref PruneFailures) >= 0)
            {
                Interlocked.Increment(ref InjectedPruneFailures);
                throw new IOException("Synthetic retention housekeeping failure");
            }
            return await inner.PruneAsync(cutoff, ct);
        }
    }

    private sealed class FakePoClient : IPoClient
    {
        public Task<IReadOnlyList<ProductionOrder>> GetOrdersAsync(bool includeCompleted = false, CancellationToken ct = default)
            => throw new InvalidOperationException("Coordinator tests never call a PO server.");
        public Task<IReadOnlyList<PoCode>> GetCodesAsync(string orderNo, CancellationToken ct = default)
            => throw new InvalidOperationException("Coordinator tests never call a PO server.");
        public Task<ProductionOrder?> CreateOrderAsync(string orderNo, int orderQty, CancellationToken ct = default)
            => throw new InvalidOperationException("Coordinator tests never call a PO server.");
    }
}
