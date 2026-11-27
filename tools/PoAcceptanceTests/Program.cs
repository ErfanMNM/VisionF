using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using ClassLibraryMT.Po;
using ClassLibraryMT.PoServer;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

const string gtinA = "00012345678905";
const string gtinB = "09506000134352";
const string valid = "https://id.example.invalid/01/00012345678905/21/AbC-raw_01?case=X%2fy&n=1,2";
const string invalid = "  không-phải-GS1,\"quoted\"  ";
const string mismatch = "https://id.example.invalid/01/09506000134352/21/OTHER";
string[] mixed = [valid, valid, invalid, mismatch];
var root = Path.Combine(Path.GetTempPath(), "TEST-ASYNC-PO-20261127-Acceptance-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var assertions = 0;
var failures = new List<string>();

void Check(string name, bool condition, string detail = "")
{
    assertions++;
    if (!condition) throw new InvalidOperationException(name + (detail.Length == 0 ? "" : ": " + Fixture.Redact(detail)));
    Console.WriteLine("[ĐẠT] " + name);
}

async Task RunAsync(string name, Func<Task> run)
{
    try { await run(); }
    catch (Exception ex)
    {
        failures.Add(name + ": " + Fixture.Redact(ex.Message));
        Console.Error.WriteLine("[LỖI] " + failures[^1]);
    }
}

async Task AssertStoredAsync(PoStore store, string orderNo, int planned, string gtin, params string[] codes)
{
    var order = (await store.ListLocalOrdersAsync(strict: true)).Single(po => po.OrderNo == orderNo);
    Check(orderNo + " giữ orderQty planned sau reload", order.OrderQty == planned,
        $"planned={planned}, thực tế orderQty={order.OrderQty}");
    Check(orderNo + " tách số mã thực nhận", order.TotalCodes == codes.Length,
        $"mong đợi {codes.Length}, thực tế {order.TotalCodes}");
    var actual = await store.GetCodesAsync(order.OrderNo, order.CreatedUtc);
    Check(orderNo + " giữ raw CodeContent ordinal", actual.Select(code => code.CodeContent)
        .Order(StringComparer.Ordinal).SequenceEqual(codes.Order(StringComparer.Ordinal), StringComparer.Ordinal));
    var meta = await store.GetMetaAsync(order.OrderNo, order.CreatedUtc);
    var storedGtin = meta.GetValueOrDefault("gtin");
    if (storedGtin is null && meta.TryGetValue(PoStore.MetaAdditional, out var additional))
    {
        using var json = JsonDocument.Parse(additional);
        if (json.RootElement.TryGetProperty("gtin", out var value)) storedGtin = value.GetString();
    }
    Check(orderNo + " giữ nguyên GTIN metadata sau reload", string.Equals(storedGtin, gtin, StringComparison.Ordinal)
        && string.Equals(order.Gtin, gtin, StringComparison.Ordinal), "GTIN lưu hoặc GTIN đọc lại khác metadata đã nhập");
    if (order.Additional.TryGetValue("gtin", out var additionalGtin))
        Check(orderNo + " giữ nguyên GTIN trong metadata mở rộng", string.Equals(additionalGtin, gtin, StringComparison.Ordinal));
}

void AssertMixed(string name, WireResponse response)
{
    Check(name + " trả HTTP 200 cho file hợp lệ một phần", response.Status == HttpStatusCode.OK, response.Body);
    using var body = JsonDocument.Parse(response.Body);
    var json = body.RootElement;
    Check(name + " báo success", json.GetProperty("success").GetBoolean(), response.Body);
    Check(name + " báo acceptedCount=1 và plannedCount=4", json.GetProperty("acceptedCount").GetInt32() == 1
        && json.GetProperty("plannedCount").GetInt32() == 4, response.Body);
    Check(name + " giữ alias accepted tương thích", json.GetProperty("accepted").GetInt32() == 1, response.Body);
    var rejected = json.GetProperty("invalidCodes").EnumerateArray().ToArray();
    Check(name + " phân loại invalidStructure và giữ raw", rejected.Count(item => item.GetProperty("reason").GetString() == "invalidStructure"
        && item.GetProperty("code").GetString() == invalid) == 1, response.Body);
    Check(name + " phân loại gtinMismatch và giữ raw", rejected.Count(item => item.GetProperty("reason").GetString() == "gtinMismatch"
        && item.GetProperty("code").GetString() == mismatch) == 1 && rejected.Length == 2, response.Body);
    Check(name + " trả duplicateCodes đúng raw", json.GetProperty("duplicateCodes").EnumerateArray()
        .Select(item => item.GetString()).SequenceEqual([valid], StringComparer.Ordinal), response.Body);
    Check(name + " giữ alias ignoredDuplicates tương thích", json.GetProperty("ignoredDuplicates").GetInt32() == 1, response.Body);
}

try
{
    foreach (var format in new[] { "csv", "json", "xlsx" })
    {
        await RunAsync("Nhận và lưu " + format, async () =>
        {
            var path = Path.Combine(root, format);
            var orderNo = "TEST-ASYNC-PO-20261127-MIXED-" + format.ToUpperInvariant();
            using (var server = new Fixture(path))
            {
                var response = await server.UploadAsync(orderNo, 4, gtinA, mixed, format);
                AssertMixed(format, response);
                using var json = JsonDocument.Parse(response.Body);
                Check(format + " phản hồi orderNo và poName tương thích", json.RootElement.GetProperty("orderNo").GetString() == orderNo
                    && json.RootElement.GetProperty("poName").GetString() == orderNo, response.Body);
            }

            using var restarted = new Fixture(path);
            await AssertStoredAsync(restarted.Store, orderNo, 4, gtinA, valid);
            var other = await restarted.UploadAsync(orderNo, 1, gtinB, [mismatch]);
            Check(format + " từ chối GTIN khác sau restart", (int)other.Status is >= 400 and < 500, other.Body);
            await AssertStoredAsync(new PoStore(restarted.Store.DbRoot), orderNo, 4, gtinA, valid);
            var replay = await restarted.UploadAsync(orderNo, 4, gtinA, mixed, format);
            Check(format + " replay được xử lý theo hợp đồng lọc", replay.Status is HttpStatusCode.OK or HttpStatusCode.UnprocessableEntity, replay.Body);
            using var replayJson = JsonDocument.Parse(replay.Body);
            Check(format + " replay báo acceptedCount=0", replayJson.RootElement.GetProperty("acceptedCount").GetInt32() == 0, replay.Body);
            await AssertStoredAsync(new PoStore(restarted.Store.DbRoot), orderNo, 4, gtinA, valid);
        });
    }

    await RunAsync("Metadata và kiểm tra nguyên tử", async () =>
    {
        using var server = new Fixture(Path.Combine(root, "metadata"));
        var missing = await server.UploadAsync("TEST-ASYNC-PO-20261127-NO-GTIN", 1, null, [valid]);
        Check("Thiếu GTIN bị từ chối và không tạo DB", (int)missing.Status is >= 400 and < 500
            && server.Store.FindExistingDbPath("TEST-ASYNC-PO-20261127-NO-GTIN") is null, missing.Body);
        foreach (var (value, suffix) in new[] { ("00012345678904", "CHECKSUM"), (gtinA + "," + gtinB, "MULTI") })
        {
            var bad = await server.UploadAsync("TEST-ASYNC-PO-20261127-BAD-" + suffix, 1, value, [valid]);
            Check("GTIN " + suffix + " bị từ chối và không tạo DB", (int)bad.Status is >= 400 and < 500
                && server.Store.FindExistingDbPath("TEST-ASYNC-PO-20261127-BAD-" + suffix) is null, bad.Body);
        }
        foreach (var planned in new[] { 1, 3 })
        {
            var bad = await server.UploadAsync("TEST-ASYNC-PO-20261127-QTY-" + planned, planned, gtinA, [valid, valid]);
            Check("Planned " + planned + " khác record count bị từ chối nguyên tử", bad.Status == HttpStatusCode.BadRequest
                && server.Store.FindExistingDbPath("TEST-ASYNC-PO-20261127-QTY-" + planned) is null, bad.Body);
        }
        var seed = await server.UploadAsync("TEST-ASYNC-PO-20261127-ATOMIC", 4, gtinA, mixed);
        AssertMixed("TEST-ASYNC-PO-20261127-ATOMIC", seed);
        var append = await server.UploadAsync("TEST-ASYNC-PO-20261127-ATOMIC", 3, gtinA,
            ["https://id.example.invalid/01/00012345678905/21/MUST-NOT-BE-SAVED", valid]);
        Check("Planned mismatch không sửa PO đã tồn tại", append.Status == HttpStatusCode.BadRequest, append.Body);
        await AssertStoredAsync(new PoStore(server.Store.DbRoot), "TEST-ASYNC-PO-20261127-ATOMIC", 4, gtinA, valid);

        var alias = await server.UploadAsync("TEST-ASYNC-PO-20261127-LEGACY-ALIAS", 1, gtinA, [valid], orderField: "poName");
        Check("Alias poName còn được nhận", alias.Status == HttpStatusCode.OK, alias.Body);
        await AssertStoredAsync(new PoStore(server.Store.DbRoot), "TEST-ASYNC-PO-20261127-LEGACY-ALIAS", 1, gtinA, valid);
        using (var duplicateAlias = Fixture.Form("TEST-ASYNC-PO-20261127-BOTH-NAMES", 1, gtinA, [valid], "csv", "orderNo"))
        {
            duplicateAlias.Add(new StringContent("TEST-ASYNC-PO-20261127-BOTH-NAMES"), "poName");
            var bad = await server.SendAsync(duplicateAlias);
            Check("Gửi cả orderNo và poName bị từ chối nguyên tử", bad.Status == HttpStatusCode.BadRequest
                && server.Store.FindExistingDbPath("TEST-ASYNC-PO-20261127-BOTH-NAMES") is null, bad.Body);
        }
        var invalidOnly = await server.UploadAsync("TEST-ASYNC-PO-20261127-INVALID-ONLY", 1, gtinA, [invalid]);
        Check("Toàn mã sai trả 422 và không tạo DB", invalidOnly.Status == HttpStatusCode.UnprocessableEntity
            && server.Store.FindExistingDbPath("TEST-ASYNC-PO-20261127-INVALID-ONLY") is null, invalidOnly.Body);
        using (var json = JsonDocument.Parse(invalidOnly.Body))
            Check("Toàn mã sai vẫn trả lý do trên wire", json.RootElement.GetProperty("acceptedCount").GetInt32() == 0
                && json.RootElement.GetProperty("plannedCount").GetInt32() == 1
                && json.RootElement.GetProperty("invalidCodes")[0].GetProperty("reason").GetString() == "invalidStructure", invalidOnly.Body);
        var duplicateOnly = await server.UploadAsync("TEST-ASYNC-PO-20261127-LEGACY-ALIAS", 1, gtinA, [valid]);
        Check("Toàn mã trùng trả 200", duplicateOnly.Status == HttpStatusCode.OK, duplicateOnly.Body);
        using (var json = JsonDocument.Parse(duplicateOnly.Body))
            Check("Toàn mã trùng có acceptedCount=0 và raw duplicate", json.RootElement.GetProperty("acceptedCount").GetInt32() == 0
                && json.RootElement.GetProperty("duplicateCodes").EnumerateArray().Select(item => item.GetString())
                    .SequenceEqual([valid], StringComparer.Ordinal), duplicateOnly.Body);
    });

    await RunAsync("Giữ nguyên GTIN metadata và nhận dạng ordinal", async () =>
    {
        using var server = new Fixture(Path.Combine(root, "gtin-preservation"));
        foreach (var (metadata, matchingGtin14) in new[]
        {
            ("96385074", "00000096385074"),
            ("012345678905", gtinA),
            ("0012345678905", gtinA),
            ("8935348800215", "08935348800215"),
            (gtinA, gtinA),
        })
        {
            var code = "https://id.example.invalid/01/" + matchingGtin14 + "/21/GTIN" + metadata.Length;
            var orderNo = "TEST-ASYNC-PO-20261127-GTIN-" + metadata;
            var response = await server.UploadAsync(orderNo, 1, metadata, [code]);
            Check("Metadata GTIN-" + metadata.Length + " khớp mã GTIN-14", response.Status == HttpStatusCode.OK, response.Body);
            await AssertStoredAsync(new PoStore(server.Store.DbRoot), orderNo, 1, metadata, code);
        }
        string[] ordinal =
        [
            "https://id.example.invalid/01/00012345678905/21/Case",
            "https://id.example.invalid/01/00012345678905/21/case",
            "https://other.example.invalid/01/00012345678905/21/Case",
        ];
        var distinct = await server.UploadAsync("TEST-ASYNC-PO-20261127-ORDINAL", 3, gtinA, ordinal);
        Check("Khác case hoặc URL là các CodeContent riêng", distinct.Status == HttpStatusCode.OK, distinct.Body);
        await AssertStoredAsync(new PoStore(server.Store.DbRoot), "TEST-ASYNC-PO-20261127-ORDINAL", 3, gtinA, ordinal);
        const string repeatedAi = "https://id.example.invalid/01/00012345678905/21/A/01/09506000134352/21/B";
        var ambiguous = await server.UploadAsync("TEST-ASYNC-PO-20261127-REPEATED-AI", 1, gtinB, [repeatedAi]);
        Check("Chuỗi chứa hai GTIN AI (01) bị loại", ambiguous.Status == HttpStatusCode.UnprocessableEntity
            && server.Store.FindExistingDbPath("TEST-ASYNC-PO-20261127-REPEATED-AI") is null, ambiguous.Body);
        using (var json = JsonDocument.Parse(ambiguous.Body))
            Check("Hai GTIN AI (01) được báo invalidStructure và giữ raw", json.RootElement.GetProperty("invalidCodes")[0]
                .GetProperty("reason").GetString() == "invalidStructure" && json.RootElement.GetProperty("invalidCodes")[0]
                .GetProperty("code").GetString() == repeatedAi, ambiguous.Body);
    });

    await RunAsync("Static cùng origin và API key", async () =>
    {
        var path = Path.Combine(root, "static-security");
        Directory.CreateDirectory(Path.Combine(path, "docs", "po-demo"));
        const string sentinel = "PRIVATE-DISK-CONTENT-MUST-NOT-BE-SERVED";
        await File.WriteAllTextAsync(Path.Combine(path, "docs", "po-demo", "index.html"), sentinel);
        await File.WriteAllTextAsync(Path.Combine(path, "server-private-marker.txt"), sentinel);
        var originalDirectory = Directory.GetCurrentDirectory();
        try
        {
            Directory.SetCurrentDirectory(path);
            using var server = new Fixture(path);
            using var noAuth = new HttpClient { BaseAddress = server.Http.BaseAddress, Timeout = TimeSpan.FromSeconds(15) };
            using (var response = await noAuth.GetAsync("po-demo/"))
            {
                var html = await response.Content.ReadAsStringAsync();
                Check("GET /po-demo/ trên loopback không cần key", response.StatusCode == HttpStatusCode.OK, html);
                Check("Demo trả HTML embedded thay vì file cùng working directory", response.Content.Headers.ContentType?.MediaType == "text/html"
                    && html.Contains("<html", StringComparison.OrdinalIgnoreCase) && !html.Contains(sentinel, StringComparison.Ordinal), html);
                Check("HTML không nhúng API key hoặc đường dẫn temp", !html.Contains(Fixture.ApiKey, StringComparison.Ordinal)
                    && !html.Contains(path, StringComparison.OrdinalIgnoreCase));
                Check("HTML tham chiếu đúng asset runtime validation.js", html.Contains("src=\"./validation.js\"", StringComparison.Ordinal));
            }
            using (var asset = await noAuth.GetAsync("po-demo/validation.js"))
            {
                var script = await asset.Content.ReadAsStringAsync();
                Check("Asset validation.js được phục vụ GET cùng origin không cần key", asset.StatusCode == HttpStatusCode.OK
                    && asset.Content.Headers.ContentType?.MediaType is "text/javascript" or "application/javascript", script);
                Check("Asset runtime chứa hàm checksum và không nhúng key", script.Contains("hasValidGtin", StringComparison.Ordinal)
                    && !script.Contains(Fixture.ApiKey, StringComparison.Ordinal) && !script.Contains(sentinel, StringComparison.Ordinal));
            }
            foreach (var key in new string?[] { null, "gia-tri-sai" })
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, "api/po");
                if (key is not null) request.Headers.Add("x-api-key", key);
                request.Content = Fixture.Form("TEST-ASYNC-PO-20261127-NO-AUTH", 1, gtinA, [valid], "csv", "orderNo");
                using var denied = await noAuth.SendAsync(request);
                Check(key is null ? "API thiếu key trả 401" : "API sai key trả 401", denied.StatusCode == HttpStatusCode.Unauthorized);
            }
            Check("Request không có quyền không tạo DB", server.Store.FindExistingDbPath("TEST-ASYNC-PO-20261127-NO-AUTH") is null);
            var accepted = await server.UploadAsync("TEST-ASYNC-PO-20261127-AUTH", 1, gtinA, [valid]);
            Check("API đúng key tiếp tục hoạt động cùng origin", accepted.Status == HttpStatusCode.OK, accepted.Body);

            foreach (var resource in new[] { "po-demo/", "po-demo/validation.js" })
            foreach (var method in new[] { HttpMethod.Post, HttpMethod.Put, HttpMethod.Head, HttpMethod.Delete })
            {
                using var request = new HttpRequestMessage(method, resource);
                using var response = await server.Http.SendAsync(request);
                Check(method + " không được trả static " + resource, (int)response.StatusCode >= 400
                    && response.Content.Headers.ContentType?.MediaType is not ("text/html" or "text/javascript" or "application/javascript"));
            }
            foreach (var target in new[]
            {
                "/po-demo/not-an-asset", "/po-demo/appsettings.json", "/po-demo/../server-private-marker.txt",
                "/po-demo/%2e%2e/server-private-marker.txt", "/po-demo/%2e%2e%2fserver-private-marker.txt",
                "/po-demo/%2e%2e%5cserver-private-marker.txt", "/po-demo//index.html",
                "/po-demo/validation.test.mjs", "/po-demo/README.md",
            })
            {
                var raw = await server.RawGetAsync(target);
                Check("Đường dẫn lạ không lộ đĩa: " + target, raw.Status >= 400 && !raw.Text.Contains(sentinel, StringComparison.Ordinal)
                    && !raw.Text.Contains(Fixture.ApiKey, StringComparison.Ordinal) && !raw.Text.Contains("Content-Type: text/html", StringComparison.OrdinalIgnoreCase), raw.Text);
            }
        }
        finally { Directory.SetCurrentDirectory(originalDirectory); }
    });
}
finally
{
    SqliteConnection.ClearAllPools();
}

