using System.Net;
using System.Text;
using System.Text.Json;
using ClassLibraryMT.Checkee;
using ClassLibraryMT.Po;
using ClassLibraryMT.Scans;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TanTienQR.Services;

var checks=0;
void Check(bool ok, string message) { if (!ok) throw new InvalidOperationException("[FAIL] " + message); checks++; Console.WriteLine("[OK] " + message); }
const string gtin="08935348800215";
static string Code(string serial, string product=gtin) => $"https://resolver.invalid/01/{product}/21/{serial}";

await using (var f=await SendTestSession.CreateAsync())
{
    f.Options.BatchSize=1;
    f.Options.OnlyPassCodes=false; // legacy settings cannot weaken the invariant
    for(var i=0;i<8;i++) await f.AddAsync(Code("old"+i), order:"OLD-PO");
    await f.AddAsync(Code("reuse"), created:f.Order.CreatedUtc.AddDays(-1));
    await f.AddAsync(Code("other", "09506000134352"));
    await f.AddAsync(Code("bad"), status:ScanStatuses.InvalidForOrder);
    await f.AddAsync(Code("legacy"), accepted:false);
    await f.AddAsync(Code("unready"), complete:false);
    await f.AddAsync(Code("eligible"));
    Check(await f.Worker.SendPendingAsync()==1, "SQL lọc Pass/accepted/PO/CreatedUtc/GTIN trước LIMIT, backlog cũ không gây đói batch");
    Check(f.Http.Payloads.Single().GetProperty("serialNumbers")[0].GetString()=="eligible", "Chỉ serial cùng GTIN của PO hiện hành nằm trong request");
    var rows=await f.RowsAsync();
    Check(rows.Count(r=>r.SendStatus==ScanSendStatuses.Sent)==1 && rows.Single(r=>r.CodeContent==Code("eligible")).PoAccepted,
        "Không đánh dấu Sent cho mã khác GTIN, PO cũ, PO reuse, non-Pass, legacy hoặc outbox chưa xong");
    Check(await f.Worker.SendPendingAsync()==0 && f.Http.Payloads.Count==1, "Chu kỳ sau không gửi lại mã đã ACK");
}

await using (var f=await SendTestSession.CreateAsync(new() { ["LoTnO"]="  LOT-FROM-PO  " }))
{
    await f.AddAsync(Code("metadata"));
    Check(await f.Worker.SendPendingAsync()==1, "Gửi mã Pass có metadata PO");
    var payload=f.Http.Payloads.Single();
    Check(payload.GetProperty("GTIN").GetString()==gtin && payload.GetProperty("batchCode").GetString()=="LOT-FROM-PO",
        "GTIN và Lot lấy từ snapshot PO; khóa Lot không phân biệt hoa thường");
    Check(payload.GetProperty("fromIndex").GetInt32()==0 && payload.GetProperty("toIndex").GetInt32()==0,
        "Payload giữ hai chỉ số số 0 và serialType");
    var audit=await File.ReadAllTextAsync(f.AuditPath);
    Check(audit.Split('\n',StringSplitOptions.RemoveEmptyEntries).Length==2 && audit.Contains("Started") && audit.Contains("Sent"),
        "File audit riêng ghi bền intent và kết quả HTTP");
    Check(!audit.Contains(f.Options.ApiKey) && !audit.Contains("test-only-token") && audit.Contains("orderCreatedUtc") && audit.Contains("recordIds"),
        "Audit truy vết attempt/PO/recordIds, không ghi ApiKey/token");
    using var reopened=f.OpenAnotherStore();
    var history=(await reopened.GetRequiredService<IScanSendStatusStore>().GetRecentSendAttemptsAsync()).Single();
    Check(history.OkCount==1 && history.CompletedUtc.HasValue,
        "Mở lại SQLite còn snapshot OK và thời điểm kết quả độc lập audit file");
}

await using (var f=await SendTestSession.CreateAsync())
{
    await f.AddAsync(Code("fallback"));
    await f.Worker.SendPendingAsync();
    Check(f.Http.Payloads.Single().GetProperty("batchCode").GetString()==f.Order.OrderNo, "Không có Lot rõ ràng thì fallback OrderNo");
}

