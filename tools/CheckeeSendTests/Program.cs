using System.Net;
using System.Text;
using System.Text.Json;
using ClassLibraryMT.Checkee;
using ClassLibraryMT.Scans;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TanTienQR.Services;

var checks = 0;
void Check(bool condition, string description)
{
    if (!condition) throw new InvalidOperationException("[FAIL] " + description);
    checks++;
    Console.WriteLine("[OK] " + description);
}

static ScanRecordEntity Pass(string code, string order = "PO-01") => new()
{
    CodeContent = code, Raw = code, Status = ScanStatuses.Pass,
    OrderNo = order, ReceivedUtc = DateTime.UtcNow,
};

var fullCode = "  hàng Việt/01/08934567890123/21/SAME\u001d?key=VALUE  " + new string('x', 700);
await using (var test = new SendTestSession(" 08934567890123 "))
{
    var first = Pass(fullCode);
    first.Serial = "SAME";
    first.Gtin = "LEGACY-GTIN-MUST-NOT-OVERRIDE-CONFIG";
    var second = Pass("different/21/SAME");
    second.Serial = "SAME";
    var caseVariant = Pass("DIFFERENT/21/SAME");
    var failure = Pass("NOT-A-PASS");
    failure.Status = ScanStatuses.InvalidForOrder;
    test.Writer.Enqueue(first);
    test.Writer.Enqueue(second);
    test.Writer.Enqueue(caseVariant);
    test.Writer.Enqueue(failure);
    Check(await test.Worker.SendPendingAsync() == 3, "Gửi tất cả mã Pass, bỏ qua InvalidForOrder");
    var payload = test.Http.Payloads.Single();
    var codes = payload.GetProperty("serialNumbers").EnumerateArray().Select(value => value.GetString()).ToArray();
    Check(codes.SequenceEqual(new[] { fullCode, second.CodeContent, caseVariant.CodeContent }),
        "serialNumbers giữ nguyên CodeContent dài, Unicode, GS, khoảng trắng và hoa thường; không lấy Serial");
    Check(payload.GetProperty("GTIN").GetString() == "08934567890123"
        && payload.GetProperty("batchCode").GetString() == "PO-01"
        && payload.GetProperty("type").GetString() == "DECLARE", "Giao thức Checkee giữ GTIN/batchCode/type; GTIN ưu tiên cấu hình sản phẩm");
    var rows = await test.RowsAsync();
    Check(rows.Count(row => row.SendStatus == ScanSendStatuses.Sent) == 3
        && rows.All(row => row.CodeContent == failure.CodeContent || row.Status == ScanStatuses.Pass),
        "Thành công chỉ đổi trạng thái gửi, giữ outcome quét Pass");
    Check(await test.Worker.SendPendingAsync() == 0 && test.Http.Payloads.Count == 1,
        "Không gửi lại mã đã Sent trong chu kỳ sau");
}

await using (var test = new SendTestSession(""))
{
    test.Writer.Enqueue(Pass("arbitrary code: no GS1 format"));
    Check(await test.Worker.SendPendingAsync() == 0 && test.Http.RequestCount == 0,
        "Thiếu ProductGtin không gọi mạng hoặc loại mã không có định dạng GS1");
    var row = (await test.RowsAsync()).Single();
    Check(row.Status == ScanStatuses.Pass && row.SendStatus == ScanSendStatuses.Pending
        && row.SendMessage.Contains("Checkee:ProductGtin"), "Thiếu metadata giữ Pass/Pending với hướng dẫn cấu hình");
    Check(test.Worker.State.IsError && test.Worker.State.Status.Contains("Checkee:ProductGtin")
        && test.Worker.State.LastRunUtc.HasValue, "Trạng thái worker hiển thị mã đang bị chặn và lý do thiếu metadata");
    test.Outbound.ProductGtin = "CONFIGURED-PRODUCT";
    Check(await test.Worker.SendPendingAsync() == 1 && (await test.RowsAsync()).Single().SendStatus == ScanSendStatuses.Sent,
        "Bổ sung ProductGtin cho phép gửi lại mã Pending, không cần reset Failed");
}

