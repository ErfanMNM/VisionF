using ClassLibraryMT.Po;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

internal static class CodeContentLifecycleChecks
{
    public static async Task RunAsync(string workDir, Action<string, bool, string> check)
    {
        Console.WriteLine("\n[11] Exact identity and atomic RAM-before-PLC lifecycle");
        var identity = await Fixture.Create(workDir, "identity",
            " Chuỗi\u001dopaque;Code ", "case", "Case", "remaining");
        check("Start exact-content PO", await identity.Service.StartProductionAsync(identity.Order.OrderNo.ToLowerInvariant()), "");
        check("Full Unicode/GS/whitespace content is eligible",
            identity.Service.ValidateCode(" Chuỗi\u001dopaque;Code ") == PoCodeValidation.Valid, "");
        check("Trimmed content is a different code",
            identity.Service.ValidateCode("Chuỗi\u001dopaque;Code") == PoCodeValidation.InvalidForOrder, "");
        var callbacks = 0;
        var missing = await identity.Service.ProcessCodeAsync("unknown", _ => { callbacks++; return Task.FromResult(true); });
        check("Missing code never sends PLC Pass", missing.Validation == PoCodeValidation.InvalidForOrder && callbacks == 0, "");
        var scannedUtc = new DateTime(2026, 11, 27, 1, 2, 3, DateTimeKind.Utc).AddTicks(1234);
        var rejected = await identity.Service.ProcessCodeAsync("case", _ =>
        {
            callbacks++;
            check("RAM already blocks repeat before PLC ACK",
                identity.Service.ValidateCode("case") == PoCodeValidation.Duplicate, "");
            return Task.FromResult(false);
        }, receivedUtc: scannedUtc, eventId: "fixture-event-case");
        check("PLC rejection does not undo the already consumed RAM code",
            rejected is { Validation: PoCodeValidation.Valid, PlcWritten: false, Committed: true, Mutation: not null }
            && identity.Service.ValidateCode("case") == PoCodeValidation.Duplicate
            && rejected.Mutation!.OrderNo == identity.Order.OrderNo, "");
        check("Camera acceptance does not wait for or perform PO persistence",
            await identity.Store.CountScannedAsync(identity.Order.OrderNo, identity.Order.CreatedUtc) == 0, "");
        identity.Service.Changed += (_, _) => throw new InvalidOperationException("test UI observer failure");
        var accepted = await identity.Service.ProcessCodeAsync("remaining", _ =>
        {
            callbacks++;
            return Task.FromException<bool>(new OperationCanceledException("PLC ACK unavailable"));
        });
        check("PLC cancellation and observer exception still return captured mutation",
            accepted is { Validation: PoCodeValidation.Valid, PlcWritten: null, Committed: true, Mutation: not null }
            && identity.Service.ValidateCode("remaining") == PoCodeValidation.Duplicate, "");
        var repeat = await identity.Service.ProcessCodeAsync("case", _ => { callbacks++; return Task.FromResult(true); });
        check("Duplicate before batch never sends another PLC Pass", repeat.Validation == PoCodeValidation.Duplicate && callbacks == 2, "");
        check("Ordinal case variant remains independent", identity.Service.ValidateCode("Case") == PoCodeValidation.Valid, "");
        await identity.Store.MarkScannedBatchAsync([rejected.Mutation!, accepted.Mutation!]);
        await identity.Store.MarkScannedBatchAsync([rejected.Mutation! with { ScannedUtc = scannedUtc.AddHours(1) }]);
        var persisted = await identity.Store.GetCodesAsync(identity.Order.OrderNo, identity.Order.CreatedUtc);
        check("Idempotent batch retains first scan UTC and full code identity",
            persisted.Single(c => c.CodeContent == "case").ScannedUtc == scannedUtc
            && await identity.Store.CountScannedAsync(identity.Order.OrderNo, identity.Order.CreatedUtc) == 2, "");
        var identityRestart = identity.Restart();
        check("Restart restores both persisted exact-content scans",
            await identityRestart.RestoreRunningOrderAsync()
            && identityRestart.ValidateCode("case") == PoCodeValidation.Duplicate
            && identityRestart.ValidateCode("Case") == PoCodeValidation.Valid
            && identityRestart.ScannedCount == 2, "");

        var independentOrder = new ProductionOrder
        {
            OrderNo = "TEST-ASYNC-PO-20261127-identity-independent", CreatedUtc = identity.Order.CreatedUtc, OrderQty = 2,
        };
        await identity.Store.EnsurePoDbAsync(independentOrder,
            [new() { CodeContent = "case" }, new() { CodeContent = "independent-other" }]);
        check("Same exact CodeContent retained in different PO databases",
            (await identity.Store.GetCodesAsync(independentOrder.OrderNo, independentOrder.CreatedUtc)).Count == 2, "");
        await identityRestart.CancelProductionAsync();
        check("Second PO starts with independent scan state",
            await identityRestart.StartProductionAsync(independentOrder.OrderNo)
            && identityRestart.ValidateCode("case") == PoCodeValidation.Valid
            && identityRestart.ScannedCount == 0, "");
        await identityRestart.CancelProductionAsync();

        Console.WriteLine("\n[12] Slow/failing PO persistence follows PLC and retries idempotently");
        var commitFault = await Fixture.Create(workDir, "commit-fault", "acknowledged", "other");
        await commitFault.Service.StartProductionAsync(commitFault.Order.OrderNo);
        await Execute(commitFault, """
            CREATE TRIGGER reject_scan BEFORE UPDATE OF ScannedUtc ON PoCodes
            WHEN NEW.ScannedUtc IS NOT NULL BEGIN SELECT RAISE(ABORT, 'test scan commit failure'); END;
            """);
        var plcCallbacks = 0;
        var pending = await commitFault.Service.ProcessCodeAsync("acknowledged", _ => { plcCallbacks++; return Task.FromResult(true); });
        var batchFailed = false;
        try { await commitFault.Store.MarkScannedBatchAsync([pending.Mutation!]); }
        catch (SqliteException) { batchFailed = true; }
        check("PO batch failure occurs after PLC while RAM keeps code consumed",
            plcCallbacks == 1 && batchFailed && pending.Mutation is not null
            && commitFault.Service.ValidateCode("acknowledged") == PoCodeValidation.Duplicate, "");
        var commitRestart = commitFault.Restart();
        await commitRestart.RestoreRunningOrderAsync();
        check("Documented crash-before-first-commit tail is not claimed durable",
            commitRestart.ScannedCount == 0 && commitRestart.ValidateCode("acknowledged") == PoCodeValidation.Valid, "");
        await Execute(commitFault, "DROP TRIGGER reject_scan;");
        await commitFault.Store.MarkScannedBatchAsync([pending.Mutation!]);
        await commitFault.Store.MarkScannedBatchAsync([pending.Mutation!]);
        check("Retry after temporary PO failure persists one consumed code",
            await commitFault.Store.CountScannedAsync(commitFault.Order.OrderNo, commitFault.Order.CreatedUtc) == 1, "");
        foreach (var invalid in new[]
        {
            pending.Mutation! with { OrderNo = "TEST-ASYNC-PO-20261127-missing" },
            pending.Mutation! with { OrderCreatedUtc = pending.Mutation!.OrderCreatedUtc.AddTicks(1) },
            pending.Mutation! with { CodeContent = "absent-code" },
        })
        {
            var faulted = false;
            try { await commitFault.Store.MarkScannedBatchAsync([invalid]); }
            catch (IOException) { faulted = true; }
            check("Missing PO/code or mismatched captured CreatedUtc faults batch", faulted, "");
        }
        check("Missing captured PO was never created as an empty DB",
            !File.Exists(commitFault.Store.GetDbPath("TEST-ASYNC-PO-20261127-missing", commitFault.Order.CreatedUtc)), "");

        Console.WriteLine("\n[13] Final RAM scan waits for batch, completion fault preserves safety");
        var completionFault = await Fixture.Create(workDir, "completion-fault", "last-code");
        await completionFault.Service.StartProductionAsync(completionFault.Order.OrderNo);
        await Execute(completionFault, """
            CREATE TRIGGER reject_completion BEFORE INSERT ON PoMeta
            WHEN NEW.Key = 'completedUtc' BEGIN SELECT RAISE(ABORT, 'test completion metadata failure'); END;
            """);
        var finalPass = await completionFault.Service.ProcessCodeAsync("last-code", _ => Task.FromResult(true));
        check("Final code remains in RAM with completion pending until batch",
            finalPass is { Validation: PoCodeValidation.Valid, PlcWritten: true, Committed: true, Mutation: not null }
            && completionFault.Service.IsCompletionPending && completionFault.Service.TotalCount == 1
            && !await completionFault.Service.CompletePendingAsync() && !completionFault.Service.IsHalted, "");
        await completionFault.Store.MarkScannedBatchAsync([finalPass.Mutation!]);
        var completionFailed = false;
        try { await completionFault.Service.CompletePendingAsync(); }
        catch (SqliteException) { completionFailed = true; }
        check("Completion fault is separate from accepted PLC result and retains RAM",
            completionFailed && completionFault.Service.IsHalted && completionFault.Service.ScannedCount == 1, "");
        var completionMeta = await completionFault.Store.GetMetaAsync(completionFault.Order.OrderNo, completionFault.Order.CreatedUtc);
        check("Completion status/timestamp roll back atomically",
            completionMeta[PoStore.MetaStatus] == ProductionOrderStatus.Running.ToString()
            && !completionMeta.ContainsKey(PoStore.MetaCompletedUtc)
            && completionMeta.ContainsKey(PoStore.MetaHalted), "");
        var completionRestart = completionFault.Restart();
        await completionRestart.RestoreRunningOrderAsync();
        check("Fully scanned PO with completion fault stays halted after restart",
            completionRestart.IsHalted && completionRestart.ScannedCount == 1
            && completionRestart.ValidateCode("last-code") == PoCodeValidation.Halted, "");

        Console.WriteLine("\n[14] Cancel closes acceptance and flushes pending mutations without deadlock");
        var cancelPending = await Fixture.Create(workDir, "cancel-pending", "pending", "other");
        await cancelPending.Service.StartProductionAsync(cancelPending.Order.OrderNo);
        var cancelScan = await cancelPending.Service.ProcessCodeAsync("pending", _ => Task.FromResult(true));
        var barrierEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var barrierRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        cancelPending.Service.PersistenceBarrier = async ct =>
        {
            barrierEntered.TrySetResult();
            await barrierRelease.Task.WaitAsync(ct);
            await cancelPending.Store.MarkScannedBatchAsync([cancelScan.Mutation!], ct);
        };
        var cancelling = cancelPending.Service.CancelProductionAsync();
        await barrierEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        check("Lifecycle barrier blocks RAM acceptance while keeping cache",
            cancelPending.Service.ValidateCode("other") == PoCodeValidation.Halted
            && cancelPending.Service.TotalCount == 2, "");
        check("Worker completion does not wait for lifecycle mutex", !await cancelPending.Service.CompletePendingAsync(), "");
        barrierRelease.TrySetResult();
        check("Cancel drains accepted scan before clearing cache",
            await cancelling.WaitAsync(TimeSpan.FromSeconds(10)) && cancelPending.Service.LastFinishedScannedCount == 1
            && cancelPending.Service.TotalCount == 0, "");

        var concurrent = await Fixture.Create(workDir, "concurrent", "one", "spare");
        await concurrent.Service.StartProductionAsync(concurrent.Order.OrderNo);
        var concurrentCallbacks = 0;
        var sameCode = await Task.WhenAll(Enumerable.Range(0, 32).Select(_ => Task.Run(() =>
            concurrent.Service.ProcessCodeAsync("one", _ =>
            {
                Interlocked.Increment(ref concurrentCallbacks);
                return Task.FromResult(true);
            }))));
        check("Concurrent exact code validate-and-accept yields one PLC call/mutation",
            concurrentCallbacks == 1 && concurrent.Service.ScannedCount == 1
            && sameCode.Count(result => result.Mutation is not null) == 1
            && sameCode.Count(result => result.Validation == PoCodeValidation.Duplicate) == 31, "");

        // The obsolete app-to-PO activation transport has been removed.
        // Captured PO mutations, retries and switching are covered by the async persistence harness.
        check("Production PO client exposes no legacy activation endpoint",
            typeof(IPoClient).GetMethod("ActivateAsync") is null
            && typeof(PoClient).GetMethod("ActivateAsync") is null, "");

        Console.WriteLine("\n[16] Imported activated-only state and fully processed startup");
        var mixed = await Fixture.Create(workDir, "activated-mixed", "fresh");
        await mixed.Store.EnsurePoDbAsync(mixed.Order,
            [new() { CodeContent = "activated-only", ActivatedUtc = DateTime.UtcNow }]);
        await mixed.Service.StartProductionAsync(mixed.Order.OrderNo);
        check("Activated-only import is Duplicate and contributes to processed count",
            mixed.Service.ValidateCode("activated-only") == PoCodeValidation.Duplicate
            && mixed.Service.ScannedCount == 1 && mixed.Service.TotalCount == 2
            && await mixed.Store.CountScannedAsync(mixed.Order.OrderNo, mixed.Order.CreatedUtc) == 1, "");
        var imported = (await mixed.Store.GetCodesAsync(mixed.Order.OrderNo, mixed.Order.CreatedUtc))
            .Single(p => p.CodeContent == "activated-only");
        check("Imported activation does not fabricate scan timestamp", imported.ScannedUtc is null && imported.ActivatedUtc is not null, "");
        var importedCallbacks = 0;
        await mixed.Service.ProcessCodeAsync("activated-only", _ => { importedCallbacks++; return Task.FromResult(true); });
        check("Imported activation never sends PLC Pass", importedCallbacks == 0, "");
        var fresh = await mixed.Service.ProcessCodeAsync("fresh", _ => Task.FromResult(true));
        await mixed.Store.MarkScannedBatchAsync([fresh.Mutation!]);
        await mixed.Service.CompletePendingAsync();
        check("Mixed imported/accepted state completes with union count",
            mixed.Service.LastFinishedScannedCount == 2 && mixed.Service.LastFinishedTotalCount == 2
            && !mixed.Service.IsProductionRunning, "");
        var allActivated = await Fixture.Create(workDir, "all-activated", "already-active");
        await Execute(allActivated, "UPDATE PoCodes SET ActivatedUtc = '2026-11-26T00:00:00Z';");
        check("Fully activated PO start immediately completes without requiring scan",
            await allActivated.Service.StartProductionAsync(allActivated.Order.OrderNo)
            && !allActivated.Service.IsProductionRunning && allActivated.Service.CurrentOrder is null
            && allActivated.Service.LastFinishedStatus == ProductionOrderStatus.Completed
            && allActivated.Service.LastFinishedScannedCount == 1, "");
        var allScanned = await Fixture.Create(workDir, "all-scanned-restore", "already-scanned");
        await allScanned.Store.MarkScannedAsync(allScanned.Order.OrderNo, allScanned.Order.CreatedUtc, "already-scanned");
        await allScanned.Store.SetMetaAsync(allScanned.Order.OrderNo, allScanned.Order.CreatedUtc,
            PoStore.MetaStatus, ProductionOrderStatus.Running.ToString());
        await allScanned.History.StartRunAsync(allScanned.Order.OrderNo, 1, allScanned.Order.CreatedUtc);
        check("Fully processed Running PO restore durably completes",
            await allScanned.Service.RestoreRunningOrderAsync() && !allScanned.Service.IsProductionRunning
            && allScanned.Service.LastFinishedScannedCount == 1
            && (await allScanned.Store.GetMetaAsync(allScanned.Order.OrderNo, allScanned.Order.CreatedUtc))[PoStore.MetaStatus]
                == ProductionOrderStatus.Completed.ToString(), "");

        var legacyCasing = await Fixture.Create(workDir, "RESTORE-CASING", "first", "last");
        await legacyCasing.Store.SetMetaAsync(legacyCasing.Order.OrderNo, legacyCasing.Order.CreatedUtc,
            PoStore.MetaStatus, ProductionOrderStatus.Running.ToString());
        await legacyCasing.History.StartRunAsync(legacyCasing.Order.OrderNo.ToLowerInvariant(), 2,
            legacyCasing.Order.CreatedUtc);
        check("Legacy history casing restores canonical PO identity", await legacyCasing.Service.RestoreRunningOrderAsync()
            && legacyCasing.Service.CurrentOrder!.OrderNo == legacyCasing.Order.OrderNo, "");
        var firstCasing = await legacyCasing.Service.ProcessCodeAsync("first", _ => Task.FromResult(true));
        var lastCasing = await legacyCasing.Service.ProcessCodeAsync("last", _ => Task.FromResult(true));
        await legacyCasing.Store.MarkScannedBatchAsync([firstCasing.Mutation!, lastCasing.Mutation!]);
        check("Canonical replay completes original legacy-cased run history",
            await legacyCasing.Service.CompletePendingAsync() && await legacyCasing.History.GetRunningRunAsync() is null, "");

        Console.WriteLine("\n[17] Caller cancellation after durable Running cannot halt fully processed startup");
        foreach (var restore in new[] { false, true })
        {
            var operation = restore ? "restore" : "start";
            var cancelledStartup = await Fixture.Create(workDir, "cancel-completion-" + operation, "already-processed");
            if (restore)
            {
                await cancelledStartup.Store.MarkScannedAsync(cancelledStartup.Order.OrderNo,
                    cancelledStartup.Order.CreatedUtc, "already-processed");
                await cancelledStartup.Store.SetMetaAsync(cancelledStartup.Order.OrderNo,
                    cancelledStartup.Order.CreatedUtc, PoStore.MetaStatus, ProductionOrderStatus.Running.ToString());
                await cancelledStartup.History.StartRunAsync(cancelledStartup.Order.OrderNo, 1, cancelledStartup.Order.CreatedUtc);
            }
            else
            {
                await Execute(cancelledStartup, "UPDATE PoCodes SET ActivatedUtc = '2026-11-26T00:00:00Z';");
            }

            using var boundaryCancellation = new CancellationTokenSource();
            var boundaryLogger = new CancelAtBoundaryLogger(
                restore ? "Khôi phục PO " : "Bắt đầu sản xuất PO ", boundaryCancellation.Cancel);
            var startupService = new ProductionOrderService(cancelledStartup.Client, cancelledStartup.Store,
                cancelledStartup.History, cancelledStartup.Cache, logger: boundaryLogger);
            var completedStartup = restore
                ? await startupService.RestoreRunningOrderAsync(boundaryCancellation.Token)
                : await startupService.StartProductionAsync(cancelledStartup.Order.OrderNo, boundaryCancellation.Token);
            var startupMeta = await cancelledStartup.Store.GetMetaAsync(cancelledStartup.Order.OrderNo,
                cancelledStartup.Order.CreatedUtc);
            check($"Fully processed {operation}: cancellation at durable Running boundary still completes",
                completedStartup && boundaryCancellation.IsCancellationRequested && boundaryLogger.Cancellations == 1
                && !startupService.IsProductionRunning && !startupService.IsHalted
                && startupService.LastFinishedStatus == ProductionOrderStatus.Completed
                && startupService.LastFinishedScannedCount == 1
                && startupMeta[PoStore.MetaStatus] == ProductionOrderStatus.Completed.ToString()
                && startupMeta.ContainsKey(PoStore.MetaCompletedUtc) && !startupMeta.ContainsKey(PoStore.MetaHalted)
                && await cancelledStartup.History.GetRunningRunAsync() is null, "");
            var startupPassCallbacks = 0;
            var afterStartup = await startupService.ProcessCodeAsync("already-processed", _ =>
            {
                startupPassCallbacks++;
                return Task.FromResult(true);
            });
            check($"Fully processed {operation}: cancellation does not permit PLC replay",
                afterStartup.Validation == PoCodeValidation.NoProductionOrder && startupPassCallbacks == 0, "");
        }
    }