Console.WriteLine($"Đã thực hiện {assertions} assertion; nhóm lỗi: {failures.Count}.");
Console.WriteLine("Thư mục DB/tệp tạm để chẩn đoán: " + root);
if (failures.Count > 0)
{
    Console.Error.WriteLine("Chưa đạt nghiệm thu. Xem nhóm lỗi ở trên; không suy ra các case sau assertion lỗi đã chạy.");
    Environment.ExitCode = 1;
}

sealed record WireResponse(HttpStatusCode Status, string Body);

sealed class Fixture : IDisposable
{
    // Đây là chuỗi giả lập chỉ dùng trong tiến trình kiểm thử local.
    internal const string ApiKey = "visionf-acceptance-dummy-key";
    public PoStore Store { get; }
    public PoHttpServer Server { get; }
    public HttpClient Http { get; }
    private readonly int _port;

    public Fixture(string root)
    {
        Directory.CreateDirectory(root);
        Store = new PoStore(Path.Combine(root, "po"));
        using (var probe = new TcpListener(IPAddress.Loopback, 0))
        {
            probe.Start();
            _port = ((IPEndPoint)probe.LocalEndpoint).Port;
        }
        Server = new PoHttpServer(Options.Create(new PoServerOptions
        {
            Enabled = true, BindAddress = "127.0.0.1", Port = _port, ApiKey = ApiKey,
            UploadTempDirectory = root, RequestTimeoutSeconds = 15, MaxConcurrentRequests = 4,
        }), Store, NullLogger<PoHttpServer>.Instance, new PoHistoryStore(Store.DbRoot));
        Server.Start();
        if (Server.State.IsError) throw new InvalidOperationException("Không khởi động được fixture loopback: " + Server.State.Status);
        Http = new HttpClient { BaseAddress = new Uri(Server.Prefix), Timeout = TimeSpan.FromSeconds(20) };
        Http.DefaultRequestHeaders.Add("x-api-key", ApiKey);
    }

