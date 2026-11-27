using ClassLibraryMT.Po;
using Microsoft.Data.Sqlite;

internal static class PoMetadataContractChecks
{
    public static async Task RunAsync(string root, Action<string, bool, string> check)
    {
        Console.WriteLine("\n[PO contract] Giữ nguyên GTIN metadata và đối chiếu mã tương thích dữ liệu cũ");
        foreach (var gtin in new[] { "96385074", "036000291452", "4006381333931", "09506000134352" })
            check($"GTIN {gtin} có khóa đối chiếu nội bộ tương đương", PoCodeParser.TryNormalizeGtin(gtin, out var canonical)
                && canonical == gtin.PadLeft(14, '0'), "");
        foreach (var gtin in new[] { "4006381333932", "4006381333931 ", "０４００６３８１３３３９３１", "123", "4006381333931,09506000134352" })
            check("GTIN sai checksum/định dạng bị loại", !PoCodeParser.TryNormalizeGtin(gtin, out _), gtin);

        var raw = "https://example.invalid/products/01/04006381333931/21/AbC-1?batch=A";
        check("URL GS1 trả khóa GTIN để đối chiếu nội bộ", PoCodeParser.TryGetGtin(raw, out var normalized)
            && normalized == "04006381333931", "");
        check("Dạng AI ngoặc hợp lệ", PoCodeParser.TryGetGtin("(01)04006381333931(21)AbC-1", out normalized)
            && normalized == "04006381333931", "");
        check("Dạng element string có GS hợp lệ", PoCodeParser.TryGetGtin("]C10104006381333931\u001d21AbC-1", out normalized)
            && normalized == "04006381333931", "");
        check("Lấy GTIN nguồn từ URL giữ nguyên 13 chữ số", PoCodeParser.TryGetSourceGtin(
            "https://example.invalid/01/8935348800215/21/Raw13", out var sourceGtin) && sourceGtin == "8935348800215", "");
        check("Lấy GTIN nguồn giữ nguyên 14 chữ số có số 0 đầu", PoCodeParser.TryGetSourceGtin(raw, out sourceGtin)
            && sourceGtin == "04006381333931", "");
        foreach (var invalid in new[]
        {
            "https://example.invalid/01/00012345678905/21/A/01/09506000134352/21/B",
            "https://example.invalid/01/09506000134352/21/A/01/09506000134352/21/B",
            "/01/09506000134352/21/A/21/B",
            "/01/09506000134352/21/A?01=00012345678905",
            "/01/09506000134352/21/A?%32%31=B",
            "(01)09506000134352(21)A(01)B",
            "(01)09506000134352(21)A(21)B",
            "/01/09506000134352/21/ A",
            "/01/09506000134352/21/A\n",
            "/01/09506000134353/21/A",
            "/01/09506000134352/21/123456789012345678901",
        })
            check("Mã sai cấu trúc hoặc AI lặp bị loại", !PoCodeParser.TryGetGtin(invalid, out _), invalid);

        var store = new PoStore(root);
        var order = new ProductionOrder
        {
            OrderNo = "TEST-ASYNC-PO-20261127-PLANNED", OrderQty = 4, Gtin = "4006381333931", CreatedUtc = DateTime.UtcNow,
            Additional = new() { ["product"] = "Sản phẩm thử" },
        };
        await store.EnsurePoDbAsync(order, [new() { CodeContent = raw }]);
        await store.MarkScannedAsync(order.OrderNo, order.CreatedUtc, raw);
        await store.SetMetaAsync(order.OrderNo, order.CreatedUtc, PoStore.MetaStatus, "Running");
        await store.SetMetaAsync(order.OrderNo, order.CreatedUtc, PoStore.MetaStartedUtc, order.CreatedUtc.ToString("O"));
        order.OrderQty = 1;
        await store.EnsurePoDbAsync(order, [new() { CodeContent = raw }]);
        var row = (await new PoStore(root).ListLocalOrdersAsync()).Single();
        var meta = await store.GetMetaAsync(order.OrderNo, order.CreatedUtc);
        check("Store round-trip planned4 actual1 giữ nguyên GTIN13 đã nhập", row.OrderQty == 4 && row.TotalCodes == 1
            && row.Gtin == "4006381333931" && meta[PoStore.MetaGtin] == row.Gtin, "");
        check("Replay không mất scan hoặc trạng thái Running", row.ScannedCodes == 1
            && row.Status == ProductionOrderStatus.Running && row.StartedUtc is not null, "");
        check("CodeContent raw giữ nguyên ordinal", (await store.GetCodesAsync(order.OrderNo, order.CreatedUtc))
            .Single().CodeContent.Equals(raw, StringComparison.Ordinal), "");
        order.Gtin = "09506000134352";
        order.OrderQty = 99;
        try
        {
            await store.EnsurePoDbAsync(order, [new() { CodeContent = "/01/09506000134352/21/B" }]);
            check("Store không được đổi GTIN", false, "");
        }
        catch (InvalidDataException)
        {
            row = (await store.ListLocalOrdersAsync()).Single();
            check("Store GTIN bất biến và rollback planned/scan nguyên tử", row is { Gtin: "4006381333931", OrderQty: 4, TotalCodes: 1, ScannedCodes: 1 }, "");
        }

        var legacyAdditional = new ProductionOrder
        {
            OrderNo = "TEST-ASYNC-PO-20261127-LEGACY-ADDITIONAL", OrderQty = 9, CreatedUtc = order.CreatedUtc,
            Additional = new() { ["GTIN"] = "4006381333931", ["product"] = "Cũ" },
        };
        await store.EnsurePoDbAsync(legacyAdditional, [new() { CodeContent = raw }]);
        await RemoveMetaAsync(store, legacyAdditional, PoStore.MetaGtin);
        await RemoveMetaAsync(store, legacyAdditional, PoStore.MetaSourceGtin);
        var legacyCodes = new ProductionOrder { OrderNo = "TEST-ASYNC-PO-20261127-LEGACY-CODES", OrderQty = 7, CreatedUtc = order.CreatedUtc };
        await store.EnsurePoDbAsync(legacyCodes, [new() { CodeContent = raw }]);
        await RemoveMetaAsync(store, legacyCodes, PoStore.MetaOrderQty);
        var mixed = new ProductionOrder { OrderNo = "TEST-ASYNC-PO-20261127-LEGACY-MIXED", OrderQty = 2, CreatedUtc = order.CreatedUtc };
        await store.EnsurePoDbAsync(mixed, [new() { CodeContent = raw }, new() { CodeContent = "/01/09506000134352/21/B" }]);
        var opaque = new ProductionOrder { OrderNo = "TEST-ASYNC-PO-20261127-LEGACY-OPAQUE", OrderQty = 1, CreatedUtc = order.CreatedUtc };
        await store.EnsurePoDbAsync(opaque, [new() { CodeContent = "opaque legacy identity" }]);
        var legacyRows = await store.ListLocalOrdersAsync();
        check("Legacy additional.gtin giữ nguyên GTIN13 với planned cũ", legacyRows.Single(o => o.OrderNo == legacyAdditional.OrderNo)
            is { OrderQty: 9, TotalCodes: 1, Gtin: "4006381333931" }, "");
        check("Legacy thiếu planned fallback actual và suy GTIN duy nhất", legacyRows.Single(o => o.OrderNo == legacyCodes.OrderNo)
            is { OrderQty: 1, TotalCodes: 1, Gtin: "04006381333931" }, "");
        check("Legacy trộn GTIN hoặc opaque không tự gán GTIN", legacyRows.Single(o => o.OrderNo == mixed.OrderNo).Gtin.Length == 0
            && legacyRows.Single(o => o.OrderNo == opaque.OrderNo).Gtin.Length == 0, "");

        const string rawGtin13 = "8935348800215";
        const string paddedGtin14 = "08935348800215";
        const string codeGtin13 = "https://example.invalid/01/8935348800215/21/Legacy13";
        const string codeGtin14 = "https://example.invalid/01/08935348800215/21/Legacy14";
        var legacyPadded = new ProductionOrder
        {
            OrderNo = "TEST-ASYNC-PO-20261127-LEGACY-PADDED", OrderQty = 1,
            Gtin = paddedGtin14, CreatedUtc = order.CreatedUtc,
            Additional = new() { ["gtin"] = paddedGtin14 },
        };
        await store.EnsurePoDbAsync(legacyPadded, [new() { CodeContent = codeGtin13 }]);
        await store.MarkScannedAsync(legacyPadded.OrderNo, legacyPadded.CreatedUtc, codeGtin13);
        await RemoveMetaAsync(store, legacyPadded, PoStore.MetaSourceGtin);
        var scannedBeforeReload = (await store.GetCodesAsync(legacyPadded.OrderNo, legacyPadded.CreatedUtc)).Single();

        var explicitGtin14 = new ProductionOrder
        {
            OrderNo = "TEST-ASYNC-PO-20261127-EXPLICIT-GTIN14", OrderQty = 1,
            Gtin = paddedGtin14, CreatedUtc = order.CreatedUtc,
        };
        await store.EnsurePoDbAsync(explicitGtin14, [new() { CodeContent = codeGtin13 }]);

        var mixedSource = new ProductionOrder
        {
            OrderNo = "TEST-ASYNC-PO-20261127-LEGACY-MIXED-SOURCE", OrderQty = 2,
            Gtin = paddedGtin14, CreatedUtc = order.CreatedUtc,
        };
        await store.EnsurePoDbAsync(mixedSource, [new() { CodeContent = codeGtin13 }, new() { CodeContent = codeGtin14 }]);
        await RemoveMetaAsync(store, mixedSource, PoStore.MetaSourceGtin);

        var unsafeSourceOrders = new List<ProductionOrder>();
        foreach (var (suffix, code) in new[]
        {
            ("OTHER-PRODUCT", "/01/09506000134352/21/OtherProduct"),
            ("OPAQUE", "opaque legacy identity"),
        })
        {
            var unsafeSource = new ProductionOrder
            {
                OrderNo = "TEST-ASYNC-PO-20261127-LEGACY-KNOWN-" + suffix, OrderQty = 1,
                Gtin = paddedGtin14, CreatedUtc = order.CreatedUtc,
            };
            await store.EnsurePoDbAsync(unsafeSource, [new() { CodeContent = code }]);
            await RemoveMetaAsync(store, unsafeSource, PoStore.MetaSourceGtin);
            unsafeSourceOrders.Add(unsafeSource);
        }

        var sourceRows = await new PoStore(root).ListLocalOrdersAsync();
        var restoredLegacy = sourceRows.Single(o => o.OrderNo == legacyPadded.OrderNo);
        var legacyPaddedMeta = await store.GetMetaAsync(legacyPadded.OrderNo, legacyPadded.CreatedUtc);
        var scannedAfterReload = (await store.GetCodesAsync(legacyPadded.OrderNo, legacyPadded.CreatedUtc)).Single();
        check("Legacy padded14 phục hồi GTIN13 chỉ khi toàn bộ mã đồng nhất và tương đương", restoredLegacy.Gtin == rawGtin13
            && restoredLegacy.Additional["gtin"] == rawGtin13, "");
        check("Đọc tương thích legacy không ghi lại metadata hoặc marker trong DB", legacyPaddedMeta[PoStore.MetaGtin] == paddedGtin14
            && !legacyPaddedMeta.ContainsKey(PoStore.MetaSourceGtin), "");
        check("Đọc GTIN legacy giữ nguyên mã và lịch sử scan", restoredLegacy.ScannedCodes == 1
            && scannedBeforeReload.ScannedUtc is not null && scannedAfterReload.ScannedUtc == scannedBeforeReload.ScannedUtc
            && scannedAfterReload.ActivatedUtc == scannedBeforeReload.ActivatedUtc && scannedAfterReload.CodeContent == codeGtin13, "");
        var explicitMeta = await store.GetMetaAsync(explicitGtin14.OrderNo, explicitGtin14.CreatedUtc);
        check("PO mới cố ý nhập GTIN14 có marker không bị rút về GTIN13", sourceRows.Single(o => o.OrderNo == explicitGtin14.OrderNo).Gtin == paddedGtin14
            && explicitMeta[PoStore.MetaSourceGtin] == paddedGtin14, "");
        check("Legacy chứa cả GTIN nguồn13 và14 giữ metadata thay vì chọn mã đầu", sourceRows.Single(o => o.OrderNo == mixedSource.OrderNo).Gtin == paddedGtin14, "");
        foreach (var unsafeSource in unsafeSourceOrders)
            check("Legacy có metadata hợp lệ nhưng mã khác sản phẩm hoặc opaque không suy lại GTIN", sourceRows.Single(o => o.OrderNo == unsafeSource.OrderNo).Gtin == paddedGtin14,
                unsafeSource.OrderNo);
    }

    private static async Task RemoveMetaAsync(PoStore store, ProductionOrder order, string key)
    {
        await using var connection = new SqliteConnection($"Data Source={store.GetDbPath(order.OrderNo, order.CreatedUtc)}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM PoMeta WHERE Key=$key;";
        command.Parameters.AddWithValue("$key", key);
        await command.ExecuteNonQueryAsync();
    }
}
