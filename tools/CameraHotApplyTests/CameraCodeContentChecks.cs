using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using ClassLibraryMT.Communications.CodeReaderProtocol;
using ClassLibraryMT.Communications.Plc;
using ClassLibraryMT.Po;
using ClassLibraryMT.Scans;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TanTienQR;
using TanTienQR.Models;
using TanTienQR.Services;

internal static class CameraCodeContentChecks
{
    public static async Task<int> RunAsync()
    {
        var passed = 0;
        void Check(bool condition, string description)
        {
            if (!condition) throw new InvalidOperationException("[FAIL] " + description);
            passed++;
            Console.WriteLine("[OK] " + description);
        }

        var root = Path.Combine(Path.GetTempPath(), "VisionF-CameraCodeContent-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var ct = timeout.Token;
        var store = new PoStore(root);
        var history = new PoHistoryStore(root);
        var client = new CodeContentPoClient();
        var seenHistory = new CodeContentHistory();
        ProductionOrderService NewService() => new(client, store, history, new PoCodeCache(), seenHistory);
        var po = NewService();
        const string plain = "PLAIN-CODE-42";
        const string firstUrl = "https://one.example/01/08934567890123/21/SHARED-SUFFIX";
        const string secondUrl = "https://two.example/01/08934567890123/21/SHARED-SUFFIX";
        const string acknowledged = "ACKNOWLEDGED-BUT-NOT-SCANNED";
        const string retryContent = "RETRY-AFTER-PLC-FAIL";
        var arbitrary = "  Việt Nam|part:01\u001danything?key=Value  " + new string('x', 1000) + "  ";
        var order = new ProductionOrder
        {
            OrderNo = "CAMERA-CONTENT-PO",
            OrderQty = 7,
            CreatedUtc = new DateTime(2026, 11, 26, 0, 0, 0, DateTimeKind.Utc),
        };
        await store.EnsurePoDbAsync(order,
        [
            new() { CodeContent = plain },
            new() { CodeContent = firstUrl },
            new() { CodeContent = secondUrl },
            new() { CodeContent = arbitrary },
            new() { CodeContent = acknowledged, ActivatedUtc = DateTime.UtcNow },
            new() { CodeContent = retryContent },
            // Keep one unscanned code so automatic completion does not close this PO.
            new() { CodeContent = "SPARE-CONTENT" },
        ], ct);
        await store.EnsurePoDbAsync(new ProductionOrder
        {
            OrderNo = "OTHER-CAMERA-PO", OrderQty = 1, CreatedUtc = order.CreatedUtc,
        }, [new() { CodeContent = "ONLY-IN-OTHER-PO" }], ct);

        using var plc = new CameraResultPlc();
        await using (var camera = await CameraPipeline.StartAsync(po, plc, ct))
        {
            var inactive = await camera.ScanAsync("BEFORE-START", ct);
            Check(inactive.Outcome == ScanOutcome.NoProductionOrder && !inactive.PlcWritten
                && plc.Writes.IsEmpty && camera.Saved.Last().PlcResult == ScanPlcResults.Skipped,
                "Camera chưa chạy PO bỏ qua mã, không ghi PLC");
            Check(await po.StartProductionAsync(order.OrderNo, ct), "Pipeline khởi động PO từ database local");

            plc.FailNextWrite();
            var rejectedByPlc = await camera.ScanAsync(retryContent, ct);
            Check(!rejectedByPlc.PlcWritten && !plc.Writes.Last().Acknowledged
                && plc.Writes.Last().Value == 1 && po.ScannedCount == 1
                && po.ValidateCode(retryContent) == PoCodeValidation.Valid
                && camera.Saved.Last().PlcResult == ScanPlcResults.Failed,
                "PLC từ chối Pass giữ mã available và số PO không tăng");
            var retry = await camera.ScanAsync(retryContent, ct);
            Check(retry is { Outcome: ScanOutcome.Pass, PlcWritten: true } && po.ScannedCount == 2
                && po.ValidateCode(retryContent) == PoCodeValidation.Duplicate,
                "Retry sau lỗi PLC được Pass thành công rồi mới commit PO");
            var retryDuplicate = await camera.ScanAsync(retryContent, ct);
            Check(retryDuplicate is { Outcome: ScanOutcome.Duplicate, PlcWritten: true }
                && plc.Writes.Last().Value == 0 && po.ScannedCount == 2,
                "Chỉ lượt retry đã được PLC ACK mới trở thành Duplicate");

            foreach (var content in new[] { plain, firstUrl, secondUrl, arbitrary })
            {
                var before = plc.Writes.Count;
                var record = await camera.ScanAsync(content, ct);
                Check(record is { Outcome: ScanOutcome.Pass, PlcWritten: true }
                    && record.CodeContent == content && plc.Writes.Count == before + 1
                    && plc.Writes.Last() is { Address: 12, Value: 1 }
                    && camera.Saved.Last() is { Status: ScanStatuses.Pass, PlcResult: ScanPlcResults.Ok } saved
                    && saved.CodeContent == content && saved.Raw == content && saved.OrderNo == order.OrderNo,
                    content == arbitrary
                        ? "Pipeline giữ nguyên mã dài Unicode/GS/khoảng trắng và Pass D12=1"
                        : content == secondUrl
                                ? "Hai URL khác nhau cùng đuôi serial đều Pass riêng biệt"
                                : content == firstUrl
                                    ? "Camera nhận URL nguyên vẹn làm CodeContent"
                                    : "Mã plain bất kỳ thuộc PO được Pass D12=1");
            }
            Check(po.ScannedCount == 6, "PO ghi nhận năm mã Pass và một mã đã kích hoạt, không gộp theo đoạn serial");
            var activated = await camera.ScanAsync(acknowledged, ct);
            Check(activated is { Outcome: ScanOutcome.Duplicate, PlcWritten: true }
                && plc.Writes.Last().Value == 0 && camera.Saved.Last().Status == ScanStatuses.Duplicate
                && po.ScannedCount == 6,
                "Mã import đã ActivatedUtc cho Duplicate dù chưa có ScannedUtc");

            var duplicate = await camera.ScanAsync(plain, ct);
            Check(duplicate is { Outcome: ScanOutcome.Duplicate, PlcWritten: true }
                && plc.Writes.Last().Value == 0 && camera.Saved.Last().Status == ScanStatuses.Duplicate
                && po.ScannedCount == 6, "Quét lại CodeContent đã dùng cho Duplicate và D12=0");

            foreach (var unknown in new[] { "UNKNOWN-CONTENT", "plain-code-42", "ONLY-IN-OTHER-PO" })
            {
                var record = await camera.ScanAsync(unknown, ct);
                Check(record is { Outcome: ScanOutcome.InvalidForOrder, PlcWritten: true }
                    && record.CodeContent == unknown && plc.Writes.Last().Value == 0
                    && camera.Saved.Last().Status == ScanStatuses.InvalidForOrder,
                    unknown == "plain-code-42"
                        ? "Dictionary phân biệt hoa thường, mã khác case là Không tồn tại"
                        : unknown == "ONLY-IN-OTHER-PO"
                            ? "Mã chỉ thuộc PO khác bị Không tồn tại trong PO đang chạy"
                            : "Mã không thuộc PO cho InvalidForOrder và D12=0");
            }
            foreach (var noRead in new[] { "FAIL", "NoRead", " fail " })
            {
                var record = await camera.ScanAsync(noRead, ct);
                Check(record is { Outcome: ScanOutcome.NoRead, PlcWritten: true }
                    && record.CodeContent == noRead && plc.Writes.Last().Value == 0
                    && camera.Saved.Last().Status == ScanStatuses.NoRead,
                    "Tín hiệu camera '" + noRead + "' cho NoRead và D12=0");
            }
            Check(client.ActivationCalls == 0, "Quyết định camera Pass/Fail hoàn toàn local, không gọi server");
        }

        // A fresh service/cache and fresh worker must reload durable per-PO scan state.
        var restored = NewService();
        Check(await restored.RestoreRunningOrderAsync(ct), "Khôi phục PO đang chạy bằng service/cache mới");
        await using (var camera = await CameraPipeline.StartAsync(restored, plc, ct))
        {
            var duplicate = await camera.ScanAsync(plain, ct);
            Check(duplicate is { Outcome: ScanOutcome.Duplicate, PlcWritten: true }
                && plc.Writes.Last().Value == 0 && restored.ScannedCount == 6,
                "Duplicate tồn tại qua restart worker/cache vì đọc lại database PO");

            seenHistory.Codes = [plain];
            Check(await restored.ActivatePendingAsync(ct) == 5 && !restored.IsHalted
                && client.ActivationCalls == 1,
                "Mã trong lịch sử PO khác không chặn kích hoạt trạng thái riêng của PO đang chạy");
            var independent = await camera.ScanAsync("SPARE-CONTENT", ct);
            Check(independent is { Outcome: ScanOutcome.Pass, PlcWritten: true }
                && !restored.IsProductionRunning && restored.LastFinishedScannedCount == 7
                && restored.LastFinishedOrder?.OrderNo == order.OrderNo
                && camera.Saved.Last().Status == ScanStatuses.Pass,
                "PO độc lập Pass mã cuối và hoàn tất đủ bảy mã đã quét hoặc kích hoạt");
        }
        var finishedMeta = await store.GetMetaAsync(order.OrderNo, order.CreatedUtc, ct);
        Check(finishedMeta[PoStore.MetaStatus] == ProductionOrderStatus.Completed.ToString()
            && finishedMeta.ContainsKey(PoStore.MetaCompletedUtc),
            "PO fixture tự hoàn tất bền vững trước khi kiểm tra PO tiếp theo");

        seenHistory.Codes = [];
        var finalOrder = new ProductionOrder
        {
            OrderNo = "CAMERA-LAST-CODE", OrderQty = 1, CreatedUtc = order.CreatedUtc,
        };
        const string finalContent = "LAST-CODE-COMPLETES-PO";
        await store.EnsurePoDbAsync(finalOrder, [new() { CodeContent = finalContent }], ct);
        var completing = NewService();
        Check(await completing.StartProductionAsync(finalOrder.OrderNo, ct), "Chuẩn bị PO một mã để kiểm tra tự hoàn tất");
        await using (var camera = await CameraPipeline.StartAsync(completing, plc, ct))
        {
            var last = await camera.ScanAsync(finalContent, ct);
            Check(last is { Outcome: ScanOutcome.Pass, PlcWritten: true } && completing.CurrentOrder is null
                && completing.LastFinishedOrder?.OrderNo == finalOrder.OrderNo
                && camera.Saved.Last().OrderNo == finalOrder.OrderNo,
                "Lượt Pass cuối vẫn lưu đúng OrderNo sau khi auto-complete xóa CurrentOrder");
        }

        var failedOrder = new ProductionOrder
        {
            OrderNo = "CAMERA-PERSISTENCE-FAIL", OrderQty = 2, CreatedUtc = order.CreatedUtc,
        };
        const string failedContent = "DB-FAIL-AFTER-PLC-PASS";
        await store.EnsurePoDbAsync(failedOrder,
            [new() { CodeContent = failedContent }, new() { CodeContent = "BLOCKED-AFTER-DB-FAIL" }], ct);
        var failAfterAck = NewService();
        Check(await failAfterAck.StartProductionAsync(failedOrder.OrderNo, ct), "Chuẩn bị PO kiểm tra lỗi commit sau PLC Pass");
        await using (var connection = new SqliteConnection($"Data Source={store.GetDbPath(failedOrder.OrderNo, failedOrder.CreatedUtc)}"))
        {
            await connection.OpenAsync(ct);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TRIGGER camera_fail_scan BEFORE UPDATE OF ScannedUtc ON PoCodes
                WHEN NEW.Code = 'DB-FAIL-AFTER-PLC-PASS'
                BEGIN SELECT RAISE(ABORT, 'camera test simulated commit failure after PLC ACK'); END;
                """;
            await command.ExecuteNonQueryAsync(ct);
        }
        await using (var camera = await CameraPipeline.StartAsync(failAfterAck, plc, ct))
        {
            var beforeFault = plc.Writes.Count;
            var failed = await camera.ScanAsync(failedContent, ct);
            var faultWrites = plc.Writes.Skip(beforeFault).ToArray();
            Check(failed is { Outcome: ScanOutcome.Halted, PlcWritten: true } && failAfterAck.IsHalted
                && camera.IsSuspended
                && faultWrites.Any(write => write is { Address: 12, Value: 1, Acknowledged: true })
                && faultWrites.Any(write => write is { Address: 16, Value: 0, Acknowledged: true })
                && camera.Errors.Any(error => error.Contains(failedContent, StringComparison.Ordinal)),
                "Commit PO lỗi sau PLC ACK khóa camera, Ready=0 và phát Halted/log đầy đủ để đối soát");
            var durable = await store.GetCodesWithFlagsAsync(failedOrder.OrderNo, failedOrder.CreatedUtc, ct);
            var failedFlags = durable.Single(pair => pair.Key == failedContent).Value;
            Check((failedFlags & PoCodeCache.Flags.Scanned) == 0
                && (failedFlags & PoCodeCache.Flags.PendingPass) != 0,
                "Commit sau PLC lỗi giữ durable PendingPass để đối soát, không xuất bản scanned giả");
            var writes = plc.Writes.Count;
            var saved = camera.Saved.Count;
            await camera.SendSuppressedAsync(failedContent, ct);
            await camera.SendSuppressedAsync("BLOCKED-AFTER-DB-FAIL", ct);
            Check(camera.IsSuspended && camera.Saved.Count == saved && plc.Writes.Count == writes,
                "Sau lỗi commit không replay Pass và không cho sản xuất mã tiếp theo");
        }
        var restartGuard = NewService();
        Check(await restartGuard.RestoreRunningOrderAsync(ct) && restartGuard.IsHalted
            && restartGuard.ValidateCode(failedContent) == PoCodeValidation.Halted
            && restartGuard.ScannedCount == 0
            && !await restartGuard.StartProductionAsync(failedOrder.OrderNo, ct),
            "Restart phát hiện PendingPass/halt marker và từ chối chạy lại PO cần đối soát");
        await using (var camera = await CameraPipeline.StartAsync(restartGuard, plc, ct))
        {
            var writes = plc.Writes.Count;
            var blocked = await camera.ScanAsync(failedContent, ct);
            Check(blocked is { Outcome: ScanOutcome.Halted, PlcWritten: false } && plc.Writes.Count == writes,
                "Camera sau restart không phát lại PLC Pass của mã có kết quả chưa đối soát");
        }

        // Completion metadata can fail after a successful, committed final Pass.
        var completionRoot = Path.Combine(root, "completion-fault");
        var completionStore = new PoStore(completionRoot);
        var completionHistory = new PoHistoryStore(completionRoot);
        ProductionOrderService CompletionService() => new(client, completionStore, completionHistory,
            new PoCodeCache(), new CodeContentHistory());
        var completionOrder = new ProductionOrder
        {
            OrderNo = "CAMERA-COMPLETION-FAULT", OrderQty = 1, CreatedUtc = order.CreatedUtc,
        };
        const string completionContent = "COMMITTED-PASS-COMPLETION-FAILED";
        await completionStore.EnsurePoDbAsync(completionOrder, [new() { CodeContent = completionContent }], ct);
        var completionPo = CompletionService();
        Check(await completionPo.StartProductionAsync(completionOrder.OrderNo, ct),
            "Chuẩn bị PO một mã kiểm tra lỗi metadata hoàn tất sau commit Pass");
        await using (var connection = new SqliteConnection($"Data Source={completionStore.GetDbPath(completionOrder.OrderNo, completionOrder.CreatedUtc)}"))
        {
            await connection.OpenAsync(ct);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TRIGGER camera_fail_completion BEFORE INSERT ON PoMeta
                WHEN NEW.Key = 'completedUtc'
                BEGIN SELECT RAISE(ABORT, 'camera test simulated completion metadata failure'); END;
                """;
            await command.ExecuteNonQueryAsync(ct);
        }
        await using (var camera = await CameraPipeline.StartAsync(completionPo, plc, ct))
        {
            var before = plc.Writes.Count;
            var committed = await camera.ScanAsync(completionContent, ct);
            Check(committed is { Outcome: ScanOutcome.Pass, PlcWritten: true }
                && camera.Saved.Last().Status == ScanStatuses.Pass && completionPo.ScannedCount == 1,
                "Lỗi hoàn tất PO không đổi Pass đã commit thành Halted hoặc Fail giả");
            Check(completionPo.IsHalted && camera.IsSuspended
                && plc.Writes.Skip(before).Any(write => write is { Address: 16, Value: 0, Acknowledged: true })
                && camera.Errors.Any(error => error.Contains(completionContent, StringComparison.Ordinal)),
                "Lỗi hoàn tất sau Pass đóng intake, Ready=0 và log đủ mã để đối soát");
            var flags = (await completionStore.GetCodesWithFlagsAsync(completionOrder.OrderNo, completionOrder.CreatedUtc, ct))
                .Single().Value;
            Check((flags & PoCodeCache.Flags.Scanned) != 0 && (flags & PoCodeCache.Flags.PendingPass) == 0,
                "Lỗi metadata hoàn tất giữ nguyên durable Scanned đã commit");
            var writes = plc.Writes.Count;
            var saved = camera.Saved.Count;
            await camera.SendSuppressedAsync(completionContent, ct);
            Check(plc.Writes.Count == writes && camera.Saved.Count == saved,
                "Lỗi hoàn tất không cho phát lại Pass đã commit");
        }
        var completionRestart = CompletionService();
        Check(await completionRestart.RestoreRunningOrderAsync(ct) && completionRestart.IsHalted
            && completionRestart.ScannedCount == 1,
            "Restart giữ halt và số mã đã commit khi metadata hoàn tất lỗi");

        // A PLC can execute Pass while its UDP acknowledgement is lost. Even
        // with generic retries configured, a production Pass must be sent once.
        var lostAckRoot = Path.Combine(root, "lost-ack");
        var lostAckStore = new PoStore(lostAckRoot);
        var lostAckHistory = new PoHistoryStore(lostAckRoot);
        ProductionOrderService LostAckService() => new(client, lostAckStore, lostAckHistory,
            new PoCodeCache(), new CodeContentHistory());
        var lostAckOrder = new ProductionOrder
        {
            OrderNo = "CAMERA-LOST-PLC-ACK", OrderQty = 2, CreatedUtc = order.CreatedUtc,
        };
        const string lostAckContent = "PLC-EXECUTED-PASS-BUT-ACK-LOST";
        await lostAckStore.EnsurePoDbAsync(lostAckOrder,
            [new() { CodeContent = lostAckContent }, new() { CodeContent = "AFTER-LOST-ACK" }], ct);
        var lostAckPo = LostAckService();
        Check(await lostAckPo.StartProductionAsync(lostAckOrder.OrderNo, ct),
            "Chuẩn bị PO kiểm tra PLC thực thi Pass nhưng mất ACK");
        await using (var camera = await CameraPipeline.StartAsync(lostAckPo, plc, ct, plcRetries: 2))
        {
            var before = plc.Writes.Count;
            plc.DropNextAcknowledgement(); // A second request would receive a successful ACK.
            var uncertain = await camera.ScanAsync(lostAckContent, ct);
            var actual = plc.Writes.Skip(before).ToArray();
            Check(uncertain.Outcome == ScanOutcome.Halted && lostAckPo.IsHalted && camera.IsSuspended
                && lostAckPo.ScannedCount == 0,
                "Mất ACK Pass giữ trạng thái chưa xác định, halt và đóng intake thay vì cho retry");
            Check(actual.Count(write => write is { Address: 12, Value: 1 }) == 1
                && actual.Any(write => write is { Address: 12, Value: 1, Applied: true, Acknowledged: false })
                && actual.Any(write => write is { Address: 16, Value: 0, Acknowledged: true }),
                "Pass chỉ phát một UDP dù Retries=2; PLC đã thực thi/mất ACK vẫn ép Ready=0");
            var flags = (await lostAckStore.GetCodesWithFlagsAsync(lostAckOrder.OrderNo, lostAckOrder.CreatedUtc, ct))
                .Single(pair => pair.Key == lostAckContent).Value;
            Check((flags & PoCodeCache.Flags.PendingPass) != 0 && (flags & PoCodeCache.Flags.Scanned) == 0
                && camera.Errors.Any(error => error.Contains(lostAckContent, StringComparison.Ordinal)),
                "Mất ACK lưu durable PendingPass và log đủ mã để đối soát, không xuất bản Pass giả");
            var writes = plc.Writes.Count;
            var saved = camera.Saved.Count;
            await camera.SendSuppressedAsync(lostAckContent, ct);
            Check(plc.Writes.Count == writes && camera.Saved.Count == saved,
                "Intake đóng ngăn replay ngay sau mất ACK");
        }
        var lostAckRestart = LostAckService();
        Check(await lostAckRestart.RestoreRunningOrderAsync(ct) && lostAckRestart.IsHalted
            && lostAckRestart.ValidateCode(lostAckContent) == PoCodeValidation.Halted
            && !await lostAckRestart.StartProductionAsync(lostAckOrder.OrderNo, ct),
            "Restart giữ quarantine của mã PLC có thể đã nhận Pass");
        await using (var camera = await CameraPipeline.StartAsync(lostAckRestart, plc, ct, plcRetries: 2))
        {
            var writes = plc.Writes.Count;
            Check((await camera.ScanAsync(lostAckContent, ct)).Outcome == ScanOutcome.Halted
                && plc.Writes.Count == writes,
                "Sau restart không gửi lại Pass của mã mất ACK");
        }
        SqliteConnection.ClearAllPools();
        Directory.Delete(root, recursive: true);
        return passed;
    }
}

/// <summary>Uses the real camera socket, queue, PO service, PLC writer and UI feed.</summary>
internal sealed class CameraPipeline : IAsyncDisposable
{
    private static readonly Assembly AppAssembly = typeof(App).Assembly;
    private readonly Reader _reader;
    private readonly BackgroundService _worker;
    private readonly TcpListener _listener;
    private readonly TcpClient _cameraSocket;
    private readonly OmronPlc _plc;
    private readonly IAsyncEnumerator<ScanRecord> _feed;
    private readonly object _workerObject;
    private readonly ScanIntakeGate _gate;
    private readonly ConcurrentQueue<string> _received;
    public CapturedScanWriter Saved { get; }
    public ConcurrentQueue<string> Errors { get; }
    public bool IsSuspended => _gate.IsSuspended;

