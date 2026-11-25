using ClassLibraryMT.Po;
using ClassLibraryMT.Scans;
using Microsoft.Data.Sqlite;

// ===================================================================
// Test harness cho lưu đồng PO local-first.
// Chạy: dotnet run --project tools/PoTests
// ===================================================================

var root = Path.Combine(AppContext.BaseDirectory);
var workDir = Path.Combine(root, "po-test-work");
if (Directory.Exists(workDir)) Directory.Delete(workDir, true);
Directory.CreateDirectory(workDir);

int passed = 0, failed = 0;

void Check(string label, bool ok, string detail = "")
{
    if (ok) { passed++; Console.WriteLine($"  [OK]   {label}"); }
    else { failed++; Console.WriteLine($"  [FAIL] {label}" + (detail.Length > 0 ? $" -- {detail}" : "")); }
}

Console.WriteLine("========================================");
Console.WriteLine("  KIEM THU LUONG PO LOCAL-FIRST");
Console.WriteLine("========================================");

// ---------------------------------------------------------------
// [1] PoStore: EnsurePoDbAsync + ListLocalOrdersAsync
// ---------------------------------------------------------------
Console.WriteLine("\n[1] PoStore: ghi va doc PO local");

var store = new PoStore(workDir);
var order = new ProductionOrder
{
    OrderNo = "PO-TEST-001",
    OrderQty = 3,
    CreatedUtc = new DateTime(2026, 11, 9, 8, 0, 0, DateTimeKind.Utc),
};
var codes = new List<PoCode>
{
    new() { CodeContent = "/01/08934567890123/21/S001" },
    new() { CodeContent = "/01/08934567890123/21/S002" },
    new() { CodeContent = "/01/08934567890123/21/S003" },
};

await store.EnsurePoDbAsync(order, codes);
var locals = await store.ListLocalOrdersAsync();
Check("Ghi PO vao file DB local", locals.Count == 1, $"count={locals.Count}");
Check("Doc lai dung OrderNo", locals.Count == 1 && locals[0].OrderNo == "PO-TEST-001");
Check("Tong so ma = 3", locals.Count == 1 && locals[0].TotalCodes == 3, $"{locals.Count}");
Check("Chua quet => ScannedCodes = 0", locals.Count == 1 && locals[0].ScannedCodes == 0);

// Sync lai: them ma moi, ma cu phai giu nguyen.
var codes2 = new List<PoCode>(codes)
{
    new() { CodeContent = "/01/08934567890123/21/S004" },
};
await store.EnsurePoDbAsync(order, codes2);
var locals2 = await store.ListLocalOrdersAsync();
Check("Sync lan 2: ma cu khong mat, ma moi duoc them",
    locals2.Count == 1 && locals2[0].TotalCodes == 4,
    $"total={(locals2.Count == 1 ? locals2[0].TotalCodes : -1)}");

// ---------------------------------------------------------------
// [2] PoCodeCache: chi nap PO dang chay, Clear khi xoa
// ---------------------------------------------------------------
Console.WriteLine("\n[2] PoCodeCache: Dictionary 1 entry / ma");

var cache = new PoCodeCache();
Check("Cache moi rong", cache.ValidCount == 0 && cache.ScannedCount == 0);

var flags = codes.Select(c => new KeyValuePair<string, PoCodeCache.Flags>(
    c.CodeContent, PoCodeCache.Flags.None)).ToList();
cache.LoadOrder("PO-TEST-001", flags);
Check("Nap 3 ma vao cache", cache.ValidCount == 3, $"{cache.ValidCount}");
Check("Contains ma hop le", cache.Contains(codes[0].CodeContent));
Check("Contains ma khong ton tai = false", !cache.Contains("/01/08934567890123/21/XXX"));
Check("Chua quet => IsScanned false", !cache.IsScanned(codes[0].CodeContent));