await using (var f=await SendTestSession.CreateAsync(enabled:false))
{
    await f.AddAsync(Code("disabled"));
    Check(await f.Worker.SendPendingAsync()==0 && f.Http.RequestCount==0, "Tắt gửi chặn manual và HTTP");
    await f.Worker.StartAsync(CancellationToken.None);
    f.Worker.SendingEnabled=true;
    using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(10));
    while(f.Http.Payloads.Count==0) await Task.Delay(20,timeout.Token);
    await f.Worker.StopAsync(timeout.Token);
    Check((await f.RowsAsync()).Single().SendStatus==ScanSendStatuses.Sent, "Bật runtime đánh thức worker khởi động disabled, không restart");
}

await using (var f=await SendTestSession.CreateAsync(enabled:false, codes:[Code("last")]))
{
    // Exactly mirror the coordinator: accept -> durable scan/outbox -> PO batch -> mark ready -> auto-complete.
    var eventId=Guid.NewGuid().ToString("N");
    var accepted=f.Po.AcceptManualCode(Code("last"),DateTime.UtcNow,eventId);
    Check(accepted.Mutation is not null, "Mã cuối được PO thật accept và cấp immutable mutation");
    var record=f.Record(Code("last"));
    await f.Persistence.PersistBatchAsync([new(eventId,record,f.Order.CreatedUtc,true)]);
    await f.PoStore.MarkScannedBatchAsync([accepted.Mutation!]);
    await f.Persistence.CompleteAsync([eventId]);
    Check(await f.Po.CompletePendingAsync() && f.Po.CurrentOrder is null, "Persistence auto-complete PO trước lần gửi kế tiếp");
    f.Worker.SendingEnabled=true;
    Check(await f.Worker.SendPendingAsync()==1, "Sending session đã quan sát Running kể cả disabled vẫn drain được mã cuối sau auto-complete");
    await f.AddAsync(Code("post-complete-fixture"));
    using var freshWorker=f.NewWorker();
    Check(await freshWorker.SendPendingAsync()==0, "Worker mới startup không tự chọn historical completed PO");
}

await using (var f=await SendTestSession.CreateAsync(codes:[Code("first")]))
{
    var eventId=Guid.NewGuid().ToString("N");
    var result=f.Po.AcceptManualCode(Code("first"),DateTime.UtcNow,eventId);
    await f.Persistence.PersistBatchAsync([new(eventId,f.Record(Code("first")),f.Order.CreatedUtc,true)]);
    await f.PoStore.MarkScannedBatchAsync([result.Mutation!]); await f.Persistence.CompleteAsync([eventId]);
    await f.Po.CompletePendingAsync();
    var next=new ProductionOrder { OrderNo="NEXT-PO", CreatedUtc=f.Order.CreatedUtc.AddDays(1), Gtin=gtin, OrderQty=1 };
    await f.PoStore.EnsurePoDbAsync(next,[new PoCode {CodeContent=Code("next")}]);
    Check(await f.Po.StartProductionAsync(next.OrderNo), "PO mới bắt đầu sau auto-complete");
    await f.AddAsync(Code("next"),order:next.OrderNo,created:next.CreatedUtc);
    Check(await f.Worker.SendPendingAsync()==1 && f.Http.Payloads.Single().GetProperty("serialNumbers")[0].GetString()=="next",
        "PO mới thay sending-session; backlog PO cũ không lẫn sang batch mới");
    Check((await f.RowsAsync()).Single(r=>r.OrderNo==f.Order.OrderNo).SendStatus!=ScanSendStatuses.Sent, "Backlog session cũ giữ nguyên trạng thái chưa gửi");
}

await using (var f=await SendTestSession.CreateAsync())
{
    await f.AddAsync(Code("cancelled")); await f.Po.CancelProductionAsync();
    Check(await f.Worker.SendPendingAsync()==0 && f.Http.RequestCount==0, "Huỷ PO chặn drain session bị huỷ");
}