    private CameraPipeline(Reader reader, BackgroundService worker, TcpListener listener, TcpClient cameraSocket,
        OmronPlc plc, IAsyncEnumerator<ScanRecord> feed, object workerObject, CapturedScanWriter saved,
        ConcurrentQueue<string> errors, ScanIntakeGate gate, ConcurrentQueue<string> received)
    {
        _reader = reader; _worker = worker; _listener = listener; _cameraSocket = cameraSocket;
        _plc = plc; _feed = feed; _workerObject = workerObject; Saved = saved; Errors = errors;
        _gate = gate; _received = received;
    }

    public static async Task<CameraPipeline> StartAsync(ProductionOrderService po, CameraResultPlc server,
        CancellationToken ct, int plcRetries = 0)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var reader = new Reader("127.0.0.1", port);
        var plc = new OmronPlc("127.0.0.1", server.Port, 1, 2, 500, plcRetries);
        var gate = new ScanIntakeGate();
        var counters = Create("RuntimeCounters");
        var feed = Create("ScanFeed");
        var plcOptions = Create("PlcOptions");
        Set(plcOptions, "Ip", "127.0.0.1"); Set(plcOptions, "Port", server.Port);
        Set(plcOptions, "Enabled", true); Set(plcOptions, "ResultAddress", "D12");
        var writerType = Type("PlcWriter");
        var writer = Activator.CreateInstance(writerType, plc, Wrap(plcOptions), counters, gate, Logger(writerType))!;
        var options = Create("CameraOptions");
        Set(options, "Ip", "127.0.0.1"); Set(options, "Port", port);
        var saved = new CapturedScanWriter();
        var workerType = Type("CameraSignalWorker");
        var errors = new ConcurrentQueue<string>();
        var workerLogger = Activator.CreateInstance(typeof(CameraPipelineLogger<>).MakeGenericType(workerType), errors)!;
        var workerObject = Activator.CreateInstance(workerType,
            reader, writer, feed, saved, Wrap(options), counters, po, gate, workerLogger)!;
        var worker = (BackgroundService)workerObject;
        var records = (IAsyncEnumerable<ScanRecord>)feed.GetType().GetMethod("ReadAllAsync")!.Invoke(feed, [ct])!;
        var enumerator = records.GetAsyncEnumerator(ct);
        await worker.StartAsync(ct);
        var camera = await listener.AcceptTcpClientAsync(ct).AsTask().WaitAsync(TimeSpan.FromSeconds(4), ct);
        var received = new ConcurrentQueue<string>();
        // Registered after worker startup, so this observer sees completed admission callbacks.
        reader.ClientCallback += (state, content) => { if (state == eReaderState.Received) received.Enqueue(content); };
        return new CameraPipeline(reader, worker, listener, camera, plc, enumerator, workerObject, saved, errors, gate, received);
    }

