using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using ClassLibraryMT.Communications.CodeReaderProtocol;
using ClassLibraryMT.Communications.Plc;
using ClassLibraryMT.DeviceStatus;
using ClassLibraryMT.Po;
using ClassLibraryMT.Scans;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
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

        var hasGs1Structure = typeof(App).Assembly.GetType("TanTienQR.Services.QrParser", true)!
            .GetMethod("HasGs1Structure", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)!
            .CreateDelegate<Func<string, bool>>();
        const string gtin14 = "08934567890123";
        foreach (var (content, description) in new[]
        {
            ("https://beta-elink.checkee.vn/01/8931234567892/21/BN8joxxKwx", "Link eTrace UAT GTIN-13 được kiểm tra cấu trúc, không kiểm checksum"),
            ("http://example.test/01/12345678/21/A", "Digital Link GTIN-8 và serial một ký tự"),
            ("/01/123456789012/21/A", "Digital Link GTIN-12"),
            ($"/01/{gtin14}/21/ABCDEFGHIJKLMNOPQRST", "Digital Link GTIN-14 và serial tối đa 20 ký tự"),
            ($"https://example.test/01/{gtin14}/21/A%2FB%3FC%25D?batch=A002#source", "Serial URL percent escape hợp lệ, có query và fragment"),
            ($"/01/{gtin14}/21/ABC?batch=A002#source", "Path Digital Link có query và fragment"),
            ($"https://example.test/prefix/01/{gtin14}/21/ABC", "Digital Link hỗ trợ path prefix của resolver"),
            ($"https://example.test/prefix%2Fbatch/01/{gtin14}/21/ABC?batch=%20#source%25", "Escape hợp lệ trong resolver prefix, query và fragment"),
            ($"(01){gtin14}(21)ABC123", "GS1 có ngoặc AI 01/21"),
            ($"01{gtin14}21ABC123", "GS1 element string AI 01/21"),
            ($"]C101{gtin14}21ABC123", "GS1-128 có symbology identifier ]C1"),
            ($"]d201{gtin14}21ABC123", "GS1 DataMatrix có symbology identifier ]d2"),
            ($"]Q301{gtin14}21ABC123", "GS1 QR có symbology identifier ]Q3"),
            ($"\u001d01{gtin14}21ABC123\u001d", "Element string có ký tự GS đầu và cuối"),
            ($"01{gtin14}21!\"%&'()*+,-./:;<=>?_", "Serial chấp nhận đầy đủ 20 ký tự đặc biệt thuộc GS1 set82"),
        })
            Check(hasGs1Structure(content), description);

        foreach (var (content, description) in new[]
        {
            ("", "mã rỗng"),
            ("PLAIN-CODE-42", "mã plain"),
            ("NoRead", "NoRead không phải cấu trúc GS1"),
            ("FAIL", "FAIL không phải cấu trúc GS1"),
            ("https://example.test/21/ABC", "thiếu AI 01"),
            ($"https://example.test/01/{gtin14}/ABC", "thiếu AI 21"),
            ($"https://example.test/01/{gtin14}/21/", "serial rỗng"),
            ($"/01/{gtin14}/21/ABCDEFGHIJKLMNOPQRSTU", "serial dài hơn 20 ký tự"),
            ("/01/12345678901/21/ABC", "độ dài GTIN không được hỗ trợ"),
            ("/01/0893456789012X/21/ABC", "GTIN có chữ"),
            ("/01/０８９３４５６７８９０１２３/21/ABC", "GTIN dùng chữ số Unicode"),
            ($"prefix/01/{gtin14}/21/ABC", "không nhận một đoạn GS1 bên trong chuỗi bất kỳ"),
            ($" /01/{gtin14}/21/ABC", "không tự bỏ khoảng trắng đầu mã"),
            ($"/01/{gtin14}/21/ABC ", "không tự bỏ khoảng trắng cuối mã"),
            ($"ftp://example.test/01/{gtin14}/21/ABC", "chỉ nhận URL HTTP hoặc HTTPS"),
            ($"https://example.test/01/{gtin14}/21/ABC/10/LOT", "AI ngoài phạm vi 01/21 hoặc path thừa"),
            ($"https://example.test/01/{gtin14}/21/A%ZZ", "percent escape không phải hex"),
            ($"https://example.test/01/{gtin14}/21/A%2", "percent escape bị thiếu ký tự"),
            ($"https://example.test/01/{gtin14}/21/A%20B", "serial đã decode chứa khoảng trắng"),
            ($"https://example.test/01/{gtin14}/21/A%23B", "serial đã decode chứa dấu # ngoài set82"),
            ($"https://example.test/01/{gtin14}/21/ABC?batch=%ZZ", "percent escape sai trong query"),
            ($"/01/{gtin14}/21/ABC?batch=%2", "percent escape thiếu ký tự trong query của path Digital Link"),
            ($"https://example.test/01/{gtin14}/21/ABC#source%ZZ", "percent escape sai trong fragment"),
            ($"/01/{gtin14}/21/ABC#source%", "percent escape thiếu ký tự trong fragment của path Digital Link"),
            ($"https://example.test/prefix%ZZ/01/{gtin14}/21/ABC", "percent escape sai trong resolver prefix"),
            ($"(01)8931234567892(21)ABC", "AI 01 có ngoặc yêu cầu đúng 14 số"),
            ($"(01){gtin14}(22)ABC", "sai AI 21 trong mã có ngoặc"),
            ($"01{gtin14}21Việt", "serial có Unicode ngoài GS1 set82"),
            ($"01{gtin14}21A|B", "serial chứa dấu | ngoài set82"),
            ($"01{gtin14}21A\u001dB", "GS ở giữa serial không thuộc cấu trúc hỗ trợ"),
            ($"]C001{gtin14}21ABC", "symbology identifier không phải GS1"),
        })
            Check(!hasGs1Structure(content), "Sai cấu trúc: " + description);

        var root = Path.Combine(Path.GetTempPath(), "TEST-ASYNC-PO-20261127-CameraRegression-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var ct = timeout.Token;
        var store = new PoStore(root);
        var history = new PoHistoryStore(root);
        var client = new CodeContentPoClient();
        var seenHistory = new CodeContentHistory();
        ProductionOrderService NewService() => new(client, store, history, new PoCodeCache(), seenHistory);
        var po = NewService();
        const string plain = "(01)08934567890123(21)PLAIN-CODE-42";
        const string firstUrl = "https://one.example/01/08934567890123/21/SHARED-SUFFIX";
        const string secondUrl = "https://two.example/01/08934567890123/21/SHARED-SUFFIX";
        const string acknowledged = "/01/08934567890123/21/ACKNOWLEDGED";
        const string retryContent = "/01/08934567890123/21/RETRY-AFTER-PLC-FAIL";
        const string escapedUrl = "https://escaped.example/01/08934567890123/21/A%2FB%3FC%25D?case=VALUE#batch";
        var order = new ProductionOrder
        {
            OrderNo = "TEST-ASYNC-PO-20261127-CAMERA-CONTENT-PO",
            OrderQty = 7,
            CreatedUtc = new DateTime(2026, 11, 26, 0, 0, 0, DateTimeKind.Utc),
        };
        await store.EnsurePoDbAsync(order,
        [
            new() { CodeContent = plain },
            new() { CodeContent = firstUrl },
            new() { CodeContent = secondUrl },
            new() { CodeContent = escapedUrl },
            new() { CodeContent = acknowledged, ActivatedUtc = DateTime.UtcNow },
            new() { CodeContent = retryContent },
            // Keep one unscanned code so automatic completion does not close this PO.
            new() { CodeContent = "/01/08934567890123/21/SPARE-CONTENT" },
        ], ct);
        await store.EnsurePoDbAsync(new ProductionOrder
        {
            OrderNo = "TEST-ASYNC-PO-20261127-OTHER-CAMERA-PO", OrderQty = 1, CreatedUtc = order.CreatedUtc,
        }, [new() { CodeContent = "/01/08934567890123/21/ONLY-IN-OTHER-PO" }], ct);

        using var plc = new CameraResultPlc();
        await using (var camera = await CameraPipeline.StartAsync(po, plc, ct))
        {
            var inactive = await camera.ScanAsync("/01/08934567890123/21/BEFORE-START", ct);
            Check(inactive.Outcome == ScanOutcome.NoProductionOrder && !inactive.PlcWritten
                && plc.Writes.IsEmpty && camera.Saved.Last().PlcResult == ScanPlcResults.Skipped,
                "Camera chưa chạy PO vẫn báo NoProductionOrder cho mã GS1 hợp lệ, không ghi PLC");
            foreach (var malformedBeforeStart in new[] { "BEFORE-START", "/01/invalid/21/ABC" })
            {
                var malformed = await camera.ScanAsync(malformedBeforeStart, ct);
                Check(malformed is { Outcome: ScanOutcome.InvalidStructure, PlcWritten: false, PlcSkipped: true }
                    && malformed.CodeContent == malformedBeforeStart
                    && plc.Writes.IsEmpty
                    && camera.Saved.Last() is { Status: ScanStatuses.InvalidStructure, PlcResult: ScanPlcResults.Skipped }
                    && camera.Saved.Last().OrderNo == string.Empty,
                    "Chưa chạy PO vẫn báo Sai cấu trúc, giữ Raw và không phát PLC");
            }
            Check(await po.StartProductionAsync(order.OrderNo, ct), "Pipeline khởi động PO từ database local");

            var disconnectedWrites = plc.Writes.Count;
            AppStateManager.SetPlcStatus(PLCStatus.Disconnect);
            var disconnected = await camera.ScanAsync(retryContent, ct);
            Check(disconnected is { Outcome: ScanOutcome.PlcError, PlcWritten: false, PlcSkipped: true }
                && plc.Writes.Count == disconnectedWrites && po.ScannedCount == 1
                && po.ValidateCode(retryContent) == PoCodeValidation.Valid
                && camera.Saved.Last() is { Status: ScanStatuses.Fail, PlcResult: ScanPlcResults.Skipped },
                "PLC offline tại lúc nhận: lưu record Skipped nhưng không gửi PLC hoặc tiêu thụ mã PO");
            AppStateManager.SetPlcStatus(PLCStatus.Connected);

            plc.FailNextWrite();
            var rejectedByPlc = await camera.ScanAsync(retryContent, ct);
            Check(rejectedByPlc is { Outcome: ScanOutcome.Pass, PlcWritten: false }
                && !plc.Writes.Last().Acknowledged && plc.Writes.Last().Value == 1 && po.ScannedCount == 2
                && po.ValidateCode(retryContent) == PoCodeValidation.Duplicate
                && camera.Saved.Last() is { Status: ScanStatuses.Pass, PlcResult: ScanPlcResults.Failed },
                "PLC từ chối sau commit vẫn giữ Pass/Checkee và Scanned; PLC failure được ghi riêng");
            AppStateManager.SetPlcStatus(PLCStatus.Connected);
            var retry = await camera.ScanAsync(retryContent, ct);
            Check(retry is { Outcome: ScanOutcome.Duplicate, PlcWritten: true } && po.ScannedCount == 2
                && po.ValidateCode(retryContent) == PoCodeValidation.Duplicate,
                "Mã đã commit không được gửi Pass lần hai sau PLC rejection");
            var retryDuplicate = await camera.ScanAsync(retryContent, ct);
            Check(retryDuplicate is { Outcome: ScanOutcome.Duplicate, PlcWritten: true }
                && plc.Writes.Last().Value == 0 && po.ScannedCount == 2,
                "Mọi lượt quét sau khi PO commit đều là Duplicate, kể cả lượt PLC không ACK");

            foreach (var content in new[] { plain, firstUrl, secondUrl, escapedUrl })
            {
                var before = plc.Writes.Count;
                var record = await camera.ScanAsync(content, ct);
                Check(record is { Outcome: ScanOutcome.Pass, PlcWritten: true }
                    && record.CodeContent == content && plc.Writes.Count == before + 1
                    && plc.Writes.Last() is { Address: 12, Value: 1 }
                    && camera.Saved.Last() is { Status: ScanStatuses.Pass, PlcResult: ScanPlcResults.Ok } saved
                    && saved.CodeContent == content && saved.Raw == content && saved.OrderNo == order.OrderNo,
                    content == escapedUrl
                        ? "Pipeline kiểm tra serial đã decode nhưng lưu nguyên URL có percent escape/query/fragment"
                        : content == secondUrl
                                ? "Hai URL khác nhau cùng đuôi serial đều Pass riêng biệt"
                                : content == firstUrl
                                    ? "Camera nhận URL nguyên vẹn làm CodeContent"
                                    : "GS1 có ngoặc thuộc PO được Pass D12=1");
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

            foreach (var unknown in new[] { "/01/08934567890123/21/UNKNOWN-CONTENT", "(01)08934567890123(21)plain-code-42", "/01/08934567890123/21/ONLY-IN-OTHER-PO" })
            {
                var record = await camera.ScanAsync(unknown, ct);
                Check(record is { Outcome: ScanOutcome.InvalidForOrder, PlcWritten: true }
                    && record.CodeContent == unknown && plc.Writes.Last().Value == 0
                    && camera.Saved.Last().Status == ScanStatuses.InvalidForOrder,
                    unknown == "(01)08934567890123(21)plain-code-42"
                        ? "Dictionary phân biệt hoa thường, mã khác case là Không tồn tại"
                        : unknown == "/01/08934567890123/21/ONLY-IN-OTHER-PO"
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
            Check(typeof(IPoClient).GetMethod("ActivateAsync") is null, "Luồng camera không còn API kích hoạt PO cũ");
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
            Check(!restored.IsHalted && restored.ValidateCode("/01/08934567890123/21/SPARE-CONTENT") == PoCodeValidation.Valid,
                "Mã trong lịch sử PO khác không chặn mã chưa quét của PO đang chạy");
            var independent = await camera.ScanAsync("/01/08934567890123/21/SPARE-CONTENT", ct);
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

        // Reuse the initialized temporary history DB; a brand-new history path
        // has no PoMeta table until the store first initializes that database.
        var structureStore = store;
        var structurePo = NewService();
        var structureOrder = new ProductionOrder
        {
            OrderNo = "TEST-ASYNC-PO-20261127-CAMERA-INVALID-STRUCTURE", OrderQty = 3, CreatedUtc = order.CreatedUtc,
        };
        const string malformedImported = "PLAIN-CODE-42";
        const string malformedActivated = "ALREADY-ACTIVATED-BUT-MALFORMED";
        await structureStore.EnsurePoDbAsync(structureOrder,
        [
            new() { CodeContent = malformedImported },
            new() { CodeContent = malformedActivated, ActivatedUtc = DateTime.UtcNow },
            new() { CodeContent = "/01/08934567890123/21/STILL-AVAILABLE" },
        ], ct);
        Check(await structurePo.StartProductionAsync(structureOrder.OrderNo, ct)
            && structurePo.ScannedCount == 1
            && structurePo.ValidateCode(malformedImported) == PoCodeValidation.Valid
            && structurePo.ValidateCode(malformedActivated) == PoCodeValidation.Duplicate,
            "Chuẩn bị PO có mã sai cấu trúc chưa quét và mã sai cấu trúc đã kích hoạt");
        var flagsBeforeStructure = await structureStore.GetCodesWithFlagsAsync(structureOrder.OrderNo, structureOrder.CreatedUtc, ct);
        await using (var camera = await CameraPipeline.StartAsync(structurePo, plc, ct))
        {
            foreach (var malformed in new[]
            {
                malformedImported,
                malformedActivated,
                "/01/invalid/21/ABC",
                "https://example.test/01/08934567890123/21/ABC/10/LOT",
                "  Việt Nam|part:01\u001danything?key=Value  " + new string('x', 1000) + "  ",
            })
            {
                var before = plc.Writes.Count;
                var rejected = await camera.ScanAsync(malformed, ct);
                Check(rejected is { Outcome: ScanOutcome.InvalidStructure, PlcWritten: true }
                    && rejected.CodeContent == malformed && plc.Writes.Count == before + 1
                    && plc.Writes.Last() is { Address: 12, Value: 0 }
                    && camera.Saved.Last() is { Status: ScanStatuses.InvalidStructure, PlcResult: ScanPlcResults.Ok } saved
                    && saved.CodeContent == malformed && saved.Raw == malformed && saved.OrderNo == structureOrder.OrderNo
                    && structurePo.ScannedCount == 1,
                    "Camera báo Sai cấu trúc trước lookup/trùng, chỉ ghi Fail=0 và giữ nguyên Raw/PO");
            }
            var beforeFailedWrite = plc.Writes.Count;
            plc.FailNextWrite();
            var plcRejected = await camera.ScanAsync(malformedImported, ct);
            Check(plcRejected is { Outcome: ScanOutcome.InvalidStructure, PlcWritten: false }
                && plc.Writes.Count == beforeFailedWrite + 1
                && plc.Writes.Last() is { Address: 12, Value: 0, Acknowledged: false }
                && camera.Saved.Last() is { Status: ScanStatuses.InvalidStructure, PlcResult: ScanPlcResults.Failed }
                && structurePo.ScannedCount == 1 && !structurePo.IsHalted,
                "PLC từ chối Fail giữ đúng Sai cấu trúc, không phát Pass hoặc tiêu thụ mã PO");
        }
        var flagsAfterStructure = (await structureStore.GetCodesWithFlagsAsync(structureOrder.OrderNo, structureOrder.CreatedUtc, ct))
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        Check(flagsBeforeStructure.Count == flagsAfterStructure.Count
            && flagsBeforeStructure.All(pair => flagsAfterStructure.TryGetValue(pair.Key, out var flags) && flags == pair.Value)
            && flagsAfterStructure.All(pair => (pair.Value & PoCodeCache.Flags.PendingPass) == 0),
            "Sai cấu trúc không đổi Scanned/Activated hoặc tạo PendingPass trong DB PO");
        await using (var camera = await CameraPipeline.StartAsync(structurePo, plc, ct))
        {
            var validRemaining = await camera.ScanAsync("/01/08934567890123/21/STILL-AVAILABLE", ct);
            Check(validRemaining is { Outcome: ScanOutcome.Pass, PlcWritten: true }
                && structurePo.IsProductionRunning && structurePo.ScannedCount == 2,
                "Mã GS1 hợp lệ còn lại vẫn Pass và ghi nhận bình thường sau các lượt Sai cấu trúc");
        }
        Check(await structurePo.CancelProductionAsync(structureOrder.OrderNo, ct),
            "Dọn PO fixture Sai cấu trúc sau khi xác minh mã hợp lệ vẫn Pass");

        seenHistory.Codes = [];
        var finalOrder = new ProductionOrder
        {
            OrderNo = "TEST-ASYNC-PO-20261127-CAMERA-LAST-CODE", OrderQty = 1, CreatedUtc = order.CreatedUtc,
        };
        const string finalContent = "/01/08934567890123/21/LAST-CODE";
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
            OrderNo = "TEST-ASYNC-PO-20261127-CAMERA-PERSISTENCE-FAIL", OrderQty = 2, CreatedUtc = order.CreatedUtc,
        };
        const string failedContent = "/01/08934567890123/21/DB-FAIL-AFTER-PASS";
        await store.EnsurePoDbAsync(failedOrder,
            [new() { CodeContent = failedContent }, new() { CodeContent = "/01/08934567890123/21/BLOCKED-AFTER-FAIL" }], ct);
        var failAfterAck = NewService();
        Check(await failAfterAck.StartProductionAsync(failedOrder.OrderNo, ct), "Chuẩn bị PO kiểm tra lỗi persistence sau PLC Pass");
        await using (var connection = new SqliteConnection($"Data Source={store.GetDbPath(failedOrder.OrderNo, failedOrder.CreatedUtc)}"))
        {
            await connection.OpenAsync(ct);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TRIGGER camera_fail_scan BEFORE UPDATE OF ScannedUtc ON PoCodes
                WHEN NEW.Code = '/01/08934567890123/21/DB-FAIL-AFTER-PASS'
                BEGIN SELECT RAISE(ABORT, 'camera test simulated PO commit failure after PLC'); END;
                """;
            await command.ExecuteNonQueryAsync(ct);
        }
        await using (var camera = await CameraPipeline.StartAsync(failAfterAck, plc, ct))
        {
            var beforeFault = plc.Writes.Count;
            var failed = await camera.ScanAsync(failedContent, ct, expectPersistenceFailure: true);
            var faultWrites = plc.Writes.Skip(beforeFault).ToArray();
            Check(failed is { Outcome: ScanOutcome.Pass, PlcWritten: true }
                && camera.IsSuspended && faultWrites.Count(write => write is { Address: 12, Value: 1 }) == 1,
                "PO batch lỗi sau PLC vẫn giữ outcome Pass và đóng intake; không giả commit-before-PLC");
            var durable = await store.GetCodesWithFlagsAsync(failedOrder.OrderNo, failedOrder.CreatedUtc, ct);
            Check(durable.Single(pair => pair.Key == failedContent).Value == PoCodeCache.Flags.None
                && failAfterAck.ValidateCode(failedContent) == PoCodeValidation.Duplicate,
                "PO DB chưa ghi được nhưng mã đã consumed trong RAM, không cho phát Pass lần hai");
            Check(camera.Saved.Last() is { Status: ScanStatuses.Pass, PlcResult: ScanPlcResults.Ok },
                "Lịch sử/outbox durable lưu đúng Pass dù PO file chưa đồng bộ");
            var writes = plc.Writes.Count;
            await camera.SendSuppressedAsync(failedContent, ct);
            Check(plc.Writes.Count == writes, "Persistence fault chặn frame tiếp theo trước PLC");
        }
        await using (var repair = new SqliteConnection($"Data Source={store.GetDbPath(failedOrder.OrderNo, failedOrder.CreatedUtc)}"))
        {
            await repair.OpenAsync(ct);
            await using var removeTrigger = repair.CreateCommand(); removeTrigger.CommandText = "DROP TRIGGER camera_fail_scan;";
            await removeTrigger.ExecuteNonQueryAsync(ct);
        }
        var restartGuard = NewService();
        await using (var camera = await CameraPipeline.StartAsync(restartGuard, plc, ct))
        {
            Check(!restartGuard.IsHalted && restartGuard.ValidateCode(failedContent) == PoCodeValidation.Duplicate
                && restartGuard.ScannedCount == 1,
                "Startup replay pending outbox trước khi khôi phục cache sau lỗi PO commit");
            var writes = plc.Writes.Count;
            var duplicate = await camera.ScanAsync(failedContent, ct);
            Check(duplicate is { Outcome: ScanOutcome.Duplicate, PlcWritten: true }
                && plc.Writes.Count == writes + 1 && plc.Writes.Last().Value == 0,
                "Camera sau restart không replay Pass cho mã đã được outbox đồng bộ");
        }

        // Completion metadata can fail after a successful, committed final Pass.
        var completionRoot = Path.Combine(root, "completion-fault");
        var completionStore = new PoStore(completionRoot);
        var completionHistory = new PoHistoryStore(completionRoot);
        ProductionOrderService CompletionService() => new(client, completionStore, completionHistory,
            new PoCodeCache(), new CodeContentHistory());
        var completionOrder = new ProductionOrder
        {
            OrderNo = "TEST-ASYNC-PO-20261127-CAMERA-COMPLETION-FAULT", OrderQty = 1, CreatedUtc = order.CreatedUtc,
        };
        const string completionContent = "/01/08934567890123/21/COMMITTED-PASS-FAULT";
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
            var committed = await camera.ScanAsync(completionContent, ct, expectPersistenceFailure: true);
            await camera.WriteReadyAsync(1, ct);
            Check(committed is { Outcome: ScanOutcome.Pass, PlcWritten: true }
                && camera.Saved.Last().Status == ScanStatuses.Pass && completionPo.ScannedCount == 1,
                "Lỗi hoàn tất PO không đổi Pass đã commit thành Halted hoặc Fail giả");
            Check(completionPo.IsHalted && camera.IsSuspended
                && plc.Writes.Skip(before).Any(write => write is { Address: 16, Value: 0, Acknowledged: true })
                && camera.Errors.Count > 0,
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

        // A PLC can execute Pass while its UDP acknowledgement is lost. The
        // already committed code stays Pass and must never be sent as Pass again.
        var lostAckRoot = Path.Combine(root, "lost-ack");
        var lostAckStore = new PoStore(lostAckRoot);
        var lostAckHistory = new PoHistoryStore(lostAckRoot);
        ProductionOrderService LostAckService() => new(client, lostAckStore, lostAckHistory,
            new PoCodeCache(), new CodeContentHistory());
        var lostAckOrder = new ProductionOrder
        {
            OrderNo = "TEST-ASYNC-PO-20261127-CAMERA-LOST-PLC-ACK", OrderQty = 2, CreatedUtc = order.CreatedUtc,
        };
        const string lostAckContent = "/01/08934567890123/21/PASS-ACK-LOST";
        await lostAckStore.EnsurePoDbAsync(lostAckOrder,
            [new() { CodeContent = lostAckContent }, new() { CodeContent = "/01/08934567890123/21/AFTER-LOST-ACK" }], ct);
        var lostAckPo = LostAckService();
        Check(await lostAckPo.StartProductionAsync(lostAckOrder.OrderNo, ct),
            "Chuẩn bị PO kiểm tra PLC thực thi Pass nhưng mất ACK");
        await using (var camera = await CameraPipeline.StartAsync(lostAckPo, plc, ct, plcRetries: 2))
        {
            var before = plc.Writes.Count;
            plc.DropNextAcknowledgement(); // A second request would receive a successful ACK.
            var uncertain = await camera.ScanAsync(lostAckContent, ct);
            var actual = plc.Writes.Skip(before).ToArray();
            Check(uncertain is { Outcome: ScanOutcome.Pass, PlcWritten: false }
                && !lostAckPo.IsHalted && !camera.IsSuspended && lostAckPo.ScannedCount == 1
                && camera.Saved.Last() is { Status: ScanStatuses.Pass, PlcResult: ScanPlcResults.Failed },
                "Mất ACK sau commit vẫn báo Pass/Checkee, giữ Scanned và ghi riêng PLC failure");
            Check(actual.Count(write => write is { Address: 12, Value: 1 }) == 1
                && actual.Any(write => write is { Address: 12, Value: 1, Applied: true, Acknowledged: false })
                && !actual.Any(write => write.Address == 16),
                "Pass chỉ phát một UDP dù Retries=2; không phát lệnh Ready khi PO đã commit");
            var flags = (await lostAckStore.GetCodesWithFlagsAsync(lostAckOrder.OrderNo, lostAckOrder.CreatedUtc, ct))
                .Single(pair => pair.Key == lostAckContent).Value;
            Check(flags == PoCodeCache.Flags.Scanned && lostAckPo.ValidateCode(lostAckContent) == PoCodeValidation.Duplicate,
                "Mất ACK vẫn để durable Scanned và chặn Pass replay theo đúng CodeContent");
            AppStateManager.SetPlcStatus(PLCStatus.Connected);
            var duplicateWrites = plc.Writes.Count;
            var repeated = await camera.ScanAsync(lostAckContent, ct);
            Check(repeated is { Outcome: ScanOutcome.Duplicate, PlcWritten: true }
                && plc.Writes.Count == duplicateWrites + 1 && plc.Writes.Last().Value == 0,
                "Quét lại sau mất ACK chỉ gửi Duplicate/Fail, không gửi Pass lần hai");
        }
        var lostAckRestart = LostAckService();
        Check(await lostAckRestart.RestoreRunningOrderAsync(ct) && !lostAckRestart.IsHalted
            && lostAckRestart.ValidateCode(lostAckContent) == PoCodeValidation.Duplicate
            && lostAckRestart.ScannedCount == 1,
            "Restart khôi phục Scanned/PO state sau khi mất ACK");
        await using (var camera = await CameraPipeline.StartAsync(lostAckRestart, plc, ct, plcRetries: 2))
        {
            var writes = plc.Writes.Count;
            Check((await camera.ScanAsync(lostAckContent, ct)) is { Outcome: ScanOutcome.Duplicate, PlcWritten: true }
                && plc.Writes.Count == writes + 1 && plc.Writes.Last().Value == 0,
                "Sau restart không gửi lại Pass của mã mất ACK");
        }
        SqliteConnection.ClearAllPools();
        // Retain the unique sandbox for review; never clean application data.
        return passed;
    }
}

/// <summary>Uses the real camera socket, queue, PO service, PLC writer and UI feed.</summary>
internal sealed class CameraPipeline : IAsyncDisposable
{
    private readonly Reader _reader;
    private readonly CameraSignalWorker _worker;
    private readonly TcpListener _listener;
    private readonly TcpClient _cameraSocket;
    private readonly OmronPlc _plc;
    private readonly PlcWriter _plcWriter;
    private readonly IAsyncEnumerator<ScanRecord> _feed;
    private readonly ScanIntakeGate _gate;
    private readonly ConcurrentQueue<string> _received;
    private readonly ScanPersistenceWorker _persistence;
    private readonly ServiceProvider _services;
    private readonly ProductionOrderService _po;
    private bool _expectedPersistenceFailure;
    public CapturedScanWriter Saved { get; } = new();
    public ConcurrentQueue<string> Errors { get; }
    public bool IsSuspended => _gate.IsSuspended;

    private CameraPipeline(Reader reader, CameraSignalWorker worker, TcpListener listener, TcpClient cameraSocket,
        OmronPlc plc, PlcWriter plcWriter, IAsyncEnumerator<ScanRecord> feed, ScanIntakeGate gate,
        ConcurrentQueue<string> received, ScanPersistenceWorker persistence, ServiceProvider services,
        ProductionOrderService po, ConcurrentQueue<string> errors)
    {
        _reader = reader; _worker = worker; _listener = listener; _cameraSocket = cameraSocket;
        _plc = plc; _plcWriter = plcWriter; _feed = feed; _gate = gate; _received = received;
        _persistence = persistence; _services = services; _po = po; Errors = errors;
    }

    public static async Task<CameraPipeline> StartAsync(ProductionOrderService po, CameraResultPlc server,
        CancellationToken ct, int plcRetries = 0)
    {
        // Access only the explicitly constructed sandbox store, never app DI/config.
        var store = (PoStore)typeof(ProductionOrderService).GetField("_store", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(po)!;
        var sandbox = Path.Combine(Path.GetTempPath(), "TEST-ASYNC-PO-20261127-CameraHistory-"
            + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(store.DbRoot))));
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Scans:DbPath"] = Path.Combine(sandbox, "scans.db"), ["Scans:FlushIntervalMs"] = "100",
            ["Scans:BatchSize"] = "100", ["Scans:QueueCapacity"] = "100", ["Scans:RetentionDays"] = "0",
        }).Build();
        var services = new ServiceCollection(); services.AddSingleton<IConfiguration>(configuration); services.AddScanStore(configuration);
        var provider = services.BuildServiceProvider();
        var gate = new ScanIntakeGate();
        var errors = new ConcurrentQueue<string>();
        var persistence = new ScanPersistenceWorker(provider.GetRequiredService<IScanPersistenceStore>(), store, po,
            gate, provider.GetRequiredService<IOptions<ScanStoreOptions>>(), new CameraPipelineLogger<ScanPersistenceWorker>(errors));
        await persistence.StartAsync(ct); // replay pending outbox before restoring the cache
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var reader = new Reader("127.0.0.1", port);
        var plc = new OmronPlc("127.0.0.1", server.Port, 1, 2, 500, plcRetries);
        AppStateManager.SetPlcStatus(PLCStatus.Connected);
        var counters = new RuntimeCounters();
        var feed = new ScanFeed();
        var writer = new PlcWriter(plc, Options.Create(new PlcOptions
        {
            Ip = "127.0.0.1", Port = server.Port, Enabled = true, ResultAddress = "D12", ReadyAddress = "D16",
        }), counters, gate, NullLogger<PlcWriter>.Instance);
        var worker = new CameraSignalWorker(reader, writer, feed, persistence,
            Options.Create(new CameraOptions { Ip = "127.0.0.1", Port = port }), counters, po, gate,
            new CameraPipelineLogger<CameraSignalWorker>(errors));
        var enumerator = feed.ReadAllAsync(ct).GetAsyncEnumerator(ct);
        await worker.StartAsync(ct);
        var camera = await listener.AcceptTcpClientAsync(ct).AsTask().WaitAsync(TimeSpan.FromSeconds(4), ct);
        var received = new ConcurrentQueue<string>();
        reader.ClientCallback += (state, content) => { if (state == eReaderState.Received) received.Enqueue(content); };
        return new(reader, worker, listener, camera, plc, writer, enumerator, gate, received, persistence, provider, po, errors);
    }

    public async Task<ScanRecord> ScanAsync(string content, CancellationToken ct, bool expectPersistenceFailure = false)
    {
        await _cameraSocket.GetStream().WriteAsync(Encoding.UTF8.GetBytes(content + "\r\n"), ct);
        if (!await _feed.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5), ct))
            throw new InvalidOperationException("Camera feed closed before outcome.");
        _expectedPersistenceFailure |= expectPersistenceFailure;
        try
        {
            await _worker.DrainAsync(ct).WaitAsync(TimeSpan.FromSeconds(5), ct);
            // On a failure scenario let the real worker own completion and fault
            // propagation. Normal cases can force the same post-barrier action.
            if (expectPersistenceFailure)
            {
                await Task.Delay(200, ct);
                await _persistence.FlushAsync(ct);
            }
            else await _po.CompletePendingAsync(ct);
        }
        catch (Exception ex) when (expectPersistenceFailure && ex is not OperationCanceledException)
        {
            // A fault is expected only in explicit durable-failure scenarios.
            await Task.Delay(150, ct); // allow common worker to observe/fail closed on a completion fault
        }
        var rows = await _services.GetRequiredService<IScanRecordQuery>().GetAsync(pageSize: 1000, cancellationToken: ct);
        Saved.Replace(rows.Items);
        return _feed.Current;
    }

    public Task WriteReadyAsync(int ready, CancellationToken ct) => _plcWriter.WriteReadyAsync(ready, ct);
    public async Task SendSuppressedAsync(string content, CancellationToken ct)
    {
        var received = _received.Count;
        await _cameraSocket.GetStream().WriteAsync(Encoding.UTF8.GetBytes(content + "\r\n"), ct);
        while (_received.Count == received) await Task.Delay(10, ct);
    }
    public async ValueTask DisposeAsync()
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        _reader.Disconnect(); await _worker.StopAsync(stop.Token); _worker.Dispose();
        try
        {
            await _persistence.StopAsync(stop.Token);
            // This fixture owns the service's persistence binding. A successful
            // final barrier leaves no pending RAM mutations; lifecycle-only test
            // actions after fixture disposal must not call its disposed writer.
            if (Equals(_po.PersistenceBarrier, (Func<CancellationToken, Task>)_persistence.FlushAsync))
                _po.PersistenceBarrier = null;
        }
        catch (Exception ex) when (_expectedPersistenceFailure && ex is not OperationCanceledException) { }
        _persistence.Dispose(); await _services.DisposeAsync();
        await _feed.DisposeAsync(); _cameraSocket.Dispose(); _listener.Stop(); _plc.Dispose();
    }
}

internal sealed class CapturedScanWriter
{
    private ScanRecordEntity[] _records = [];
    public int Count => _records.Length;
    public void Replace(IReadOnlyList<ScanRecordEntity> records) => _records = records.OrderBy(record => record.Id).ToArray();
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
    public Task<IReadOnlyList<ProductionOrder>> GetOrdersAsync(bool includeCompleted = false, CancellationToken ct = default) =>
        throw new InvalidOperationException("Camera pipeline must not fetch orders from server.");
    public Task<IReadOnlyList<PoCode>> GetCodesAsync(string orderNo, CancellationToken ct = default) =>
        throw new InvalidOperationException("Camera pipeline must use local PO data.");
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