cache.MarkScanned(codes[0].CodeContent);
Check("MarkScanned => ScannedCount = 1", cache.ScannedCount == 1, $"{cache.ScannedCount}");
Check("MarkScanned lan 2 tra false (truong) [dung cho chong race]",
    !cache.MarkScanned(codes[0].CodeContent));
Check("IsScanned sau khi quet", cache.IsScanned(codes[0].CodeContent));

cache.Clear();
Check("Clear() xoa het cache", cache.ValidCount == 0 && cache.ScannedCount == 0);
Check("Clear() xoa OrderNo", cache.OrderNo.Length == 0);

// ---------------------------------------------------------------
// [3] PoHistoryStore: ghi va doc lich su chay PO
// ---------------------------------------------------------------
Console.WriteLine("\n[3] PoHistoryStore: lich su chay PO");

var history = new PoHistoryStore(workDir);

// Ghi truoc de tao file DB (schema duoc tao luc mo connection dau tien).
await history.StartRunAsync("PO-WARMUP", 1, order.CreatedUtc);
Check("File po-history.db duoc tao", File.Exists(history.DbPath));

await history.CompleteRunAsync("PO-WARMUP");
var running = await history.GetRunningRunAsync();
Check("Chua co PO nao dang chay truoc khi bat dau", running is null);

await history.StartRunAsync("PO-TEST-001", 3, order.CreatedUtc);
running = await history.GetRunningRunAsync();
Check("Ghi StartRun => co PO dang chay", running is not null);
Check("Doc dung OrderNo", running?.OrderNo == "PO-TEST-001", running?.OrderNo ?? "null");

await history.CompleteRunAsync("PO-TEST-001");
running = await history.GetRunningRunAsync();
Check("CompleteRun => khong con PO dang chay", running is null);

var runs = await history.GetRunsAsync();
Check("Lich su co 2 dong (warmup + PO-TEST-001)", runs.Count == 2, $"{runs.Count}");
Check("Trang thai PO-TEST-001 = Completed",
    runs.Any(r => r.OrderNo == "PO-TEST-001" && r.Status == "Completed"),
    string.Join(", ", runs.Select(r => $"{r.OrderNo}={r.Status}")));

// ---------------------------------------------------------------
// [4] ScanStore: migration cot OrderNo + GetSeenCodeContentsAsync
// ---------------------------------------------------------------
Console.WriteLine("\n[4] ScanStore: migration OrderNo + truy van lich su quet");

var scansDb = Path.Combine(workDir, "scans.db");

// Tao DB cu theo schema v2 (khong co OrderNo) de kiem tra migration.
Directory.CreateDirectory(workDir);
using (var c = new SqliteConnection($"Data Source={scansDb}"))
{
    c.Open();
    using var cmd = c.CreateCommand();
    cmd.CommandText = """
        CREATE TABLE ScanRecords (
            Id               INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
            ReceivedUtc      TEXT    NOT NULL,
            Code             TEXT    NOT NULL DEFAULT '',
            Gtin             TEXT    NOT NULL DEFAULT '',
            Serial           TEXT    NOT NULL DEFAULT '',
            Status           TEXT    NOT NULL DEFAULT 'Fail',
            ProcessingTimeMs INTEGER NOT NULL DEFAULT 0,
            PlcResult        TEXT    NOT NULL DEFAULT 'Skipped',
            BatchNo          TEXT    NOT NULL DEFAULT 'NoBatch',
            Raw              TEXT    NOT NULL DEFAULT ''
        );
        """;
    cmd.ExecuteNonQuery();
}