    private static async Task Execute(Fixture fixture, string sql)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = fixture.Store.GetDbPath(fixture.Order.OrderNo, fixture.Order.CreatedUtc),
        }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private sealed record Fixture(PoStore Store, PoHistoryStore History, PoCodeCache Cache,
        LifecycleClient Client, ProductionOrderService Service, ProductionOrder Order)
    {
        public static async Task<Fixture> Create(string root, string name, params string[] codes)
        {
            var store = new PoStore(Path.Combine(root, name));
            var history = new PoHistoryStore(store.DbRoot);
            var cache = new PoCodeCache();
            var client = new LifecycleClient();
            var order = new ProductionOrder { OrderNo = "TEST-ASYNC-PO-20261127-" + name, OrderQty = codes.Length, CreatedUtc = DateTime.UtcNow };
            await store.EnsurePoDbAsync(order, codes.Select(code => new PoCode { CodeContent = code }).ToList());
            return new(store, history, cache, client, new ProductionOrderService(client, store, history, cache), order);
        }

        public ProductionOrderService Restart()
        {
            var store = new PoStore(Store.DbRoot);
            return new(Client, store, new PoHistoryStore(store.DbRoot), new PoCodeCache());
        }
    }

    private sealed class LifecycleClient : IPoClient
    {

        public Task<IReadOnlyList<ProductionOrder>> GetOrdersAsync(bool includeCompleted = false, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<ProductionOrder>>([]);
        public Task<IReadOnlyList<PoCode>> GetCodesAsync(string orderNo, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<PoCode>>([]);
        public Task<ProductionOrder?> CreateOrderAsync(string orderNo, int orderQty, CancellationToken ct = default)
            => Task.FromResult<ProductionOrder?>(null);
    }

    private sealed class CancelAtBoundaryLogger(string prefix, Action cancel) : ILogger<ProductionOrderService>
    {
        public int Cancellations { get; private set; }
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!formatter(state, exception).StartsWith(prefix, StringComparison.Ordinal))
                return;
            Cancellations++;
            cancel();
        }
    }
}
