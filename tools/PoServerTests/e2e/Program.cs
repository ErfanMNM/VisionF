using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using ClassLibraryMT.Po;
using ClassLibraryMT.PoServer;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

// Real HTTP listener test: dotnet run --project tools/PoServerTests/e2e
var workDir = Path.Combine(Path.GetTempPath(), "visionf-po-e2e-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(workDir);
var store = new PoStore(Path.Combine(workDir, "po"));
using var loggerFactory = LoggerFactory.Create(builder => builder.AddConsole().SetMinimumLevel(LogLevel.Warning));
using var server = new PoHttpServer(Microsoft.Extensions.Options.Options.Create(new PoServerOptions
{
    Enabled = true,
    BindAddress = "127.0.0.1",
    Port = FindFreePort(),
    ApiKey = "e2e-test-key",
    MaxBodyMB = 64,
    MaxFileMB = 64,
    UploadTempDirectory = workDir,
    RequestTimeoutSeconds = 60,
    MaxConcurrentRequests = 4,
}), store, loggerFactory.CreateLogger<PoHttpServer>());
server.Start();
if (server.State.IsError) throw new Exception("Không mở được HTTP listener: " + server.State.Status);
using var http = new HttpClient { BaseAddress = new Uri(server.Prefix), Timeout = TimeSpan.FromSeconds(60) };
var passed = 0;

void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
    Console.WriteLine("[OK] " + message);
    passed++;
}

async Task<(HttpStatusCode Status, string Body)> SendAsync(string uri, HttpContent content, bool authenticate = true)
{
    using var request = new HttpRequestMessage(HttpMethod.Post, uri) { Content = content };
    if (authenticate) request.Headers.Add("x-api-key", "e2e-test-key");
    using var response = await http.SendAsync(request);
    return (response.StatusCode, await response.Content.ReadAsStringAsync());
}

static ByteArrayContent FileContent(string data, string mediaType, string? fileName = null)
{
    var content = new ByteArrayContent(Encoding.UTF8.GetBytes(data));
    content.Headers.ContentType = new MediaTypeHeaderValue(mediaType);
    if (fileName is not null)
        content.Headers.ContentDisposition = new ContentDispositionHeaderValue("attachment") { FileName = fileName };
    return content;
}

static int ResultNumber(string body, string property)
{
    using var json = JsonDocument.Parse(body);
    return json.RootElement.GetProperty(property).GetInt32();
}

async Task<IReadOnlyList<PoCode>> ReadCodesAsync(string orderNo)
{
    var order = (await store.ListLocalOrdersAsync()).Single(item => item.OrderNo == orderNo);
    return await store.GetCodesAsync(orderNo, order.CreatedUtc);
}