var scanStore = new ScanStore(scansDb);
await using (var conn = await scanStore.OpenAsync())
{
    var cols = new List<string>();
    using var pragma = conn.CreateCommand();
    pragma.CommandText = "PRAGMA table_info(ScanRecords);";
    using var reader = await pragma.ExecuteReaderAsync();
    while (await reader.ReadAsync()) cols.Add(reader.GetString(1));

    Check("Migration: them duoc cot OrderNo", cols.Contains("OrderNo"), string.Join(",", cols));
    Check("Migration giữ đủ cột trạng thái gửi mã",
        new[] { "SendStatus", "SendStatusCode", "SendMessage", "SendAttempts", "SendTimeUtc", "SendAttemptUtc" }
            .All(cols.Contains), string.Join(",", cols));
    Check("Migration: ProcessingTimeMs la REAL",
        cols.Contains("ProcessingTimeMs"));

    // Ghi ban ghi Pass cua PO cu -> phai duoc GetSeenCodeContentsAsync tra ve.
    await scanStore.InsertBatchAsync(conn, new List<ScanRecordEntity>
    {
        new() { ReceivedUtc = DateTime.UtcNow, CodeContent = "OLD001",
                Status = ScanStatuses.Pass, OrderNo = "PO-OLD" },
        new() { ReceivedUtc = DateTime.UtcNow, CodeContent = "OLD002",
                Status = ScanStatuses.Pass, OrderNo = "PO-OLD" },
        new() { ReceivedUtc = DateTime.UtcNow, CodeContent = "FAIL01",
                Status = ScanStatuses.Fail, OrderNo = "PO-OLD" },
    }, ct: default);
}

var seen = await scanStore.GetSeenCodeContentsAsync("PO-NEW");
Check("GetSeenCodeContentsAsync tra ve 2 complete Pass codes", seen.Count == 2, $"{seen.Count}");
Check("Chua chua serial Fail", !seen.Contains("FAIL01"));

// Ban ghi cung PO -> bi loai (mua cua PO do hop le)
await using (var conn = await scanStore.OpenAsync())
{
    await scanStore.InsertBatchAsync(conn, new List<ScanRecordEntity>
    {
        new() { ReceivedUtc = DateTime.UtcNow, CodeContent = "/01/08934567890123/21/S001",
                Status = ScanStatuses.Pass, OrderNo = "PO-NEW" },
    }, ct: default);
}
seen = await scanStore.GetSeenCodeContentsAsync("PO-NEW");
Check("Ban ghi cung PO bi loai khoi ket qua", seen.Count == 2, $"{seen.Count}");

// ---------------------------------------------------------------
// [5] ProductionOrderService: local-first + auto-complete + halt
// ---------------------------------------------------------------
Console.WriteLine("\n[5] ProductionOrderService: local-first + auto-complete");

var poRoot = Path.Combine(workDir, "po");
Directory.CreateDirectory(poRoot);
var svcStore = new PoStore(poRoot);
var svcHistory = new PoHistoryStore(poRoot);
var svcCache = new PoCodeCache();

// Client gia lap: khong dung mang.
var fakeClient = new FakePoClient();
var svc = new ProductionOrderService(
    fakeClient, svcStore, svcHistory, svcCache,
    new FakeScanHistoryReader());

var svcOrder = new ProductionOrder
{
    OrderNo = "PO-SVC-001",
    OrderQty = 3,
    CreatedUtc = new DateTime(2026, 11, 9, 9, 0, 0, DateTimeKind.Utc),
};
var svcCodes = new List<PoCode>
{
    new() { CodeContent = "/01/G/21/A1" },
    new() { CodeContent = "/01/G/21/A2" },
    new() { CodeContent = "/01/G/21/A3" },
};
await svcStore.EnsurePoDbAsync(svcOrder, svcCodes);

// Bat dau san xuat tu DB local (khong goi mang)
var started = await svc.StartProductionAsync("PO-SVC-001");
Check("StartProductionAsync thanh cong", started);
Check("IsProductionRunning = true", svc.IsProductionRunning);
Check("Cache nap 3 ma cua PO dang chay", svcCache.ValidCount == 3, $"{svcCache.ValidCount}");
Check("TotalCount = 3", svc.TotalCount == 3, $"{svc.TotalCount}");

// Validate code
Check("Ma hop le => Valid",
    svc.ValidateCode("/01/G/21/A1") == PoCodeValidation.Valid);
