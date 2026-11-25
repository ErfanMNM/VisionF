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
void Check(bool condition, string label)
{
    if (!condition) throw new Exception(label);
    Console.WriteLine("[OK] " + label);
    passed++;
}
try
{
    // A prior console DB must remain readable, including activated records.
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
                VALUES('OLD','https://old/full/code','legacy','suffix','2026-11-02T00:00:00.0000000Z');
            """;
        command.ExecuteNonQuery();
    }
    var migrated = new PoServerStore(migratedPath);
    Check(migrated.GetCodes("OLD") is [{ CodeContent: "https://old/full/code", ActivatedUtc: not null }],
        "Legacy migration preserves whole code and activation timestamp");
    Check(migrated.ImportOrder("NEW", 1, ["https://old/full/code"]).Accepted == 1,
        "Migrated schema permits the same exact code in another PO");
    Check(migrated.GetCodes("OLD").Single().ActivatedUtc is not null
        && migrated.GetCodes("NEW").Single().ActivatedUtc is null, "Migration keeps independent per-PO state");
    var restarted = new PoServerStore(migratedPath);
    Check(restarted.ListOrders(true).Count == 2, "Migration is idempotent after restart");

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
        string[] codes = ["https://first/21/SAME", "https://second/21/SAME", "Case", "case", "  mã đầy đủ,quoted \"value\"\r\nnext  "];
        var csv = "CodeContent\r\n" + string.Join("\r\n", codes.Select(value => "\"" + value.Replace("\"", "\"\"") + "\"")) + "\r\n";
        using (var form = new MultipartFormDataContent())
        {
            form.Add(new StringContent("PO-ONE"), "poName");
            form.Add(new StringContent(codes.Length.ToString()), "orderQty");
            form.Add(new StringContent("LOT-A"), "batchCode");
            form.Add(new StringContent(csv, Encoding.UTF8, "text/csv"), "file", "codes.csv");
            using var result = await http.PostAsync("api/po", form);
            Check(result.IsSuccessStatusCode, "Console imports strict multipart CodeContent CSV");
        }
        var snapshot = await client.GetCodesAsync("PO-ONE");
        Check(snapshot.Select(code => code.CodeContent).ToHashSet(StringComparer.Ordinal).SetEquals(codes),
            "GET via production PoClient preserves Unicode, whitespace, newline, suffix and case");
        Check((await client.GetOrdersAsync()).Single().Additional.GetValueOrDefault("batchCode") == "LOT-A",
            "GET PO snapshot retains descriptive metadata");
        using (var result = await http.PostAsync("api/po?poName=PO-TWO&orderQty=2",
            new StringContent("[{\"CodeContent\":\"Case\"},{\"CodeContent\":\"case\"}]", Encoding.UTF8, "application/json")))
            Check(result.IsSuccessStatusCode, "Same exact codes import independently in another PO");
        using (var result = await http.PostAsync("api/po?poName=po-two&orderQty=3",
            new StringContent("CodeContent\nCase\nnew\nnew\n", Encoding.UTF8, "text/csv")))
        {
            using var body = JsonDocument.Parse(await result.Content.ReadAsStringAsync());
            Check(result.IsSuccessStatusCode && body.RootElement.GetProperty("accepted").GetInt32() == 1
                && body.RootElement.GetProperty("ignoredDuplicates").GetInt32() == 2,
                "Same-PO merge dedupes exact rows before updating total quantity");
        }
        Check((await client.GetOrdersAsync()).Single(order => order.OrderNo == "PO-TWO").OrderQty == 3,
            "Merge retains canonical PO name and persisted unique quantity");

        foreach (var payload in new[]
        {
            "{\"orderNo\":\"LEGACY\",\"orderQty\":10}",
            "[\"legacy string row\"]",
            "[{\"CodeContent\":\"would-leak\"},{\"CodeContent\":\"\"}]",
            "[{\"CodeContent\":\"X\",\"serial\":\"S\"}]",
        })
        {
            using var result = await http.PostAsync("api/po?poName=BAD&orderQty=1",
                new StringContent(payload, Encoding.UTF8, "application/json"));
            Check(result.StatusCode == HttpStatusCode.BadRequest && server.Store.GetCodes("BAD").Count == 0,
                "Malformed/legacy import is rejected atomically: " + payload[..Math.Min(25, payload.Length)]);
        }
        using (var result = await http.PostAsync("api/po?poName=BAD-QTY&orderQty=1",
            new StringContent("CodeContent\nA\nB\n", Encoding.UTF8, "text/csv")))
            Check(result.StatusCode == HttpStatusCode.BadRequest && server.Store.GetCodes("BAD-QTY").Count == 0,
                "Exact row quantity is checked before DB publication");
        using (var result = await http.PostAsync("api/activate", new StringContent(
            "{\"orderNo\":\"PO-ONE\",\"serials\":[\"Case\"]}", Encoding.UTF8, "application/json")))
            Check(result.StatusCode == HttpStatusCode.BadRequest, "Legacy serial activation payload is rejected");
        using (var result = await http.PostAsync("api/activate", new StringContent(
            "{\"orderNo\":\"PO-ONE\",\"codeContents\":[\"Case\",\"unknown\"]}", Encoding.UTF8, "application/json")))
            Check(result.StatusCode == HttpStatusCode.BadRequest
                && server.Store.GetCodes("PO-ONE").All(code => code.ActivatedUtc is null),
                "Unknown code rejects the whole activation batch without partial ACK state");
        var activation = await client.ActivateAsync("PO-ONE", ["Case", "https://first/21/SAME"]);
        Check(activation.Ok && activation.Activated == 2, "Production PoClient activates exact whole CodeContents");
        Check(server.Store.GetCodes("PO-ONE").Where(code => code.ActivatedUtc is not null)
            .Select(code => code.CodeContent).ToHashSet(StringComparer.Ordinal).SetEquals(["Case", "https://first/21/SAME"]),
            "Activation leaves case-only and shared-suffix variants unchanged");
        Check(server.Store.GetCodes("PO-TWO").All(code => code.ActivatedUtc is null),
            "Activation does not affect another PO's identical content");
        Check((await client.ActivateAsync("PO-ONE", ["Case"])).Activated == 0,
            "Repeated activation is idempotent");

        // The store transaction must roll back new rows and the PO count together.
        using (var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path }.ToString()))
        {
            db.Open();
            using var command = db.CreateCommand();
            command.CommandText = "CREATE TRIGGER reject_bad BEFORE INSERT ON Codes WHEN NEW.Code='BAD' BEGIN SELECT RAISE(ABORT,'test reject'); END;";
            command.ExecuteNonQuery();
        }
        using (var result = await http.PostAsync("api/po?poName=PO-TWO&orderQty=2",
            new StringContent("CodeContent\nWOULD-LEAK\nBAD\n", Encoding.UTF8, "text/csv")))
            Check(result.StatusCode == HttpStatusCode.InternalServerError && server.Store.GetCodes("PO-TWO").Count == 3
                && server.Store.ListOrders(true).Single(order => order.OrderNo == "PO-TWO").OrderQty == 3,
                "SQLite rejection rolls back rows and order count");
        using (var result = await http.GetAsync("api/po/DOES-NOT-EXIST/codes"))
            Check(result.StatusCode == HttpStatusCode.NotFound, "Unknown PO snapshot returns 404");
    }
    var finalStore = new PoServerStore(path);
    Check(finalStore.GetCodes("PO-ONE").Count(code => code.ActivatedUtc is not null) == 2,
        "Activation acknowledgement survives server restart");
    Console.WriteLine($"Passed {passed} PO console checks.");
}
finally
{
    SqliteConnection.ClearAllPools();
    Directory.Delete(work, recursive: true);
}