await using (var f=await SendTestSession.CreateAsync())
{
    f.Http.ActivateStatus=HttpStatusCode.BadRequest; await f.AddAsync(Code("retry"));
    Check(await f.Worker.SendPendingAsync()==0, "HTTP từ chối không tính Sent");
    var row=(await f.RowsAsync()).Single(); var first=(await f.Store.GetRecentSendAttemptsAsync()).Single();
    Check(row.SendStatus==ScanSendStatuses.Failed && first.FailCount==1, "Lưu Failed và snapshot lỗi tách scan Pass");
    f.Worker.SendingEnabled=false;
    Check(await f.Worker.ResendFailedAsync([row.Id])==(0,0) && (await f.RowsAsync()).Single().SendStatus==ScanSendStatuses.Failed,
        "Runtime disable chặn resend trước reset Failed");
    f.Worker.SendingEnabled=true; f.Http.ActivateStatus=HttpStatusCode.OK;
    Check(await f.Worker.ResendFailedAsync([row.Id])==(1,1), "Retry hợp lệ đi qua cùng gate");
    Check((await f.Store.GetSendAttemptItemsAsync(first.Id)).Single().Status==CheckeeSendAttemptStatuses.Failed
        && (await f.Store.GetRecentSendAttemptsAsync()).Count==2, "Retry tạo snapshot mới và giữ kết quả lần cũ");
}

await using (var f=await SendTestSession.CreateAsync())
{
    f.Http.ThrowNetwork=true; await f.AddAsync(Code("offline"));
    Check(await f.Worker.SendPendingAsync()==0 && (await f.RowsAsync()).Single().SendStatus==ScanSendStatuses.Pending,
        "Lỗi transport giữ Pending và audit retry outcome");
    f.Http.ThrowNetwork=false;
    Check(await f.Worker.SendPendingAsync()==1, "Mạng hồi phục gửi lại Pending");
}

foreach(var body in new[]{"not-json","{}","{\"data\":{}}"})
{
    await using var f=await SendTestSession.CreateAsync(); await f.AddAsync(Code("unknown")); f.Http.ActivateBody=body;
    Check(await f.Worker.SendPendingAsync()==0 && (await f.Store.GetRecentSendAttemptsAsync()).Single().UnknownCount==1,
        "HTTP200 thiếu ACK lưu Unknown, không đánh dấu Sent: "+body);
    await f.Worker.SendPendingAsync(); Check(f.Http.Payloads.Count==1,"Unknown không auto-retry");
}

await using (var f=await SendTestSession.CreateAsync())
{
    await f.AddAsync(Code("mismatch"), metadataGtin:gtin, contentOverride:Code("mismatch","09506000134352"));
    Check(await f.Worker.SendPendingAsync()==0 && f.Http.RequestCount==0 && (await f.RowsAsync()).Single().SendStatus==ScanSendStatuses.Pending,
        "GTIN trong nội dung khác PO dù metadata giả khớp vẫn bị loại request, không Sent");
}

await using (var f=await SendTestSession.CreateAsync())
{
    await f.AddAsync(Code("audit-failure")); f.Outbound.AuditLogPath=f.Root; // directory cannot be opened as append file
    Check(await f.Worker.SendPendingAsync()==0 && f.Http.RequestCount==0
        && (await f.Store.GetRecentSendAttemptsAsync()).Single().UnknownCount==1,
        "Audit file lỗi thì fail closed trước HTTP và giữ SQLite Unknown để đối soát");
}

await using (var f=await SendTestSession.CreateAsync())
{
    await f.AddAsync(Code("cancel-http")); f.Http.ActivationStarted=new(); f.Http.AllowActivation=new();
    using var ct=new CancellationTokenSource(); using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(10));
    var send=f.Worker.SendPendingAsync(ct.Token); await f.Http.ActivationStarted.Task.WaitAsync(deadline.Token); ct.Cancel();
    try { await send; throw new InvalidOperationException("Cancellation expected"); } catch(OperationCanceledException) { }
    Check((await f.Store.GetRecentSendAttemptsAsync()).Single().UnknownCount==1
        && File.ReadAllText(f.AuditPath).Contains("Unknown"), "Hủy HTTP vẫn lưu Unknown vào SQLite và audit với token độc lập");
}

