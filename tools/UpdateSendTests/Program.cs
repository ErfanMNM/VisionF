using System.Net;
using ClassLibraryMT.Checkee;
using ClassLibraryMT.Po;
using Microsoft.Data.Sqlite;
using ClassLibraryMT.Scans;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TanTienQR.Services;

using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
var passed = 0;

void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
    passed++;
    Console.WriteLine("[OK] " + message);
}

using var fixture = new Fixture();
fixture.Intake.SetSuspended(true);
Check(await fixture.Worker.SendPendingAsync(timeout.Token) == 0, "Bảo trì chặn lượt gửi mới ngay tại worker");
Check(fixture.Store.ReadCount == 0 && fixture.Http.RequestCount == 0 && fixture.Writer.FlushCount == 0,
    "Khởi động thử không đọc/ghi trạng thái hoặc gọi HTTP gửi mã");
await fixture.Worker.DrainAsync(timeout.Token);

fixture.Intake.SetSuspended(false);
var first = fixture.Worker.SendPendingAsync(timeout.Token);
await fixture.Http.ActivationStarted.Task.WaitAsync(timeout.Token);
var queued = fixture.Worker.SendPendingAsync(timeout.Token);
Check(fixture.Store.ReadCount == 1, "Manual và timer dùng cùng khóa, không đọc/gửi trùng tập Pending");
fixture.Intake.SetSuspended(true);
var drain = fixture.Worker.DrainAsync(timeout.Token);
Check(!drain.IsCompleted, "Drain chờ HTTP đang chạy");
Check(await fixture.Worker.SendPendingAsync(timeout.Token) == 0, "Bảo trì từ chối gửi thêm trong khi đang drain");

fixture.Http.AllowActivation.TrySetResult();
await fixture.Store.StatusWriteStarted.Task.WaitAsync(timeout.Token);
Check(!drain.IsCompleted, "Drain tiếp tục chờ ghi status SQLite sau khi HTTP thành công");
fixture.Store.AllowStatusWrite.TrySetResult();
Check(await first == 1, "Lượt đã nhận hoàn tất và ghi trạng thái thành công");
Check(await queued == 0, "Lượt đang đợi khóa được bỏ khi bắt đầu bảo trì");
await drain.WaitAsync(timeout.Token);
Check(fixture.Store.Sent == 1 && fixture.Http.ActivationCount == 1, "Chỉ một lượt được kích hoạt trước bàn giao");

fixture.Intake.SetSuspended(false);
fixture.Store.ThrowOnRead = true;
try
{
    await fixture.Worker.SendPendingAsync(timeout.Token);
    throw new Exception("Không thấy lỗi store giả lập.");
}
catch (IOException) { }
fixture.Intake.SetSuspended(true);
await fixture.Worker.DrainAsync(timeout.Token);
Check(true, "Lỗi gửi vẫn giải phóng khóa và bộ đếm để drain kết thúc");

using var retryFixture = new Fixture();
retryFixture.Intake.SetSuspended(true);
Check(await retryFixture.Worker.ResendFailedAsync([1], timeout.Token) == (0, 0) && retryFixture.Store.ResetCount == 0,
    "Bảo trì chặn cả thao tác reset mã lỗi trước khi gửi lại");
retryFixture.Intake.SetSuspended(false);
var retry = retryFixture.Worker.ResendFailedAsync([1], timeout.Token);
await retryFixture.Store.ResetStarted.Task.WaitAsync(timeout.Token);
retryFixture.Intake.SetSuspended(true);
var retryDrain = retryFixture.Worker.DrainAsync(timeout.Token);
Check(!retryDrain.IsCompleted, "Drain chờ cả ResetToPending đang chạy từ UI");
retryFixture.Store.AllowReset.TrySetResult();
await retryFixture.Http.ActivationStarted.Task.WaitAsync(timeout.Token);
Check(!retryDrain.IsCompleted, "Lượt retry được nhận trước bảo trì hoàn tất cả gửi và ghi trạng thái");
retryFixture.Http.AllowActivation.TrySetResult();
retryFixture.Store.AllowStatusWrite.TrySetResult();
Check(await retry == (1, 1), "Retry trả đúng số bản ghi reset và gửi");
await retryDrain.WaitAsync(timeout.Token);
Console.WriteLine($"Đạt {passed} kiểm thử dừng luồng gửi khi cập nhật; không dùng mạng/DB thật.");

