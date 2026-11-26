using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using ClassLibraryMT.Po;
using Microsoft.Data.Sqlite;
using PoServer;

var work = Path.Combine(Path.GetTempPath(), "visionf-po-console-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(work);
var passed = 0;
const string gtin = "00012345678905";
const string otherGtin = "09506000134352";
string Gs1(string serial) => $"(01){gtin}(21){serial}";
StringContent JsonCodes(params string[] codes) => new(JsonSerializer.Serialize(codes.Select(code => new { CodeContent = code })), Encoding.UTF8, "application/json");
void Check(bool condition, string label)
{
    if (!condition) throw new Exception(label);
    Console.WriteLine("[OK] " + label);
    passed++;
}
try
{
    // DB console cũ vẫn đọc được, giữ raw và thời điểm kích hoạt.
    var migratedPath = Path.Combine(work, "legacy.db");
    using (var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = migratedPath }.ToString()))
    {
        db.Open();
        using var command = db.CreateCommand();
        command.CommandText = """
            CREATE TABLE Orders(OrderNo TEXT NOT NULL PRIMARY KEY,OrderQty INTEGER NOT NULL DEFAULT 0,
                CreatedUtc TEXT NOT NULL,IssuedUtc TEXT NULL,CompletedUtc TEXT NULL,Status TEXT NOT NULL DEFAULT 'Available');
            CREATE TABLE Codes(OrderNo TEXT NOT NULL,Code TEXT NOT NULL PRIMARY KEY,Gtin TEXT NOT NULL DEFAULT '',
                Serial TEXT NOT NULL DEFAULT '',ActivatedUtc TEXT NULL);
            INSERT INTO Orders(OrderNo,OrderQty,CreatedUtc) VALUES('OLD',1,'2026-11-01T00:00:00.0000000Z');
            INSERT INTO Codes(OrderNo,Code,Gtin,Serial,ActivatedUtc)
                VALUES('OLD','(01)00012345678905(21)LEGACY','legacy','suffix','2026-11-02T00:00:00.0000000Z');
            INSERT INTO Orders(OrderNo,OrderQty,CreatedUtc) VALUES('OLD-BAD',1,'2026-11-01T00:00:00.0000000Z');
            INSERT INTO Codes(OrderNo,Code) VALUES('OLD-BAD','opaque legacy');
            """;
        command.ExecuteNonQuery();
    }
    var migrated = new PoServerStore(migratedPath);
    Check(migrated.GetCodes("OLD") is [{ CodeContent: "(01)00012345678905(21)LEGACY", ActivatedUtc: not null }],
        "Legacy migration preserves whole code and activation timestamp");
    Check(migrated.ListOrders(true).Single(order => order.OrderNo == "OLD").Gtin == gtin,
        "Snapshot legacy chỉ suy GTIN khi toàn bộ raw chứng minh cùng định danh");
    Check(!migrated.ImportOrder("OLD-BAD", 1, [Gs1("NEW")], gtin: gtin).Success
        && migrated.GetCodes("OLD-BAD") is [{ CodeContent: "opaque legacy" }],
        "Legacy opaque không được gán GTIN hoặc append âm thầm");
    Check(migrated.ImportOrder("NEW", 1, [Gs1("LEGACY")], gtin: gtin).Accepted == 1,
        "Migrated schema permits the same exact code in another PO");
    Check(migrated.GetCodes("OLD").Single().ActivatedUtc is not null
        && migrated.GetCodes("NEW").Single().ActivatedUtc is null, "Migration keeps independent per-PO state");
    var restarted = new PoServerStore(migratedPath);
    Check(restarted.ListOrders(true).Count == 3, "Migration is idempotent after restart");

    var probe = new TcpListener(IPAddress.Loopback, 0);
    probe.Start();
    var port = ((IPEndPoint)probe.LocalEndpoint).Port;
    probe.Stop();
    var prefix = $"http://127.0.0.1:{port}/";
    var path = Path.Combine(work, "http.db");
    await using (var server = new PoConsoleServer(prefix, path))
    using (var http = new HttpClient { BaseAddress = new Uri(prefix), Timeout = TimeSpan.FromSeconds(15) })
    using (var client = new PoClient(prefix))
    {
        server.Start();
        Check((await client.GetOrdersAsync()).Count == 0, "Empty server does not seed generated PO codes");
        string[] codes = [$"https://first.example/01/{gtin}/21/SAME", $"https://second.example/01/{gtin}/21/SAME", Gs1("Case"), Gs1("case"), Gs1("quoted,\"value\"")];
        var csv = "CodeContent\r\n" + string.Join("\r\n", codes.Select(value => "\"" + value.Replace("\"", "\"\"") + "\"")) + "\r\n";
        using (var form = new MultipartFormDataContent())
        {
            form.Add(new StringContent("PO-ONE"), "orderNo");
            form.Add(new StringContent(codes.Length.ToString()), "orderQty");
            form.Add(new StringContent(gtin), "gtin");
            form.Add(new StringContent("LOT-A"), "batchCode");
            form.Add(new StringContent(csv, Encoding.UTF8, "text/csv"), "file", "codes.csv");
            using var result = await http.PostAsync("api/po", form);
            Check(result.IsSuccessStatusCode, "Console imports strict multipart CodeContent CSV");
        }
        var snapshot = await client.GetCodesAsync("PO-ONE");
        Check(snapshot.Select(code => code.CodeContent).ToHashSet(StringComparer.Ordinal).SetEquals(codes),
            "GET qua PoClient giữ raw GS1, quote, comma, host, suffix và phân biệt hoa thường");
        Check((await client.GetOrdersAsync()).Single().Additional.GetValueOrDefault("batchCode") == "LOT-A",
            "GET PO snapshot retains descriptive metadata");
        Check((await client.GetOrdersAsync()).Single().Gtin == gtin, "Snapshot trả GTIN đã chuẩn hóa");
        using (var result = await http.PostAsync($"api/po?orderNo=PO-DUPE-PLANNED&orderQty=1&gtin={gtin}", JsonCodes(Gs1("DUPLICATE"))))
            Check(result.IsSuccessStatusCode, "Tạo PO planned=1 cho kiểm tra all-duplicate");
        var beforeDuplicate = server.Store.ListOrders(true).Single(order => order.OrderNo == "PO-DUPE-PLANNED");
        using (var result = await http.PostAsync($"api/po?orderNo=PO-DUPE-PLANNED&orderQty=4&gtin={gtin}",
            JsonCodes(Gs1("DUPLICATE"), Gs1("DUPLICATE"), Gs1("DUPLICATE"), Gs1("DUPLICATE"))))
        {
            using var body = JsonDocument.Parse(await result.Content.ReadAsStringAsync());
            Check(result.IsSuccessStatusCode && body.RootElement.GetProperty("acceptedCount").GetInt32() == 0
                && body.RootElement.GetProperty("plannedCount").GetInt32() == 4
                && body.RootElement.GetProperty("duplicateCodes").GetArrayLength() == 4,
                "All-duplicate trả accepted=0 và planned mới bằng 4");
        }
        var afterDuplicate = new PoServerStore(path).ListOrders(true).Single(order => order.OrderNo == "PO-DUPE-PLANNED");
        Check(afterDuplicate.OrderQty == 4 && afterDuplicate.CreatedUtc == beforeDuplicate.CreatedUtc && afterDuplicate.Gtin == beforeDuplicate.Gtin
            && server.Store.GetCodes("PO-DUPE-PLANNED") is [{ CodeContent: "(01)00012345678905(21)DUPLICATE", ActivatedUtc: null }],
            "All-duplicate persist planned max sau reload, giữ nguyên mã và trạng thái kích hoạt");
        using (var result = await http.PostAsync($"api/po?poName=PO-TWO&orderQty=2&gtin={gtin}", JsonCodes(Gs1("Case"), Gs1("case"))))
            Check(result.IsSuccessStatusCode, "Same exact codes import independently in another PO");
        using (var result = await http.PostAsync($"api/po?poName=po-two&orderQty=3&gtin={gtin}", JsonCodes(Gs1("Case"), Gs1("new"), Gs1("new"))))
        {
            using var body = JsonDocument.Parse(await result.Content.ReadAsStringAsync());
            Check(result.IsSuccessStatusCode && body.RootElement.GetProperty("accepted").GetInt32() == 1
                && body.RootElement.GetProperty("ignoredDuplicates").GetInt32() == 2,
                "Same-PO merge lọc trùng ordinal trước khi lưu planned");
        }
        Check((await client.GetOrdersAsync()).Single(order => order.OrderNo == "PO-TWO").OrderQty == 3,
            "Merge giữ tên PO canonical và planned lớn nhất");

        const string invalidRaw = "  mã sai,\"raw\"\r\nnext  ";
        var partialCodes = new[] { Gs1("PARTIAL"), Gs1("PARTIAL"), invalidRaw, $"(01){otherGtin}(21)OTHER" };
        using (var result = await http.PostAsync($"api/po?orderNo=PO-PARTIAL&orderQty=4&gtin={gtin}", JsonCodes(partialCodes)))
        {
            using var body = JsonDocument.Parse(await result.Content.ReadAsStringAsync());
            var root = body.RootElement;
            Check(result.IsSuccessStatusCode && root.GetProperty("acceptedCount").GetInt32() == 1
                && root.GetProperty("plannedCount").GetInt32() == 4 && root.GetProperty("duplicateCodes").GetArrayLength() == 1
                && root.GetProperty("invalidCodes").GetArrayLength() == 2, "Partial import phân loại accepted/planned/invalid/duplicate");
            Check(root.GetProperty("invalidCodes")[0].GetProperty("code").GetString() == invalidRaw,
                "Response mã bị loại giữ nguyên Unicode, quote, whitespace và CRLF");
        }
        var partialOrder = (await client.GetOrdersAsync()).Single(order => order.OrderNo == "PO-PARTIAL");
        Check(partialOrder.OrderQty == 4 && partialOrder.Gtin == gtin && (await client.GetCodesAsync("PO-PARTIAL")).Count == 1,
            "Snapshot accepted-only giữ planned=4 khác actual=1 và GTIN");
        var localRoot = Path.Combine(work, "download");
        var localStore = new PoStore(localRoot);
        var service = new ProductionOrderService(client, localStore, new PoHistoryStore(localRoot), new PoCodeCache());
        var sync = await service.SyncFromServerAsync();
        var partialSync = sync.Orders.Single(order => order.OrderNo == "PO-PARTIAL");
        Check(partialSync.ValidationError is null && partialSync.InputCount == 1 && partialSync.AcceptedCount == 1 && partialSync.PlannedCount == 4,
            "Download chấp nhận snapshot nguồn partial mà không bịa các mã nguồn đã loại");
        var reloadedPartial = (await new PoStore(localRoot).ListLocalOrdersAsync()).Single(order => order.OrderNo == "PO-PARTIAL");
        Check(reloadedPartial.Gtin == gtin && reloadedPartial.OrderQty == 4 && reloadedPartial.TotalCodes == 1,
            "Download reload giữ GTIN/planned/actual độc lập");
        using (var result = await http.PostAsync($"api/po?orderNo=PO-INVALID&orderQty=1&gtin={gtin}", JsonCodes(invalidRaw)))
            Check(result.StatusCode == HttpStatusCode.UnprocessableEntity && server.Store.ListOrders(true).All(order => order.OrderNo != "PO-INVALID"),
                "All-invalid trả 422 và không tạo PO");
        using (var result = await http.PostAsync("api/po?orderNo=PO-NOGTIN&orderQty=1", JsonCodes(Gs1("NOGTIN"))))
            Check(result.StatusCode == HttpStatusCode.UnprocessableEntity && server.Store.GetCodes("PO-NOGTIN").Count == 0,
                "Metadata thiếu GTIN trả 422 không mutation");
        using (var result = await http.PostAsync($"api/po?orderNo=PO-PARTIAL&orderQty=1&gtin={otherGtin}", JsonCodes($"(01){otherGtin}(21)NO-MIX")))
            Check(result.StatusCode == HttpStatusCode.UnprocessableEntity && server.Store.GetCodes("PO-PARTIAL").Count == 1,
                "Không cho append GTIN khác vào PO đã lưu");

        foreach (var payload in new[]
        {
            "{\"orderNo\":\"LEGACY\",\"orderQty\":10}",
            "[\"legacy string row\"]",
            "[{\"CodeContent\":\"would-leak\"},{\"CodeContent\":\"\"}]",
            "[{\"CodeContent\":\"X\",\"serial\":\"S\"}]",
        })
        {
            using var result = await http.PostAsync($"api/po?poName=BAD&orderQty=1&gtin={gtin}",
                new StringContent(payload, Encoding.UTF8, "application/json"));
            Check(result.StatusCode == HttpStatusCode.BadRequest && server.Store.GetCodes("BAD").Count == 0,
                "Malformed/legacy import is rejected atomically: " + payload[..Math.Min(25, payload.Length)]);
        }
        using (var result = await http.PostAsync($"api/po?poName=BAD-QTY&orderQty=1&gtin={gtin}",
            new StringContent("CodeContent\nA\nB\n", Encoding.UTF8, "text/csv")))
            Check(result.StatusCode == HttpStatusCode.BadRequest && server.Store.GetCodes("BAD-QTY").Count == 0,
                "Exact row quantity is checked before DB publication");
        using (var result = await http.PostAsync("api/activate", new StringContent(
            "{\"orderNo\":\"PO-ONE\",\"serials\":[\"Case\"]}", Encoding.UTF8, "application/json")))
            Check(result.StatusCode == HttpStatusCode.BadRequest, "Legacy serial activation payload is rejected");
        using (var result = await http.PostAsync("api/activate", new StringContent(
            JsonSerializer.Serialize(new { orderNo = "PO-ONE", codeContents = new[] { Gs1("Case"), "unknown" } }), Encoding.UTF8, "application/json")))
            Check(result.StatusCode == HttpStatusCode.BadRequest
                && server.Store.GetCodes("PO-ONE").All(code => code.ActivatedUtc is null),
                "Unknown code rejects the whole activation batch without partial ACK state");
        using var activationResponse = await http.PostAsync("api/activate", new StringContent(
            JsonSerializer.Serialize(new { orderNo = "PO-ONE", codeContents = new[] { Gs1("Case"), codes[0] } }), Encoding.UTF8, "application/json"));
        var activation = JsonSerializer.Deserialize<PoActivateResult>(await activationResponse.Content.ReadAsStringAsync(), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        Check(activation.Ok && activation.Activated == 2, "Standalone demo server activates exact whole CodeContents");
        Check(server.Store.GetCodes("PO-ONE").Where(code => code.ActivatedUtc is not null)
            .Select(code => code.CodeContent).ToHashSet(StringComparer.Ordinal).SetEquals([Gs1("Case"), codes[0]]),
            "Activation leaves case-only and shared-suffix variants unchanged");
        Check(server.Store.GetCodes("PO-TWO").All(code => code.ActivatedUtc is null),
            "Activation does not affect another PO's identical content");
        using var repeatedActivation = await http.PostAsync("api/activate", new StringContent(
            JsonSerializer.Serialize(new { orderNo = "PO-ONE", codeContents = new[] { Gs1("Case") } }), Encoding.UTF8, "application/json"));
        var repeated = JsonSerializer.Deserialize<PoActivateResult>(await repeatedActivation.Content.ReadAsStringAsync(), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        Check(repeated.Activated == 0, "Standalone demo server activation is idempotent");

        // Transaction rollback cả mã mới và metadata/planned của PO.
        using (var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path }.ToString()))
        {
            db.Open();
            using var command = db.CreateCommand();
            command.CommandText = $"CREATE TRIGGER reject_bad BEFORE INSERT ON Codes WHEN NEW.Code='{Gs1("BAD")}' BEGIN SELECT RAISE(ABORT,'test reject'); END;";
            command.ExecuteNonQuery();
        }
        using (var result = await http.PostAsync($"api/po?poName=PO-TWO&orderQty=4&gtin={gtin}", JsonCodes(Gs1("WOULD-LEAK"), Gs1("BAD"), Gs1("LATER"), Gs1("LAST"))))
            Check(result.StatusCode == HttpStatusCode.InternalServerError && server.Store.GetCodes("PO-TWO").Count == 3
                && server.Store.ListOrders(true).Single(order => order.OrderNo == "PO-TWO").OrderQty == 3,
                "SQLite rejection rolls back rows and order count");
        using (var result = await http.GetAsync("api/po/DOES-NOT-EXIST/codes"))
            Check(result.StatusCode == HttpStatusCode.NotFound, "Unknown PO snapshot returns 404");
    }
    var finalStore = new PoServerStore(path);
    Check(finalStore.ListOrders(true).Single(order => order.OrderNo == "PO-PARTIAL") is { OrderQty: 4, Gtin: gtin },
        "Console restart giữ GTIN và planned của partial PO");
    Check(finalStore.GetCodes("PO-ONE").Count(code => code.ActivatedUtc is not null) == 2,
        "Activation acknowledgement survives server restart");
    Console.WriteLine($"Passed {passed} PO console checks.");
}
finally
{
    SqliteConnection.ClearAllPools();
    Directory.Delete(work, recursive: true);
}