    public async Task<ScanRecord> ScanAsync(string content, CancellationToken ct)
    {
        var frame = Encoding.UTF8.GetBytes(content + "\r\n");
        await _cameraSocket.GetStream().WriteAsync(frame, ct);
        if (!await _feed.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5), ct))
            throw new InvalidOperationException("Camera feed closed before outcome.");
        await ((Task)_workerObject.GetType().GetMethod("DrainAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(_workerObject, [ct])!).WaitAsync(TimeSpan.FromSeconds(5), ct);
        return _feed.Current;
    }

    public async Task SendSuppressedAsync(string content, CancellationToken ct)
    {
        var received = _received.Count;
        await _cameraSocket.GetStream().WriteAsync(Encoding.UTF8.GetBytes(content + "\r\n"), ct);
        while (_received.Count == received)
            await Task.Delay(10, ct);
        await (Task)_workerObject.GetType().GetMethod("DrainAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(_workerObject, [ct])!;
    }

    private static Type Type(string name) => AppAssembly.GetType("TanTienQR.Services." + name, throwOnError: true)!;
    private static object Create(string name) => Activator.CreateInstance(Type(name))!;
    private static object Wrap(object value) => Activator.CreateInstance(typeof(OptionsWrapper<>).MakeGenericType(value.GetType()), value)!;
    private static object Logger(Type type) => typeof(NullLogger<>).MakeGenericType(type).GetField("Instance")!.GetValue(null)!;
    private static void Set(object value, string name, object propertyValue) => value.GetType().GetProperty(name)!.SetValue(value, propertyValue);

    public async ValueTask DisposeAsync()
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(4));
        _reader.Disconnect();
        await _worker.StopAsync(stop.Token);
        _worker.Dispose();
        await _feed.DisposeAsync();
        _cameraSocket.Dispose(); _listener.Stop(); _plc.Dispose();
    }
}