await using (var f=await SendTestSession.CreateAsync())
{
    await f.AddAsync(Code("retention"));
    for(var i=0;i<65;i++)
    {
        await f.SqlAsync("UPDATE ScanRecords SET SendStatus='Pending';");
        Check(await f.Worker.SendPendingAsync()==1,"Retention HTTP attempt "+i);
    }
    var history=await f.Store.GetRecentSendAttemptsAsync(5000);
    Check(history.Count==60 && history[0].Id==65 && history[^1].Id==6, "SQLite giữ đúng 60 snapshots mới nhất");
    var audit=File.ReadLines(f.AuditPath).Select(line=>JsonDocument.Parse(line)).ToArray();
    Check(audit.Length==130 && audit[0].RootElement.GetProperty("attemptId").GetInt64()==1
        && audit[0].RootElement.GetProperty("codes")[0].GetProperty("codeContent").GetString()==Code("retention")
        && audit[0].RootElement.GetProperty("codes")[0].GetProperty("serial").GetString()=="retention"
        && (await f.Store.GetSendAttemptItemsAsync(1)).Count==0,
        "Audit độc lập vẫn truy nguyên CodeContent/serial của attempt1 sau retention60 SQLite");
    foreach(var json in audit) json.Dispose();
    var sent=(await f.RowsAsync()).Single();
    try { await f.Store.BeginSendAttemptAsync([sent],gtin,"wrong","DECLARE"); throw new Exception("Stale begin accepted"); }
    catch(InvalidOperationException) { Check(true,"Transaction intent recheck chặn bản ghi đã Sent"); }
}

await using (var f=await SendTestSession.CreateAsync())
{
    await f.AddAsync(Code("redact"));
    f.Http.ActivateBody="{\"status\":false,\"message\":\"test-only-key test-only-client Bearer sensitive-token accessToken=sensitive-access\"}";
    await f.Worker.SendPendingAsync(); var audit=File.ReadAllText(f.AuditPath);
    Check(!audit.Contains("test-only-key") && !audit.Contains("test-only-client") && !audit.Contains("sensitive-token") && !audit.Contains("sensitive-access"),
        "Audit redact cả credential và Bearer/accessToken bị server echo trong response/error");
}

Check(CheckeeClient.ExtractSerialNumber("(01)"+gtin+"(21)AbC123")=="AbC123"
    && CheckeeClient.ExtractSerialNumber("]d201"+gtin+"21Gs1Serial\u001d")=="Gs1Serial",
    "Outbound tách serial GS1 parenthesized và element string, giữ hoa thường");

await using (var f=await SendTestSession.CreateAsync())
{
    var relative="/01/"+gtin+"/21/RelativeCase?next=/21/QUERY#fragment/21/IGNORE";
    Check(PoCodeParser.TryGetGtin(relative,out var parsed) && parsed==gtin,"Relative GS1 có query/fragment được PO parser chấp nhận");
    await f.AddAsync(relative);
    Check(await f.Worker.SendPendingAsync()==1
        && f.Http.Payloads.Single().GetProperty("serialNumbers")[0].GetString()=="RelativeCase"
        && (await f.RowsAsync()).Single().CodeContent==relative,
        "Request relative GS1 chỉ chứa serial path, giữ nguyên query/fragment trong identity local");
}

await using (var f=await SendTestSession.CreateAsync(enabled:false,codes:[Code("A-final")]))
{
    var eventId=Guid.NewGuid().ToString("N"); var accepted=f.Po.AcceptManualCode(Code("A-final"),DateTime.UtcNow,eventId);
    await f.Persistence.PersistBatchAsync([new(eventId,f.Record(Code("A-final")),f.Order.CreatedUtc,true)]);
    await f.PoStore.MarkScannedBatchAsync([accepted.Mutation!]); await f.Persistence.CompleteAsync([eventId]);
    Check(await f.Po.CompletePendingAsync(),"Session A auto-complete khi sending disabled");
    var b=new ProductionOrder { OrderNo="B-ALREADY-PROCESSED",CreatedUtc=f.Order.CreatedUtc.AddDays(1),Gtin=gtin,OrderQty=1 };
    await f.PoStore.EnsurePoDbAsync(b,[new PoCode {CodeContent=Code("B-processed")}]);
    await f.PoStore.MarkScannedBatchAsync([new(Guid.NewGuid().ToString("N"),b.OrderNo,b.CreatedUtc,Code("B-processed"),DateTime.UtcNow)]);
    Check(await f.Po.StartProductionAsync(b.OrderNo) && f.Po.CurrentOrder is null
        && f.Po.LastFinishedOrder?.OrderNo==b.OrderNo,"B đã processed auto-complete ngay Start trước RaiseChanged Running");
    f.Worker.SendingEnabled=true;
    Check(await f.Worker.SendPendingAsync()==0 && f.Http.RequestCount==0,
        "LastFinished khác identity vô hiệu sticky A; không tự chọn historical B và không drain nhầm A");
}