try
{
    using (var request = new HttpRequestMessage(HttpMethod.Get, "/api/health"))
    {
        request.Headers.Add("x-api-key", "e2e-test-key");
        using var response = await http.SendAsync(request);
        Check(response.StatusCode == HttpStatusCode.OK, "HTTP health có xác thực");
    }
    var unauthorized = await SendAsync("/api/po?poName=NO-KEY&orderQty=1",
        FileContent("[{\"CodeContent\":\"one\"}]", "application/json"), authenticate: false);
    Check(unauthorized.Status == HttpStatusCode.Unauthorized && store.FindExistingDbPath("NO-KEY") is null,
        "Thiếu API key bị từ chối trước khi tạo PO");

    string[] csvCodes = ["  opaque content  ", "comma,value", "line1\r\nline2", "Case", "case"];
    const string csv = "CodeContent\r\n\"  opaque content  \"\r\n\"comma,value\"\r\n\"line1\r\nline2\"\r\nCase\r\ncase\r\nCase\r\n";
    var csvResponse = await SendAsync("/api/po?poName=PO-E2E-CSV&orderQty=6&productName=Opaque%20codes",
        FileContent(csv, "text/csv"));
    Check(csvResponse.Status == HttpStatusCode.OK && ResultNumber(csvResponse.Body, "accepted") == 5
        && ResultNumber(csvResponse.Body, "ignoredDuplicates") == 1, "CSV đếm số dòng trước khi lọc trùng");
    Check((await ReadCodesAsync("PO-E2E-CSV")).Select(code => code.CodeContent)
            .ToHashSet(StringComparer.Ordinal).SetEquals(csvCodes),
        "CSV giữ nguyên khoảng trắng, dấu phẩy, xuống dòng và phân biệt hoa thường");

    var samePo = await SendAsync("/api/po?poName=PO-E2E-CSV&orderQty=6", FileContent(csv, "text/csv", "codes.csv"));
    Check(samePo.Status == HttpStatusCode.OK && ResultNumber(samePo.Body, "accepted") == 0
        && ResultNumber(samePo.Body, "ignoredDuplicates") == 6, "Upload lại cùng PO bỏ qua mã đã có");
    var differentPo = await SendAsync("/api/po?poName=PO-E2E-INDEPENDENT&orderQty=6", FileContent(csv, "text/csv"));
    Check(differentPo.Status == HttpStatusCode.OK && ResultNumber(differentPo.Body, "accepted") == 5,
        "Hai PO lưu tập CodeContent độc lập");

    const string json = """[{"CodeContent":"https://first/21/SAME"},{"CodeContent":"https://second/21/SAME"},{"CodeContent":" mã nguyên vẹn ? & | \" "}]""";
    var jsonResponse = await SendAsync("/api/po?poName=PO-E2E-JSON&orderQty=3", FileContent(json, "application/json", "codes.json"));
    Check(jsonResponse.Status == HttpStatusCode.OK && ResultNumber(jsonResponse.Body, "accepted") == 3,
        "JSON nhận duy nhất thuộc tính CodeContent, không diễn giải hậu tố serial");
    var jsonCodes = await ReadCodesAsync("PO-E2E-JSON");
    Check(jsonCodes.Any(code => code.CodeContent == " mã nguyên vẹn ? & | \" "), "JSON lưu nguyên Unicode và ký tự đặc biệt");

    var xlsxPath = Path.Combine(workDir, "codes.xlsx");
    string[] excelCodes = ["XLSX:001", "xlsx:001", "  excel & raw <content>  "];
    BuildXlsx(xlsxPath, ["CodeContent", .. excelCodes]);
    using (var multipart = new MultipartFormDataContent())
    {
        multipart.Add(new StringContent("PO-E2E-XLSX"), "poName");
        multipart.Add(new StringContent("3"), "orderQty");
        multipart.Add(new StringContent("LOT-E2E"), "batchCode");
        var file = new ByteArrayContent(await File.ReadAllBytesAsync(xlsxPath));
        file.Headers.ContentType = new MediaTypeHeaderValue("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet");
        multipart.Add(file, "file", "codes.xlsx");
        var result = await SendAsync("/api/po", multipart);
        Check(result.Status == HttpStatusCode.OK && ResultNumber(result.Body, "accepted") == 3,
            "Multipart XLSX có một cột CodeContent");
    }
    Check((await ReadCodesAsync("PO-E2E-XLSX")).Select(code => code.CodeContent)
            .ToHashSet(StringComparer.Ordinal).SetEquals(excelCodes), "XLSX giữ nguyên nội dung ô văn bản");

    const string boundary = "CodeContentBoundary-qa";
    var opaqueMultipartCode = $"prefix--{boundary}inline\r\n--{boundary}Suffix\r\n--{boundary}--Suffix\r\ntrailer";
    var filePart = $"--{boundary}\r\nContent-Disposition: form-data; name=\"file\"; filename=\"codes.csv\"\r\nContent-Type: text/csv\r\n\r\n";
    var multipartBody = filePart + "CodeContent\r\n\"" + opaqueMultipartCode + $"\"\r\n\r\n--{boundary}--\r\n";
    var boundaryContent = FileContent(multipartBody, "multipart/form-data");
    boundaryContent.Headers.ContentType!.Parameters.Add(new NameValueHeaderValue("boundary", boundary));
    var boundaryResult = await SendAsync("/api/po?poName=PO-E2E-BOUNDARY&orderQty=1", boundaryContent);
    Check(boundaryResult.Status == HttpStatusCode.OK
            && (await ReadCodesAsync("PO-E2E-BOUNDARY")).Single().CodeContent == opaqueMultipartCode,
        "Multipart giữ nguyên boundary nằm trong mã và prefix giống boundary ở đầu dòng");

    var validFilePrefix = filePart + $"CodeContent\r\nWOULD-BE-NEW\r\n\r\n--{boundary}\r\n";
    (string Name, string Body)[] invalidMultipart =
    [
        ("MISSING-CLOSE", validFilePrefix + "Content-Disposition: form-data; name=\"customer\"\r\n\r\nunfinished"),
        ("MALFORMED-HEADER", validFilePrefix + $"Malformed header without colon\r\n\r\nvalue\r\n--{boundary}--\r\n"),
        ("MISSING-HEADER", validFilePrefix + $"\r\nvalue\r\n--{boundary}--\r\n"),
        ("LARGE-HEADER", validFilePrefix + "Content-Disposition: form-data; name=\"customer\"\r\nX-Large: "
            + new string('x', 64 * 1024) + $"\r\n\r\nvalue\r\n--{boundary}--\r\n"),
    ];
    foreach (var test in invalidMultipart)
    {
        var invalidContent = FileContent(test.Body, "multipart/form-data");
        invalidContent.Headers.ContentType!.Parameters.Add(new NameValueHeaderValue("boundary", boundary));
        var poName = "PO-E2E-MULTIPART-" + test.Name;
        var result = await SendAsync($"/api/po?poName={poName}&orderQty=1", invalidContent);
        Check(result.Status == HttpStatusCode.BadRequest && store.FindExistingDbPath(poName) is null,
            "Không chấp nhận file hợp lệ ở đầu request multipart bị lỗi: " + test.Name);
    }

    (string Name, string Body, string Type, int Qty)[] invalid =
    [
        ("STRINGS", "[\"one\"]", "application/json", 1),
        ("ENVELOPE", "{\"poName\":\"legacy\",\"orderQty\":1,\"codes\":[\"one\"]}", "application/json", 1),
        ("EXTRA", "[{\"CodeContent\":\"one\",\"GTIN\":\"123\"}]", "application/json", 1),
        ("MISSING", "[{\"serial\":\"one\"}]", "application/json", 1),
        ("NUMBER", "[{\"CodeContent\":123}]", "application/json", 1),
        ("EMPTY", "[{\"CodeContent\":\" \"}]", "application/json", 1),
        ("HEADER", "serial\none\n", "text/csv", 1),
        ("COLUMNS", "CodeContent,Serial\none,two\n", "text/csv", 1),
        ("BLANK", "CodeContent\n\"\"\n", "text/csv", 1),
        ("QTY-SMALL", "CodeContent\none\ntwo\n", "text/csv", 1),
        ("QTY-LARGE", "CodeContent\none\n", "text/csv", 2),
    ];
    foreach (var test in invalid)
    {
        var poName = "PO-E2E-INVALID-" + test.Name;
        var result = await SendAsync($"/api/po?poName={poName}&orderQty={test.Qty}", FileContent(test.Body, test.Type));
        Check(result.Status == HttpStatusCode.BadRequest && store.FindExistingDbPath(poName) is null,
            "Không tạo PO khi file không hợp lệ: " + test.Name);
    }
    var invalidMetadata = await SendAsync("/api/po?poName=PO-E2E-GTIN&orderQty=1&gtin=123",
        FileContent("CodeContent\none\n", "text/csv"));
    Check(invalidMetadata.Status == HttpStatusCode.BadRequest && store.FindExistingDbPath("PO-E2E-GTIN") is null,
        "GTIN không được dùng làm metadata nhập PO");
    var invalidAppend = await SendAsync("/api/po?poName=PO-E2E-CSV&orderQty=2",
        FileContent("CodeContent\nWOULD-BE-NEW\nbad,extra\n", "text/csv"));
    Check(invalidAppend.Status == HttpStatusCode.BadRequest && (await ReadCodesAsync("PO-E2E-CSV")).Count == 5,
        "File bổ sung lỗi không ghi một phần dữ liệu vào PO cũ");
    var xls = await SendAsync("/api/po?poName=PO-E2E-XLS&orderQty=1",
        FileContent("legacy workbook", "application/vnd.ms-excel", "codes.xls"));
    Check(xls.Status == HttpStatusCode.BadRequest && store.FindExistingDbPath("PO-E2E-XLS") is null,
        "File XLS legacy bị từ chối; dùng XLSX một cột CodeContent");

    var uploadRoot = Path.Combine(workDir, "visionf-po-uploads");
    Check(!Directory.Exists(uploadRoot) || Directory.GetFiles(uploadRoot, "po-upload-*").Length == 0,
        "Dọn file upload tạm sau cả request thành công và lỗi");
    Console.WriteLine($"Đạt {passed} kiểm thử CodeContent qua HTTP listener thật.");
    return 0;
}
finally
{
    server.Stop();
    SqliteConnection.ClearAllPools();
    Directory.Delete(workDir, recursive: true);
}