internal sealed class CapturedScanWriter : IScanRecordWriter
{
    private readonly ConcurrentQueue<ScanRecordEntity> _records = new();
    public int Count => _records.Count;
    public void Enqueue(ScanRecordEntity record) => _records.Enqueue(record);
    public Task FlushAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task VerifyHealthyAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    public ScanRecordEntity Last() => _records.Last();
}

internal sealed class CameraPipelineLogger<T>(ConcurrentQueue<string> errors) : ILogger<T>
{
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (logLevel >= LogLevel.Error)
            errors.Enqueue(formatter(state, exception) + Environment.NewLine + exception?.Message);
    }
}

internal sealed class CodeContentHistory : IPoScanHistoryReader
{
    public IReadOnlyList<string> Codes { get; set; } = [];
    public Task<IReadOnlyList<string>> GetSeenCodeContentsAsync(string orderNo, CancellationToken ct = default) => Task.FromResult(Codes);
}

internal sealed class CodeContentPoClient : IPoClient
{
    public int ActivationCalls { get; private set; }
    public Task<IReadOnlyList<ProductionOrder>> GetOrdersAsync(bool includeCompleted = false, CancellationToken ct = default) =>
        throw new InvalidOperationException("Camera pipeline must not fetch orders from server.");
    public Task<IReadOnlyList<PoCode>> GetCodesAsync(string orderNo, CancellationToken ct = default) =>
        throw new InvalidOperationException("Camera pipeline must use local PO data.");
    public Task<PoActivateResult> ActivateAsync(string orderNo, IReadOnlyList<string> codeContents, CancellationToken ct = default)
    {
        ActivationCalls++;
        return Task.FromResult(new PoActivateResult { Ok = true, Activated = codeContents.Count });
    }
    public Task<ProductionOrder?> CreateOrderAsync(string orderNo, int orderQty, CancellationToken ct = default) =>
        throw new InvalidOperationException("Camera pipeline must not create server orders.");
}