Console.WriteLine($"Đạt {checks} assertions Checkee PO identity, runtime, audit, outbox, lịch sử và HTTP giả; chỉ DB sandbox.");

sealed class SendTestSession : IAsyncDisposable
{
    public const string Gtin="08935348800215";
    public string Root {get;}=Path.Combine(Path.GetTempPath(),"TEST-ASYNC-PO-20261127-Checkee-"+Guid.NewGuid().ToString("N"));
    private readonly ServiceProvider _services;
    private readonly HttpClient _http;
    private readonly CheckeeClient _client;
    public RecordingHttpHandler Http {get;}=new();
    public CheckeeOptions Options {get;}=new() { BaseUrl="https://checkee.invalid", ApiKey="test-only-key", ClientId="test-only-client", SendIntervalMs=86400000 };
    public CheckeeOutboundOptions Outbound {get;}
    public ScanIntakeGate Gate {get;}=new();
    public ProductionOrder Order {get;}=new() { OrderNo="TEST-ASYNC-PO-20261127-CHECKEE", CreatedUtc=new(2026,11,27,0,0,0,DateTimeKind.Utc), Gtin=Gtin, OrderQty=10 };
    public ProductionOrderService Po {get;}
    public PoStore PoStore {get;}
    public IScanPersistenceStore Persistence=>_services.GetRequiredService<IScanPersistenceStore>();
    public IScanSendStatusStore Store=>_services.GetRequiredService<IScanSendStatusStore>();
    public CheckeeSendWorker Worker {get;}
    public string DbPath=>Path.Combine(Root,"scans.db");
    public string AuditPath=>Path.Combine(Root,"checkee-audit.jsonl");
    private SendTestSession(bool enabled, Dictionary<string,string>? additional, string[] codes)
    {
        Directory.CreateDirectory(Root);
        var services=new ServiceCollection(); var config=Configuration();
        services.AddSingleton<IConfiguration>(config); services.AddScanStore(config); _services=services.BuildServiceProvider();
        PoStore=new(Path.Combine(Root,"po")); Po=new(new EmptyPoClient(),PoStore,new PoHistoryStore(PoStore.DbRoot),new PoCodeCache());
        Order.Additional=additional ?? []; Order.OrderQty=codes.Length;
        _http=new(Http) { BaseAddress=new Uri("https://checkee.invalid/") }; _client=new(_http,Options);
        Outbound=new() { AuditLogPath=AuditPath }; Options.Enabled=enabled; Worker=NewWorker();
    }
    public static async Task<SendTestSession> CreateAsync(Dictionary<string,string>? additional=null,bool enabled=true,string[]? codes=null)
    {
        codes ??= Enumerable.Range(0,10).Select(i=>$"https://resolver.invalid/01/{Gtin}/21/fixture{i}").ToArray();
        var f=new SendTestSession(enabled,additional,codes);
        await f.PoStore.EnsurePoDbAsync(f.Order,codes.Select(c=>new PoCode { CodeContent=c }).ToArray());
        if(!await f.Po.StartProductionAsync(f.Order.OrderNo)) throw new InvalidOperationException("Fixture failed to start PO");
        return f;
    }
    public CheckeeSendWorker NewWorker()=>new(_client,Store,_services.GetRequiredService<IScanRecordWriter>(),
        Microsoft.Extensions.Options.Options.Create(Options),Microsoft.Extensions.Options.Options.Create(Outbound),Gate,Po,NullLogger<CheckeeSendWorker>.Instance);
    private IConfiguration Configuration()=>new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?> { ["Scans:DbPath"]=DbPath }).Build();
    public ServiceProvider OpenAnotherStore()
    {
        var services=new ServiceCollection();var config=Configuration();services.AddSingleton(config);services.AddScanStore(config);return services.BuildServiceProvider();
    }
    public ScanRecordEntity Record(string code)=>new() { CodeContent=code,Raw=code,Gtin=Gtin,Status=ScanStatuses.Pass,OrderNo=Order.OrderNo,ReceivedUtc=DateTime.UtcNow };
    public async Task AddAsync(string code,string? order=null,DateTime? created=null,string status=ScanStatuses.Pass,bool accepted=true,bool complete=true,string? metadataGtin=null,string? contentOverride=null)
    {
        var record=Record(contentOverride??code); record.OrderNo=order??Order.OrderNo;record.Status=status;
        if(metadataGtin is not null) record.Gtin=metadataGtin;
        else if(PoCodeParser.TryGetGtin(record.CodeContent,out var parsed)) record.Gtin=parsed;
        var id=Guid.NewGuid().ToString("N");
        await Persistence.PersistBatchAsync([new(id,record,accepted ? created??Order.CreatedUtc : null,accepted)]);
        if(accepted && complete) await Persistence.CompleteAsync([id]);
    }
    public Task<IReadOnlyList<ScanRecordEntity>> RowsAsync()=>Store.GetBySendStatusAsync(new() {Limit=5000});
    public async Task SqlAsync(string sql)
    {
        await using var db=new SqliteConnection("Data Source="+DbPath);await db.OpenAsync();await using var cmd=db.CreateCommand();cmd.CommandText=sql;await cmd.ExecuteNonQueryAsync();
    }
    public async ValueTask DisposeAsync()
    {
        Worker.Dispose();_client.Dispose();_http.Dispose();await _services.DisposeAsync();SqliteConnection.ClearAllPools();
        var target=Path.GetFullPath(Root);var parent=Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar);
        if(!string.Equals(Path.GetDirectoryName(target),parent,StringComparison.OrdinalIgnoreCase)||!Path.GetFileName(target).StartsWith("TEST-ASYNC-PO-20261127-Checkee-")) throw new Exception("Unsafe sandbox cleanup");
        Directory.Delete(target,true);
    }
}
sealed class EmptyPoClient : IPoClient
{
    public Task<IReadOnlyList<ProductionOrder>> GetOrdersAsync(bool includeCompleted=false,CancellationToken ct=default)=>Task.FromResult<IReadOnlyList<ProductionOrder>>([]);
    public Task<IReadOnlyList<PoCode>> GetCodesAsync(string orderNo,CancellationToken ct=default)=>Task.FromResult<IReadOnlyList<PoCode>>([]);
    public Task<ProductionOrder?> CreateOrderAsync(string orderNo,int orderQty,CancellationToken ct=default)=>Task.FromResult<ProductionOrder?>(null);
}

sealed class RecordingHttpHandler : HttpMessageHandler
{
    public readonly List<JsonElement> Payloads = [];
    public HttpStatusCode ActivateStatus = HttpStatusCode.OK;
    public bool ThrowNetwork;
    public bool BusinessFailure;
    public string? ActivateBody;
    public TaskCompletionSource? ActivationStarted;
    public TaskCompletionSource? AllowActivation;
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

        ActivationStarted?.TrySetResult();
        if (AllowActivation is not null) await AllowActivation.Task.WaitAsync(cancellationToken);
        using var payload = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
        Payloads.Add(payload.RootElement.Clone());
        if (ActivateBody is not null) return Json(ActivateStatus, ActivateBody);
        return ActivateStatus == HttpStatusCode.OK && !BusinessFailure
            ? Json(ActivateStatus, """{"code":200,"status":true,"message":"accepted","data":{}}""")
            : Json(ActivateStatus, """{"code":400,"status":false,"message":"NOTFOUND_SERIAL","data":null}""");
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body) => new(status)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };
}