Check("Ma ngoai PO => InvalidForOrder",
    svc.ValidateCode("/01/G/21/ZZZ") == PoCodeValidation.InvalidForOrder);

// Quet 2 ma -> chua du 100%, van chay
await svc.ProcessCodeAsync("/01/G/21/A1", _ => Task.FromResult(true));
await svc.ProcessCodeAsync("/01/G/21/A2", _ => Task.FromResult(true));
Check("Sau 2 ma: van dang chay", svc.IsProductionRunning);
Check("Sau 2 ma: ScannedCount = 2", svc.ScannedCount == 2, $"{svc.ScannedCount}");
Check("Ma da quet => Duplicate",
    svc.ValidateCode("/01/G/21/A1") == PoCodeValidation.Duplicate);

// Quet ma cuoi => tu dong hoan thanh
await svc.ProcessCodeAsync("/01/G/21/A3", _ => Task.FromResult(true));
Check("Quet du 100% => tu hoan tat", !svc.IsProductionRunning);
Check("CurrentOrder = null sau khi hoan tat", svc.CurrentOrder is null);
Check("Chua co PO => NoProductionOrder",
    svc.ValidateCode("/01/G/21/A1") == PoCodeValidation.NoProductionOrder);
Check("Cache Clear() sau khi hoan tat (RAM bounded)", svcCache.ValidCount == 0,
    $"{svcCache.ValidCount}");

var svcMeta = await svcStore.ListLocalOrdersAsync();
Check("Meta ghi Completed",
    svcMeta.Count == 1 && svcMeta[0].Status == ProductionOrderStatus.Completed,
    svcMeta.Count == 1 ? svcMeta[0].Status.ToString() : "?");

// ---------------------------------------------------------------
// [6] Cancel: PO bi huy, khong chay lai duoc
// ---------------------------------------------------------------
Console.WriteLine("\n[6] Cancel: PO bi huy khong chay lai duoc");

var cancelOrder = new ProductionOrder
{
    OrderNo = "PO-CANCEL-001",
    OrderQty = 2,
    CreatedUtc = new DateTime(2026, 11, 9, 10, 0, 0, DateTimeKind.Utc),
};
await svcStore.EnsurePoDbAsync(cancelOrder, new List<PoCode>
{
    new() { CodeContent = "/01/G/21/B1" },
    new() { CodeContent = "/01/G/21/B2" },
});

var cancelled = await svc.CancelProductionAsync("PO-CANCEL-001");
Check("CancelProductionAsync thanh cong", cancelled);
Check("IsCancelledAsync = true", await svcStore.IsCancelledAsync(
    "PO-CANCEL-001", cancelOrder.CreatedUtc));

var restart = await svc.StartProductionAsync("PO-CANCEL-001");
Check("PO da huy khong chay lai duoc (Start tra false)", !restart);

var localList = await svcStore.ListLocalOrdersAsync();
var cancelRow = localList.FirstOrDefault(p => p.OrderNo == "PO-CANCEL-001");
Check("PO da huy van hien trong bang local", cancelRow is not null);
Check("PO da huy co IsCancelled = true", cancelRow?.IsCancelled == true);

// ---------------------------------------------------------------
// [7] Auto-resume
// ---------------------------------------------------------------
Console.WriteLine("\n[7] Auto-resume: khoi phuc PO dang chay");

var resumeOrder = new ProductionOrder
{
    OrderNo = "PO-RESUME-001",
    OrderQty = 2,
    CreatedUtc = new DateTime(2026, 11, 9, 11, 0, 0, DateTimeKind.Utc),
};
await svcStore.EnsurePoDbAsync(resumeOrder, new List<PoCode>
{
    new() { CodeContent = "/01/G/21/C1" },
    new() { CodeContent = "/01/G/21/C2" },
});
await svcHistory.StartRunAsync("PO-RESUME-001", 2, resumeOrder.CreatedUtc);

