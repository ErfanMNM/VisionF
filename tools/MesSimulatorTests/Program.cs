using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using ClassLibraryMT.MesSimulator;
using ClassLibraryMT.Po;
using ClassLibraryMT.PoServer;
using Microsoft.Data.Sqlite;
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
var options = new MesSimulatorOptions { TargetUrl = $"http://127.0.0.1:{port}", ApiKey = "mes-local-key" };
var client = new MesSimulatorClient(http, Options.Create(options), NullLogger<MesSimulatorClient>.Instance);
async Task<IReadOnlyList<PoCode>> CodesAsync(string orderNo)
{
    var order = (await store.ListLocalOrdersAsync()).Single(row => row.OrderNo == orderNo);
    return await store.GetCodesAsync(order.OrderNo, order.CreatedUtc);
}

try
{
    string[] codes = ["  giữ khoảng trắng  ", "comma,value", "quote\"value", "line1\r\nline2", "Case", "case", "https://first/21/SAME", "https://second/21/SAME"];
    var explicitOrder = await client.UploadTestOrderAsync("MES-EXPLICIT", [.. codes, codes[0]]);
    Check("Explicit codes reach strict production listener", explicitOrder.Success && Number(explicitOrder, "accepted") == codes.Length);
    Check("Explicit duplicate rows counted before dedupe", Number(explicitOrder, "ignoredDuplicates") == 1);
    Check("CSV wire preserves quotes, commas, Unicode, whitespace and CR/LF", (await CodesAsync("MES-EXPLICIT"))
        .Select(row => row.CodeContent).ToHashSet(StringComparer.Ordinal).SetEquals(codes));
    Check("New stored codes do not synthesize identity fragments", (await CodesAsync("MES-EXPLICIT"))
        .All(row => row.ScannedUtc is null && row.ActivatedUtc is null));

    var repeated = await client.UploadTestOrderAsync("MES-EXPLICIT", codes);
    Check("Repeated explicit PO remains idempotent", repeated.Success && Number(repeated, "accepted") == 0);
    var independent = await client.UploadTestOrderAsync("MES-INDEPENDENT", codes);
    Check("Another PO retains independent same CodeContent set", independent.Success && Number(independent, "accepted") == codes.Length);

    var beforeInvalid = server.State.ReceivedCount;
    await RejectAsync<ArgumentException>("Blank explicit code is rejected before HTTP",
        () => client.UploadTestOrderAsync("MES-BLANK", [" "]));
    Check("Invalid explicit request did not create a PO", server.State.ReceivedCount == beforeInvalid
        && store.FindExistingDbPath("MES-BLANK") is null);

    var jsonPath = Path.Combine(work, "input.json");
    await File.WriteAllTextAsync(jsonPath, """[{"CodeContent":"one"},{"CodeContent":"one"},{"CodeContent":"ONE"}]""", new UTF8Encoding(false));
    var fileResult = await client.UploadExistingFileAsync("MES-FILE", 3, jsonPath);
    Check("Strict custom JSON file uploads without legacy metadata", fileResult.Success && Number(fileResult, "accepted") == 2);
    Check("Custom JSON distinguishes case and reports duplicate count", Number(fileResult, "ignoredDuplicates") == 1
        && (await CodesAsync("MES-FILE")).Select(row => row.CodeContent).ToHashSet(StringComparer.Ordinal).SetEquals(["one", "ONE"]));

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
        CustomFilePath = jsonPath, CustomPoName = "MES-WORKER", OrderQty = 3, IntervalSeconds = 1,
    };
    using (var worker = new MesSimulatorWorker(client, Options.Create(workerOptions), NullLogger<MesSimulatorWorker>.Instance))
    {
        await worker.StartAsync(CancellationToken.None);
        await worker.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(10));
        Check("Hosted worker finishes after one configured file upload", worker.State.SentCount == 1 && !worker.State.IsError);
        Check("Hosted worker uses supplied file rather than generated codes", (await CodesAsync("MES-WORKER"))
            .Select(row => row.CodeContent).ToHashSet(StringComparer.Ordinal).SetEquals(["one", "ONE"]));
        await worker.StopAsync(CancellationToken.None);
    }

    var noFile = new MesSimulatorOptions { Enabled = true, TargetUrl = options.TargetUrl };
    using (var missing = new MesSimulatorWorker(client, Options.Create(noFile), NullLogger<MesSimulatorWorker>.Instance))
        await RejectAsync<InvalidOperationException>("Missing file never falls back to generated codes", () => missing.SendOneAsync());

    var fallbackOptions = new MesSimulatorOptions { CustomFilePath = jsonPath, OrderQty = 3 };
    using (var fallback = new MesSimulatorWorker(client, Options.Create(fallbackOptions), NullLogger<MesSimulatorWorker>.Instance))
    {
        var fallbackResult = await fallback.SendOneAsync();
        Check("Empty custom PO name uses file stem", fallbackResult.Success && fallbackResult.PoName == "input");
    }

    using var deniedHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
    var deniedClient = new MesSimulatorClient(deniedHttp, Options.Create(new MesSimulatorOptions { TargetUrl = options.TargetUrl }),
        NullLogger<MesSimulatorClient>.Instance);
    var denied = await deniedClient.UploadTestOrderAsync("MES-NOAUTH", ["one"]);
    Check("Server rejection is returned without inventing success", !denied.Success && denied.StatusCode == 401
        && store.FindExistingDbPath("MES-NOAUTH") is null);

    using var cancelled = new CancellationTokenSource();
    cancelled.Cancel();
    await RejectAsync<OperationCanceledException>("Caller cancellation propagates without importing",
        () => client.UploadTestOrderAsync("MES-CANCELLED", ["one"], cancelled.Token));
    Check("Cancelled explicit upload leaves no PO", store.FindExistingDbPath("MES-CANCELLED") is null);

    Console.WriteLine($"Passed {passed} MES simulator CodeContent checks.");
}
finally
{
    await server.StopAsync();
    SqliteConnection.ClearAllPools();
    try { Directory.Delete(work, true); } catch (IOException) { }
}
