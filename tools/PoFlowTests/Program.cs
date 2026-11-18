using ClassLibraryMT.Po;

// Kiem thu rieng luong PO (khong dung Scans de tranh xung dot voi
// cac thay doi dang lam song song tren ScanStore).
var workDir = Path.Combine(Path.GetTempPath(), "poflow-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(workDir);

var failures = 0;

void Check(string name, bool ok, string detail = "")
{
    Console.WriteLine($"  [{(ok ? "OK" : "FAIL")}]   {name}{(ok || detail.Length == 0 ? "" : " -> " + detail)}");
    if (!ok) failures++;
}

var poRoot = Path.Combine(workDir, "po");
var store = new PoStore(poRoot);
var history = new PoHistoryStore(poRoot);
var cache = new PoCodeCache();
var client = new FakeClient();
var svc = new ProductionOrderService(client, store, history, cache);

// ---------------------------------------------------------------------
// [1] Sync tu server KHONG duoc ghi de trang thai local (Cancelled/Completed)
// ---------------------------------------------------------------------
Console.WriteLine("\n[1] Sync tu server giu nguyen trang thai local");

var order = new ProductionOrder
{
    OrderNo = "PO-KEEP-001",
    OrderQty = 2,
    CreatedUtc = new DateTime(2026, 11, 10, 8, 0, 0, DateTimeKind.Utc),
};
var codes = new List<PoCode>
{
    new() { Code = "/01/G/21/A1", Gtin = "G", Serial = "A1" },
    new() { Code = "/01/G/21/A2", Gtin = "G", Serial = "A2" },
};

await store.EnsurePoDbAsync(order, codes);
await store.MarkCancelledAsync(order.OrderNo, order.CreatedUtc);

// Sync lai tu server (mo phong bam nut "Tai PO tu server").
await store.EnsurePoDbAsync(order, codes);

var afterSync = (await store.ListLocalOrdersAsync()).First(p => p.OrderNo == "PO-KEEP-001");
Check("PO da huy van giu trang thai Cancelled sau khi sync lai",
    afterSync.Status == ProductionOrderStatus.Cancelled,
    afterSync.Status.ToString());

// ---------------------------------------------------------------------
// [2] PO da huy / da hoan thanh KHONG chay lai duoc
// ---------------------------------------------------------------------
Console.WriteLine("\n[2] PO ket thuc khong chay lai duoc");

var startCancelled = await svc.StartProductionAsync("PO-KEEP-001");
Check("PO da huy: StartProductionAsync tra false", !startCancelled);

var doneOrder = new ProductionOrder
{
    OrderNo = "PO-DONE-001",
    OrderQty = 1,
    CreatedUtc = new DateTime(2026, 11, 10, 9, 0, 0, DateTimeKind.Utc),
};
await store.EnsurePoDbAsync(doneOrder, new List<PoCode>
{
    new() { Code = "/01/G/21/D1", Gtin = "G", Serial = "D1" },
});

// Chay het 1 ma -> tu dong hoan thanh.
Check("Start PO moi thanh cong", await svc.StartProductionAsync("PO-DONE-001"));
await svc.MarkCodeScannedAsync("G", "D1");
Check("PO da hoan thanh khong con chay", !svc.IsProductionRunning);
Check("LastFinishedOrder ghi nho PO vua hoan thanh",
    svc.LastFinishedOrder?.OrderNo == "PO-DONE-001");
Check("LastFinishedStatus = Completed",
    svc.LastFinishedStatus == ProductionOrderStatus.Completed,
    svc.LastFinishedStatus.ToString());

var startDone = await svc.StartProductionAsync("PO-DONE-001");
Check("PO da hoan thanh: StartProductionAsync tra false", !startDone);

// ---------------------------------------------------------------------
// [3] PO co the chay lai: trang thai Downloaded
// ---------------------------------------------------------------------
Console.WriteLine("\n[3] PO chua chay van chay duoc");

var freshOrder = new ProductionOrder
{
    OrderNo = "PO-FRESH-001",
    OrderQty = 1,
    CreatedUtc = new DateTime(2026, 11, 10, 10, 0, 0, DateTimeKind.Utc),
};
await store.EnsurePoDbAsync(freshOrder, new List<PoCode>
{
    new() { Code = "/01/G/21/F1", Gtin = "G", Serial = "F1" },
});
Check("PO chua chay: StartProductionAsync thanh cong",
    await svc.StartProductionAsync("PO-FRESH-001"));

// ---------------------------------------------------------------------
// [4] Danh sach local + truong mo rong (phuc vu tim kiem)
// ---------------------------------------------------------------------
Console.WriteLine("\n[4] Danh sach local va truong mo rong");

var extraOrder = new ProductionOrder
{
    OrderNo = "PO-EXTRA-001",
    OrderQty = 1,
    CreatedUtc = new DateTime(2026, 11, 10, 11, 0, 0, DateTimeKind.Utc),
    Additional = new Dictionary<string, string>
    {
        ["product"] = "Bia Sài Gòn",
        ["customer"] = "Công ty ABC",
    },
};
await store.EnsurePoDbAsync(extraOrder, new List<PoCode>
{
    new() { Code = "/01/G/21/E1", Gtin = "G", Serial = "E1" },
});

var locals = await store.ListLocalOrdersAsync();
var extraRow = locals.First(p => p.OrderNo == "PO-EXTRA-001");
Check("Truong mo rong duoc luu vao DB local",
    extraRow.Additional.TryGetValue("customer", out var customer)
        && customer == "Công ty ABC",
    string.Join(",", extraRow.Additional.Select(kv => kv.Key + "=" + kv.Value)));

var summaries = await svc.GetLocalOrdersAsync();
var extraSummary = summaries.First(p => p.OrderNo == "PO-EXTRA-001");
Check("Summary mang theo truong mo rong",
    extraSummary.Order.Additional.ContainsKey("product"),
    string.Join(",", extraSummary.Order.Additional.Keys));

// PO da huy van xuat hien trong danh sach (de tra cuu).
Check("PO da huy van xuat hien trong danh sach local",
    locals.Any(p => p.OrderNo == "PO-KEEP-001" && p.IsCancelled));

// Moc thoi gian that cua PO da hoan thanh.
var doneSummary = summaries.First(p => p.OrderNo == "PO-DONE-001");
Check("PO da hoan thanh co moc CompletedUtc",
    doneSummary.CompletedUtc is not null,
    doneSummary.CompletedUtc?.ToString("O") ?? "null");

Console.WriteLine("\n[5] Tải PO từ server cũng lọc trùng, giữ PO đã kết thúc và chặn khi đang chạy");
await svc.CancelProductionAsync();
client.Orders =
[
    new() { OrderNo = "PO-SYNC-NEW", OrderQty = 3, CreatedUtc = DateTime.UtcNow },
    new() { OrderNo = "PO-KEEP-001", OrderQty = 3, CreatedUtc = DateTime.UtcNow },
    new() { OrderNo = "PO-DONE-001", OrderQty = 3, CreatedUtc = DateTime.UtcNow },
];
client.Codes =
[
    new() { Code = "/01/G/21/A1", Gtin = "G", Serial = "A1" },
    new() { Code = "/01/G/21/SYNC-1", Gtin = "G", Serial = "SYNC-1" },
    new() { Code = "/01/G/21/SYNC-1", Gtin = "G", Serial = "SYNC-1" },
];
var syncFirst = await svc.SyncFromServerAsync();
Check("Tải lần đầu chỉ tạo một PO và thêm một mã mới",
    syncFirst.NewPoCount == 1 && syncFirst.NewCodeCount == 1);
var syncAgain = await svc.SyncFromServerAsync();
Check("Tải lại không thêm mã hoặc PO", syncAgain.NewPoCount == 0 && syncAgain.NewCodeCount == 0);
var syncedOrders = await store.ListLocalOrdersAsync();
Check("PO đã huỷ không được bổ sung", syncedOrders.Single(p => p.OrderNo == "PO-KEEP-001").TotalCodes == 2);
Check("PO hoàn tất không được bổ sung", syncedOrders.Single(p => p.OrderNo == "PO-DONE-001").TotalCodes == 1);
Check("Bắt đầu PO vừa tải", await svc.StartProductionAsync("PO-SYNC-NEW"));
var callsBefore = client.GetOrdersCalls;
try
{
    await svc.SyncFromServerAsync();
    Check("Đang sản xuất phải chặn tải PO", false);
}
catch (PoServerException)
{
    Check("Đang sản xuất bị chặn trước khi gọi mạng", client.GetOrdersCalls == callsBefore);
}
await svc.CancelProductionAsync();

Console.WriteLine($"\nKet qua: {(failures == 0 ? "TAT CA PASS" : failures + " FAIL")}");
return failures == 0 ? 0 : 1;

// Client PO gia lap: khong goi mang.
file sealed class FakeClient : IPoClient
{
    public IReadOnlyList<ProductionOrder> Orders { get; set; } = [];
    public IReadOnlyList<PoCode> Codes { get; set; } = [];
    public int GetOrdersCalls { get; private set; }

    public Task<IReadOnlyList<ProductionOrder>> GetOrdersAsync(
        bool includeCompleted = false, CancellationToken ct = default)
    {
        GetOrdersCalls++;
        return Task.FromResult(Orders);
    }

    public Task<IReadOnlyList<PoCode>> GetCodesAsync(
        string orderNo, CancellationToken ct = default) =>
        Task.FromResult(Codes);

    public Task<PoActivateResult> ActivateAsync(
        string orderNo, IReadOnlyList<string> serials, CancellationToken ct = default) =>
        Task.FromResult(new PoActivateResult { Ok = true, Activated = serials.Count });

    public Task<ProductionOrder?> CreateOrderAsync(
        string orderNo, int orderQty, CancellationToken ct = default) =>
        Task.FromResult<ProductionOrder?>(null);
}