await using (var test = new SendTestSession("PRODUCT"))
{
    test.Writer.Enqueue(Pass("unstructured/content", order: ""));
    Check(await test.Worker.SendPendingAsync() == 0 && test.Http.RequestCount == 0,
        "Thiếu mã lô và PO không gửi payload thiếu metadata");
    var row = (await test.RowsAsync()).Single();
    Check(row.SendStatus == ScanSendStatuses.Pending && row.SendMessage.Contains("Checkee:BatchCode")
        && row.Status == ScanStatuses.Pass, "Thiếu mã lô vẫn giữ Pass/Pending và thông báo rõ");
    test.Options.BatchCode = " BATCH-CONFIG ";
    Check(await test.Worker.SendPendingAsync() == 1
        && test.Http.Payloads.Single().GetProperty("batchCode").GetString() == "BATCH-CONFIG",
        "Mã lô cấu hình bổ sung giúp mã chờ được gửi lại");
}

await using (var test = new SendTestSession("PRODUCT"))
{
    var empty = Pass("");
    empty.Serial = "LEGACY-SERIAL-MUST-NOT-BE-SENT";
    empty.BatchNo = "RECORD-BATCH";
    var valid = Pass("valid arbitrary code");
    valid.BatchNo = "RECORD-BATCH";
    test.Options.BatchCode = "FALLBACK-BATCH";
    test.Writer.Enqueue(empty);
    test.Writer.Enqueue(valid);
    Check(await test.Worker.SendPendingAsync() == 1, "Bản ghi không có CodeContent không ngăn gửi mã hợp lệ");
    var rows = await test.RowsAsync();
    Check(rows.Single(row => row.CodeContent.Length == 0).SendStatus == ScanSendStatuses.Pending
        && rows.Single(row => row.CodeContent == valid.CodeContent).SendStatus == ScanSendStatuses.Sent,
        "Không đánh dấu Sent hoặc dùng Serial thay cho bản ghi thiếu CodeContent");
    Check(test.Http.Payloads.Single().GetProperty("batchCode").GetString() == "RECORD-BATCH",
        "Mã lô trên bản ghi ưu tiên trước cấu hình fallback");
}

await using (var test = new SendTestSession(""))
{
    var legacy = Pass("LEGACY-FULL/21/SAME");
    legacy.Gtin = " LEGACY-PRODUCT ";
    legacy.Serial = "SAME";
    test.Writer.Enqueue(legacy);
    Check(await test.Worker.SendPendingAsync() == 1
        && test.Http.Payloads.Single().GetProperty("GTIN").GetString() == "LEGACY-PRODUCT"
        && test.Http.Payloads.Single().GetProperty("serialNumbers")[0].GetString() == legacy.CodeContent,
        "Fallback metadata GTIN cũ vẫn gửi toàn bộ CodeContent");
}