static int FindFreePort()
{
    var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
    listener.Start();
    var port = ((IPEndPoint)listener.LocalEndpoint).Port;
    listener.Stop();
    return port;
}

static void BuildXlsx(string path, string[] colA)
{
    static string Xml(string value) => System.Security.SecurityElement.Escape(value) ?? string.Empty;
    var shared = colA.Distinct(StringComparer.Ordinal).ToArray();
    var ids = shared.Select((value, index) => (value, index)).ToDictionary(item => item.value, item => item.index, StringComparer.Ordinal);
    var sheet = new StringBuilder("<?xml version=\"1.0\" encoding=\"UTF-8\"?><worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><sheetData>");
    for (var row = 0; row < colA.Length; row++)
        sheet.Append($"<row r=\"{row + 1}\"><c r=\"A{row + 1}\" t=\"s\"><v>{ids[colA[row]]}</v></c></row>");
    sheet.Append("</sheetData></worksheet>");
    using var file = File.Create(path);
    using var zip = new ZipArchive(file, ZipArchiveMode.Create);
    void Add(string name, string content)
    {
        using var writer = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false));
        writer.Write(content);
    }
    Add("[Content_Types].xml", "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"><Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/><Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/><Override PartName=\"/xl/sharedStrings.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sharedStrings+xml\"/><Override PartName=\"/xl/worksheets/sheet1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/></Types>");
    Add("_rels/.rels", "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/></Relationships>");
    Add("xl/workbook.xml", "<workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><sheets><sheet name=\"Data\" sheetId=\"1\" r:id=\"rId1\"/></sheets></workbook>");
    Add("xl/_rels/workbook.xml.rels", "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet1.xml\"/><Relationship Id=\"rIdSst\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/sharedStrings\" Target=\"sharedStrings.xml\"/></Relationships>");
    Add("xl/sharedStrings.xml", $"<sst xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" count=\"{colA.Length}\" uniqueCount=\"{shared.Length}\">" + string.Concat(shared.Select(value => $"<si><t xml:space=\"preserve\">{Xml(value)}</t></si>")) + "</sst>");
    Add("xl/worksheets/sheet1.xml", sheet.ToString());
}
