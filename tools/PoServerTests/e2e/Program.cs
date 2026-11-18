using System.Net;
using System.Text;
using ClassLibraryMT.Po;
using ClassLibraryMT.PoServer;
using Microsoft.Extensions.Logging;

// =====================================================================
// Kiem thu E2E voi SERVER THAT chay tren HTTP thuoc (khong dung
// HttpClient noi bo) — mo phong MES that su goi qua mang.
//
// Chay: dotnet run --project tools/PoServerTests/e2e
// =====================================================================

var workDir = Path.Combine(Path.GetTempPath(), "po-e2e-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(workDir);

var port = FindFreePort();
var prefix = $"http://127.0.0.1:{port}/";
Console.WriteLine("=== PoServer E2E (HTTP that) ===");
Console.WriteLine($"Prefix: {prefix}");
Console.WriteLine($"Thu muc tam: {workDir}\n");

var store = new PoStore(Path.Combine(workDir, "po"));

using var loggerFactory = LoggerFactory.Create(b => b.AddConsole().SetMinimumLevel(LogLevel.Warning));
var logger = loggerFactory.CreateLogger<PoHttpServer>();

var options = Microsoft.Extensions.Options.Options.Create(new PoServerOptions
{
    Enabled = true,
    Port = port,
    BindAddress = "127.0.0.1",
    ApiKey = "e2e-secret-key",
    MaxBodyMB = 64,
    MaxFileMB = 64,
    UploadTempDirectory = workDir,
    RequestTimeoutSeconds = 60,
    MaxConcurrentRequests = 4,
});

using var server = new PoHttpServer(options, store, logger);
server.Start();

if (server.State.IsError)
{
    Console.WriteLine($"Khong khoi dong duoc: {server.State.Status}");
    return 1;
}

Console.WriteLine($"Server: {server.State.Status}\n");

var failures = 0;

void Check(string name, bool ok, string detail = "")
{
    Console.WriteLine($"  [{(ok ? "OK" : "FAIL")}]   {name}{(ok || detail.Length == 0 ? "" : " -> " + detail)}");
    if (!ok) failures++;
}

// ---- Tao file .xlsx that (da sinh san boi GenXlsx) ----
var xlsxPath = Path.Combine(workDir, "e2e-codes.xlsx");
BuildXlsx(xlsxPath, new[] { "serial", "E2E-SER-001", "E2E-SER-002", "E2E-SER-003", "E2E-SER-004", "E2E-SER-005" });
Console.WriteLine($"File .xlsx: {xlsxPath} ({new FileInfo(xlsxPath).Length} bytes)\n");

using var http = new HttpClient
{
    BaseAddress = new Uri(prefix),
    Timeout = TimeSpan.FromSeconds(60),
};

const string Key = "e2e-secret-key";

// =====================================================================
// [1] Health (co api key)
// =====================================================================
Console.WriteLine("[1] GET /api/health");

var healthReq = new HttpRequestMessage(HttpMethod.Get, "/api/health");
healthReq.Headers.Add("x-api-key", Key);
var health = await http.SendAsync(healthReq);
var healthBody = await health.Content.ReadAsStringAsync();
Check("Health tra HTTP 200", health.StatusCode == HttpStatusCode.OK, health.StatusCode.ToString());
Console.WriteLine($"      -> {healthBody}");

// =====================================================================
// [2] Upload file .xlsx THAT qua HTTP (Content-Type + filename)
// =====================================================================
Console.WriteLine("\n[2] Upload file .xlsx THAT (x-api-key + filename)");

var xlsxBytes = await File.ReadAllBytesAsync(xlsxPath);
var xlsxContent = new ByteArrayContent(xlsxBytes);
xlsxContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(
    "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet");
xlsxContent.Headers.ContentDisposition =
    new System.Net.Http.Headers.ContentDispositionHeaderValue("attachment")
    {
        FileName = "e2e-codes.xlsx",
    };

var xlsxReq = new HttpRequestMessage(
    HttpMethod.Post, "/api/po?poName=PO-E2E-XLSX&orderQty=5&gtin=08934567890123")
{
    Content = xlsxContent,
};
xlsxReq.Headers.Add("x-api-key", Key);

var xlsxPost = await http.SendAsync(xlsxReq);
var xlsxBody = await xlsxPost.Content.ReadAsStringAsync();
Check("Upload .xlsx that tra HTTP 200", xlsxPost.StatusCode == HttpStatusCode.OK, xlsxBody);
Console.WriteLine($"      -> {xlsxBody}");
Check("Phan hoi accepted = 5", xlsxBody.Contains("\"accepted\":5", StringComparison.Ordinal), xlsxBody);

var xlsxStored = (await store.ListLocalOrdersAsync()).FirstOrDefault(p => p.OrderNo == "PO-E2E-XLSX");
Check("PO .xlsx ghi xuong DB local voi 5 ma", xlsxStored?.TotalCodes == 5,
    xlsxStored?.TotalCodes.ToString() ?? "null");
Check("PO .xlsx co gtin trong truong mo rong",
    xlsxStored?.Additional.ContainsKey("gtin") == true,
    xlsxStored is null ? "" : string.Join(",", xlsxStored.Additional.Keys));

// =====================================================================
// [3] Upload .xlsx qua multipart
// =====================================================================
Console.WriteLine("\n[3] Upload .xlsx qua multipart/form-data");

var multi = new MultipartFormDataContent();
multi.Add(new StringContent("PO-E2E-MULTI"), "poName");
multi.Add(new StringContent("5"), "orderQty");
multi.Add(new StringContent("08934567890123"), "gtin");
multi.Add(new ByteArrayContent(xlsxBytes), "file", "e2e-codes.xlsx");

var multiReq = new HttpRequestMessage(HttpMethod.Post, "/api/po") { Content = multi };
multiReq.Headers.Add("x-api-key", Key);

var multiPost = await http.SendAsync(multiReq);
var multiBody = await multiPost.Content.ReadAsStringAsync();
Console.WriteLine($"      -> {multiBody}");

// Toàn bộ mã đã có: báo bỏ qua và không tạo PO rỗng.
Check("Upload .xlsx trùng mã: nhận 0, bỏ qua 5",
    multiPost.StatusCode == HttpStatusCode.OK
    && multiBody.Contains("\"accepted\":0", StringComparison.Ordinal)
    && multiBody.Contains("\"ignoredDuplicates\":5", StringComparison.Ordinal), multiBody);
Check("Không tạo PO rỗng sau upload lại file Excel", store.FindExistingDbPath("PO-E2E-MULTI") is null);

// =====================================================================
// [4] Upload file CSV that
// =====================================================================
Console.WriteLine("\n[4] Upload file CSV THAT");

var csvContent = new ByteArrayContent(
    Encoding.UTF8.GetBytes("serial\nE2E-CSV-001\nE2E-CSV-002\n"));
csvContent.Headers.ContentType =
    new System.Net.Http.Headers.MediaTypeHeaderValue("text/csv");
csvContent.Headers.ContentDisposition =
    new System.Net.Http.Headers.ContentDispositionHeaderValue("attachment")
    {
        FileName = "e2e-codes.csv",
    };

var csvReq = new HttpRequestMessage(
    HttpMethod.Post, "/api/po?poName=PO-E2E-CSV&orderQty=2&gtin=08934567890123")
{
    Content = csvContent,
};
csvReq.Headers.Add("x-api-key", Key);

var csvPost = await http.SendAsync(csvReq);
var csvBody = await csvPost.Content.ReadAsStringAsync();
Check("Upload CSV that tra HTTP 200", csvPost.StatusCode == HttpStatusCode.OK, csvBody);
Console.WriteLine($"      -> {csvBody}");

// =====================================================================
// [5] Upload file JSON that (mang doi tuong)
// =====================================================================
Console.WriteLine("\n[5] Upload file JSON THAT (mang doi tuong)");

var jsonContent = new ByteArrayContent(
    Encoding.UTF8.GetBytes("[{\"code\":\"E2E-JSON-001\"},{\"serial\":\"E2E-JSON-002\"}]"));
jsonContent.Headers.ContentType =
    new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
jsonContent.Headers.ContentDisposition =
    new System.Net.Http.Headers.ContentDispositionHeaderValue("attachment")
    {
        FileName = "e2e-codes.json",
    };

var jsonReq = new HttpRequestMessage(
    HttpMethod.Post, "/api/po?poName=PO-E2E-JSON&orderQty=2&gtin=08934567890123")
{
    Content = jsonContent,
};
jsonReq.Headers.Add("x-api-key", Key);

var jsonPost = await http.SendAsync(jsonReq);
var jsonBody = await jsonPost.Content.ReadAsStringAsync();
Check("Upload JSON that tra HTTP 200", jsonPost.StatusCode == HttpStatusCode.OK, jsonBody);
Console.WriteLine($"      -> {jsonBody}");

// =====================================================================
// [6] Thieu api key -> 401
// =====================================================================
Console.WriteLine("\n[6] Thieu x-api-key -> 401");

var noKeyContent = new ByteArrayContent(Encoding.UTF8.GetBytes("[\"X\"]"));
noKeyContent.Headers.ContentType =
    new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");

var noKeyPost = await http.PostAsync("/api/po?poName=PO-E2E-NOKEY&orderQty=1", noKeyContent);
Check("Thieu api key tra HTTP 401", noKeyPost.StatusCode == HttpStatusCode.Unauthorized,
    noKeyPost.StatusCode.ToString());

// =====================================================================
// [7] File .xls (BIFF) that
// =====================================================================
Console.WriteLine("\n[7] Upload file .xls (BIFF) THAT");

var xlsPath = Path.Combine(workDir, "e2e-codes.xls");
BuildBiff8Xls(xlsPath, new[] { "E2E-XLS-001", "E2E-XLS-002", "E2E-XLS-003" });
Console.WriteLine($"      File .xls: {new FileInfo(xlsPath).Length} bytes");

var xlsContent = new ByteArrayContent(await File.ReadAllBytesAsync(xlsPath));
xlsContent.Headers.ContentType =
    new System.Net.Http.Headers.MediaTypeHeaderValue("application/vnd.ms-excel");
xlsContent.Headers.ContentDisposition =
    new System.Net.Http.Headers.ContentDispositionHeaderValue("attachment")
    {
        FileName = "e2e-codes.xls",
    };

var xlsReq = new HttpRequestMessage(
    HttpMethod.Post, "/api/po?poName=PO-E2E-XLS&orderQty=3&gtin=08934567890123")
{
    Content = xlsContent,
};
xlsReq.Headers.Add("x-api-key", Key);

var xlsPost = await http.SendAsync(xlsReq);
var xlsBody = await xlsPost.Content.ReadAsStringAsync();
Check("Upload .xls that tra HTTP 200", xlsPost.StatusCode == HttpStatusCode.OK, xlsBody);
Console.WriteLine($"      -> {xlsBody}");

// =====================================================================
// [8] File tam da duoc xoa
// =====================================================================
Console.WriteLine("\n[8] File tam duoc don sach");

var uploadRoot = Path.Combine(workDir, "visionf-po-uploads");
var leftover = Directory.Exists(uploadRoot)
    ? Directory.GetFiles(uploadRoot, "po-upload-*").Length
    : 0;
Check("Khong con file tam con lai", leftover == 0, leftover.ToString());

// =====================================================================
// [9] Tong ket
// =====================================================================
Console.WriteLine("\n[9] Tong ket PO trong DB local");
foreach (var p in (await store.ListLocalOrdersAsync()).OrderBy(p => p.OrderNo))
{
    Console.WriteLine($"      {p.OrderNo,-20} qty={p.OrderQty,-6} codes={p.TotalCodes,-6} {p.Status}");
}

Console.WriteLine($"\nKet qua: {(failures == 0 ? "TAT CA PASS" : failures + " FAIL")}");
return failures == 0 ? 0 : 1;

// ---------------------------------------------------------------------
// Helper
// ---------------------------------------------------------------------
static int FindFreePort()
{
    var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
    listener.Start();
    var port = ((IPEndPoint)listener.LocalEndpoint).Port;
    listener.Stop();
    return port;
}

/// <summary>Sinh file .xlsx (OOXML) that.</summary>
static void BuildXlsx(string path, string[] colA)
{
    var shared = new List<string>();
    var map = new Dictionary<string, int>();

    int Id(string s)
    {
        if (!map.TryGetValue(s, out var i))
        {
            i = shared.Count;
            shared.Add(s);
            map[s] = i;
        }

        return i;
    }

    var sheet = new StringBuilder();
    sheet.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
    sheet.Append("<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><sheetData>");
    for (var r = 0; r < colA.Length; r++)
    {
        sheet.Append($"<row r=\"{r + 1}\"><c r=\"A{r + 1}\" t=\"s\"><v>{Id(colA[r])}</v></c></row>");
    }

    sheet.Append("</sheetData></worksheet>");

    using var fs = File.Create(path);
    using var zip = new System.IO.Compression.ZipArchive(
        fs, System.IO.Compression.ZipArchiveMode.Create);

    void Add(string name, string content)
    {
        var e = zip.CreateEntry(name);
        using var w = new StreamWriter(e.Open(), new UTF8Encoding(false));
        w.Write(content);
    }

    Add("[Content_Types].xml",
        "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>"
        + "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">"
        + "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>"
        + "<Default Extension=\"xml\" ContentType=\"application/xml\"/>"
        + "<Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/>"
        + "<Override PartName=\"/xl/sharedStrings.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sharedStrings+xml\"/>"
        + "<Override PartName=\"/xl/worksheets/sheet1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/>"
        + "</Types>");

    Add("_rels/.rels",
        "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>"
        + "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">"
        + "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/>"
        + "</Relationships>");

    Add("xl/workbook.xml",
        "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>"
        + "<workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" "
        + "xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\">"
        + "<sheets><sheet name=\"Data\" sheetId=\"1\" r:id=\"rId1\"/></sheets></workbook>");

    Add("xl/_rels/workbook.xml.rels",
        "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>"
        + "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">"
        + "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet1.xml\"/>"
        + "<Relationship Id=\"rIdSst\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/sharedStrings\" Target=\"sharedStrings.xml\"/>"
        + "</Relationships>");

    var sst = new StringBuilder();
    sst.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
    sst.Append($"<sst xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" count=\"{shared.Count}\" uniqueCount=\"{shared.Count}\">");
    foreach (var s in shared)
    {
        sst.Append($"<si><t>{s}</t></si>");
    }

    sst.Append("</sst>");
    Add("xl/sharedStrings.xml", sst.ToString());
    Add("xl/worksheets/sheet1.xml", sheet.ToString());
}

/// <summary>
/// Sinh file .xls BIFF8 that (Workbook stream tra, khong phai OLE2) —
/// dung de kiem thu bo doc BIFF cua server.
/// </summary>
static void BuildBiff8Xls(string path, string[] serialValues)
{
    using var fs = File.Create(path);
    using var w = new BinaryWriter(fs);

    // BOF (BIFF8): type=0x0005 (workbook globals), version, ...
    w.Write((ushort)0x0809);
    w.Write((ushort)16);
    w.Write((ushort)0x0600);   // vers
    w.Write((ushort)0x0005);   // dt (workbook globals)
    w.Write((ushort)0x0000);
    w.Write((ushort)0x0000);
    w.Write((ushort)0x0000);
    w.Write((ushort)0x0000);
    w.Write((uint)0x00000000);
    w.Write((uint)0x00000000);
    w.Write((ushort)0x000C);
    w.Write((ushort)0x0000);

    // BOF (sheet)
    w.Write((ushort)0x0809);
    w.Write((ushort)16);
    w.Write((ushort)0x0600);
    w.Write((ushort)0x0010);   // dt = worksheet
    w.Write((ushort)0x0000);
    w.Write((ushort)0x0000);
    w.Write((ushort)0x0000);
    w.Write((ushort)0x0000);
    w.Write((uint)0x00000000);
    w.Write((uint)0x00000000);
    w.Write((ushort)0x000C);
    w.Write((ushort)0x0000);

    for (var r = 0; r < serialValues.Length; r++)
    {
        var text = serialValues[r];

        // ROW record
        w.Write((ushort)0x0208);
        w.Write((ushort)16);
        w.Write((ushort)r);            // row index
        w.Write((ushort)0x0100);       // colMic = first col
        w.Write((ushort)0x0001);       // colMac
        w.Write((ushort)0x0000);       // miyRw
        w.Write((ushort)0x0000);       // irwMac
        w.Write((ushort)0x0000);       // reserved
        w.Write((ushort)0x00FF);       // grbit (fUnsynced|fGhostDirty...)
        w.Write((ushort)0x0000);

        // LABEL record (BIFF8, XLUnicodeString)
        var payload = new List<byte>();
        payload.AddRange(BitConverter.GetBytes((ushort)r));       // row
        payload.AddRange(BitConverter.GetBytes((ushort)0));       // col
        payload.AddRange(BitConverter.GetBytes((ushort)0));       // xf
        payload.AddRange(BitConverter.GetBytes((ushort)text.Length)); // cch
        payload.Add(0x00);                                        // flags: compressed (8-bit)
        payload.AddRange(Encoding.ASCII.GetBytes(text));

        w.Write((ushort)0x0204);
        w.Write((ushort)payload.Count);
        foreach (var b in payload)
        {
            w.Write(b);
        }
    }

    // EOF
    w.Write((ushort)0x000A);
    w.Write((ushort)0);
}