await using (var test = new SendTestSession("PRODUCT"))
{
    test.Options.BatchSize = 2;
    var missingBatch = Pass("blocked until batch is configured", order: "");
    var missingContent = Pass("");
    var valid = Pass("valid code behind a full blocked batch");
    var received = new DateTime(2026, 11, 26, 0, 0, 0, DateTimeKind.Utc);
    missingBatch.ReceivedUtc = received;
    missingContent.ReceivedUtc = received.AddSeconds(1);
    valid.ReceivedUtc = received.AddSeconds(2);
    test.Writer.Enqueue(missingBatch);
    test.Writer.Enqueue(missingContent);
    test.Writer.Enqueue(valid);

    Check(await test.Worker.SendPendingAsync() == 0 && test.Http.RequestCount == 0,
        "Batch đầu đầy bản ghi thiếu metadata/nội dung được giữ lại mà không gọi HTTP");
    var blocked = (await test.RowsAsync()).Where(row => row.CodeContent != valid.CodeContent).ToArray();
    Check(blocked.Length == 2 && blocked.All(row => row.SendStatus == ScanSendStatuses.Pending
        && row.Status == ScanStatuses.Pass && row.SendAttemptUtc.HasValue),
        "Mọi bản ghi blocked vẫn Pass/Pending và lưu lần thử để nhường lượt batch sau");
    Check(await test.Worker.SendPendingAsync() == 1
        && test.Http.Payloads.Single().GetProperty("serialNumbers")[0].GetString() == valid.CodeContent,
        "Mã hợp lệ sau một batch toàn blocked vẫn được gửi ở chu kỳ kế tiếp");

    test.Options.BatchCode = "REPAIRED-BATCH";
    Check(await test.Worker.SendPendingAsync() == 1,
        "Bổ sung metadata vẫn tự gửi lại mã Pending sau khi hàng đợi nhường lượt");
    var rows = await test.RowsAsync();
    Check(rows.Single(row => row.CodeContent == missingBatch.CodeContent).SendStatus == ScanSendStatuses.Sent
        && rows.Single(row => row.CodeContent.Length == 0).SendStatus == ScanSendStatuses.Pending
        && rows.Single(row => row.CodeContent == valid.CodeContent).SendStatus == ScanSendStatuses.Sent,
        "Metadata repair chỉ giải phóng mã đã đủ dữ liệu, giữ bản ghi thiếu CodeContent chờ đối soát");
}

await using (var test = new SendTestSession("PRODUCT"))
{
    test.Http.ActivateStatus = HttpStatusCode.BadRequest;
    test.Writer.Enqueue(Pass("server-rejected arbitrary code"));
    Check(await test.Worker.SendPendingAsync() == 0, "Server từ chối không tính mã đã gửi thành công");
    var row = (await test.RowsAsync()).Single();
    Check(row.Status == ScanStatuses.Pass && row.SendStatus == ScanSendStatuses.Failed && row.SendStatusCode == 400,
        "HTTP từ chối chỉ làm Failed trạng thái gửi, không đổi scan Pass");
    Check(await test.Worker.SendPendingAsync() == 0 && test.Http.Payloads.Count == 1,
        "Server từ chối không tự gửi lại mã Failed");
    test.Http.ActivateStatus = HttpStatusCode.OK;
    var retry = await test.Worker.ResendFailedAsync(new[] { row.Id });
    Check(retry == (1, 1) && (await test.RowsAsync()).Single().Status == ScanStatuses.Pass,
        "Gửi lại thủ công phục hồi trạng thái gửi mà không đổi outcome quét");
    test.Http.BusinessFailure = true;
    test.Writer.Enqueue(Pass("business-rejected full code"));
    Check(await test.Worker.SendPendingAsync() == 0
        && (await test.RowsAsync()).Single(item => item.CodeContent == "business-rejected full code") is
            { Status: ScanStatuses.Pass, SendStatus: ScanSendStatuses.Failed, SendStatusCode: 200 },
        "HTTP 200 nhưng nghiệp vụ từ chối cũng chỉ đổi trạng thái gửi Failed");
}

await using (var test = new SendTestSession("PRODUCT"))
{
    test.Http.ThrowNetwork = true;
    test.Writer.Enqueue(Pass("network retry full content"));
    Check(await test.Worker.SendPendingAsync() == 0, "Lỗi mạng không mất bản ghi hoặc ném lỗi ra caller");
    var row = (await test.RowsAsync()).Single();
    Check(row.SendStatus == ScanSendStatuses.Pending && row.Status == ScanStatuses.Pass
        && test.Worker.State.Status.Contains("chờ thử lại"), "Lỗi mạng giữ Pending và hiện trạng thái chờ thử lại");
    test.Http.ThrowNetwork = false;
    Check(await test.Worker.SendPendingAsync() == 1, "Chu kỳ kế tiếp gửi lại mã sau khi mạng hồi phục");
}