// Kiểm tra phần xác thực của CheckeeClient: header gửi kèm + làm mới token khi 401.
using var authHandler = new AuthFakeHttp();
using var authHttp = new HttpClient(authHandler) { BaseAddress = new Uri("https://checkee.test/") };
var authOptions = new CheckeeOptions
{
    BaseUrl = "https://checkee.test",
    ApiKey = "KEY-123",
    ClientId = "CLIENT-1",
};
using var authClient = new CheckeeClient(authHttp, authOptions);
var authResult = await authClient.ActivateAsync(
    "08934567890123", "LOT-1", "DECLARE", ["SER-1"], timeout.Token);
Check(authResult.Ok, "Kích hoạt thành công sau khi làm mới token khi gặp 401");
Check(authHandler.TokenRequests == 2 && authHandler.Activations == 2,
    "401 gọi lại token và gửi lại đúng 1 lần");
Check(authHandler.LastAuthorization == "Bearer accessToken=fake",
    "Header Authorization đúng chuẩn Bearer accessToken=<JWT>");
Check(authHandler.LastApiKey == "KEY-123",
    "Header x-api-key được gắn khi gửi kích hoạt");
Console.WriteLine($"Đạt {passed} kiểm thử tổng cộng.");

sealed class AuthFakeHttp : HttpMessageHandler
{
    public int TokenRequests;
    public int Activations;
    public string? LastAuthorization;
    public string? LastApiKey;

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.Method == HttpMethod.Get)
        {
            Interlocked.Increment(ref TokenRequests);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"code":200,"status":true,"data":{"token":"accessToken=fake"}}"""),
            });
        }

        var activation = Interlocked.Increment(ref Activations);
        LastAuthorization = request.Headers.TryGetValues("Authorization", out var auth)
            ? string.Join(",", auth)
            : null;
        LastApiKey = request.Headers.TryGetValues("x-api-key", out var key)
            ? string.Join(",", key)
            : null;

        var unauthorized = activation == 1;
        var status = unauthorized ? HttpStatusCode.Unauthorized : HttpStatusCode.OK;
        var body = unauthorized
            ? """{"code":401,"status":false,"message":"UNAUTHORIZED"}"""
            : """{"code":200,"status":true,"message":"OK","data":{}}""";
        return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
    }
}

sealed class Fixture : IDisposable
{
    public ScanIntakeGate Intake { get; } = new();
    public FakeStore Store { get; } = new();
    public FakeWriter Writer { get; } = new();
    public FakeHttp Http { get; } = new();
    public CheckeeSendWorker Worker { get; }
    private readonly HttpClient _http;
    private readonly CheckeeClient _client;
    private readonly string _root=Path.Combine(Path.GetTempPath(), "TEST-ASYNC-PO-20261127-UpdateSend-"+Guid.NewGuid().ToString("N"));

    public Fixture()
    {
        var options = new CheckeeOptions { ApiKey = "fake", ClientId = "fake", BaseUrl = "https://checkee.test" };
        _http = new HttpClient(Http) { BaseAddress = new Uri("https://checkee.test/") };
        _client = new CheckeeClient(_http, options);
        Directory.CreateDirectory(_root);
        var poStore=new PoStore(Path.Combine(_root,"po"));
        var po=new ProductionOrderService(new EmptyPoClient(),poStore,new PoHistoryStore(poStore.DbRoot),new PoCodeCache());
        var order=new ProductionOrder { OrderNo=FakeStore.OrderNo,CreatedUtc=FakeStore.Created,Gtin=FakeStore.Gtin,OrderQty=2 };
        poStore.EnsurePoDbAsync(order,[new PoCode {CodeContent=FakeStore.Code},new PoCode {CodeContent=FakeStore.Code+"2"}]).GetAwaiter().GetResult();
        if(!po.StartProductionAsync(order.OrderNo).GetAwaiter().GetResult()) throw new Exception("Fixture cannot start sandbox PO");
        Worker = new CheckeeSendWorker(_client, Store, Writer, Options.Create(options),
            Options.Create(new CheckeeOutboundOptions { AuditLogPath=Path.Combine(_root,"audit.jsonl") }), Intake, po,
            NullLogger<CheckeeSendWorker>.Instance);
    }

    public void Dispose()
    {
        Worker.Dispose(); _client.Dispose(); _http.Dispose(); SqliteConnection.ClearAllPools();
        var target=Path.GetFullPath(_root);
        if(!string.Equals(Path.GetDirectoryName(target),Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar),StringComparison.OrdinalIgnoreCase)
            || !Path.GetFileName(target).StartsWith("TEST-ASYNC-PO-20261127-UpdateSend-")) throw new Exception("Unsafe cleanup path");
        Directory.Delete(target,true);
    }
}

sealed class FakeHttp : HttpMessageHandler
{
    public int RequestCount;
    public int ActivationCount;
    public TaskCompletionSource ActivationStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource AllowActivation { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref RequestCount);
        if (request.Method == HttpMethod.Get)
            return new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent("""{"code":200,"status":true,"data":{"token":"accessToken=fake"}}""") };
        Interlocked.Increment(ref ActivationCount);
        ActivationStarted.TrySetResult();
        await AllowActivation.Task.WaitAsync(cancellationToken);
        return new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StringContent("""{"code":200,"status":true,"message":"OK","data":{}}""") };
    }
}

sealed class FakeWriter : IScanRecordWriter
{
    public int FlushCount;
    public void Enqueue(ScanRecordEntity record) { }
    public Task FlushAsync(CancellationToken cancellationToken = default) { Interlocked.Increment(ref FlushCount); return Task.CompletedTask; }
    public Task VerifyHealthyAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
}

sealed class FakeStore : IScanSendStatusStore
{
    public const string Gtin="08935348800215";
    public const string Code="https://resolver.invalid/01/08935348800215/21/TEST";
    public const string OrderNo="TEST-ASYNC-PO-20261127-UPDATESEND";
    public static readonly DateTime Created=new(2026,11,27,0,0,0,DateTimeKind.Utc);
    private static ScanRecordEntity Row()=>new() { Id=1,CodeContent=Code,Gtin=Gtin,Status=ScanStatuses.Pass,OrderNo=OrderNo,OrderCreatedUtc=Created,PoAccepted=true };
    public Task<IReadOnlyList<ScanRecordEntity>> GetPendingForOrderAsync(string orderNo,DateTime orderCreatedUtc,string gtin,int limit=100,CancellationToken cancellationToken=default)
        => GetPendingAsync(true,limit,cancellationToken);
    public Task<long> BeginSendAttemptAsync(IReadOnlyList<ScanRecordEntity> records, string productGtin,
        string batchCode, string serialType, CancellationToken cancellationToken = default) => Task.FromResult(1L);
    public Task CompleteSendAttemptAsync(long attemptId, string status, int statusCode, string message,
        CancellationToken cancellationToken = default) => status == CheckeeSendAttemptStatuses.Sent
            ? MarkSentAsync(1, message, cancellationToken) : Task.CompletedTask;
    public Task<IReadOnlyList<CheckeeSendAttempt>> GetRecentSendAttemptsAsync(int limit = 60,
        CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<CheckeeSendAttempt>>([]);
    public Task<IReadOnlyList<CheckeeSendAttemptItem>> GetSendAttemptItemsAsync(long attemptId,
        CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<CheckeeSendAttemptItem>>([]);
    public int ReadCount;
    public int Sent;
    public bool ThrowOnRead;
    public int ResetCount;
    public TaskCompletionSource ResetStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource AllowReset { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource StatusWriteStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource AllowStatusWrite { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task<IReadOnlyList<ScanRecordEntity>> GetPendingAsync(bool onlyPass = true, int limit = 100, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref ReadCount);
        if (ThrowOnRead) throw new IOException("Lỗi đọc giả lập.");
        return Task.FromResult<IReadOnlyList<ScanRecordEntity>>(Sent==0 ? [Row()] : []);
    }
    public async Task MarkSentAsync(long id, string message, CancellationToken cancellationToken = default)
    {
        StatusWriteStarted.TrySetResult();
        await AllowStatusWrite.Task.WaitAsync(cancellationToken);
        Interlocked.Increment(ref Sent);
    }
    public Task MarkFailedAsync(long id, int statusCode, string message, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task MarkPendingAsync(long id, string message, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public async Task<int> ResetToPendingAsync(IReadOnlyList<long> ids, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref ResetCount);
        ResetStarted.TrySetResult();
        await AllowReset.Task.WaitAsync(cancellationToken);
        return ids.Count;
    }
    public Task<IReadOnlyList<ScanRecordEntity>> GetBySendStatusAsync(ScanSendFilter? filter = null, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<ScanRecordEntity>>(Sent==0 ? [Row()] : []);
    public Task<Dictionary<string, long>> CountBySendStatusAsync(CancellationToken cancellationToken = default) => Task.FromResult(new Dictionary<string, long>());
}

sealed class EmptyPoClient : IPoClient
{
    public Task<IReadOnlyList<ProductionOrder>> GetOrdersAsync(bool includeCompleted=false,CancellationToken ct=default)=>Task.FromResult<IReadOnlyList<ProductionOrder>>([]);
    public Task<IReadOnlyList<PoCode>> GetCodesAsync(string orderNo,CancellationToken ct=default)=>Task.FromResult<IReadOnlyList<PoCode>>([]);
    public Task<ProductionOrder?> CreateOrderAsync(string orderNo,int orderQty,CancellationToken ct=default)=>Task.FromResult<ProductionOrder?>(null);
}