// Service moi (gia lap mo lai app)
var svc2 = new ProductionOrderService(
    fakeClient, svcStore, svcHistory, new PoCodeCache(),
    new FakeScanHistoryReader());
var resumed = await svc2.RestoreRunningOrderAsync();
Check("RestoreRunningOrderAsync thanh cong", resumed);
Check("PO chay lai => IsProductionRunning", svc2.IsProductionRunning);
Check("Cache nap lai ma cua PO", svc2.TotalCount == 2, $"{svc2.TotalCount}");

// ---------------------------------------------------------------
// [8] Scan history from a different PO must not block local membership.
// ---------------------------------------------------------------
Console.WriteLine("\n[8] Lich su PO khac khong chan CodeContent cua PO hien tai");

var haltStore = new PoStore(Path.Combine(workDir, "po-halt"));
var haltHistory = new PoHistoryStore(Path.Combine(workDir, "po-halt"));
var haltCache = new PoCodeCache();
var haltSvc = new ProductionOrderService(
    fakeClient, haltStore, haltHistory, haltCache,
    new FakeScanHistoryReader(new[] { "/01/G/21/TRUNG01" }));

var haltOrder = new ProductionOrder
{
    OrderNo = "PO-HALT-001",
    OrderQty = 2,
    CreatedUtc = new DateTime(2026, 11, 9, 12, 0, 0, DateTimeKind.Utc),
};
await haltStore.EnsurePoDbAsync(haltOrder, new List<PoCode>
{
    new() { CodeContent = "/01/G/21/TRUNG01" },
    new() { CodeContent = "/01/G/21/OK01" },
});

await haltSvc.StartProductionAsync("PO-HALT-001");
Check("Bat dau PO de test halt", haltSvc.IsProductionRunning);

var conflicts = new List<string>();
haltSvc.HistoryConflict += (_, s) => conflicts.AddRange(s);

Check("Unscanned codes are not activated", await haltSvc.ActivatePendingAsync() == 0);
var independentPass = await haltSvc.ProcessCodeAsync("/01/G/21/TRUNG01", _ => Task.FromResult(true));
Check("Code seen in another PO remains eligible", independentPass.Committed);
var activated = await haltSvc.ActivatePendingAsync();
Check("Activation uses scanned current PO codes", activated == 1, $"{activated}");
Check("Global scan history does not halt production", !haltSvc.IsHalted);
Check("No obsolete global-history conflict event", conflicts.Count == 0);
Check("Another current PO code remains valid",
    haltSvc.ValidateCode("/01/G/21/OK01") == PoCodeValidation.Valid);
Check("Current PO is not cancelled by global history",
    !await haltStore.IsCancelledAsync("PO-HALT-001", haltOrder.CreatedUtc));
Check("Active cache stays scoped to current PO", haltCache.ValidCount == 2);

// ---------------------------------------------------------------
// [9] Local-first: doc danh sach PO tu local, khong goi mang
// ---------------------------------------------------------------
Console.WriteLine("\n[9] Local-first: doc PO tu local, khong goi mang");

fakeClient.CallCount = 0;
var localOnly = await svc.GetLocalOrdersAsync();
Check("GetLocalOrdersAsync khong goi mang", fakeClient.CallCount == 0,
    $"calls={fakeClient.CallCount}");
Check("Doc duoc danh sach PO local", localOnly.Count >= 2, $"{localOnly.Count}");
Check("Danh sach co PO da huy (van hien de tra cuu)",
    localOnly.Any(p => p.OrderNo == "PO-CANCEL-001"));
Check("Danh sach co PO hoan tat",
    localOnly.Any(p => p.OrderNo == "PO-SVC-001"));

// ---------------------------------------------------------------
// [10] PoActivationPolicy: worker chi chay khi co the goi duoc server
// ---------------------------------------------------------------
Console.WriteLine("\n[10] PoActivationPolicy: activation không phụ thuộc ingestion listener");

