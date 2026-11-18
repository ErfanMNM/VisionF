using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using ClassLibraryMT.Po;
using ClassLibraryMT.PoServer;
using ClassLibraryMT.MesSimulator;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

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
var history = new PoHistoryStore(Path.Combine(workDir, "po"));

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
    MaxFileMB = 64,
    UploadTempDirectory = workDir,
    RequestTimeoutSeconds = 60,
    MaxConcurrentRequests = 4,
});

using var server = new PoHttpServer(options, store, logger, history);

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
// [4] Upload lại cùng PO: bỏ qua toàn bộ mã đã có
// =====================================================================
Console.WriteLine("\n[4] Upload lại cùng PO không tạo mã trùng");

var dupPost = await http.PostAsJsonAsync("/api/po", payload);
var dupBody = await dupPost.Content.ReadAsStringAsync();

Check("Upload lại cùng PO trả HTTP 200", dupPost.IsSuccessStatusCode, dupBody);
Check("Upload lại bỏ qua 3 mã, không nhận mã mới",
    ResultOf(dupBody).Accepted == 0 && ResultOf(dupBody).IgnoredDuplicates == 3, dupBody);
Check("Upload lại giữ nguyên số mã",
    (await store.ListLocalOrdersAsync()).Single(p => p.OrderNo == "PO-MES-001").TotalCodes == 3);
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
// [14b] MES simulator client upload PO qua multipart
// =====================================================================
Console.WriteLine("\n[14b] MES simulator upload PO qua multipart/form-data");

var simulatorOptions = Options.Create(new MesSimulatorOptions
{
    Enabled = true,
    TargetUrl = prefix.TrimEnd('/'),
    OrderQty = 2,
    Gtin = "08934567890123",
    PoPrefix = "MES-TEST",
});
var simulatorLogger = loggerFactory.CreateLogger<MesSimulatorClient>();
var simulatorClient = new MesSimulatorClient(http, simulatorOptions, simulatorLogger);
var simulatorResult = await simulatorClient.UploadTestOrderAsync(
    "PO-SIMULATOR-001",
    ["MES-SIM-001", "MES-SIM-002"]);
var simulatorOrder = (await store.ListLocalOrdersAsync())
    .FirstOrDefault(p => p.OrderNo == "PO-SIMULATOR-001");

Check("Simulator POST multipart tra HTTP 200", simulatorResult.Success, simulatorResult.ResponseBody);
Check("Simulator PO duoc luu vao DB local", simulatorOrder is not null);
Check("Simulator PO co du ma", simulatorOrder?.TotalCodes == 2, simulatorOrder?.TotalCodes.ToString() ?? "null");

var customCsvPath = Path.Combine(workDir, "custom-po.csv");
await File.WriteAllTextAsync(customCsvPath, "serial\nCUSTOM-001\nCUSTOM-002\nCUSTOM-003\n");
var customFileResult = await simulatorClient.UploadExistingFileAsync(
    "PO-CUSTOM-FILE-001", 3, customCsvPath, "08934567890123");
var customOrder = (await store.ListLocalOrdersAsync())
    .FirstOrDefault(p => p.OrderNo == "PO-CUSTOM-FILE-001");
Check("Upload file custom CSV tra HTTP 200", customFileResult.Success, customFileResult.ResponseBody);
Check("PO tu file custom co du ma", customOrder?.TotalCodes == 3, customOrder?.TotalCodes.ToString() ?? "null");

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
// [18] Bổ sung vào PO tháng cũ, kể cả DB thiếu metadata CreatedUtc
// =====================================================================
Console.WriteLine("\n[18] Bổ sung mã vào đúng DB PO tháng cũ");

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

Check("Bổ sung PO tháng cũ trả HTTP 200",
    oldMonthPost.IsSuccessStatusCode && ResultOf(oldMonthBody).Accepted == 1, oldMonthBody);
Console.WriteLine($"      -> {oldMonthBody}");

// Giữ DB gốc, thêm mã mới và không tạo DB khác tháng.
var oldMonthInfo = (await store.ListLocalOrdersAsync())
    .FirstOrDefault(p => p.OrderNo == "PO-OLD-MONTH-001");
Check("PO tháng cũ nhận thêm mã và giữ mã cũ",
    oldMonthInfo?.TotalCodes == 2, oldMonthInfo?.TotalCodes.ToString() ?? "null");
Check("Bổ sung đúng đường dẫn DB cũ", oldMonthInfo?.DbPath == oldPoFile, oldMonthInfo?.DbPath ?? "null");
Check("Chỉ tồn tại một DB cho PO tháng cũ",
    (await store.ListLocalOrdersAsync()).Count(p => p.OrderNo == "PO-OLD-MONTH-001") == 1);

// =====================================================================
// [19] Mã trùng với PO khác được lọc, phần mã mới được nhận
// =====================================================================
Console.WriteLine("\n[19] Lọc mã trùng và nhận phần mã mới");

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

Check("Lô có mã trùng và mã mới trả HTTP 200", dupCodePost.IsSuccessStatusCode, dupCodeBody);
Check("Báo đúng một mã nhận, một mã trùng",
    ResultOf(dupCodeBody).Accepted == 1 && ResultOf(dupCodeBody).IgnoredDuplicates == 1, dupCodeBody);
var dupLocals = await store.ListLocalOrdersAsync();
var partialOrder = dupLocals.Single(p => p.OrderNo == "PO-DUP-CODE-001");
var partialCodes = await store.GetCodesAsync(partialOrder.OrderNo, partialOrder.CreatedUtc);
Check("PO mới chỉ chứa mã mới", partialCodes.Count == 1 && partialCodes[0].Serial == "NEWSER999");
Check("Số lượng PO phản ánh mã thực nhận", partialOrder.OrderQty == 1);

// =====================================================================
// [20] Mã trùng ở DẠNG KHÁC (mã trần vs Digital Link) vẫn bị phát hiện
// =====================================================================
Console.WriteLine("\n[20] Mã trần và Digital Link được so theo GTIN|Serial");

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

Check("Mã trùng khác định dạng được bỏ qua",
    dupFormatPost.IsSuccessStatusCode && ResultOf(dupFormatBody).Accepted == 0
    && ResultOf(dupFormatBody).IgnoredDuplicates == 1, dupFormatBody);
Check("Không tạo PO rỗng khi toàn bộ mã đã có",
    store.FindExistingDbPath("PO-DUP-FORMAT-001") is null);
Console.WriteLine($"      -> {dupFormatBody}");

// =====================================================================
// [21] Mã trùng với PO tháng cũ vẫn bị lọc
// =====================================================================
Console.WriteLine("\n[21] Lọc mã trùng ở tháng cũ");

var dupOldMonthPost = await http.PostAsJsonAsync("/api/po", new
{
    poName = "PO-DUP-OLD-MONTH-001",
    orderQty = 1,
    codes = new[] { "https://digital-link.txng.vn/01/08934567890123/21/OLDSER001" },
});
var dupOldMonthBody = await dupOldMonthPost.Content.ReadAsStringAsync();

Check("Bỏ qua mã đã có ở tháng cũ",
    dupOldMonthPost.IsSuccessStatusCode && ResultOf(dupOldMonthBody).Accepted == 0, dupOldMonthBody);
Console.WriteLine($"      -> {dupOldMonthBody}");
Check("Báo đúng số mã trùng ở tháng cũ",
    ResultOf(dupOldMonthBody).IgnoredDuplicates == 1, dupOldMonthBody);

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

// Lọc 100.000 mã đã có và không tạo PO rỗng.
var bigDupSw = System.Diagnostics.Stopwatch.StartNew();
var bigDupPost = await http.PostAsJsonAsync("/api/po", new
{
    poName = "PO-BIG-003",
    orderQty = 100_000,
    codes = bigCodes2,
});
bigDupSw.Stop();
var bigDupBody = await bigDupPost.Content.ReadAsStringAsync();