await using (var test = new SendTestSession("PRODUCT"))
{
    test.Writer.Enqueue(Pass("maintenance held code"));
    test.Gate.SetSuspended(true);
    Check(await test.Worker.SendPendingAsync() == 0 && test.Http.RequestCount == 0,
        "Cổng bảo trì chặn gửi thủ công như timer");
    test.Gate.SetSuspended(false);
    Check(await test.Worker.SendPendingAsync() == 1, "Kết thúc bảo trì vẫn gửi mã đã giữ trong hàng đợi");
}

Console.WriteLine($"Đã qua {checks} kiểm thử Checkee CodeContent, HTTP giả và SQLite tạm; không build UI/PO backend.");

sealed class SendTestSession : IAsyncDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "VisionF-CheckeeSend-" + Guid.NewGuid().ToString("N"));
    private readonly ServiceProvider _services;
    private readonly HttpClient _httpClient;
    private readonly CheckeeClient _client;
    public readonly RecordingHttpHandler Http = new();
    public readonly CheckeeOutboundOptions Outbound;
    public readonly CheckeeOptions Options = new()
    {
        ApiKey = "test-only-key", ClientId = "test-only-client", BaseUrl = "https://checkee.invalid",
        OnlyPassCodes = true,
    };
    public readonly ScanIntakeGate Gate = new();
    public readonly IScanRecordWriter Writer;
    public readonly CheckeeSendWorker Worker;
    private readonly IScanRecordQuery _query;

    public SendTestSession(string productGtin)
    {
        Directory.CreateDirectory(_root);
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Scans:DbPath"] = Path.Combine(_root, "scans.db"), ["Scans:FlushIntervalMs"] = "10",
        }).Build();
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddScanStore(configuration);
        _services = services.BuildServiceProvider();
        Writer = _services.GetRequiredService<IScanRecordWriter>();
        _query = _services.GetRequiredService<IScanRecordQuery>();
        _httpClient = new HttpClient(Http) { BaseAddress = new Uri("https://checkee.invalid/") };
        _client = new CheckeeClient(_httpClient, Options);
        Outbound = new CheckeeOutboundOptions { ProductGtin = productGtin };
        Worker = new CheckeeSendWorker(_client, _services.GetRequiredService<IScanSendStatusStore>(), Writer,
            Microsoft.Extensions.Options.Options.Create(Options), Microsoft.Extensions.Options.Options.Create(Outbound),
            Gate, NullLogger<CheckeeSendWorker>.Instance);
    }

    public async Task<IReadOnlyList<ScanRecordEntity>> RowsAsync()
    {
        await Writer.FlushAsync();
        return (await _query.GetAsync(pageSize: 100)).Items;
    }

    public async ValueTask DisposeAsync()
    {
        Worker.Dispose();
        _client.Dispose();
        _httpClient.Dispose();
        await _services.DisposeAsync();
        SqliteConnection.ClearAllPools();
        Directory.Delete(_root, recursive: true);
    }
}

sealed class RecordingHttpHandler : HttpMessageHandler
{
    public readonly List<JsonElement> Payloads = [];
    public HttpStatusCode ActivateStatus = HttpStatusCode.OK;
    public bool ThrowNetwork;
    public bool BusinessFailure;
    public int RequestCount;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        RequestCount++;
        if (ThrowNetwork) throw new HttpRequestException("Offline test transport");
        if (request.Method == HttpMethod.Get && request.RequestUri!.AbsolutePath == "/onboarding/token")
        {
            return Json(HttpStatusCode.OK, """{"code":200,"status":true,"data":{"token":"accessToken=test-only-token"}}""");
        }
        if (request.Method != HttpMethod.Post || request.RequestUri!.AbsolutePath != "/middleware-api/CTEs/active")
            throw new InvalidOperationException("Unexpected Checkee request: " + request.RequestUri);

        using var payload = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
        Payloads.Add(payload.RootElement.Clone());
        return ActivateStatus == HttpStatusCode.OK && !BusinessFailure
            ? Json(ActivateStatus, """{"code":200,"status":true,"message":"accepted","data":{}}""")
            : Json(ActivateStatus, """{"code":400,"status":false,"message":"NOTFOUND_SERIAL","data":null}""");
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body) => new(status)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };
}
