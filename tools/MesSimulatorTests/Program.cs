using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using ClassLibraryMT.MesSimulator;
using ClassLibraryMT.Po;
using ClassLibraryMT.PoServer;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

var work = Path.Combine(Path.GetTempPath(), "visionf-mes-simulator-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(work);
var passed = 0;
void Check(string label, bool ok)
{
    if (!ok) throw new Exception(label);
    passed++;
    Console.WriteLine("[OK] " + label);
}
async Task RejectAsync<T>(string label, Func<Task> action) where T : Exception
{
    try { await action(); }
    catch (T) { Check(label, true); return; }
    throw new Exception(label + ": expected " + typeof(T).Name);
}
int Number(MesSimulatorResult result, string key)
{
    using var json = JsonDocument.Parse(result.ResponseBody);
    return json.RootElement.GetProperty(key).GetInt32();
}

using var portProbe = new TcpListener(IPAddress.Loopback, 0);
portProbe.Start();
var port = ((IPEndPoint)portProbe.LocalEndpoint).Port;
portProbe.Stop();
var store = new PoStore(Path.Combine(work, "po"));
using var server = new PoHttpServer(Options.Create(new PoServerOptions
{
    Enabled = true, Port = port, BindAddress = "127.0.0.1", ApiKey = "mes-local-key",
    UploadTempDirectory = Path.Combine(work, "uploads"),
}), store, NullLogger<PoHttpServer>.Instance);
server.Start();
using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
const string gtin = "00012345678905";
const string otherGtin = "09506000134352";
string Gs1(string serial) => $"(01){gtin}(21){serial}";
var options = new MesSimulatorOptions { TargetUrl = $"http://127.0.0.1:{port}", ApiKey = "mes-local-key", Gtin = gtin };
var client = new MesSimulatorClient(http, Options.Create(options), NullLogger<MesSimulatorClient>.Instance);
async Task<IReadOnlyList<PoCode>> CodesAsync(string orderNo)
{
    var order = (await store.ListLocalOrdersAsync()).Single(row => row.OrderNo == orderNo);
    return await store.GetCodesAsync(order.OrderNo, order.CreatedUtc);
}

try
{
    string[] codes = [Gs1("comma,value"), Gs1("quote\"value"), Gs1("Case"), Gs1("case"),
        $"https://first.example/01/{gtin}/21/SAME", $"https://second.example/01/{gtin}/21/SAME"];
    const string invalidRaw = "  giữ raw,\"value\"\r\nnext  ";
    var mismatchRaw = $"(01){otherGtin}(21)OTHER";
    var explicitOrder = await client.UploadTestOrderAsync("MES-EXPLICIT", [.. codes, codes[0], invalidRaw, mismatchRaw]);
    Check("Explicit codes reach strict production listener", explicitOrder.Success && Number(explicitOrder, "acceptedCount") == codes.Length);
    Check("Explicit duplicate rows counted before dedupe", Number(explicitOrder, "ignoredDuplicates") == 1);
    Check("CSV giữ nguyên quote, comma, host và phân biệt hoa thường của mã GS1", (await CodesAsync("MES-EXPLICIT"))
        .Select(row => row.CodeContent).ToHashSet(StringComparer.Ordinal).SetEquals(codes));
    using (var response = JsonDocument.Parse(explicitOrder.ResponseBody))
    {
        var invalid = response.RootElement.GetProperty("invalidCodes");
        Check("Mã sai giữ raw Unicode/space/CRLF và lý do loại",
            invalid.EnumerateArray().Any(row => row.GetProperty("code").GetString() == invalidRaw
                && row.GetProperty("reason").GetString() == "invalidStructure")
            && invalid.EnumerateArray().Any(row => row.GetProperty("code").GetString() == mismatchRaw
                && row.GetProperty("reason").GetString() == "gtinMismatch"));
    }
    var stored = (await store.ListLocalOrdersAsync()).Single(row => row.OrderNo == "MES-EXPLICIT");
    Check("Planned và GTIN giữ riêng với actual sau partial receive", Number(explicitOrder, "plannedCount") == codes.Length + 3
        && stored.OrderQty == codes.Length + 3 && stored.TotalCodes == codes.Length && stored.Gtin == gtin);
    Check("New stored codes do not synthesize identity fragments", (await CodesAsync("MES-EXPLICIT"))
        .All(row => row.ScannedUtc is null && row.ActivatedUtc is null));

    var repeated = await client.UploadTestOrderAsync("MES-EXPLICIT", codes);
    Check("Repeated explicit PO remains idempotent", repeated.Success && Number(repeated, "accepted") == 0);
    Check("Replay ít record hơn không làm giảm planned", (await store.ListLocalOrdersAsync()).Single(row => row.OrderNo == "MES-EXPLICIT").OrderQty == codes.Length + 3);
    var independent = await client.UploadTestOrderAsync("MES-INDEPENDENT", codes);
    Check("Another PO retains independent same CodeContent set", independent.Success && Number(independent, "accepted") == codes.Length);

    var beforeInvalid = server.State.ReceivedCount;
    await RejectAsync<ArgumentException>("Blank explicit code is rejected before HTTP",
        () => client.UploadTestOrderAsync("MES-BLANK", [" "]));
    Check("Invalid explicit request did not create a PO", server.State.ReceivedCount == beforeInvalid
        && store.FindExistingDbPath("MES-BLANK") is null);

    var jsonPath = Path.Combine(work, "input.json");
    var jsonCodes = new[] { Gs1("one"), Gs1("one"), Gs1("ONE") };
    await File.WriteAllTextAsync(jsonPath, JsonSerializer.Serialize(jsonCodes.Select(code => new { CodeContent = code })), new UTF8Encoding(false));
    var fileResult = await client.UploadExistingFileAsync("MES-FILE", 3, jsonPath);
    Check("Strict custom JSON file uploads without legacy metadata", fileResult.Success && Number(fileResult, "accepted") == 2);
    Check("Custom JSON distinguishes case and reports duplicate count", Number(fileResult, "ignoredDuplicates") == 1
        && (await CodesAsync("MES-FILE")).Select(row => row.CodeContent).ToHashSet(StringComparer.Ordinal).SetEquals([Gs1("one"), Gs1("ONE")]));

    var beforeBadFile = server.State.ReceivedCount;
    await RejectAsync<ArgumentException>("Custom quantity must match all rows before dedupe",
        () => client.UploadExistingFileAsync("MES-BAD-QTY", 2, jsonPath));
    var legacyPath = Path.Combine(work, "legacy.csv");
    await File.WriteAllTextAsync(legacyPath, "serial\r\nold\r\n");
    await RejectAsync<PoFileFormatException>("Legacy serial CSV rejected before HTTP",
        () => client.UploadExistingFileAsync("MES-LEGACY", 1, legacyPath));
    var extraPath = Path.Combine(work, "extra.json");
    await File.WriteAllTextAsync(extraPath, """[{"CodeContent":"one","serial":"old"}]""");
    await RejectAsync<PoFileFormatException>("Extra JSON identity field rejected before HTTP",
        () => client.UploadExistingFileAsync("MES-EXTRA", 1, extraPath));
    await RejectAsync<ArgumentException>("Legacy XLS rejected explicitly",
        () => client.UploadExistingFileAsync("MES-XLS", 1, Path.Combine(work, "old.xls")));
    Check("Invalid custom files perform no accepted HTTP import", server.State.ReceivedCount == beforeBadFile);

    var workerOptions = new MesSimulatorOptions
    {
        Enabled = true, TargetUrl = options.TargetUrl, ApiKey = options.ApiKey,
        CustomFilePath = jsonPath, CustomPoName = "LEGACY-NAME", CustomOrderNo = "MES-WORKER", Gtin = gtin, OrderQty = 3, IntervalSeconds = 1,
    };
    using (var worker = new MesSimulatorWorker(client, Options.Create(workerOptions), NullLogger<MesSimulatorWorker>.Instance))
    {
        await worker.StartAsync(CancellationToken.None);
        await worker.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(10));
        Check("Hosted worker finishes after one configured file upload", worker.State.SentCount == 1 && !worker.State.IsError);
        Check("Hosted worker uses supplied file rather than generated codes", (await CodesAsync("MES-WORKER"))
            .Select(row => row.CodeContent).ToHashSet(StringComparer.Ordinal).SetEquals([Gs1("one"), Gs1("ONE")]));
        await worker.StopAsync(CancellationToken.None);
    }

    var noFile = new MesSimulatorOptions { Enabled = true, TargetUrl = options.TargetUrl };
    using (var missing = new MesSimulatorWorker(client, Options.Create(noFile), NullLogger<MesSimulatorWorker>.Instance))
        await RejectAsync<InvalidOperationException>("Missing file never falls back to generated codes", () => missing.SendOneAsync());

    var fallbackOptions = new MesSimulatorOptions { CustomFilePath = jsonPath, Gtin = gtin, OrderQty = 3 };
    using (var fallback = new MesSimulatorWorker(client, Options.Create(fallbackOptions), NullLogger<MesSimulatorWorker>.Instance))
    {
        var fallbackResult = await fallback.SendOneAsync();
        Check("Empty custom PO name uses file stem", fallbackResult.Success && fallbackResult.PoName == "input");
    }

    var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["MesSimulator:Enabled"] = "true",
        ["MesSimulator:TargetUrl"] = options.TargetUrl,
        ["MesSimulator:ApiKey"] = options.ApiKey,
        ["MesSimulator:CustomFilePath"] = jsonPath,
        ["MesSimulator:CustomPoName"] = "MES-DI-LEGACY",
        ["MesSimulator:CustomOrderNo"] = "MES-DI-CONFIG",
        ["MesSimulator:Gtin"] = gtin,
        ["MesSimulator:OrderQty"] = "3",
    }).Build();
    var services = new ServiceCollection();
    services.AddSingleton<IConfiguration>(configuration);
    services.AddLogging();
    services.AddMesSimulator(configuration);
    using (var provider = services.BuildServiceProvider())
    {
        var configured = provider.GetRequiredService<IOptions<MesSimulatorOptions>>().Value;
        Check("AddMesSimulator ánh xạ GTIN và CustomOrderNo từ IConfiguration",
            configured.Gtin == gtin && configured.CustomOrderNo == "MES-DI-CONFIG");
        var configuredWorker = provider.GetRequiredService<MesSimulatorWorker>();
        var configuredResult = await configuredWorker.SendOneAsync();
        Check("Worker qua DI upload thành công bằng metadata cấu hình thật",
            configuredResult.Success && configuredResult.PoName == "MES-DI-CONFIG"
            && Number(configuredResult, "acceptedCount") == 2);
        var configuredOrder = (await store.ListLocalOrdersAsync()).Single(row => row.OrderNo == "MES-DI-CONFIG");
        Check("DI giữ GTIN/planned và ưu tiên CustomOrderNo hơn alias cũ",
            configuredOrder.Gtin == gtin && configuredOrder.OrderQty == 3 && configuredOrder.TotalCodes == 2
            && store.FindExistingDbPath("MES-DI-LEGACY") is null);
    }

    using var deniedHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
    var deniedClient = new MesSimulatorClient(deniedHttp, Options.Create(new MesSimulatorOptions { TargetUrl = options.TargetUrl, Gtin = gtin }),
        NullLogger<MesSimulatorClient>.Instance);
    var denied = await deniedClient.UploadTestOrderAsync("MES-NOAUTH", [Gs1("one")]);
    Check("Server rejection is returned without inventing success", !denied.Success && denied.StatusCode == 401
        && store.FindExistingDbPath("MES-NOAUTH") is null);

    using var cancelled = new CancellationTokenSource();
    cancelled.Cancel();
    await RejectAsync<OperationCanceledException>("Caller cancellation propagates without importing",
        () => client.UploadTestOrderAsync("MES-CANCELLED", [Gs1("one")], cancelled.Token));
    Check("Cancelled explicit upload leaves no PO", store.FindExistingDbPath("MES-CANCELLED") is null);

    var missingGtinClient = new MesSimulatorClient(http, Options.Create(new MesSimulatorOptions { TargetUrl = options.TargetUrl }),
        NullLogger<MesSimulatorClient>.Instance);
    await RejectAsync<ArgumentException>("Thiếu GTIN bị chặn trước HTTP", () => missingGtinClient.UploadTestOrderAsync("MES-NOGTIN", codes));
    await RejectAsync<ArgumentException>("GTIN checksum sai bị chặn trước HTTP",
        () => client.UploadTestOrderAsync("MES-BADGTIN", codes, gtin: "00012345678904"));
    var shortGtin = await client.UploadTestOrderAsync("MES-SHORTGTIN", ["(01)04006381333931(21)SHORT"], gtin: "4006381333931");
    Check("Tham số GTIN ngắn tương đương được chuẩn hóa riêng metadata", shortGtin.Success
        && (await store.ListLocalOrdersAsync()).Single(row => row.OrderNo == "MES-SHORTGTIN").Gtin == "04006381333931");

    Console.WriteLine($"Passed {passed} MES simulator CodeContent checks.");
}
finally
{
    await server.StopAsync();
    SqliteConnection.ClearAllPools();
    try { Directory.Delete(work, true); } catch (IOException) { }
}
