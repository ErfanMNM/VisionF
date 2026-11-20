using System.Net;
using ClassLibraryMT.Checkee;
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

sealed class Fixture : IDisposable
{
    public ScanIntakeGate Intake { get; } = new();
    public FakeStore Store { get; } = new();
    public FakeWriter Writer { get; } = new();
    public FakeHttp Http { get; } = new();
    public CheckeeSendWorker Worker { get; }
    private readonly HttpClient _http;
    private readonly CheckeeClient _client;

    public Fixture()
    {
        var options = new CheckeeOptions { ApiKey = "fake", ClientId = "fake", BaseUrl = "https://checkee.test" };
        _http = new HttpClient(Http) { BaseAddress = new Uri("https://checkee.test/") };
        _client = new CheckeeClient(_http, options);
        Worker = new CheckeeSendWorker(_client, Store, Writer, Options.Create(options), Intake,
            NullLogger<CheckeeSendWorker>.Instance);
    }

    public void Dispose() { Worker.Dispose(); _client.Dispose(); _http.Dispose(); }
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
        return Task.FromResult<IReadOnlyList<ScanRecordEntity>>([new ScanRecordEntity { Id = 1, Code = "TEST", Gtin = "1234", Serial = "001", BatchNo = "BATCH" }]);
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
    public Task<IReadOnlyList<ScanRecordEntity>> GetBySendStatusAsync(ScanSendFilter? filter = null, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<ScanRecordEntity>>([]);
    public Task<Dictionary<string, long>> CountBySendStatusAsync(CancellationToken cancellationToken = default) => Task.FromResult(new Dictionary<string, long>());
}