    public async Task<WireResponse> UploadAsync(string orderNo, int planned, string? gtin, string[] codes,
        string format = "csv", string orderField = "orderNo") => await SendAsync(Form(orderNo, planned, gtin, codes, format, orderField));

    public async Task<WireResponse> SendAsync(HttpContent content)
    {
        using (content)
        using (var response = await Http.PostAsync("api/po", content))
            return new WireResponse(response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    public static MultipartFormDataContent Form(string orderNo, int planned, string? gtin, string[] codes, string format, string orderField)
    {
        var form = new MultipartFormDataContent();
        form.Add(new StringContent(orderNo, Encoding.UTF8), orderField);
        form.Add(new StringContent(planned.ToString(CultureInfo.InvariantCulture)), "orderQty");
        if (gtin is not null) form.Add(new StringContent(gtin), "gtin");
        var data = format switch
        {
            "csv" => Encoding.UTF8.GetBytes("CodeContent\r\n" + string.Join("\r\n", codes.Select(code => "\"" + code.Replace("\"", "\"\"") + "\"")) + "\r\n"),
            "json" => Encoding.UTF8.GetBytes(JsonSerializer.Serialize(codes.Select(code => new Dictionary<string, string> { ["CodeContent"] = code }))),
            "xlsx" => Xlsx(codes),
            _ => throw new ArgumentOutOfRangeException(nameof(format)),
        };
        var file = new ByteArrayContent(data);
        file.Headers.ContentType = new MediaTypeHeaderValue(format switch
        {
            "csv" => "text/csv", "json" => "application/json",
            _ => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        });
        form.Add(file, "file", "codes." + format);
        return form;
    }

    private static byte[] Xlsx(string[] codes)
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            void Entry(string name, string text)
            {
                using var writer = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false));
                writer.Write(text);
            }
            XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
            var rows = new[] { "CodeContent" }.Concat(codes).Select((code, index) => new XElement(ns + "row",
                new XAttribute("r", index + 1), new XElement(ns + "c", new XAttribute("r", "A" + (index + 1)),
                    new XAttribute("t", "inlineStr"), new XElement(ns + "is", new XElement(ns + "t",
                        new XAttribute(XNamespace.Xml + "space", "preserve"), code)))));
            Entry("[Content_Types].xml", """<Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Default Extension="xml" ContentType="application/xml"/><Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/><Override PartName="/xl/worksheets/codes.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/></Types>""");
            Entry("_rels/.rels", """<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/></Relationships>""");
            Entry("xl/workbook.xml", """<workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships"><sheets><sheet name="codes" sheetId="1" r:id="rId1"/></sheets></workbook>""");
            Entry("xl/_rels/workbook.xml.rels", """<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/codes.xml"/></Relationships>""");
            Entry("xl/worksheets/codes.xml", new XElement(ns + "worksheet", new XElement(ns + "sheetData", rows)).ToString(SaveOptions.DisableFormatting));
        }
        return buffer.ToArray();
    }

    public async Task<(int Status, string Text)> RawGetAsync(string target)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var socket = new TcpClient();
        await socket.ConnectAsync(IPAddress.Loopback, _port, deadline.Token);
        await using var stream = socket.GetStream();
        var request = $"GET {target} HTTP/1.1\r\nHost: 127.0.0.1:{_port}\r\nx-api-key: {ApiKey}\r\nConnection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(request), deadline.Token);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var response = await reader.ReadToEndAsync(deadline.Token);
        var status = int.Parse(response.Split(' ', 3)[1], CultureInfo.InvariantCulture);
        return (status, response);
    }

    public static string Redact(string value) => value.Replace(ApiKey, "[API key giả lập đã ẩn]", StringComparison.Ordinal);

    public void Dispose()
    {
        try { Server.StopAsync().GetAwaiter().GetResult(); }
        finally { Http.Dispose(); Server.Dispose(); }
    }
}