Check("Loopback: localhost", PoActivationPolicy.IsLoopbackBaseUrl("http://localhost:5080"));
Check("Loopback: 127.0.0.1", PoActivationPolicy.IsLoopbackBaseUrl("http://127.0.0.1:5080"));
Check("Loopback: 127.0.0.5 co path", PoActivationPolicy.IsLoopbackBaseUrl("http://127.0.0.5:5080/api"));
Check("Loopback: IPv6 [::1]", PoActivationPolicy.IsLoopbackBaseUrl("http://[::1]:5080"));
Check("Khong phai loopback: IP LAN",
    !PoActivationPolicy.IsLoopbackBaseUrl("http://192.168.1.10:5080"));
Check("Khong phai loopback: domain",
    !PoActivationPolicy.IsLoopbackBaseUrl("https://mes.congty.vn"));
Check("Khong phai loopback: rong / null / sai dinh dang",
    !PoActivationPolicy.IsLoopbackBaseUrl("")
    && !PoActivationPolicy.IsLoopbackBaseUrl(null)
    && !PoActivationPolicy.IsLoopbackBaseUrl("abc"));

var loopbackOptions = new PoOptions { BaseUrl = "http://127.0.0.1:5080", AutoActivate = true };
var lanOptions = new PoOptions { BaseUrl = "http://10.0.0.30:5080", AutoActivate = true };

Check("AutoActivate=false => khong chay",
    !PoActivationPolicy.ShouldRun(new PoOptions { AutoActivate = false }));
Check("AutoActivate=true + external localhost server => chay, ingestion listener tat",
    PoActivationPolicy.ShouldRun(loopbackOptions, poServerEnabled: false));
Check("AutoActivate=true + BaseUrl LAN => chay",
    PoActivationPolicy.ShouldRun(lanOptions, poServerEnabled: false));

// ---------------------------------------------------------------
await CodeContentLifecycleChecks.RunAsync(workDir, Check);

Console.WriteLine("\n========================================");
Console.WriteLine($"  KET QUA: {passed} pass, {failed} fail");
Console.WriteLine("========================================");

return failed == 0 ? 0 : 1;

// ===================================================================
// Cac lop gia lap
// ===================================================================

/// <summary>PoClient gia lap - khong goi mang that.</summary>
sealed class FakePoClient : IPoClient
{
    public int CallCount;

    public Task<IReadOnlyList<ProductionOrder>> GetOrdersAsync(
        bool includeCompleted = false, CancellationToken ct = default)
    {
        CallCount++;
        return Task.FromResult<IReadOnlyList<ProductionOrder>>([]);
    }

    public Task<IReadOnlyList<PoCode>> GetCodesAsync(
        string orderNo, CancellationToken ct = default)
    {
        CallCount++;
        return Task.FromResult<IReadOnlyList<PoCode>>([]);
    }

    public Task<PoActivateResult> ActivateAsync(
        string orderNo, IReadOnlyList<string> serials, CancellationToken ct = default)
    {
        CallCount++;
        return Task.FromResult(new PoActivateResult { Ok = true, Activated = serials.Count });
    }

    public Task<ProductionOrder?> CreateOrderAsync(
        string orderNo, int orderQty, CancellationToken ct = default)
    {
        CallCount++;
        return Task.FromResult<ProductionOrder?>(
            new ProductionOrder { OrderNo = orderNo, OrderQty = orderQty });
    }
}

/// <summary>Nguon lich su quet gia lap cho test.</summary>
sealed class FakeScanHistoryReader : IPoScanHistoryReader
{
    private readonly HashSet<string> _seen;

    public FakeScanHistoryReader(IEnumerable<string>? seen = null)
    {
        _seen = new HashSet<string>(seen ?? [], StringComparer.Ordinal);
    }

    public Task<IReadOnlyList<string>> GetSeenCodeContentsAsync(
        string orderNo, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<string>>(_seen.ToList());
}
