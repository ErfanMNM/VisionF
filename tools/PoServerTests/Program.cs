using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using ClassLibraryMT.Po;
using ClassLibraryMT.PoServer;
using Microsoft.Extensions.Logging;

// =====================================================================
// Kiem thu HTTP server nhan PO tu MES.
//
// - Bat listener that (System.Net.HttpListener) tren port trong.
// - POST that cac payload that / kiem tra HTTP status + noi dung phan hoi.
// - Kiem tra PO co that su duoc ghi xuong DB PO local (PoStore) hay khong.
// - Kiem tra cac truong hop loi: 400 / 401 / 409 / 413.
//
// Chay:  dotnet run --project tools/PoServerTests
// =====================================================================

var workDir = Path.Combine(Path.GetTempPath(), "poserver-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(workDir);

var failures = 0;

void Check(string name, bool ok, string detail = "")
{
    Console.WriteLine($"  [{(ok ? "OK" : "FAIL")}]   {name}{(ok || detail.Length == 0 ? "" : " -> " + detail)}");
    if (!ok) failures++;
}

// ---- Chon port trong (tranh trung) ----
var port = FindFreePort();
var prefix = $"http://127.0.0.1:{port}/";
Console.WriteLine($"\n=== PoServerTests ===");
Console.WriteLine($"Thu muc tam: {workDir}");
Console.WriteLine($"Prefix:     {prefix}\n");

// ---- Dung store + server that ----
var store = new PoStore(Path.Combine(workDir, "po"));

using var loggerFactory = LoggerFactory.Create(b => b.AddConsole().SetMinimumLevel(LogLevel.Warning));
var logger = loggerFactory.CreateLogger<PoHttpServer>();

var options = Microsoft.Extensions.Options.Options.Create(new PoServerOptions
{
    Enabled = true,
    Port = port,
    BindAddress = "127.0.0.1",
    ApiKey = string.Empty,

    // 64 MB: du cho PO 100.000 ma (test [10] dung server rieng voi gioi han
    // 1 MB de kiem tra HTTP 413).
    MaxBodyMB = 64,
    RequestTimeoutSeconds = 60,
    MaxConcurrentRequests = 4,
});

using var server = new PoHttpServer(options, store, logger);

var receivedEvents = new List<string>();
server.PoReceived += (_, poName) =>
{
    lock (receivedEvents)
    {
        receivedEvents.Add(poName);
    }
};

server.Start();
if (server.State.IsError)
{
    Console.WriteLine($"Khong khoi dong duoc server: {server.State.Status}");
    return 1;
}

Console.WriteLine($"Server: {server.State.Status}\n");

using var http = new HttpClient
{
    BaseAddress = new Uri(prefix),
    Timeout = TimeSpan.FromSeconds(30),
};

// =====================================================================
// [1] GET /api/health
// =====================================================================
Console.WriteLine("[1] GET /api/health");

var health = await http.GetAsync("/api/health");
Check("Health trả HTTP 200", health.StatusCode == System.Net.HttpStatusCode.OK, health.StatusCode.ToString());

var healthJson = await health.Content.ReadAsStringAsync();
Console.WriteLine($"      -> {healthJson}");
Check("Health có trường success=true",
    healthJson.Contains("\"success\":true", StringComparison.Ordinal));

// =====================================================================
// [2] POST /api/po voi codes (mang JSON)
// =====================================================================
Console.WriteLine("\n[2] POST /api/po voi codes (mang JSON)");

var payload = new
{
    poName = "PO-MES-001",
    orderQty = 3,
    codes = new[]
    {
        "https://digital-link.txng.vn/01/08934567890123/21/SER001",
        "https://digital-link.txng.vn/01/08934567890123/21/SER002",
        "https://digital-link.txng.vn/01/08934567890123/21/SER003",
    },
    gtin = "08934567890123",
    batchCode = "LOT-2026-01",
    productName = "Bia Sai Gon",
    customer = "Cong ty ABC",
};

var post = await http.PostAsJsonAsync("/api/po", payload);
var postBody = await post.Content.ReadAsStringAsync();

Check("POST PO moi tra HTTP 200", post.StatusCode == System.Net.HttpStatusCode.OK, postBody);
Console.WriteLine($"      -> {postBody}");
Check("Phan hoi success=true",
    postBody.Contains("\"success\":true", StringComparison.Ordinal));
Check("Phan hoi co poName",
    postBody.Contains("PO-MES-001", StringComparison.Ordinal));
Check("Phan hoi accepted = 3",
    postBody.Contains("\"accepted\":3", StringComparison.Ordinal));

// ---- PO da ghi xuong DB local chua? ----
var locals = await store.ListLocalOrdersAsync();
var stored = locals.FirstOrDefault(p => p.OrderNo == "PO-MES-001");

Check("PO da duoc ghi xuong DB PO local", stored is not null);
Check("PO co 3 ma", stored?.TotalCodes == 3, stored?.TotalCodes.ToString() ?? "null");
Check("PO co orderQty = 3", stored?.OrderQty == 3, stored?.OrderQty.ToString() ?? "null");
Check("PO co trang thai Downloaded (chua chay)",
    stored?.Status == ProductionOrderStatus.Downloaded,
    stored?.Status.ToString() ?? "null");

// ---- Truong mo rong ----
Check("Truong mo rong customer duoc luu",
    stored?.Additional.ContainsKey("customer") == true
    && stored.Additional["customer"] == "Cong ty ABC",
    stored is null ? "" : string.Join(",", stored.Additional.Select(kv => kv.Key + "=" + kv.Value)));
Check("Truong mo rong productName duoc luu",
    stored?.Additional.ContainsKey("productName") == true);
Check("Truong mo rong batchCode duoc luu",
    stored?.Additional.ContainsKey("batchCode") == true);

// ---- Event PoReceived co bat khong? ----
lock (receivedEvents)
{
    Check("Event PoReceived duoc phat", receivedEvents.Contains("PO-MES-001"),
        string.Join(",", receivedEvents));
}

// =====================================================================
// [3] POST /api/po voi csv (chuoi CSV)
// =====================================================================
Console.WriteLine("\n[3] POST /api/po voi csv (chuoi CSV)");

var csvBody = new
{
    poName = "PO-MES-CSV-001",
    orderQty = 2,
    gtin = "08934567890123",
    csv = "serial\nSER-CSV-1\nSER-CSV-2\n",
};

var csvPost = await http.PostAsJsonAsync("/api/po", csvBody);
var csvBodyText = await csvPost.Content.ReadAsStringAsync();

Check("POST PO CSV tra HTTP 200", csvPost.StatusCode == System.Net.HttpStatusCode.OK, csvBodyText);
Console.WriteLine($"      -> {csvBodyText}");
Check("Phan hoi CSV accepted = 2",
    csvBodyText.Contains("\"accepted\":2", StringComparison.Ordinal));

var csvLocals = await store.ListLocalOrdersAsync();
var csvStored = csvLocals.FirstOrDefault(p => p.OrderNo == "PO-MES-CSV-001");
Check("PO CSV da ghi xuong DB local", csvStored is not null);
Check("PO CSV co 2 ma (bo qua dong tieu de)", csvStored?.TotalCodes == 2,
    csvStored?.TotalCodes.ToString() ?? "null");

// =====================================================================
// [4] Trung PO -> 409
// =====================================================================
Console.WriteLine("\n[4] POST lai cung mot PO -> 409");

var dupPost = await http.PostAsJsonAsync("/api/po", payload);
var dupBody = await dupPost.Content.ReadAsStringAsync();

Check("POST trung PO tra HTTP 409", dupPost.StatusCode == System.Net.HttpStatusCode.Conflict,
    dupBody);
Console.WriteLine($"      -> {dupBody}");

// =====================================================================
// [5] Thieu poName -> 400
// =====================================================================
Console.WriteLine("\n[5] POST thieu poName -> 400");

var noNamePost = await http.PostAsJsonAsync("/api/po", new { orderQty = 1, codes = new[] { "A" } });
var noNameBody = await noNamePost.Content.ReadAsStringAsync();

Check("POST thieu poName tra HTTP 400",
    noNamePost.StatusCode == System.Net.HttpStatusCode.BadRequest, noNameBody);
Console.WriteLine($"      -> {noNameBody}");

// =====================================================================
// [6] orderQty <= 0 -> 400
// =====================================================================
Console.WriteLine("\n[6] POST orderQty <= 0 -> 400");

var badQtyPost = await http.PostAsJsonAsync(
    "/api/po", new { poName = "PO-BAD-QTY", orderQty = 0, codes = new[] { "A" } });
var badQtyBody = await badQtyPost.Content.ReadAsStringAsync();

Check("POST orderQty = 0 tra HTTP 400",
    badQtyPost.StatusCode == System.Net.HttpStatusCode.BadRequest, badQtyBody);

// =====================================================================
// [7] So ma it hon orderQty -> 400
// =====================================================================
Console.WriteLine("\n[7] POST so ma it hon orderQty -> 400");

var shortPost = await http.PostAsJsonAsync(
    "/api/po",
    new { poName = "PO-SHORT", orderQty = 10, codes = new[] { "A", "B" } });
var shortBody = await shortPost.Content.ReadAsStringAsync();

Check("POST thieu ma tra HTTP 400",
    shortPost.StatusCode == System.Net.HttpStatusCode.BadRequest, shortBody);
Console.WriteLine($"      -> {shortBody}");

// =====================================================================
// [8] Thieu ca codes va csv -> 400
// =====================================================================
Console.WriteLine("\n[8] POST khong co codes/csv -> 400");

var noCodesPost = await http.PostAsJsonAsync(
    "/api/po", new { poName = "PO-NO-CODES", orderQty = 5 });
var noCodesBody = await noCodesPost.Content.ReadAsStringAsync();

Check("POST khong co ma tra HTTP 400",
    noCodesPost.StatusCode == System.Net.HttpStatusCode.BadRequest, noCodesBody);

// =====================================================================
// [9] De-duplicate + trim + bo dong rong
// =====================================================================
Console.WriteLine("\n[9] De-duplicate + trim + bo dong rong");

var dedupPost = await http.PostAsJsonAsync("/api/po", new
{
    poName = "PO-DEDUP-001",
    orderQty = 2,
    gtin = "08934567890123",
    codes = new[] { "  SER-D1  ", "SER-D1", "", "   ", "SER-D2" },
});
var dedupBody = await dedupPost.Content.ReadAsStringAsync();

Check("POST de-duplicate tra HTTP 200", dedupPost.StatusCode == System.Net.HttpStatusCode.OK, dedupBody);
Check("De-duplicate con 2 ma", dedupBody.Contains("\"accepted\":2", StringComparison.Ordinal),
    dedupBody);

var dedupLocals = await store.ListLocalOrdersAsync();
var dedupStored = dedupLocals.FirstOrDefault(p => p.OrderNo == "PO-DEDUP-001");
Check("DB chi luu 2 ma sau de-duplicate", dedupStored?.TotalCodes == 2,
    dedupStored?.TotalCodes.ToString() ?? "null");

// =====================================================================
// [10] Body qua lon -> 413 (server rieng voi gioi han 1 MB)
// =====================================================================
Console.WriteLine("\n[10] Body vuot qua gioi han -> 413");

var smallOptions = Microsoft.Extensions.Options.Options.Create(new PoServerOptions
{
    Enabled = true,
    Port = FindFreePort(),
    BindAddress = "127.0.0.1",
    MaxBodyMB = 1,
});

using var smallServer = new PoHttpServer(smallOptions, store, logger);
smallServer.Start();

using var smallHttp = new HttpClient
{
    BaseAddress = new Uri($"http://127.0.0.1:{smallOptions.Value.Port}/"),
    Timeout = TimeSpan.FromSeconds(30),
};

var hugeCodes = string.Join(",", Enumerable.Range(0, 120_000).Select(i => $"\"CODE-{i}\""));
var hugeJson = $"{{\"poName\":\"PO-HUGE\",\"orderQty\":1,\"codes\":[{hugeCodes}]}}";
var hugeContent = new StringContent(hugeJson, Encoding.UTF8, "application/json");

var hugePost = await smallHttp.PostAsync("/api/po", hugeContent);
var hugeBody = await hugePost.Content.ReadAsStringAsync();

Check("POST body lon tra HTTP 413",
    hugePost.StatusCode == System.Net.HttpStatusCode.RequestEntityTooLarge,
    $"{hugePost.StatusCode} {hugeBody}");
Console.WriteLine($"      -> {hugeBody}");

// =====================================================================
// [11] API key sai -> 401
// =====================================================================
Console.WriteLine("\n[11] Sai API key -> 401");

var optionsWithKey = Microsoft.Extensions.Options.Options.Create(new PoServerOptions
{
    Enabled = true,
    Port = FindFreePort(),
    BindAddress = "127.0.0.1",
    ApiKey = "secret-key-123",
});

using var securedServer = new PoHttpServer(optionsWithKey, store, logger);
securedServer.Start();

if (securedServer.State.IsError)
{
    Check("Server co API key khoi dong duoc", false, securedServer.State.Status);
}
else
{
    Check("Server co API key khoi dong duoc", true);

    using var securedHttp = new HttpClient
    {
        BaseAddress = new Uri($"http://127.0.0.1:{optionsWithKey.Value.Port}/"),
        Timeout = TimeSpan.FromSeconds(30),
    };

    var noKeyPost = await securedHttp.PostAsJsonAsync(
        "/api/po", new { poName = "PO-NOKEY", orderQty = 1, codes = new[] { "A" } });
    Check("Khong co x-api-key tra HTTP 401",
        noKeyPost.StatusCode == System.Net.HttpStatusCode.Unauthorized,
        noKeyPost.StatusCode.ToString());

    var wrongKeyReq = new HttpRequestMessage(HttpMethod.Post, "/api/po")
    {
        Content = JsonContent.Create(new { poName = "PO-WRONGKEY", orderQty = 1, codes = new[] { "A" } }),
    };
    wrongKeyReq.Headers.Add("x-api-key", "sai-roi");
    var wrongKeyPost = await securedHttp.SendAsync(wrongKeyReq);
    Check("Sai x-api-key tra HTTP 401",
        wrongKeyPost.StatusCode == System.Net.HttpStatusCode.Unauthorized,
        wrongKeyPost.StatusCode.ToString());

    var rightKeyReq = new HttpRequestMessage(HttpMethod.Post, "/api/po")
    {
        Content = JsonContent.Create(new { poName = "PO-RIGHTKEY", orderQty = 1, codes = new[] { "A1" } }),
    };
    rightKeyReq.Headers.Add("x-api-key", "secret-key-123");
    var rightKeyPost = await securedHttp.SendAsync(rightKeyReq);
    var rightKeyBody = await rightKeyPost.Content.ReadAsStringAsync();
    Check("Dung x-api-key tra HTTP 200",
        rightKeyPost.StatusCode == System.Net.HttpStatusCode.OK, rightKeyBody);
    Console.WriteLine($"      -> {rightKeyBody}");
}

// =====================================================================
// [12] Endpoint la -> 404
// =====================================================================
Console.WriteLine("\n[12] Endpoint la -> 404");

var notFound = await http.GetAsync("/api/khong-ton-tai");
Check("GET endpoint la tra HTTP 404",
    notFound.StatusCode == System.Net.HttpStatusCode.NotFound,
    notFound.StatusCode.ToString());

// =====================================================================
// [13] Upload file CSV (text/csv + query string)
// =====================================================================
Console.WriteLine("\n[13] Upload file CSV (text/csv)");

var csvFileContent = new StringContent(
    "SER-UPL-1\nSER-UPL-2\nSER-UPL-3\n", Encoding.UTF8, "text/csv");
var uploadPost = await http.PostAsync(
    "/api/po?poName=PO-UPLOAD-001&orderQty=3&gtin=08934567890123",
    csvFileContent);
var uploadBody = await uploadPost.Content.ReadAsStringAsync();

Check("Upload CSV tra HTTP 200", uploadPost.StatusCode == System.Net.HttpStatusCode.OK, uploadBody);
Console.WriteLine($"      -> {uploadBody}");

var uploadLocals = await store.ListLocalOrdersAsync();
var uploadStored = uploadLocals.FirstOrDefault(p => p.OrderNo == "PO-UPLOAD-001");
Check("PO upload CSV co 3 ma", uploadStored?.TotalCodes == 3,
    uploadStored?.TotalCodes.ToString() ?? "null");

// =====================================================================
// [14] Upload multipart/form-data
// =====================================================================
Console.WriteLine("\n[14] Upload multipart/form-data");

var multipart = new MultipartFormDataContent();
multipart.Add(new StringContent("PO-MULTI-001"), "poName");
multipart.Add(new StringContent("2"), "orderQty");
multipart.Add(new StringContent("08934567890123"), "gtin");
multipart.Add(
    new StringContent("SER-M1\nSER-M2\n", Encoding.UTF8) { Headers = { ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/csv") } },
    "file",
    "codes.csv");

var multiPost = await http.PostAsync("/api/po", multipart);
var multiBody = await multiPost.Content.ReadAsStringAsync();

Check("Upload multipart tra HTTP 200", multiPost.StatusCode == System.Net.HttpStatusCode.OK, multiBody);
Console.WriteLine($"      -> {multiBody}");

var multiLocals = await store.ListLocalOrdersAsync();
var multiStored = multiLocals.FirstOrDefault(p => p.OrderNo == "PO-MULTI-001");
Check("PO multipart co 2 ma", multiStored?.TotalCodes == 2,
    multiStored?.TotalCodes.ToString() ?? "null");

// =====================================================================
// [15] PO da huỷ khong bi chay lai + khong bi ghi de trang thai
// =====================================================================
Console.WriteLine("\n[15] PO da huy giu nguyen trang thai khi MES gui lai");

var cancelOrder = new ProductionOrder
{
    OrderNo = "PO-CANCEL-001",
    OrderQty = 1,
    CreatedUtc = new DateTime(2026, 11, 18, 8, 0, 0, DateTimeKind.Utc),
};
await store.EnsurePoDbAsync(
    cancelOrder,
    new List<PoCode> { new() { Code = "C1", Gtin = "G", Serial = "C1" } });
await store.MarkCancelledAsync(cancelOrder.OrderNo, cancelOrder.CreatedUtc);

// MES gui lai PO da huy -> 409 (khong ghi de trang thai Cancelled).
var rePushPost = await http.PostAsJsonAsync("/api/po", new
{
    poName = "PO-CANCEL-001",
    orderQty = 1,
    codes = new[] { "C1", "C2" },
});
var rePushBody = await rePushPost.Content.ReadAsStringAsync();

Check("POST lai PO da huy tra HTTP 409",
    rePushPost.StatusCode == System.Net.HttpStatusCode.Conflict, rePushBody);

var afterRePush = (await store.ListLocalOrdersAsync())
    .First(p => p.OrderNo == "PO-CANCEL-001");
Check("PO da huy van giu trang thai Cancelled",
    afterRePush.Status == ProductionOrderStatus.Cancelled,
    afterRePush.Status.ToString());
Check("PO da huy khong bi them ma moi", afterRePush.TotalCodes == 1,
    afterRePush.TotalCodes.ToString());

// =====================================================================
// [16] Nhieu request dong thoi (app khong treo)
// =====================================================================
Console.WriteLine("\n[16] Nhieu request dong thoi");

var sw = System.Diagnostics.Stopwatch.StartNew();
var tasks = Enumerable.Range(0, 20).Select(async i =>
{
    var req = new HttpRequestMessage(HttpMethod.Post, "/api/po")
    {
        Content = JsonContent.Create(new
        {
            poName = $"PO-CONC-{i:D3}",
            orderQty = 1,
            gtin = "08934567890123",
            codes = new[] { $"SER-C{i:D3}" },
        }),
    };
    var resp = await http.SendAsync(req);
    return resp.StatusCode;
});
var concResults = await Task.WhenAll(tasks);
sw.Stop();

Check("20 request dong thoi deu HTTP 200",
    concResults.All(c => c == System.Net.HttpStatusCode.OK),
    string.Join(",", concResults.Select(c => (int)c)));
Console.WriteLine($"      Thoi gian: {sw.ElapsedMilliseconds} ms");

var concLocals = await store.ListLocalOrdersAsync();
Check("Da luu du 20 PO tu request dong thoi",
    concLocals.Count(p => p.OrderNo.StartsWith("PO-CONC-", StringComparison.Ordinal)) == 20,
    concLocals.Count(p => p.OrderNo.StartsWith("PO-CONC-", StringComparison.Ordinal)).ToString());

// =====================================================================
// [17] PO lon (100k ma) -> giu duoc, app khong treo
// =====================================================================
Console.WriteLine("\n[17] PO lon (100.000 ma)");

var bigCodes = Enumerable.Range(0, 100_000)
    .Select(i => $"https://digital-link.txng.vn/01/08934567890123/21/BIG{i:D6}")
    .ToArray();

var bigSw = System.Diagnostics.Stopwatch.StartNew();
var bigPost = await http.PostAsJsonAsync("/api/po", new
{
    poName = "PO-BIG-001",
    orderQty = 100_000,
    codes = bigCodes,
});
bigSw.Stop();
var bigBody = await bigPost.Content.ReadAsStringAsync();

Check("POST 100.000 ma tra HTTP 200", bigPost.StatusCode == System.Net.HttpStatusCode.OK, bigBody);
Console.WriteLine($"      Thoi gian: {bigSw.ElapsedMilliseconds} ms -> {bigBody}");

var bigLocals = await store.ListLocalOrdersAsync();
var bigStored = bigLocals.FirstOrDefault(p => p.OrderNo == "PO-BIG-001");
Check("PO lon luu du 100.000 ma", bigStored?.TotalCodes == 100_000,
    bigStored?.TotalCodes.ToString() ?? "null");

// ---- Server van con song sau khi nhan payload lon ----
var healthAfter = await http.GetAsync("/api/health");
Check("Server van phan hoi sau khi nhan PO lon",
    healthAfter.StatusCode == System.Net.HttpStatusCode.OK,
    healthAfter.StatusCode.ToString());

// =====================================================================
// [18] Trùng PO ở THÁNG KHÁC (logs/po/yyyy-MM) -> phải bị từ chối 409
// =====================================================================
Console.WriteLine("\n[18] Trùng PO ở tháng khác -> 409");

// Tạo thủ công một file DB PO nằm ở tháng KHÁC (không phải tháng hiện tại)
// để mô phỏng PO đã tồn tại từ lâu / tải từ PO server vào tháng trước.
var otherMonth = DateTime.UtcNow.AddMonths(-3);
var otherMonthDir = Path.Combine(workDir, "po", otherMonth.ToString("yyyy-MM"));
Directory.CreateDirectory(otherMonthDir);
var oldPoFile = Path.Combine(otherMonthDir, "PO-OLD-MONTH-001.db");
await using (var oldConn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={oldPoFile}"))
{
    await oldConn.OpenAsync();
    await using var cmd = oldConn.CreateCommand();
    cmd.CommandText = """
        CREATE TABLE IF NOT EXISTS PoCodes (
            Code        TEXT NOT NULL PRIMARY KEY,
            Gtin        TEXT NOT NULL DEFAULT '',
            Serial      TEXT NOT NULL DEFAULT '',
            ScannedUtc  TEXT NULL,
            Status      TEXT NOT NULL DEFAULT 'Pending',
            ActivatedUtc TEXT NULL
        );
        CREATE TABLE IF NOT EXISTS PoMeta (
            Key   TEXT NOT NULL PRIMARY KEY,
            Value TEXT NOT NULL
        );
        INSERT OR IGNORE INTO PoMeta (Key, Value) VALUES ('orderNo', 'PO-OLD-MONTH-001');
        INSERT OR IGNORE INTO PoMeta (Key, Value) VALUES ('orderQty', '1');
        INSERT OR IGNORE INTO PoCodes (Code, Gtin, Serial, ScannedUtc, Status, ActivatedUtc)
            VALUES ('https://digital-link.txng.vn/01/08934567890123/21/OLDSER001',
                    '08934567890123', 'OLDSER001', NULL, 'Pending', NULL);
        """;
    await cmd.ExecuteNonQueryAsync();
}

Check("File DB PO tháng khac da duoc tao",
    File.Exists(oldPoFile), oldPoFile);

var oldMonthPost = await http.PostAsJsonAsync("/api/po", new
{
    poName = "PO-OLD-MONTH-001",
    orderQty = 1,
    codes = new[] { "https://digital-link.txng.vn/01/08934567890123/21/OLDSER002" },
});
var oldMonthBody = await oldMonthPost.Content.ReadAsStringAsync();

Check("POST trung PO o thang khac tra HTTP 409",
    oldMonthPost.StatusCode == System.Net.HttpStatusCode.Conflict, oldMonthBody);
Console.WriteLine($"      -> {oldMonthBody}");

// PO cũ KHÔNG bị ghi đè / thêm mã mới
var oldMonthInfo = (await store.ListLocalOrdersAsync())
    .FirstOrDefault(p => p.OrderNo == "PO-OLD-MONTH-001");
Check("PO thang khac van giu nguyen so ma cu",
    oldMonthInfo?.TotalCodes == 1, oldMonthInfo?.TotalCodes.ToString() ?? "null");

// =====================================================================
// [19] Mã trùng với PO KHÁC -> phải bị từ chối 409
// =====================================================================
Console.WriteLine("\n[19] Ma trung voi PO khac -> 409");

// PO mới dùng lại mã đã có trong PO-MES-001 (SER001) nhưng khác tên PO.
var dupCodePost = await http.PostAsJsonAsync("/api/po", new
{
    poName = "PO-DUP-CODE-001",
    orderQty = 2,
    codes = new[]
    {
        // Trùng với PO-MES-001 (SER001) — gửi dạng Digital Link.
        "https://digital-link.txng.vn/01/08934567890123/21/SER001",
        // Mã mới, không trùng.
        "https://digital-link.txng.vn/01/08934567890123/21/NEWSER999",
    },
});
var dupCodeBody = await dupCodePost.Content.ReadAsStringAsync();

Check("POST co ma trung voi PO khac tra HTTP 409",
    dupCodePost.StatusCode == System.Net.HttpStatusCode.Conflict, dupCodeBody);
Console.WriteLine($"      -> {dupCodeBody}");

Check("Thong bao loi la tieng Viet",
    MsgOf(dupCodeBody).Contains("trùng", StringComparison.OrdinalIgnoreCase), dupCodeBody);
Check("Thong bao loi nen so ma trung",
    dupCodeBody.Contains("1", StringComparison.Ordinal), dupCodeBody);
Check("Thong bao loi nen ten PO chua ma trung",
    dupCodeBody.Contains("PO-MES-001", StringComparison.Ordinal), dupCodeBody);
Check("Thong bao loi nen co ma vi du",
    dupCodeBody.Contains("SER001", StringComparison.Ordinal), dupCodeBody);
Check("Thong bao loi nen so ma trung chinh xac",
    MsgOf(dupCodeBody).Contains("1 mã", StringComparison.Ordinal), dupCodeBody);

// PO bị từ chối -> KHÔNG được tạo trong DB local
var dupLocals = await store.ListLocalOrdersAsync();
Check("PO bi tu choi khong duoc tao trong DB",
    !dupLocals.Any(p => p.OrderNo == "PO-DUP-CODE-001"),
    dupLocals.Count(p => p.OrderNo == "PO-DUP-CODE-001").ToString());

// =====================================================================
// [20] Mã trùng ở DẠNG KHÁC (mã trần vs Digital Link) vẫn bị phát hiện
// =====================================================================
Console.WriteLine("\n[20] Ma trung o dang khac (ma tran vs Digital Link) -> 409");

// PO-MES-001 chứa SER002 (dạng Digital Link). Gửi lại SER002 dạng mã trần
// cùng GTIN -> phải bị coi là trùng (cùng cách chuẩn hoá khi quét).
var dupFormatPost = await http.PostAsJsonAsync("/api/po", new
{
    poName = "PO-DUP-FORMAT-001",
    orderQty = 1,
    gtin = "08934567890123",
    codes = new[] { "SER002" },
});
var dupFormatBody = await dupFormatPost.Content.ReadAsStringAsync();

Check("Ma trung o dang khac van tra HTTP 409",
    dupFormatPost.StatusCode == System.Net.HttpStatusCode.Conflict, dupFormatBody);
Console.WriteLine($"      -> {dupFormatBody}");

// =====================================================================
// [21] Mã trùng với PO ở THÁNG KHÁC -> phải bị từ chối 409
// =====================================================================
Console.WriteLine("\n[21] Ma trung voi PO o thang khac -> 409");

var dupOldMonthPost = await http.PostAsJsonAsync("/api/po", new
{
    poName = "PO-DUP-OLD-MONTH-001",
    orderQty = 1,
    codes = new[] { "https://digital-link.txng.vn/01/08934567890123/21/OLDSER001" },
});
var dupOldMonthBody = await dupOldMonthPost.Content.ReadAsStringAsync();

Check("Ma trung voi PO thang khac tra HTTP 409",
    dupOldMonthPost.StatusCode == System.Net.HttpStatusCode.Conflict, dupOldMonthBody);
Console.WriteLine($"      -> {dupOldMonthBody}");
Check("Thong bao nen ten PO thang khac chua ma trung",
    dupOldMonthBody.Contains("PO-OLD-MONTH-001", StringComparison.Ordinal), dupOldMonthBody);

// =====================================================================
// [22] PO không trùng mã nào -> vẫn nhận bình thường (200)
// =====================================================================
Console.WriteLine("\n[22] PO khong trung ma -> van nhan binh thuong");

var noDupPost = await http.PostAsJsonAsync("/api/po", new
{
    poName = "PO-NODUP-001",
    orderQty = 2,
    codes = new[]
    {
        "https://digital-link.txng.vn/01/08934567890123/21/FRESH001",
        "https://digital-link.txng.vn/01/08934567890123/21/FRESH002",
    },
});
var noDupBody = await noDupPost.Content.ReadAsStringAsync();

Check("POST khong trung ma tra HTTP 200",
    noDupPost.StatusCode == System.Net.HttpStatusCode.OK, noDupBody);
Console.WriteLine($"      -> {noDupBody}");

// =====================================================================
// [23] PO lớn (100.000 mã) vẫn nhận nhanh khi có kiểm tra mã trùng
// =====================================================================
Console.WriteLine("\n[23] PO lon 100.000 ma + kiem tra ma trung (van nhan duoc)");

var bigCodes2 = Enumerable.Range(0, 100_000)
    .Select(i => $"https://digital-link.txng.vn/01/08934567890123/21/BIG2{i:D6}")
    .ToArray();

var bigSw2 = System.Diagnostics.Stopwatch.StartNew();
var bigPost2 = await http.PostAsJsonAsync("/api/po", new
{
    poName = "PO-BIG-002",
    orderQty = 100_000,
    codes = bigCodes2,
});
bigSw2.Stop();
var bigBody2 = await bigPost2.Content.ReadAsStringAsync();

Check("POST 100.000 ma (co kiem tra trung) tra HTTP 200",
    bigPost2.StatusCode == System.Net.HttpStatusCode.OK, bigBody2);
Console.WriteLine($"      Thoi gian: {bigSw2.ElapsedMilliseconds} ms -> {bigBody2}");

var bigLocals2 = await store.ListLocalOrdersAsync();
var bigStored2 = bigLocals2.FirstOrDefault(p => p.OrderNo == "PO-BIG-002");
Check("PO lon 2 luu du 100.000 ma", bigStored2?.TotalCodes == 100_000,
    bigStored2?.TotalCodes.ToString() ?? "null");

// PO lớn có mã trùng với PO lớn trước -> phải 409 (kiểm tra hiệu năng khi
// đã có sẵn 100k mã trong DB).
var bigDupSw = System.Diagnostics.Stopwatch.StartNew();
var bigDupPost = await http.PostAsJsonAsync("/api/po", new
{
    poName = "PO-BIG-003",
    orderQty = 100_000,
    codes = bigCodes2,
});
bigDupSw.Stop();
var bigDupBody = await bigDupPost.Content.ReadAsStringAsync();

Check("POST 100.000 ma trung voi PO lon truoc tra HTTP 409",
    bigDupPost.StatusCode == System.Net.HttpStatusCode.Conflict, bigDupBody);
Check("So ma trung cua PO lon dung bang 100.000",
    MsgOf(bigDupBody).Contains("100000 mã", StringComparison.Ordinal), bigDupBody);
Console.WriteLine($"      Thoi gian: {bigDupSw.ElapsedMilliseconds} ms -> {bigDupBody}");

// =====================================================================
// [24] Tong ket danh sach PO trong DB local
// =====================================================================
Console.WriteLine("\n[24] Tong ket danh sach PO trong DB local");

foreach (var p in locals2(store).OrderBy(p => p.OrderNo))
{
    Console.WriteLine($"      {p.OrderNo,-20} qty={p.OrderQty,-8} codes={p.TotalCodes,-8} {p.Status}");
}

Console.WriteLine($"\nKet qua: {(failures == 0 ? "TAT CA PASS" : failures + " FAIL")}");
return failures == 0 ? 0 : 1;

// ---------------------------------------------------------------------
// Helper
// ---------------------------------------------------------------------
static IReadOnlyList<LocalPoInfo> locals2(PoStore store) => store.ListLocalOrdersAsync().GetAwaiter().GetResult();

/// <summary>
/// Lấy nội dung trường "message" từ response JSON (giải mã escape tiếng Việt).
/// </summary>
static string MsgOf(string json)
{
    try
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.TryGetProperty("message", out var el)
            ? el.GetString() ?? string.Empty
            : string.Empty;
    }
    catch (Exception)
    {
        return string.Empty;
    }
}

static int FindFreePort()
{
    var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
    listener.Start();
    var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
    listener.Stop();
    return port;
}
