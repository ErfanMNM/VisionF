using ClassLibraryMT.Po;

internal static class PoDownloadContractChecks
{
    public static async Task RunAsync(string root, Action<string, bool, string> check)
    {
        Console.WriteLine("\n[PO download] Lọc từng code, giữ planned và tách validation với transport");
        Directory.CreateDirectory(root);
        var store = new PoStore(root);
        var client = new ContractClient();
        var service = new ProductionOrderService(client, store, new PoHistoryStore(root), new PoCodeCache());
        var raw = "https://example.invalid/01/04006381333931/21/Case?batch=A";
        client.Order = new() { OrderNo = "MIXED", OrderQty = 4, Gtin = "4006381333931", CreatedUtc = DateTime.UtcNow };
        client.Codes = [new() { CodeContent = raw }, new() { CodeContent = raw },
            new() { CodeContent = "invalid raw " }, new() { CodeContent = "/01/09506000134352/21/Other" }];
        var result = await service.SyncFromServerAsync();
        var detail = result.Orders.Single();
        check("Download4→1 báo counts và từng lý do đúng", result is { NewPoCount: 1, NewCodeCount: 1, AcceptedCount: 1, PlannedCount: 4 }
            && detail.InputCount == 4 && detail.InvalidCodes.Count == 2 && detail.DuplicateCodes.Count == 1
            && detail.InvalidCodes[0] == new PoCodeRejection("invalid raw ", "invalidStructure")
            && detail.InvalidCodes[1].Reason == "gtinMismatch" && detail.DuplicateCodes[0] == raw
            && result.HasValidationErrors && detail.ValidationError is null, "");
        var row = (await store.ListLocalOrdersAsync()).Single();
        check("Download lưu planned4 actual1 và GTIN normalized", row is { OrderQty: 4, TotalCodes: 1, Gtin: "04006381333931" }, "");
        check("Download không thay đổi DTO đầu vào hoặc raw identity", client.Order.OrderQty == 4
            && client.Order.Gtin == "4006381333931" && (await store.GetCodesAsync(row.OrderNo, row.CreatedUtc)).Single().CodeContent == raw, "");
        var summary = (await service.GetLocalOrdersAsync()).Single();
        check("Summary tách planned4 khỏi actual1", summary.OrderQty == 4 && summary.TotalCodes == 1
            && summary.Order.Gtin == row.Gtin, "");
        var replay = await service.SyncFromServerAsync();
        row = (await store.ListLocalOrdersAsync()).Single();
        check("Replay không tăng planned/actual và báo duplicate đầy đủ", replay.NewCodeCount == 0
            && replay.NewPoCount == 0 && replay.AcceptedCount == 0 && replay.DuplicateCodes.Count == 2
            && row.OrderQty == 4 && row.TotalCodes == 1, "");

        client.Codes = [new() { CodeContent = "/01/04006381333931/21/case?batch=A" }];
        client.Order.OrderQty = 1;
        var appended = await service.SyncFromServerAsync();
        row = (await store.ListLocalOrdersAsync()).Single();
        check("Append raw khác case được giữ riêng, planned không hạ", appended.NewCodeCount == 1
            && row.OrderQty == 4 && row.TotalCodes == 2, "");
        check("Start dùng planned nhưng tiến độ dùng actual", await service.StartProductionAsync(row.OrderNo)
            && service.CurrentOrder?.OrderQty == 4 && service.TotalCount == 2 && service.CurrentOrder.Gtin == row.Gtin, "");
        var reloaded = new ProductionOrderService(client, store, new PoHistoryStore(root), new PoCodeCache());
        check("Restore giữ planned và GTIN", await reloaded.RestoreRunningOrderAsync()
            && reloaded.CurrentOrder?.OrderQty == 4 && reloaded.CurrentOrder.Gtin == row.Gtin && reloaded.TotalCount == 2, "");
        await reloaded.CancelProductionAsync();
        service = new ProductionOrderService(client, store, new PoHistoryStore(root), new PoCodeCache());

        client.Order = new() { OrderNo = "PARTIAL", OrderQty = 4, Gtin = "4006381333931", CreatedUtc = DateTime.UtcNow };
        client.Codes = [new() { CodeContent = raw }];
        var partial = await service.SyncFromServerAsync();
        row = (await store.ListLocalOrdersAsync()).Single(o => o.OrderNo == "PARTIAL");
        check("Snapshot accepted-only planned4/raw1 được lưu minh bạch", partial.PlannedCount == 4
            && partial.AcceptedCount == 1 && partial.Orders.Single().InputCount == 1
            && row.OrderQty == 4 && row.TotalCodes == 1 && partial.InvalidCodes.Count == 0, "");
        client.Order.Gtin = "09506000134352";
        client.Codes = [new() { CodeContent = "/01/09506000134352/21/Other" }];
        var mismatch = await service.SyncFromServerAsync();
        check("Reload rồi append GTIN khác bị từ chối nguyên tử", mismatch.Orders.Single().ValidationError is not null
            && mismatch.NewCodeCount == 0 && (await store.ListLocalOrdersAsync()).Single(o => o.OrderNo == "PARTIAL")
                is { Gtin: "04006381333931", OrderQty: 4, TotalCodes: 1 }, "");

        foreach (var scenario in new[] { "over-planned", "zero-planned", "missing-gtin", "bad-checksum", "empty" })
        {
            client.Order = new() { OrderNo = scenario, OrderQty = scenario == "zero-planned" ? 0 : 1,
                Gtin = scenario == "missing-gtin" ? "" : scenario == "bad-checksum" ? "4006381333932" : "4006381333931",
                CreatedUtc = DateTime.UtcNow };
            client.Codes = scenario == "empty" ? [] : scenario == "over-planned"
                ? [new() { CodeContent = raw }, new() { CodeContent = raw }] : [new() { CodeContent = raw }];
            var rejected = await service.SyncFromServerAsync();
            check($"Download {scenario} bị từ chối không tạo DB", rejected.HasValidationErrors && rejected.AcceptedCount == 0
                && rejected.Orders.Single().ValidationError is not null
                && !(await store.ListLocalOrdersAsync()).Any(o => o.OrderNo == scenario), "");
        }
        client.Order = new() { OrderNo = "ALL-INVALID", OrderQty = 2, Gtin = "4006381333931", CreatedUtc = DateTime.UtcNow };
        client.Codes = [new() { CodeContent = "opaque" }, new() { CodeContent = "/01/09506000134352/21/Other" }];
        var allInvalid = await service.SyncFromServerAsync();
        check("All-invalid trả chi tiết không tạo PO rỗng", allInvalid.InvalidCodes.Count == 2 && allInvalid.AcceptedCount == 0
            && allInvalid.Orders.Single().ValidationError is not null
            && !(await store.ListLocalOrdersAsync()).Any(o => o.OrderNo == "ALL-INVALID"), "");

        client.Order = new() { OrderNo = "LEGACY-DOWNLOAD", OrderQty = 3, CreatedUtc = DateTime.UtcNow,
            Additional = new() { ["gtin"] = "4006381333931" } };
        client.Codes = [new() { CodeContent = raw }];
        var legacy = await service.SyncFromServerAsync();
        check("Metadata GTIN legacy additional tương thích download", legacy.AcceptedCount == 1
            && (await store.ListLocalOrdersAsync()).Single(o => o.OrderNo == "LEGACY-DOWNLOAD").Gtin == "04006381333931", "");

        var legacyMixed = new ProductionOrder { OrderNo = "LEGACY-MIXED", OrderQty = 3, CreatedUtc = DateTime.UtcNow,
            Additional = new() { ["gtin"] = "4006381333931" } };
        await store.EnsurePoDbAsync(legacyMixed, [new() { CodeContent = raw }, new() { CodeContent = "/01/09506000134352/21/Other" }]);
        client.Order = legacyMixed;
        client.Codes = [new() { CodeContent = "/01/04006381333931/21/New" }];
        var mixedLegacyResult = await service.SyncFromServerAsync();
        check("Legacy có metadata nhưng mã trộn GTIN bị chặn nguyên tử", mixedLegacyResult.Orders.Single().ValidationError is not null
            && mixedLegacyResult.NewCodeCount == 0 && (await store.ListLocalOrdersAsync()).Single(o => o.OrderNo == "LEGACY-MIXED").TotalCodes == 2, "");

        client.Failure = new PoServerException("Lỗi HTTP giả lập");
        try
        {
            await service.SyncFromServerAsync();
            check("Lỗi vận chuyển phải throw thay vì trả validation", false, "");
        }
        catch (PoServerException error)
        {
            check("Lỗi vận chuyển giữ exception gốc", ReferenceEquals(error, client.Failure), "");
        }
        client.Failure = null;
        client.CodeFailure = new PoServerException("JSON giả lập không hợp lệ");
        try
        {
            await service.SyncFromServerAsync();
            check("Lỗi tải codes phải throw", false, "");
        }
        catch (PoServerException error)
        {
            check("Lỗi tải codes không bị nuốt thành thành công rỗng", ReferenceEquals(error, client.CodeFailure), "");
        }
    }

    private sealed class ContractClient : IPoClient
    {
        public ProductionOrder Order { get; set; } = new();
        public IReadOnlyList<PoCode> Codes { get; set; } = [];
        public Exception? Failure { get; set; }
        public Exception? CodeFailure { get; set; }
        public Task<IReadOnlyList<ProductionOrder>> GetOrdersAsync(bool includeCompleted = false, CancellationToken ct = default) =>
            Failure is null ? Task.FromResult<IReadOnlyList<ProductionOrder>>([Order]) : Task.FromException<IReadOnlyList<ProductionOrder>>(Failure);
        public Task<IReadOnlyList<PoCode>> GetCodesAsync(string orderNo, CancellationToken ct = default) =>
            CodeFailure is null ? Task.FromResult(Codes) : Task.FromException<IReadOnlyList<PoCode>>(CodeFailure);

        public Task<ProductionOrder?> CreateOrderAsync(string orderNo, int orderQty, CancellationToken ct = default) =>
            Task.FromResult<ProductionOrder?>(null);
    }
}