Check("Bỏ qua toàn bộ 100.000 mã trùng",
    bigDupPost.IsSuccessStatusCode && ResultOf(bigDupBody).Accepted == 0
    && ResultOf(bigDupBody).IgnoredDuplicates == 100_000, bigDupBody);
Check("Không tạo DB cho PO toàn mã trùng", store.FindExistingDbPath("PO-BIG-003") is null);
Check("So ma trung cua PO lon dung bang 100.000",
    MsgOf(bigDupBody).Contains("100000 mã", StringComparison.Ordinal), bigDupBody);
Console.WriteLine($"      Thoi gian: {bigDupSw.ElapsedMilliseconds} ms -> {bigDupBody}");

// =====================================================================
// [24] Upload file CSV THẬT (Content-Disposition filename + text/csv)
// =====================================================================
Console.WriteLine("\n[24] Upload file CSV that (filename trong header)");

var csvRealPath = Path.Combine(workDir, "codes-real.csv");
await File.WriteAllTextAsync(
    csvRealPath,
    "serial\nSER-REAL-1\nSER-REAL-2\nSER-REAL-3\nSER-REAL-4\n",
    new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

var csvRealContent = new ByteArrayContent(await File.ReadAllBytesAsync(csvRealPath));
csvRealContent.Headers.ContentType =
    new System.Net.Http.Headers.MediaTypeHeaderValue("text/csv");
csvRealContent.Headers.ContentDisposition =
    new System.Net.Http.Headers.ContentDispositionHeaderValue("attachment")
    {
        FileName = "codes-real.csv",
    };

var csvRealPost = await http.PostAsync(
    "/api/po?poName=PO-CSV-REAL&orderQty=4&gtin=08934567890123",
    csvRealContent);
var csvRealBody = await csvRealPost.Content.ReadAsStringAsync();

Check("Upload file CSV that (co ten file) tra HTTP 200",
    csvRealPost.StatusCode == System.Net.HttpStatusCode.OK, csvRealBody);
Console.WriteLine($"      -> {csvRealBody}");
Check("Phan hoi CSV that accepted = 4",
    csvRealBody.Contains("\"accepted\":4", StringComparison.Ordinal), csvRealBody);

var csvRealStored = (await store.ListLocalOrdersAsync())
    .FirstOrDefault(p => p.OrderNo == "PO-CSV-REAL");
Check("PO CSV that co 4 ma (bo qua dong tieu de + BOM)",
    csvRealStored?.TotalCodes == 4,
    csvRealStored?.TotalCodes.ToString() ?? "null");

// =====================================================================
// [25] Upload file JSON thật (mảng chuỗi) — application/json + filename
// =====================================================================
Console.WriteLine("\n[25] Upload file JSON that (mang chuoi)");

var jsonRealContent = new ByteArrayContent(
    Encoding.UTF8.GetBytes("[\"SER-J1\",\"SER-J2\",\"SER-J3\"]"));
jsonRealContent.Headers.ContentType =
    new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
jsonRealContent.Headers.ContentDisposition =
    new System.Net.Http.Headers.ContentDispositionHeaderValue("attachment")
    {
        FileName = "codes.json",
    };

var jsonRealPost = await http.PostAsync(
    "/api/po?poName=PO-JSON-REAL&orderQty=3&gtin=08934567890123",
    jsonRealContent);
var jsonRealBody = await jsonRealPost.Content.ReadAsStringAsync();

Check("Upload file JSON that tra HTTP 200",
    jsonRealPost.StatusCode == System.Net.HttpStatusCode.OK, jsonRealBody);
Console.WriteLine($"      -> {jsonRealBody}");

var jsonRealStored = (await store.ListLocalOrdersAsync())
    .FirstOrDefault(p => p.OrderNo == "PO-JSON-REAL");
Check("PO JSON that co 3 ma", jsonRealStored?.TotalCodes == 3,
    jsonRealStored?.TotalCodes.ToString() ?? "null");

// =====================================================================
// [26] Upload file JSON dang mang doi tuong {code/serial}
// =====================================================================
Console.WriteLine("\n[26] Upload file JSON mang doi tuong {code|serial}");

var jsonObjContent = new ByteArrayContent(
    Encoding.UTF8.GetBytes(
        "[{\"code\":\"SER-OC-1\"},{\"serial\":\"SER-OC-2\"},{\"Code\":\"SER-OC-3\"}]"));
jsonObjContent.Headers.ContentType =
    new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
jsonObjContent.Headers.ContentDisposition =
    new System.Net.Http.Headers.ContentDispositionHeaderValue("attachment")
    {
        FileName = "codes-objects.json",
    };

var jsonObjPost = await http.PostAsync(
    "/api/po?poName=PO-JSON-OBJ&orderQty=3&gtin=08934567890123",
    jsonObjContent);
var jsonObjBody = await jsonObjPost.Content.ReadAsStringAsync();

Check("Upload file JSON doi tuong tra HTTP 200",
    jsonObjPost.StatusCode == System.Net.HttpStatusCode.OK, jsonObjBody);
Console.WriteLine($"      -> {jsonObjBody}");

var jsonObjStored = (await store.ListLocalOrdersAsync())
    .FirstOrDefault(p => p.OrderNo == "PO-JSON-OBJ");
Check("PO JSON doi tuong co 3 ma", jsonObjStored?.TotalCodes == 3,
    jsonObjStored?.TotalCodes.ToString() ?? "null");

// =====================================================================
// [27] Upload file EXCEL .xlsx that (sinh file OOXML that)
// =====================================================================
Console.WriteLine("\n[27] Upload file Excel .xlsx that");

var xlsxPath = Path.Combine(workDir, "codes.xlsx");
await WriteTestXlsxAsync(
    xlsxPath,
    new[] { "serial", "SER-XLSX-1", "SER-XLSX-2", "SER-XLSX-3", "SER-XLSX-4" },
    secondColumnValues: new[] { "note-1", "note-2", "note-3", "note-4", "note-5" });

Check("File .xlsx test da duoc tao", File.Exists(xlsxPath), xlsxPath);

var xlsxContent = new ByteArrayContent(await File.ReadAllBytesAsync(xlsxPath));
xlsxContent.Headers.ContentType =
    new System.Net.Http.Headers.MediaTypeHeaderValue(
        "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet");
xlsxContent.Headers.ContentDisposition =
    new System.Net.Http.Headers.ContentDispositionHeaderValue("attachment")
    {
        FileName = "codes.xlsx",
    };

var xlsxPost = await http.PostAsync(
    "/api/po?poName=PO-XLSX-REAL&orderQty=4&gtin=08934567890123",
    xlsxContent);
var xlsxBody = await xlsxPost.Content.ReadAsStringAsync();

Check("Upload file .xlsx that tra HTTP 200",
    xlsxPost.StatusCode == System.Net.HttpStatusCode.OK, xlsxBody);
Console.WriteLine($"      -> {xlsxBody}");
Check("Phan hoi .xlsx accepted = 4",
    xlsxBody.Contains("\"accepted\":4", StringComparison.Ordinal), xlsxBody);

var xlsxStored = (await store.ListLocalOrdersAsync())
    .FirstOrDefault(p => p.OrderNo == "PO-XLSX-REAL");
Check("PO .xlsx co 4 ma (sheet dau tien, cot dau tien, bo dong tieu de)",
    xlsxStored?.TotalCodes == 4,
    xlsxStored?.TotalCodes.ToString() ?? "null");

// ---- Uu tien sheet dau tien: them sheet thu 2 voi du lieu khac ----
var xlsx2Path = Path.Combine(workDir, "codes-2sheets.xlsx");
await WriteTestXlsxAsync(
    xlsx2Path,
    new[] { "A-S1", "A-S2" },
    secondSheetValues: new[] { "B-S1", "B-S2", "B-S3" });

var xlsx2Content = new ByteArrayContent(await File.ReadAllBytesAsync(xlsx2Path));
xlsx2Content.Headers.ContentType =
    new System.Net.Http.Headers.MediaTypeHeaderValue(
        "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet");
xlsx2Content.Headers.ContentDisposition =
    new System.Net.Http.Headers.ContentDispositionHeaderValue("attachment")
    {
        FileName = "codes-2sheets.xlsx",
    };

var xlsx2Post = await http.PostAsync(
    "/api/po?poName=PO-XLSX-2SHEET&orderQty=2&gtin=08934567890123",
    xlsx2Content);
var xlsx2Body = await xlsx2Post.Content.ReadAsStringAsync();

Check("Upload .xlsx 2 sheet tra HTTP 200",
    xlsx2Post.StatusCode == System.Net.HttpStatusCode.OK, xlsx2Body);
Console.WriteLine($"      -> {xlsx2Body}");

var xlsx2Stored = (await store.ListLocalOrdersAsync())
    .FirstOrDefault(p => p.OrderNo == "PO-XLSX-2SHEET");
Check("PO .xlsx lay SHEET DAU TIEN (2 ma, khong phai 3 ma sheet 2)",
    xlsx2Stored?.TotalCodes == 2,
    xlsx2Stored?.TotalCodes.ToString() ?? "null");

// =====================================================================
// [27b] .xlsx dang Excel that sinh ra: inline string + o so (khong dung sharedStrings)
// =====================================================================
Console.WriteLine("\n[27b] .xlsx voi inline string + o so (dung cach Excel that luu)");

var xlsxInlinePath = Path.Combine(workDir, "codes-inline.xlsx");
await WriteTestXlsxInlineAsync(
    xlsxInlinePath,
    new[] { "SER-INL-1", "SER-INL-2", "SER-INL-3" });

var xlsxInlineContent = new ByteArrayContent(
    await File.ReadAllBytesAsync(xlsxInlinePath));
xlsxInlineContent.Headers.ContentType =
    new System.Net.Http.Headers.MediaTypeHeaderValue(
        "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet");
xlsxInlineContent.Headers.ContentDisposition =
    new System.Net.Http.Headers.ContentDispositionHeaderValue("attachment")
    {
        FileName = "codes-inline.xlsx",
    };

var xlsxInlinePost = await http.PostAsync(
    "/api/po?poName=PO-XLSX-INLINE&orderQty=3&gtin=08934567890123",
    xlsxInlineContent);
var xlsxInlineBody = await xlsxInlinePost.Content.ReadAsStringAsync();

Check("Upload .xlsx inline string tra HTTP 200",
    xlsxInlinePost.StatusCode == System.Net.HttpStatusCode.OK, xlsxInlineBody);
Console.WriteLine($"      -> {xlsxInlineBody}");

var xlsxInlineStored = (await store.ListLocalOrdersAsync())
    .FirstOrDefault(p => p.OrderNo == "PO-XLSX-INLINE");
Check("PO .xlsx inline string co 3 ma", xlsxInlineStored?.TotalCodes == 3,
    xlsxInlineStored?.TotalCodes.ToString() ?? "null");

// ---- .xlsx voi o so (Excel luu serial duoi dang number) ----
var xlsxNumPath = Path.Combine(workDir, "codes-number.xlsx");
await WriteTestXlsxNumericAsync(
    xlsxNumPath,
    new[] { "SER-NUM-1", "SER-NUM-2", "SER-NUM-3" });

var xlsxNumContent = new ByteArrayContent(
    await File.ReadAllBytesAsync(xlsxNumPath));
xlsxNumContent.Headers.ContentType =
    new System.Net.Http.Headers.MediaTypeHeaderValue(
        "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet");
xlsxNumContent.Headers.ContentDisposition =
    new System.Net.Http.Headers.ContentDispositionHeaderValue("attachment")
    {
        FileName = "codes-number.xlsx",
    };

var xlsxNumPost = await http.PostAsync(
    "/api/po?poName=PO-XLSX-NUM&orderQty=3&gtin=08934567890123",
    xlsxNumContent);
var xlsxNumBody = await xlsxNumPost.Content.ReadAsStringAsync();

Check("Upload .xlsx o so tra HTTP 200",
    xlsxNumPost.StatusCode == System.Net.HttpStatusCode.OK, xlsxNumBody);
Console.WriteLine($"      -> {xlsxNumBody}");

var xlsxNumStored = (await store.ListLocalOrdersAsync())
    .FirstOrDefault(p => p.OrderNo == "PO-XLSX-NUM");
Check("PO .xlsx o so co 3 ma", xlsxNumStored?.TotalCodes == 3,
    xlsxNumStored?.TotalCodes.ToString() ?? "null");

// =====================================================================
// [28] Upload .xlsx qua multipart/form-data
// =====================================================================
Console.WriteLine("\n[28] Upload .xlsx qua multipart/form-data");
var xlsxMultipart = new MultipartFormDataContent();
xlsxMultipart.Add(new StringContent("PO-XLSX-MULTI"), "poName");
xlsxMultipart.Add(new StringContent("3"), "orderQty");
xlsxMultipart.Add(new StringContent("08934567890123"), "gtin");

// File riêng voi ma rieng (khong trung voi PO-XLSX-REAL).
var xlsxMultiPath = Path.Combine(workDir, "codes-multi.xlsx");
await WriteTestXlsxAsync(
    xlsxMultiPath,
    new[] { "SER-XM-1", "SER-XM-2", "SER-XM-3" });

xlsxMultipart.Add(
    new ByteArrayContent(await File.ReadAllBytesAsync(xlsxMultiPath)),
    "file",
    "codes-multi.xlsx");

var xlsxMultiPost = await http.PostAsync("/api/po", xlsxMultipart);
var xlsxMultiBody = await xlsxMultiPost.Content.ReadAsStringAsync();

Check("Upload .xlsx multipart tra HTTP 200",
    xlsxMultiPost.StatusCode == System.Net.HttpStatusCode.OK, xlsxMultiBody);
Console.WriteLine($"      -> {xlsxMultiBody}");

var xlsxMultiStored = (await store.ListLocalOrdersAsync())
    .FirstOrDefault(p => p.OrderNo == "PO-XLSX-MULTI");
Check("PO .xlsx multipart co 3 ma", xlsxMultiStored?.TotalCodes == 3,
    xlsxMultiStored?.TotalCodes.ToString() ?? "null");

// =====================================================================
// [29] File khong ho tro -> 400
// =====================================================================
Console.WriteLine("\n[29] File khong ho tro (.txt) -> 400");

var txtContent = new ByteArrayContent(Encoding.UTF8.GetBytes("SER-1\nSER-2\n"));
txtContent.Headers.ContentType =
    new System.Net.Http.Headers.MediaTypeHeaderValue("text/plain");
txtContent.Headers.ContentDisposition =
    new System.Net.Http.Headers.ContentDispositionHeaderValue("attachment")
    {
        FileName = "codes.txt",
    };

var txtPost = await http.PostAsync(
    "/api/po?poName=PO-TXT&orderQty=2", txtContent);
var txtBody = await txtPost.Content.ReadAsStringAsync();

Check("File .txt tra HTTP 400",
    txtPost.StatusCode == System.Net.HttpStatusCode.BadRequest, txtBody);
Console.WriteLine($"      -> {txtBody}");
Check("Thong bao nhac dinh dang ho tro",
    MsgOf(txtBody).Contains(".csv", StringComparison.Ordinal), txtBody);

// =====================================================================
// [30] Ten file khong hop le -> 400
// =====================================================================
Console.WriteLine("\n[30] Ten file khong hop le -> 400");

var badNameContent = new ByteArrayContent(Encoding.UTF8.GetBytes("SER-1\n"));
badNameContent.Headers.ContentType =
    new System.Net.Http.Headers.MediaTypeHeaderValue("text/csv");

// Gửi header thô để né việc client tự làm sạch tên file.
badNameContent.Headers.TryAddWithoutValidation(
    "Content-Disposition", "attachment; filename=\"..\\..\\evil.csv\"");

var badNamePost = await http.PostAsync(
    "/api/po?poName=PO-BADNAME&orderQty=1", badNameContent);
var badNameBody = await badNamePost.Content.ReadAsStringAsync();

Check("Ten file chua duong dan -> HTTP 400",
    badNamePost.StatusCode == System.Net.HttpStatusCode.BadRequest, badNameBody);
Console.WriteLine($"      -> {badNameBody}");

// =====================================================================
// [31] File vuot gioi han kich thuoc -> 413
// =====================================================================
Console.WriteLine("\n[31] File vuot gioi han MaxFileMB -> 413");

var tinyFileOptions = Microsoft.Extensions.Options.Options.Create(new PoServerOptions
{
    Enabled = true,
    Port = FindFreePort(),
    BindAddress = "127.0.0.1",
    MaxBodyMB = 64,
    MaxFileMB = 1,
});

using var tinyFileServer = new PoHttpServer(tinyFileOptions, store, logger);
tinyFileServer.Start();

using var tinyFileHttp = new HttpClient
{
    BaseAddress = new Uri($"http://127.0.0.1:{tinyFileOptions.Value.Port}/"),
    Timeout = TimeSpan.FromSeconds(60),
};

// File CSV ~3 MB -> vuot gioi han 1 MB.
var bigCsv = new StringBuilder();
while (bigCsv.Length < 3 * 1024 * 1024)
{
    bigCsv.Append("SER-BIGFILE-").Append(bigCsv.Length).Append('\n');
}

var bigFileContent = new ByteArrayContent(Encoding.UTF8.GetBytes(bigCsv.ToString()));
bigFileContent.Headers.ContentType =
    new System.Net.Http.Headers.MediaTypeHeaderValue("text/csv");
bigFileContent.Headers.ContentDisposition =
    new System.Net.Http.Headers.ContentDispositionHeaderValue("attachment")
    {
        FileName = "big.csv",
    };

var bigFilePost = await tinyFileHttp.PostAsync(
    "/api/po?poName=PO-BIGFILE&orderQty=1", bigFileContent);
var bigFileBody = await bigFilePost.Content.ReadAsStringAsync();

Check("File vuot MaxFileMB tra HTTP 413",
    bigFilePost.StatusCode == System.Net.HttpStatusCode.RequestEntityTooLarge,
    $"{bigFilePost.StatusCode} {bigFileBody}");
Console.WriteLine($"      -> {bigFileBody}");

// =====================================================================
// [32] File tam da duoc xoa sau khi xu ly (khong con file sot)
// =====================================================================
Console.WriteLine("\n[32] File tam duoc don sau khi xu ly");

var uploadTempRoot = Path.Combine(workDir, "visionf-po-uploads");
var leftover = Directory.Exists(uploadTempRoot)
    ? Directory.GetFiles(uploadTempRoot, "po-upload-*").Length
    : 0;

Check("Khong con file tam nao con lai sau khi xu ly",
    leftover == 0, leftover.ToString());

// =====================================================================
// [33] File CSV rong / khong co ma -> 400
// =====================================================================
Console.WriteLine("\n[33] File CSV rong -> 400");

var emptyFileContent = new ByteArrayContent(Array.Empty<byte>());
emptyFileContent.Headers.ContentType =
    new System.Net.Http.Headers.MediaTypeHeaderValue("text/csv");
emptyFileContent.Headers.ContentDisposition =
    new System.Net.Http.Headers.ContentDispositionHeaderValue("attachment")
    {
        FileName = "empty.csv",
    };

var emptyFilePost = await http.PostAsync(
    "/api/po?poName=PO-EMPTY&orderQty=1", emptyFileContent);
var emptyFileBody = await emptyFilePost.Content.ReadAsStringAsync();

Check("File rong tra HTTP 400",
    emptyFilePost.StatusCode == System.Net.HttpStatusCode.BadRequest, emptyFileBody);
Console.WriteLine($"      -> {emptyFileBody}");

// =====================================================================
// [34] Upload CSV chứa mã trùng: chỉ nhận phần mã mới
// =====================================================================
Console.WriteLine("\n[34] Lọc mã trùng khi upload CSV");

var dupFileContent = new ByteArrayContent(
    Encoding.UTF8.GetBytes("SER001\nSER-NEW-999\n"));
dupFileContent.Headers.ContentType =
    new System.Net.Http.Headers.MediaTypeHeaderValue("text/csv");
dupFileContent.Headers.ContentDisposition =
    new System.Net.Http.Headers.ContentDispositionHeaderValue("attachment")
    {
        FileName = "dup.csv",
    };

var dupFilePost = await http.PostAsync(
    "/api/po?poName=PO-DUP-FILE&orderQty=2&gtin=08934567890123",
    dupFileContent);
var dupFileBody = await dupFilePost.Content.ReadAsStringAsync();

Check("CSV có mã trùng vẫn nhận phần mã mới",
    dupFilePost.IsSuccessStatusCode && ResultOf(dupFileBody).Accepted == 1
    && ResultOf(dupFileBody).IgnoredDuplicates == 1, dupFileBody);
Console.WriteLine($"      -> {dupFileBody}");

// =====================================================================
// [35] Upload thêm file vào PO chưa sản xuất
// =====================================================================
Console.WriteLine("\n[35] Upload thêm file vào cùng PO");

var dupNameContent = new ByteArrayContent(
    Encoding.UTF8.GetBytes("SER-ANOTHER-1\n"));
dupNameContent.Headers.ContentType =
    new System.Net.Http.Headers.MediaTypeHeaderValue("text/csv");
dupNameContent.Headers.ContentDisposition =
    new System.Net.Http.Headers.ContentDispositionHeaderValue("attachment")
    {
        FileName = "dup-name.csv",
    };

var dupNamePost = await http.PostAsync(
    "/api/po?poName=PO-CSV-REAL&orderQty=1", dupNameContent);
var dupNameBody = await dupNamePost.Content.ReadAsStringAsync();

Check("Upload thêm cùng PO trả HTTP 200",
    dupNamePost.IsSuccessStatusCode && ResultOf(dupNameBody).Accepted == 1, dupNameBody);
var mergedFileOrder = (await store.ListLocalOrdersAsync()).Single(p => p.OrderNo == "PO-CSV-REAL");
Check("PO tăng từ 4 lên 5 mã", mergedFileOrder.TotalCodes == 5);
Check("Merge giữ CreatedUtc", mergedFileOrder.CreatedUtc == csvRealStored!.CreatedUtc);
Check("Merge giữ metadata cũ khi request không gửi lại",
    mergedFileOrder.Additional.TryGetValue("gtin", out var keptGtin) && keptGtin == "08934567890123");
Console.WriteLine($"      -> {dupNameBody}");

// =====================================================================
// [36] Kiểm thử trạng thái sản xuất và upload đồng thời trên kho riêng
// =====================================================================
Console.WriteLine("\n[36] Chặn nhận PO khi sản xuất đang chạy hoặc còn dở dang");

var safetyRoot = Path.Combine(workDir, "safety-po");
var safetyStore = new PoStore(safetyRoot);
var safetyHistory = new PoHistoryStore(safetyRoot);
using var safetyServer = new PoHttpServer(Options.Create(new PoServerOptions
{
    Enabled = true,
    Port = FindFreePort(),
    BindAddress = "127.0.0.1",
    UploadTempDirectory = workDir,
}), safetyStore, logger, safetyHistory);
safetyServer.Start();
Check("Server kiểm thử trạng thái khởi động được", !safetyServer.State.IsError, safetyServer.State.Status);
using var safetyHttp = new HttpClient { BaseAddress = new Uri(safetyServer.Prefix), Timeout = TimeSpan.FromSeconds(15) };
var production = new ProductionOrderService(new LocalOnlyPoClient(), safetyStore, safetyHistory, new PoCodeCache());

async Task<(System.Net.HttpStatusCode Status, PoPushResponse Result)> PushSafetyAsync(string name, params string[] codes)
{
    using var response = await safetyHttp.PostAsJsonAsync("api/po", new
    {
        poName = name,
        orderQty = codes.Length,
        gtin = "08934567890123",
        codes,
    });
    return (response.StatusCode, ResultOf(await response.Content.ReadAsStringAsync()));
}

await PushSafetyAsync("PO-ACTIVE", "ACTIVE-1", "ACTIVE-2");
Check("Bắt đầu sản xuất thật", await production.StartProductionAsync("PO-ACTIVE"));
var activeBefore = (await safetyStore.ListLocalOrdersAsync()).Single(p => p.OrderNo == "PO-ACTIVE");
var blockJson = await PushSafetyAsync("PO-BLOCK-JSON", "BLOCK-JSON-1");
Check("Có PO đang chạy: chặn JSON PO mới", blockJson.Status == System.Net.HttpStatusCode.Conflict && blockJson.Result.Accepted == 0);
var blockRepeat = await PushSafetyAsync("PO-ACTIVE", "ACTIVE-1", "ACTIVE-NEW");
Check("Có PO đang chạy: chặn bổ sung và upload lại cùng PO", blockRepeat.Status == System.Net.HttpStatusCode.Conflict);
using (var raw = new StringContent("BLOCK-RAW-1\n", Encoding.UTF8, "text/csv"))
using (var response = await safetyHttp.PostAsync("api/po?poName=PO-BLOCK-RAW&orderQty=1&gtin=08934567890123", raw))
{
    Check("Có PO đang chạy: chặn file CSV thuần", response.StatusCode == System.Net.HttpStatusCode.Conflict);
}
using (var form = new MultipartFormDataContent())
{
    form.Add(new StringContent("PO-BLOCK-MULTI"), "poName");
    form.Add(new StringContent("1"), "orderQty");
    form.Add(new StringContent("BLOCK-MULTI-1\n", Encoding.UTF8), "file", "blocked.csv");
    using var response = await safetyHttp.PostAsync("api/po", form);
    Check("Có PO đang chạy: chặn multipart", response.StatusCode == System.Net.HttpStatusCode.Conflict);
}
var activeAfter = (await safetyStore.ListLocalOrdersAsync()).Single(p => p.OrderNo == "PO-ACTIVE");
Check("Các lần bị chặn không tạo PO mới", !(await safetyStore.ListLocalOrdersAsync()).Any(p => p.OrderNo.StartsWith("PO-BLOCK-", StringComparison.Ordinal)));
Check("PO đang chạy không bị thay đổi", activeAfter.TotalCodes == activeBefore.TotalCodes && activeAfter.Status == ProductionOrderStatus.Running);

// Hoàn tất bằng luồng quét thực tế để kiểm tra khóa không gây deadlock.
await production.MarkCodeScannedAsync("08934567890123", "ACTIVE-1");
await production.MarkCodeScannedAsync("08934567890123", "ACTIVE-2");
Check("Quét đủ mã tự hoàn tất", !production.IsProductionRunning && production.LastFinishedStatus == ProductionOrderStatus.Completed);
var completeRetry = await PushSafetyAsync("PO-ACTIVE", "ACTIVE-NEW");
Check("PO đã hoàn tất không nhận thêm mã", completeRetry.Status == System.Net.HttpStatusCode.Conflict && completeRetry.Result.Accepted == 0);
Check("PO hoàn tất giữ nguyên số mã", (await safetyStore.ListLocalOrdersAsync()).Single(p => p.OrderNo == "PO-ACTIVE").TotalCodes == 2);
var afterComplete = await PushSafetyAsync("PO-AFTER-COMPLETE", "AFTER-COMPLETE-1");
Check("Sau hoàn tất cho phép nhận PO mới", afterComplete.Status == System.Net.HttpStatusCode.OK && afterComplete.Result.Accepted == 1);

Check("Chạy PO tiếp theo", await production.StartProductionAsync("PO-AFTER-COMPLETE"));
Check("Huỷ PO đang chạy", await production.CancelProductionAsync());
var cancelRetry = await PushSafetyAsync("PO-AFTER-COMPLETE", "CANCEL-NEW");
Check("PO đã huỷ không nhận thêm mã", cancelRetry.Status == System.Net.HttpStatusCode.Conflict);
var afterCancel = await PushSafetyAsync("PO-AFTER-CANCEL", "AFTER-CANCEL-1");
Check("Sau huỷ cho phép nhận PO mới", afterCancel.Status == System.Net.HttpStatusCode.OK);
var closed = (await safetyStore.ListLocalOrdersAsync()).Single(p => p.OrderNo == "PO-AFTER-CANCEL");
await safetyStore.SetMetaAsync(closed.OrderNo, closed.CreatedUtc, PoStore.MetaStatus, ProductionOrderStatus.Closed.ToString());
var closedRetry = await PushSafetyAsync(closed.OrderNo, "CLOSED-NEW");
Check("PO đã đóng không nhận thêm mã", closedRetry.Status == System.Net.HttpStatusCode.Conflict);

// Dữ liệu dở dang trên đĩa phải chặn ngay cả khi service chưa khôi phục RAM.
await PushSafetyAsync("PO-PERSISTED-RUNNING", "PERSISTED-1", "PERSISTED-2");
var persisted = (await safetyStore.ListLocalOrdersAsync()).Single(p => p.OrderNo == "PO-PERSISTED-RUNNING");
await safetyStore.SetMetaAsync(persisted.OrderNo, persisted.CreatedUtc, PoStore.MetaStatus, ProductionOrderStatus.Running.ToString());
var persistedBlock = await PushSafetyAsync("PO-PERSISTED-BLOCK", "PERSISTED-BLOCK-1");
Check("Chặn PO Running trên đĩa dù chưa khôi phục RAM", persistedBlock.Status == System.Net.HttpStatusCode.Conflict);
await safetyStore.MarkCancelledAsync(persisted.OrderNo, persisted.CreatedUtc);

await PushSafetyAsync("PO-PERSISTED-STARTED", "STARTED-1", "STARTED-2");
var started = (await safetyStore.ListLocalOrdersAsync()).Single(p => p.OrderNo == "PO-PERSISTED-STARTED");
await safetyStore.SetMetaAsync(started.OrderNo, started.CreatedUtc, PoStore.MetaStartedUtc, DateTime.UtcNow.ToString("O"));
var startedBlock = await PushSafetyAsync("PO-STARTED-BLOCK", "STARTED-BLOCK-1");
Check("Chặn PO đã bắt đầu nhưng chưa ghi trạng thái kết thúc", startedBlock.Status == System.Net.HttpStatusCode.Conflict);
await safetyStore.MarkCancelledAsync(started.OrderNo, started.CreatedUtc);

await PushSafetyAsync("PO-PERSISTED-SCANNED", "SCANNED-1", "SCANNED-2");
var scanned = (await safetyStore.ListLocalOrdersAsync()).Single(p => p.OrderNo == "PO-PERSISTED-SCANNED");
await safetyStore.MarkScannedAsync(scanned.OrderNo, scanned.CreatedUtc, "08934567890123|SCANNED-1");
var scannedBlock = await PushSafetyAsync("PO-SCANNED-BLOCK", "SCANNED-BLOCK-1");
Check("Chặn PO đã quét dở dù status chưa là Running", scannedBlock.Status == System.Net.HttpStatusCode.Conflict);
await safetyStore.MarkCancelledAsync(scanned.OrderNo, scanned.CreatedUtc);

await safetyHistory.StartRunAsync("PO-HISTORY-ONLY", 2, DateTime.UtcNow);
var historyBlock = await PushSafetyAsync("PO-HISTORY-BLOCK", "HISTORY-BLOCK-1");
Check("Lịch sử còn Running cũng chặn nhận khi DB PO thiếu", historyBlock.Status == System.Net.HttpStatusCode.Conflict);
await safetyHistory.CancelRunAsync("PO-HISTORY-ONLY");

// Các PO kết thúc không được giữ khóa nhận vô thời hạn.
var unlocked = await PushSafetyAsync("PO-UNLOCKED", "UNLOCKED-1");
Check("Các PO đã huỷ/hoàn tất không chặn PO mới", unlocked.Status == System.Net.HttpStatusCode.OK);
Check("Không có DB nào cho các request dở dang bị chặn",
    new[] { "PO-PERSISTED-BLOCK", "PO-STARTED-BLOCK", "PO-SCANNED-BLOCK", "PO-HISTORY-BLOCK" }
        .All(name => safetyStore.FindExistingDbPath(name) is null));

Console.WriteLine("\n[37] Lọc trùng theo khóa chuẩn và giữa nhiều PO");
using (var response = await safetyHttp.PostAsJsonAsync("api/po", new
{
    poName = "PO-GTIN-KEY",
    orderQty = 3,
    codes = new[]
    {
        "https://example.test/01/08934567890123/21/SAME-SERIAL",
        "(01)08934567890123(21)SAME-SERIAL",
        "https://example.test/01/08934567890124/21/SAME-SERIAL",
    },
}))
{
    var result = ResultOf(await response.Content.ReadAsStringAsync());
    Check("Cùng GTIN/Serial khác định dạng bị loại trùng; khác GTIN vẫn giữ",
        response.IsSuccessStatusCode && result.Accepted == 2 && result.IgnoredDuplicates == 1);
}
await PushSafetyAsync("PO-OWNER-A", "OWNER-A-1");
await PushSafetyAsync("PO-OWNER-B", "OWNER-B-1");
var severalOwners = await PushSafetyAsync("PO-SEVERAL-OWNERS", "OWNER-A-1", "OWNER-B-1", "OWNER-NEW-1", "OWNER-NEW-1");
Check("Quét hết nhiều PO sở hữu mã trùng và trùng trong cùng request",
    severalOwners.Status == System.Net.HttpStatusCode.OK && severalOwners.Result.Accepted == 1
    && severalOwners.Result.IgnoredDuplicates == 3);

Console.WriteLine("\n[38] Upload đồng thời không tạo mã trùng");
var competing = await Task.WhenAll(Enumerable.Range(0, 8).Select(i => PushSafetyAsync($"PO-RACE-{i}", "RACE-SHARED-1")));
Check("Các lượt upload đồng thời đều trả kết quả hợp lệ", competing.All(r => r.Status == System.Net.HttpStatusCode.OK));
Check("Chỉ một lượt nhận mã mới", competing.Sum(r => r.Result.Accepted) == 1);
Check("Bảy lượt còn lại báo bỏ qua mã trùng", competing.Sum(r => r.Result.IgnoredDuplicates) == 7);
Check("Chỉ tạo một PO chứa mã chung", (await safetyStore.ListLocalOrdersAsync()).Count(p => p.OrderNo.StartsWith("PO-RACE-", StringComparison.Ordinal)) == 1);
var concurrentMerge = await Task.WhenAll(
    PushSafetyAsync("PO-CONCURRENT-MERGE", "MERGE-SHARED", "MERGE-LEFT"),
    PushSafetyAsync("PO-CONCURRENT-MERGE", "MERGE-SHARED", "MERGE-RIGHT"));
Check("Bổ sung đồng thời cùng PO chỉ nhận ba mã duy nhất", concurrentMerge.Sum(r => r.Result.Accepted) == 3);
Check("DB merge đồng thời có đủ ba mã", (await safetyStore.ListLocalOrdersAsync()).Single(p => p.OrderNo == "PO-CONCURRENT-MERGE").TotalCodes == 3);

// Xếp thao tác bắt đầu trước request nhận trong cùng khóa; request phải thấy trạng thái mới.
await PushSafetyAsync("PO-START-RACE", "START-RACE-1", "START-RACE-2");
Task<bool> startWhileLocked;
Task<(System.Net.HttpStatusCode Status, PoPushResponse Result)> receiveWhileLocked;
using (await safetyStore.AcquireMutationLockAsync())
{
    startWhileLocked = production.StartProductionAsync("PO-START-RACE");
    receiveWhileLocked = PushSafetyAsync("PO-RECEIVE-RACE", "RECEIVE-RACE-1");
}
Check("Bắt đầu sản xuất qua khóa dùng chung", await startWhileLocked.WaitAsync(TimeSpan.FromSeconds(10)));
var receiveRace = await receiveWhileLocked.WaitAsync(TimeSpan.FromSeconds(10));
Check("Không nhận PO chen vào lúc bắt đầu sản xuất", receiveRace.Status == System.Net.HttpStatusCode.Conflict);
Check("Request đua với Start không tạo DB", safetyStore.FindExistingDbPath("PO-RECEIVE-RACE") is null);
await production.CancelProductionAsync();

Console.WriteLine("\n[39] Không ghi khi không xác minh được dữ liệu PO hiện hữu");
var corruptRoot = Path.Combine(workDir, "corrupt-po");
var corruptMonth = Path.Combine(corruptRoot, DateTime.UtcNow.ToString("yyyy-MM"));
Directory.CreateDirectory(corruptMonth);
await File.WriteAllTextAsync(Path.Combine(corruptMonth, "unreadable.db"), "not a sqlite database");
var corruptStore = new PoStore(corruptRoot);
using var corruptServer = new PoHttpServer(Options.Create(new PoServerOptions
{
    Enabled = true,
    Port = FindFreePort(),
    BindAddress = "127.0.0.1",
}), corruptStore, logger, new PoHistoryStore(corruptRoot));
corruptServer.Start();
using var corruptHttp = new HttpClient { BaseAddress = new Uri(corruptServer.Prefix), Timeout = TimeSpan.FromSeconds(15) };
using (var response = await corruptHttp.PostAsJsonAsync("api/po", new
{
    poName = "PO-MUST-NOT-WRITE",
    orderQty = 1,
    codes = new[] { "UNVERIFIED-1" },
}))
{
    var result = ResultOf(await response.Content.ReadAsStringAsync());
    Check("DB cũ hỏng: trả lỗi và không nhận mã", response.StatusCode == System.Net.HttpStatusCode.InternalServerError && result.Accepted == 0);
    Check("DB cũ hỏng: không tạo PO mới", corruptStore.FindExistingDbPath("PO-MUST-NOT-WRITE") is null);
}

// Bản ghi lịch sử còn sót sau khi PO local đã kết thúc không được khóa máy.
await safetyHistory.StartRunAsync("PO-ACTIVE", 2, activeBefore.CreatedUtc);
var staleCompletedRun = await PushSafetyAsync("PO-AFTER-STALE-COMPLETE", "STALE-COMPLETE-1");
Check("Lịch sử Running cũ của PO hoàn tất không chặn nhận", staleCompletedRun.Status == System.Net.HttpStatusCode.OK);
await safetyHistory.StartRunAsync("PO-AFTER-COMPLETE", 1, activeBefore.CreatedUtc);
var staleCancelledRun = await PushSafetyAsync("PO-AFTER-STALE-CANCEL", "STALE-CANCEL-1");
Check("Lịch sử Running cũ của PO đã huỷ không chặn nhận", staleCancelledRun.Status == System.Net.HttpStatusCode.OK);
await safetyHistory.StartRunAsync("PO-REAL-UNFINISHED", 1, DateTime.UtcNow);
await safetyHistory.StartRunAsync("PO-ACTIVE", 2, activeBefore.CreatedUtc);
var hiddenRun = await PushSafetyAsync("PO-HIDDEN-RUN-BLOCK", "HIDDEN-RUN-1");
Check("Lịch sử dở dang cũ vẫn chặn dù lần mới nhất đã kết thúc", hiddenRun.Status == System.Net.HttpStatusCode.Conflict);
await safetyHistory.CancelRunAsync("PO-REAL-UNFINISHED");

// Lần nhận đầu thất bại không để DB rỗng; lần bổ sung thất bại rollback metadata và mã.
var rollbackOrder = new ProductionOrder { OrderNo = "PO-ROLLBACK", OrderQty = 2, CreatedUtc = DateTime.UtcNow };
try
{
    await safetyStore.EnsurePoDbAsync(rollbackOrder,
    [
        new() { Code = "ROLLBACK-1", Gtin = "G", Serial = "ROLLBACK-1" },
        new() { Code = null!, Gtin = "G", Serial = "ROLLBACK-BAD" },
    ]);
    Check("Lỗi lưu mã phải được báo", false);
}
catch (InvalidOperationException)
{
    Check("Lần nhận đầu lỗi không để lại DB công khai", safetyStore.FindExistingDbPath(rollbackOrder.OrderNo) is null);
}
await safetyStore.EnsurePoDbAsync(rollbackOrder, [new() { Code = "ROLLBACK-1", Gtin = "G", Serial = "ROLLBACK-1" }]);
rollbackOrder.OrderQty = 999;
try
{
    var failingCodes = Enumerable.Range(0, 300).Select(i => new PoCode
        { Code = $"ROLLBACK-NEW-{i}", Gtin = "G", Serial = $"ROLLBACK-NEW-{i}" }).ToList();
    failingCodes.Add(new() { Code = null!, Gtin = "G", Serial = "ROLLBACK-BAD" });
    await safetyStore.EnsurePoDbAsync(rollbackOrder, failingCodes);
    Check("Lỗi bổ sung phải được báo", false);
}
catch (InvalidOperationException)
{
    var rollbackMeta = await safetyStore.GetMetaAsync(rollbackOrder.OrderNo, rollbackOrder.CreatedUtc);
    var rollbackCodes = await safetyStore.GetCodesAsync(rollbackOrder.OrderNo, rollbackOrder.CreatedUtc);
    Check("Lỗi ở batch sau rollback toàn bộ mã và metadata", rollbackCodes.Count == 1
        && rollbackMeta[PoStore.MetaOrderQty] == "2");
}
var uploadAfterFailure = await PushSafetyAsync("PO-AFTER-WRITE-FAILURE", "AFTER-WRITE-FAILURE-1");
Check("Tiếp tục nhận được sau lỗi ghi DB", uploadAfterFailure.Status == System.Net.HttpStatusCode.OK);

// Không gửi lại GTIN thì dùng GTIN đã lưu của chính PO khi bổ sung serial trần.
using (var response = await safetyHttp.PostAsJsonAsync("api/po", new
{
    poName = "PO-OWNER-A", orderQty = 2, codes = new[] { "OWNER-A-1", "OWNER-A-EXTRA" },
}))
{
    var result = ResultOf(await response.Content.ReadAsStringAsync());
    Check("Bổ sung serial trần dùng GTIN cũ để lọc trùng", response.IsSuccessStatusCode
        && result.Accepted == 1 && result.IgnoredDuplicates == 1);
}

// =====================================================================
// [40] Tổng kết danh sách PO trong DB local
// =====================================================================
Console.WriteLine("\n[40] Tổng kết danh sách PO trong DB local");

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

static PoPushResponse ResultOf(string json) =>
    JsonSerializer.Deserialize(json, PoServerJsonContext.Default.PoPushResponse)
    ?? throw new InvalidOperationException("Phản hồi PO rỗng.");

static int FindFreePort()
{
    var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
    listener.Start();
    var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
    listener.Stop();
    return port;
}

/// <summary>
/// Sinh file Excel .xlsx (OOXML) THẬT để kiểm thử việc upload.
///
/// <para>
/// File được tạo theo đúng cấu trúc OOXML: ZIP chứa
/// <c>xl/workbook.xml</c>, <c>xl/worksheets/sheetN.xml</c> và
/// <c>xl/sharedStrings.xml</c> — cùng định dạng Excel thật sinh ra, nên bộ
/// đọc Excel của server được kiểm thử trên dữ liệu thực tế.
/// </para>
/// </summary>
/// <param name="path">Đường dẫn file .xlsx đầu ra.</param>
/// <param name="firstColumnValues">Giá trị cột đầu tiên của sheet 1 (mỗi phần tử 1 dòng).</param>
/// <param name="secondColumnValues">Giá trị cột thứ hai của sheet 1 (tuỳ chọn).</param>
/// <param name="secondSheetValues">Giá trị cột đầu tiên của sheet 2 (tuỳ chọn).</param>
static async Task WriteTestXlsxAsync(
    string path,
    string[] firstColumnValues,
    string[]? secondColumnValues = null,
    string[]? secondSheetValues = null)
{
    var shared = new List<string>();
    var sharedIndex = new Dictionary<string, int>(StringComparer.Ordinal);

    int SharedId(string value)
    {
        if (!sharedIndex.TryGetValue(value, out var idx))
        {
            idx = shared.Count;
            shared.Add(value);
            sharedIndex[value] = idx;
        }

        return idx;
    }

    static string Escape(string s) =>
        s.Replace("&", "&amp;", StringComparison.Ordinal)
         .Replace("<", "&lt;", StringComparison.Ordinal)
         .Replace(">", "&gt;", StringComparison.Ordinal);

    string BuildSheetXml(string[] colA, string[]? colB)
    {
        var sb = new StringBuilder();
        sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
        sb.Append("<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">");
        sb.Append("<sheetData>");

        var maxRows = colA.Length;
        if (colB is not null && colB.Length > maxRows)
        {
            maxRows = colB.Length;
        }

        for (var r = 0; r < maxRows; r++)
        {
            var rowNum = r + 1;
            sb.Append("<row r=\"").Append(rowNum).Append("\">");

            if (r < colA.Length)
            {
                sb.Append("<c r=\"A").Append(rowNum)
                  .Append("\" t=\"s\"><v>").Append(SharedId(colA[r])).Append("</v></c>");
            }

            if (colB is not null && r < colB.Length)
            {
                sb.Append("<c r=\"B").Append(rowNum)
                  .Append("\" t=\"s\"><v>").Append(SharedId(colB[r])).Append("</v></c>");
            }

            sb.Append("</row>");
        }

        sb.Append("</sheetData></worksheet>");
        return sb.ToString();
    }

    var sheet1Xml = BuildSheetXml(firstColumnValues, secondColumnValues);
    var sheet2Xml = secondSheetValues is not null
        ? BuildSheetXml(secondSheetValues, null)
        : null;

    // File tam trong thu muc canh bao (khong ghi de file dang mo).
    var tmp = path + ".tmp";
    if (File.Exists(tmp))
    {
        File.Delete(tmp);
    }

    using (var fs = File.Create(tmp))
    using (var zip = new System.IO.Compression.ZipArchive(
        fs, System.IO.Compression.ZipArchiveMode.Create))
    {
        void AddEntry(string name, string content)
        {
            var entry = zip.CreateEntry(name);
            using var w = new StreamWriter(entry.Open(), new UTF8Encoding(false));
            w.Write(content);
        }

        // [Content_Types].xml
        var contentTypes = new StringBuilder();
        contentTypes.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
        contentTypes.Append("<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">");
        contentTypes.Append("<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>");
        contentTypes.Append("<Default Extension=\"xml\" ContentType=\"application/xml\"/>");
        contentTypes.Append("<Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/>");
        contentTypes.Append("<Override PartName=\"/xl/sharedStrings.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sharedStrings+xml\"/>");
        contentTypes.Append("<Override PartName=\"/xl/worksheets/sheet1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/>");
        if (sheet2Xml is not null)
        {
            contentTypes.Append("<Override PartName=\"/xl/worksheets/sheet2.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/>");
        }

        contentTypes.Append("</Types>");
        AddEntry("[Content_Types].xml", contentTypes.ToString());

        // _rels/.rels
        AddEntry("_rels/.rels",
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>"
            + "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">"
            + "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/>"
            + "</Relationships>");

        // xl/workbook.xml
        var workbook = new StringBuilder();
        workbook.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
        workbook.Append("<workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" ");
        workbook.Append("xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\">");
        workbook.Append("<sheets>");
        workbook.Append("<sheet name=\"Sheet1\" sheetId=\"1\" r:id=\"rId1\"/>");
        if (sheet2Xml is not null)
        {
            workbook.Append("<sheet name=\"Sheet2\" sheetId=\"2\" r:id=\"rId2\"/>");
        }

        workbook.Append("</sheets></workbook>");
        AddEntry("xl/workbook.xml", workbook.ToString());

        // xl/_rels/workbook.xml.rels
        var wbRels = new StringBuilder();
        wbRels.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
        wbRels.Append("<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">");
        wbRels.Append("<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet1.xml\"/>");
        if (sheet2Xml is not null)
        {
            wbRels.Append("<Relationship Id=\"rId2\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet2.xml\"/>");
        }

        wbRels.Append("<Relationship Id=\"rIdSst\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/sharedStrings\" Target=\"sharedStrings.xml\"/>");
        wbRels.Append("</Relationships>");
        AddEntry("xl/_rels/workbook.xml.rels", wbRels.ToString());

        // xl/sharedStrings.xml
        var sst = new StringBuilder();
        sst.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
        sst.Append("<sst xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" count=\"")
           .Append(shared.Count).Append("\" uniqueCount=\"").Append(shared.Count).Append("\">");
        foreach (var s in shared)
        {
            sst.Append("<si><t>").Append(Escape(s)).Append("</t></si>");
        }

        sst.Append("</sst>");
        AddEntry("xl/sharedStrings.xml", sst.ToString());

        // xl/worksheets/sheetN.xml
        AddEntry("xl/worksheets/sheet1.xml", sheet1Xml);
        if (sheet2Xml is not null)
        {
            AddEntry("xl/worksheets/sheet2.xml", sheet2Xml);
        }
    }

    File.Move(tmp, path, overwrite: true);
    await Task.CompletedTask;
}

/// <summary>
/// Sinh file .xlsx dùng <b>inline string</b> (cách một số ứng dụng lưu, không
/// dùng sharedStrings) — kiểm thử bộ đọc xử lý đúng.
/// </summary>
static async Task WriteTestXlsxInlineAsync(string path, string[] values)
{
    var sheet = new StringBuilder();
    sheet.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
    sheet.Append("<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><sheetData>");
    for (var r = 0; r < values.Length; r++)
    {
        sheet.Append($"<row r=\"{r + 1}\">");
        sheet.Append($"<c r=\"A{r + 1}\" t=\"inlineStr\"><is><t>{values[r]}</t></is></c>");
        sheet.Append("</row>");
    }

    sheet.Append("</sheetData></worksheet>");

    await WriteZipAsync(path, sheet.ToString(), includeSharedStrings: false);
}

/// <summary>
/// Sinh file .xlsx với ô cột A ở dạng <b>số</b> (Excel hay lưu serial dạng
/// number) — kiểm thử bộ đọc ép kiểu về chuỗi.
/// </summary>
static async Task WriteTestXlsxNumericAsync(string path, string[] values)
{
    var sheet = new StringBuilder();
    sheet.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
    sheet.Append("<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><sheetData>");
    for (var r = 0; r < values.Length; r++)
    {
        // Ghi serial dạng so (bo "SER-NUM-" de o so thuan).
        var numeric = values[r].Replace("SER-NUM-", string.Empty, StringComparison.Ordinal);
        sheet.Append($"<row r=\"{r + 1}\">");
        sheet.Append($"<c r=\"A{r + 1}\"><v>{numeric}</v></c>");
        sheet.Append("</row>");
    }

    sheet.Append("</sheetData></worksheet>");

    await WriteZipAsync(path, sheet.ToString(), includeSharedStrings: false);
}

/// <summary>Gói một sheet XML thành file .xlsx hợp lệ.</summary>
static async Task WriteZipAsync(
    string path,
    string sheetXml,
    bool includeSharedStrings)
{
    var tmp = path + ".tmp";
    if (File.Exists(tmp))
    {
        File.Delete(tmp);
    }

    using (var fs = File.Create(tmp))
    using (var zip = new System.IO.Compression.ZipArchive(
        fs, System.IO.Compression.ZipArchiveMode.Create))
    {
        void AddEntry(string name, string content)
        {
            var entry = zip.CreateEntry(name);
            using var w = new StreamWriter(entry.Open(), new UTF8Encoding(false));
            w.Write(content);
        }

        var contentTypes = new StringBuilder();
        contentTypes.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
        contentTypes.Append("<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">");
        contentTypes.Append("<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>");
        contentTypes.Append("<Default Extension=\"xml\" ContentType=\"application/xml\"/>");
        contentTypes.Append("<Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/>");
        contentTypes.Append("<Override PartName=\"/xl/worksheets/sheet1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/>");
        if (includeSharedStrings)
        {
            contentTypes.Append("<Override PartName=\"/xl/sharedStrings.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sharedStrings+xml\"/>");
        }

        contentTypes.Append("</Types>");
        AddEntry("[Content_Types].xml", contentTypes.ToString());

        AddEntry("_rels/.rels",
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>"
            + "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">"
            + "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/>"
            + "</Relationships>");

        AddEntry("xl/workbook.xml",
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>"
            + "<workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" "
            + "xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\">"
            + "<sheets><sheet name=\"Sheet1\" sheetId=\"1\" r:id=\"rId1\"/></sheets></workbook>");

        var rels = new StringBuilder();
        rels.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
        rels.Append("<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">");
        rels.Append("<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet1.xml\"/>");
        rels.Append("</Relationships>");
        AddEntry("xl/_rels/workbook.xml.rels", rels.ToString());

        AddEntry("xl/worksheets/sheet1.xml", sheetXml);
    }

    File.Move(tmp, path, overwrite: true);
    await Task.CompletedTask;
}


// Client cô lập dùng cho kiểm thử lifecycle: các PO đều đã được nhận qua HTTP.
file sealed class LocalOnlyPoClient : IPoClient
{
    public Task<IReadOnlyList<ProductionOrder>> GetOrdersAsync(bool includeCompleted = false, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<ProductionOrder>>([]);
    public Task<IReadOnlyList<PoCode>> GetCodesAsync(string orderNo, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<PoCode>>([]);
    public Task<PoActivateResult> ActivateAsync(string orderNo, IReadOnlyList<string> serials, CancellationToken ct = default) =>
        Task.FromResult(new PoActivateResult { Ok = true, Activated = serials.Count });
    public Task<ProductionOrder?> CreateOrderAsync(string orderNo, int orderQty, CancellationToken ct = default) =>
        Task.FromResult<ProductionOrder?>(null);
}