internal sealed record CameraPlcWrite(int Address, short Value, bool Acknowledged, bool Applied);

internal sealed class CameraResultPlc : IDisposable
{
    private readonly UdpClient _socket = new(new IPEndPoint(IPAddress.Loopback, 0));
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _worker;
    private int _failNext;
    private int _dropNextAcknowledgement;
    public int Port => ((IPEndPoint)_socket.Client.LocalEndPoint!).Port;
    public ConcurrentQueue<CameraPlcWrite> Writes { get; } = new();
    public CameraResultPlc() => _worker = RunAsync();
    public void FailNextWrite() => Interlocked.Exchange(ref _failNext, 1);
    public void DropNextAcknowledgement() => Interlocked.Exchange(ref _dropNextAcknowledgement, 1);
    private async Task RunAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var packet = await _socket.ReceiveAsync(_stop.Token);
                var request = packet.Buffer;
                if (request.Length < 20 || request[11] != 2)
                    throw new InvalidOperationException("Expected FINS result word write only.");
                var fail = Interlocked.Exchange(ref _failNext, 0) != 0;
                var dropAcknowledgement = Interlocked.Exchange(ref _dropNextAcknowledgement, 0) != 0;
                Writes.Enqueue(new CameraPlcWrite(BinaryPrimitives.ReadUInt16BigEndian(request.AsSpan(13, 2)),
                    BinaryPrimitives.ReadInt16BigEndian(request.AsSpan(18, 2)), !fail && !dropAcknowledgement, !fail));
                if (dropAcknowledgement) continue;
                var response = new byte[14];
                Array.Copy(request, response, 12);
                response[0] = 0xC0;
                if (fail) { response[12] = 0x21; response[13] = 0x01; }
                await _socket.SendAsync(response, packet.RemoteEndPoint, _stop.Token);
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (ObjectDisposedException) when (_stop.IsCancellationRequested) { }
    }
    public void Dispose()
    {
        _stop.Cancel(); _socket.Dispose(); _worker.GetAwaiter().GetResult(); _stop.Dispose();
    }
}
