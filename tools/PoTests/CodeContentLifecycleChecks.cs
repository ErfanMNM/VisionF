using ClassLibraryMT.Po;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

internal static class CodeContentLifecycleChecks
{
    public static async Task RunAsync(string workDir, Action<string, bool, string> check)
    {
        Console.WriteLine("\n[11] Exact identity and commit-before-PLC Pass lifecycle");
        var identity = await Fixture.Create(workDir, "identity",
            " Chuỗi\u001dopaque;Code ", "case", "Case", "remaining");
        check("Start exact-content PO", await identity.Service.StartProductionAsync(identity.Order.OrderNo), "");
        check("Full Unicode/GS/whitespace content is eligible",
            identity.Service.ValidateCode(" Chuỗi\u001dopaque;Code ") == PoCodeValidation.Valid, "");
        check("Trimmed content is a different code",
            identity.Service.ValidateCode("Chuỗi\u001dopaque;Code") == PoCodeValidation.InvalidForOrder, "");
        var callbacks = 0;
        var missing = await identity.Service.ProcessCodeAsync("unknown", _ => { callbacks++; return Task.FromResult(true); });
        check("Missing code never sends PLC Pass", missing.Validation == PoCodeValidation.InvalidForOrder && callbacks == 0, "");
        var rejected = await identity.Service.ProcessCodeAsync("case", _ => { callbacks++; return Task.FromResult(false); });
        check("PLC rejection does not undo the already consumed code",
            rejected.Validation == PoCodeValidation.Valid && rejected.PlcWritten == false && rejected.Committed
            && identity.Service.ValidateCode("case") == PoCodeValidation.Duplicate, "");
        check("PLC rejection leaves durable Scanned state and PO count",
            (await identity.Store.GetCodesWithFlagsAsync(identity.Order.OrderNo, identity.Order.CreatedUtc))
                .Single(p => p.Key == "case").Value.HasFlag(PoCodeCache.Flags.Scanned)
            && await identity.Store.CountScannedAsync(identity.Order.OrderNo, identity.Order.CreatedUtc) == 1, "");
        var accepted = await identity.Service.ProcessCodeAsync("remaining", _ =>
        {
            callbacks++;
            return Task.FromException<bool>(new IOException("PLC result unavailable"));
        });
        check("PLC exception after commit keeps the code consumed",
            accepted.Validation == PoCodeValidation.Valid && accepted.PlcWritten is null && accepted.Committed
            && identity.Service.ValidateCode("remaining") == PoCodeValidation.Duplicate, "");
        var repeat = await identity.Service.ProcessCodeAsync("case", _ => { callbacks++; return Task.FromResult(true); });
        check("Duplicate never sends another PLC Pass", repeat.Validation == PoCodeValidation.Duplicate && callbacks == 2, "");
        check("Ordinal case variant remains independent", identity.Service.ValidateCode("Case") == PoCodeValidation.Valid, "");
        var identityRestart = identity.Restart();
        check("Restart restores both committed exact-content scans",
            await identityRestart.RestoreRunningOrderAsync()
            && identityRestart.ValidateCode("case") == PoCodeValidation.Duplicate
            && identityRestart.ValidateCode("Case") == PoCodeValidation.Valid
            && identityRestart.ScannedCount == 2, "");

        var independentOrder = new ProductionOrder
        {
            OrderNo = "identity-independent", CreatedUtc = identity.Order.CreatedUtc, OrderQty = 2,
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

        Console.WriteLine("\n[12] PLC failure after commit and PO database failure before PLC");
        var ambiguous = await Fixture.Create(workDir, "ambiguous", "uncertain", "other");
        await ambiguous.Service.StartProductionAsync(ambiguous.Order.OrderNo);
        var uncertain = await ambiguous.Service.ProcessCodeAsync("uncertain",
            _ => Task.FromException<bool>(new IOException("ACK lost")));
        check("Lost ACK after commit keeps Pass state without rolling PO back",
            uncertain is { Validation: PoCodeValidation.Valid, PlcWritten: null, Committed: true }
            && !ambiguous.Service.IsHalted
            && ambiguous.Service.ValidateCode("uncertain") == PoCodeValidation.Duplicate, "");
        check("Lost ACK has durable Scanned state and no pending intent",
            (await ambiguous.Store.GetCodesWithFlagsAsync(ambiguous.Order.OrderNo, ambiguous.Order.CreatedUtc))
                .Single(p => p.Key == "uncertain").Value == PoCodeCache.Flags.Scanned
            && await ambiguous.Store.CountScannedAsync(ambiguous.Order.OrderNo, ambiguous.Order.CreatedUtc) == 1, "");
        var ambiguousRestart = ambiguous.Restart();
        await ambiguousRestart.RestoreRunningOrderAsync();
        var replayCallbacks = 0;
        var quarantined = await ambiguousRestart.ProcessCodeAsync("uncertain", _ => { replayCallbacks++; return Task.FromResult(true); });
        check("Restart prevents duplicate Pass replay after lost ACK",
            !ambiguousRestart.IsHalted && quarantined.Validation == PoCodeValidation.Duplicate && replayCallbacks == 0, "");

        var commitFault = await Fixture.Create(workDir, "commit-fault", "acknowledged", "other");
        await commitFault.Service.StartProductionAsync(commitFault.Order.OrderNo);
        await Execute(commitFault, """
            CREATE TRIGGER reject_scan BEFORE UPDATE OF ScannedUtc ON PoCodes
            WHEN NEW.ScannedUtc IS NOT NULL BEGIN SELECT RAISE(ABORT, 'test scan commit failure'); END;
            """);
        var plcCallbacks = 0;
        try { await commitFault.Service.ProcessCodeAsync("acknowledged", _ => { plcCallbacks++; return Task.FromResult(true); }); }
        catch (SqliteException) { }
        check("PO commit failure prevents PLC Pass",
            plcCallbacks == 0 && commitFault.Service.ValidateCode("acknowledged") == PoCodeValidation.Valid, "");
        var commitRestart = commitFault.Restart();
        await commitRestart.RestoreRunningOrderAsync();
        check("Pre-PLC PO commit failure leaves code eligible after restart",
            !commitRestart.IsHalted && commitRestart.ScannedCount == 0
            && commitRestart.ValidateCode("acknowledged") == PoCodeValidation.Valid, "");

        Console.WriteLine("\n[13] Completion failure after committed final Pass");
        var completionFault = await Fixture.Create(workDir, "completion-fault", "last-code");
        await completionFault.Service.StartProductionAsync(completionFault.Order.OrderNo);
        await Execute(completionFault, """
            CREATE TRIGGER reject_completion BEFORE INSERT ON PoMeta
            WHEN NEW.Key = 'completedUtc' BEGIN SELECT RAISE(ABORT, 'test completion metadata failure'); END;
            """);
        var finalPass = await completionFault.Service.ProcessCodeAsync("last-code", _ => Task.FromResult(true));
        check("Completion fault retains committed Pass with separate halt signal",
            finalPass is { Validation: PoCodeValidation.Valid, PlcWritten: true, Committed: true, ProductionHalted: true }
            && completionFault.Service.IsHalted && !string.IsNullOrEmpty(finalPass.Message), "");
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

        Console.WriteLine("\n[14] Final code activation drains completed POs and retries after restart");
        var drain = await Fixture.Create(workDir, "activation-drain", "final\u001dCode");
        await drain.Service.StartProductionAsync(drain.Order.OrderNo);
        await drain.Service.ProcessCodeAsync("final\u001dCode", _ => Task.FromResult(true));
        check("Final accepted code completes PO before activation",
            !drain.Service.IsProductionRunning && drain.Service.LastFinishedStatus == ProductionOrderStatus.Completed, "");
        drain.Client.Handler = (_, _, _) => Task.FromResult(new PoActivateResult { Ok = false });
        check("Rejected activation stays pending on completed PO",
            await drain.Service.ActivatePendingAsync() == 0
            && (await drain.Store.GetPendingActivationCodesAsync(drain.Order.OrderNo, drain.Order.CreatedUtc)).Count == 1, "");
        drain.Client.Handler = (_, _, _) => Task.FromException<PoActivateResult>(new IOException("MES offline"));
        check("Activation transport failure stays pending for later cycle",
            await drain.Service.ActivatePendingAsync() == 0
            && (await drain.Store.GetPendingActivationCodesAsync(drain.Order.OrderNo, drain.Order.CreatedUtc)).Count == 1, "");
        drain.Client.Handler = (_, codes, _) => Task.FromResult(new PoActivateResult { Ok = true, Activated = codes.Count });
        var drainRestart = drain.Restart();
        check("Restart drains completed PO without active production",
            await drainRestart.ActivatePendingAsync() == 1
            && drain.Client.Calls.Last().Codes.SequenceEqual(["final\u001dCode"])
            && drain.Client.Calls.Last().OrderNo == drain.Order.OrderNo, "");
        check("Successful activation acknowledgement persists and does not resend",
            (await drain.Store.GetPendingActivationCodesAsync(drain.Order.OrderNo, drain.Order.CreatedUtc)).Count == 0
            && await drainRestart.ActivatePendingAsync() == 0, "");

        Console.WriteLine("\n[15] Activation network await cannot contaminate newly selected PO cache");
        var switching = await Fixture.Create(workDir, "activation-switch", "shared", "a-other");
        var bOrder = new ProductionOrder { OrderNo = "switch-B", OrderQty = 2, CreatedUtc = switching.Order.CreatedUtc };
        await switching.Store.EnsurePoDbAsync(bOrder, [new() { CodeContent = "shared" }, new() { CodeContent = "b-other" }]);
        await switching.Service.StartProductionAsync(switching.Order.OrderNo);
        await switching.Service.ProcessCodeAsync("shared", _ => Task.FromResult(true));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        switching.Client.Handler = async (_, contents, ct) =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(ct);
            return new PoActivateResult { Ok = true, Activated = contents.Count };
        };
        var activating = switching.Service.ActivatePendingAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await switching.Service.CancelProductionAsync();
        check("Can switch POs while activation awaits MES",
            await switching.Service.StartProductionAsync(bOrder.OrderNo), "");
        release.TrySetResult();
        check("Captured source PO activation completes", await activating.WaitAsync(TimeSpan.FromSeconds(10)) == 1, "");
        check("Activation ACK persisted only in original cancelled PO",
            (await switching.Store.GetCodesAsync(switching.Order.OrderNo, switching.Order.CreatedUtc))
                .Single(p => p.CodeContent == "shared").ActivatedUtc is not null
            && (await switching.Store.GetCodesAsync(bOrder.OrderNo, bOrder.CreatedUtc))
                .Single(p => p.CodeContent == "shared").ActivatedUtc is null, "");
        check("New PO cache retains eligibility and zero counter",
            switching.Cache.OrderNo == bOrder.OrderNo && !switching.Cache.IsActivated("shared")
            && switching.Service.ValidateCode("shared") == PoCodeValidation.Valid && switching.Service.ScannedCount == 0, "");

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
        await mixed.Service.ProcessCodeAsync("fresh", _ => Task.FromResult(true));
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
            var order = new ProductionOrder { OrderNo = name, OrderQty = codes.Length, CreatedUtc = DateTime.UtcNow };
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
        public List<(string OrderNo, IReadOnlyList<string> Codes)> Calls { get; } = [];
        public Func<string, IReadOnlyList<string>, CancellationToken, Task<PoActivateResult>> Handler { get; set; }
            = (_, codes, _) => Task.FromResult(new PoActivateResult { Ok = true, Activated = codes.Count });

        public Task<IReadOnlyList<ProductionOrder>> GetOrdersAsync(bool includeCompleted = false, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<ProductionOrder>>([]);
        public Task<IReadOnlyList<PoCode>> GetCodesAsync(string orderNo, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<PoCode>>([]);
        public Task<ProductionOrder?> CreateOrderAsync(string orderNo, int orderQty, CancellationToken ct = default)
            => Task.FromResult<ProductionOrder?>(null);
        public Task<PoActivateResult> ActivateAsync(string orderNo, IReadOnlyList<string> codes, CancellationToken ct = default)
        {
            Calls.Add((orderNo, codes.ToArray()));
            return Handler(orderNo, codes, ct);
        }
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
