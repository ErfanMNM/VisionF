using ClassLibraryMT.Scans;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

var checks = 0;
void Check(bool valid, string description)
{
    if (!valid) throw new InvalidOperationException("[FAIL] " + description);
    checks++;
    Console.WriteLine("[OK] " + description);
}

var root = Path.Combine(Path.GetTempPath(), "VisionF-ScanCodeContent-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
var token = timeout.Token;
var path = Path.Combine(root, "scans.db");
var store = new ScanStore(path);

// Code content can contain arbitrary separators, Unicode and significant whitespace.
var content = "  PRODUCT/Việt|part:01\u001danything?x=YES&serial=SAME  " + new string('x', 1000);
var caseVariant = content.Replace("PRODUCT", "product", StringComparison.Ordinal);
await using (var connection = await store.OpenAsync(token))
{
    await store.InsertBatchAsync(connection,
    [
        new() { CodeContent = content, Raw = content, ReceivedUtc = DateTime.UtcNow, Status = ScanStatuses.Pass, OrderNo = "PO-OLD" },
        new() { CodeContent = caseVariant, Raw = caseVariant, ReceivedUtc = DateTime.UtcNow, Status = ScanStatuses.Pass, OrderNo = "PO-OLD" },
        new() { CodeContent = content, Raw = content, ReceivedUtc = DateTime.UtcNow, Status = ScanStatuses.Pass, OrderNo = "PO-OLD" },
        new() { CodeContent = "PO-LOCAL", ReceivedUtc = DateTime.UtcNow, Status = ScanStatuses.Pass, OrderNo = "PO-NOW" },
        new() { CodeContent = "OTHER|SAME", ReceivedUtc = DateTime.UtcNow, Status = ScanStatuses.Pass, OrderNo = "PO-OLD" },
        new() { CodeContent = "TRUNG", ReceivedUtc = DateTime.UtcNow, Status = ScanStatuses.Duplicate },
        new() { CodeContent = "KHONG-TON-TAI", ReceivedUtc = DateTime.UtcNow, Status = ScanStatuses.InvalidForOrder },
        new() { CodeContent = content, Raw = content, ReceivedUtc = DateTime.UtcNow, Status = ScanStatuses.InvalidStructure, OrderNo = "PO-NOW" },
        new() { CodeContent = "", ReceivedUtc = DateTime.UtcNow, Status = ScanStatuses.NoRead },
        new() { CodeContent = "OLD-FAIL", ReceivedUtc = DateTime.UtcNow, Status = ScanStatuses.Fail },
        new() { CodeContent = "BUSY", ReceivedUtc = DateTime.UtcNow, Status = ScanStatuses.Overload },
    ], token);

    // A legacy row has a truncated Code/Serial but the exact camera content in Raw.
    await using var legacy = connection.CreateCommand();
    legacy.CommandText = """
        INSERT INTO ScanRecords (ReceivedUtc, Code, Gtin, Serial, Raw, Status, OrderNo)
        VALUES ($time, 'SAME', 'LEGACY-GTIN', 'SAME', $raw, 'Pass', 'PO-OLD');
        """;
    legacy.Parameters.AddWithValue("$time", DateTime.UtcNow.ToString("O"));
    legacy.Parameters.AddWithValue("$raw", "LEGACY-FULL/GTIN|SAME");
    await legacy.ExecuteNonQueryAsync(token);
}

var query = new ScanRecordQuery(store);
var page = await query.GetAsync(pageSize: 100, cancellationToken: token);
Check(page.TotalCount == 12 && page.Items.Any(row => row.CodeContent == content), "Giữ CodeContent dài, Unicode, GS và khoảng trắng nguyên vẹn");
Check(page.Items.Single(row => row.Status == ScanStatuses.InvalidStructure) is { } malformed
    && malformed.CodeContent == content && malformed.Raw == content && malformed.OrderNo == "PO-NOW",
    "SQLite giữ nguyên mã/Raw/PO và trạng thái Sai cấu trúc riêng");
Check(page.Items.Single(row => row.Raw == "LEGACY-FULL/GTIN|SAME").CodeContent == "LEGACY-FULL/GTIN|SAME", "Đọc dữ liệu cũ lấy mã đầy đủ từ Raw thay vì Serial");
var seen = await query.GetSeenCodeContentsAsync("PO-NOW", token);
Check(seen.Count == 4 && seen.Contains(content) && seen.Contains(caseVariant), "Lịch sử mã đầy đủ distinct theo ordinal, phân biệt hoa thường");
Check(seen.Contains("OTHER|SAME") && seen.Contains("LEGACY-FULL/GTIN|SAME") && !seen.Contains("SAME"), "Không gộp hai mã khác nhau chỉ vì cùng đuôi serial");
Check(!seen.Contains("PO-LOCAL") && !seen.Contains("TRUNG") && !seen.Contains("BUSY"), "History chỉ lấy Pass của PO khác");
var failures = await query.GetAsync(new ScanRecordFilter { Status = ScanStatus.Fail }, cancellationToken: token);
Check(failures.TotalCount == 5 && failures.Items.Any(row => row.Status == ScanStatuses.InvalidStructure),
    "Bộ lọc Fail tổng hợp cả InvalidStructure, Duplicate, InvalidForOrder, NoRead và Fail cũ");
foreach (var status in new[] { ScanStatus.Duplicate, ScanStatus.InvalidForOrder, ScanStatus.NoRead, ScanStatus.InvalidStructure })
{
    var filtered = await query.GetAsync(new ScanRecordFilter { Status = status }, cancellationToken: token);
    Check(filtered.TotalCount == 1 && filtered.Items[0].Status == ScanStatuses.ToStorage(status), "Giữ riêng outcome " + status);
}
var sendStore = new ScanSendStatusStore(store);
var pending = await sendStore.GetPendingAsync(onlyPass: true, limit: 100, cancellationToken: token);
Check(pending.Count == 6 && pending.All(row => row.Status == ScanStatuses.Pass), "Gửi Checkee mặc định vẫn chỉ nhận Pass");
Check(pending.Any(row => row.CodeContent == content) && pending.Any(row => row.CodeContent == "LEGACY-FULL/GTIN|SAME"), "Đọc pending giữ trọn CodeContent mới và cũ");
var legacyMetadataOnly = await query.GetAsync(new ScanRecordFilter { CodeContains = "LEGACY-GTIN" }, cancellationToken: token);
Check(legacyMetadataOnly.TotalCount == 0, "Tìm kiếm CodeContent không tìm theo metadata GTIN cũ");

var exactStore = new ScanStore(Path.Combine(root, "exact-lookup.db"));
await using (var connection = await exactStore.OpenAsync(token))
{
    await exactStore.InsertBatchAsync(connection,
    [
        new() { CodeContent = "FULL%CODE_1", Status = ScanStatuses.Pass, ReceivedUtc = DateTime.UtcNow },
        new() { CodeContent = "FULLxCODEx1", Status = ScanStatuses.Pass, ReceivedUtc = DateTime.UtcNow },
        new() { CodeContent = "full%CODE_1", Status = ScanStatuses.Pass, ReceivedUtc = DateTime.UtcNow },
        new() { CodeContent = "  EXACT SPACES  ", Status = ScanStatuses.Pass, ReceivedUtc = DateTime.UtcNow },
        new() { CodeContent = "", Status = ScanStatuses.NoRead, ReceivedUtc = DateTime.UtcNow },
    ], token);
    await using var legacy = connection.CreateCommand();
    legacy.CommandText = """
        INSERT INTO ScanRecords (ReceivedUtc, Code, Serial, Raw, Status)
        VALUES ($time, 'TAIL', 'TAIL', 'LEGACY%FULL/TAIL', 'Pass');
        """;
    legacy.Parameters.AddWithValue("$time", DateTime.UtcNow.ToString("O"));
    await legacy.ExecuteNonQueryAsync(token);
}
var exactQuery = new ScanRecordQuery(exactStore);
var literal = await exactQuery.GetAsync(new ScanRecordFilter { CodeContentEquals = "FULL%CODE_1" }, pageSize: 1, cancellationToken: token);
Check(literal.TotalCount == 1 && literal.Items.Single().CodeContent == "FULL%CODE_1", "Exact lookup đếm/lấy đúng một mã, ký tự % và _ là literal");
var caseMiss = await exactQuery.GetAsync(new ScanRecordFilter { CodeContentEquals = "Full%CODE_1" }, cancellationToken: token);
Check(caseMiss.TotalCount == 0 && caseMiss.Items.Count == 0, "Exact lookup không gộp CodeContent khác hoa thường");
var fragmentMiss = await exactQuery.GetAsync(new ScanRecordFilter { CodeContentEquals = "CODE_1" }, cancellationToken: token);
Check(fragmentMiss.TotalCount == 0, "Exact lookup không nhận fragment nội dung mã");
var fullLegacy = await exactQuery.GetAsync(new ScanRecordFilter { CodeContentEquals = "LEGACY%FULL/TAIL" }, cancellationToken: token);
var serialMiss = await exactQuery.GetAsync(new ScanRecordFilter { CodeContentEquals = "TAIL" }, cancellationToken: token);
Check(fullLegacy.TotalCount == 1 && fullLegacy.Items.Single().CodeContent == "LEGACY%FULL/TAIL" && serialMiss.TotalCount == 0,
    "Exact lookup dữ liệu cũ dùng Raw đầy đủ, không khớp Serial rút gọn");
var spaces = await exactQuery.GetAsync(new ScanRecordFilter { CodeContentEquals = "  EXACT SPACES  " }, cancellationToken: token);
var trimmedMiss = await exactQuery.GetAsync(new ScanRecordFilter { CodeContentEquals = "EXACT SPACES" }, cancellationToken: token);
Check(spaces.TotalCount == 1 && trimmedMiss.TotalCount == 0, "Exact lookup giữ khoảng trắng có ý nghĩa");
var emptyExact = await exactQuery.GetAsync(new ScanRecordFilter { CodeContentEquals = "" }, cancellationToken: token);
Check(emptyExact.TotalCount == 1 && emptyExact.Items.Single().Status == ScanStatuses.NoRead, "Giá trị exact rỗng vẫn là một bộ lọc, khác null");
var exactSend = new ScanSendStatusStore(exactStore);
var sendLiteral = await exactSend.GetBySendStatusAsync(new ScanSendFilter { CodeContentEquals = "FULL%CODE_1" }, token);
var sendFragment = await exactSend.GetBySendStatusAsync(new ScanSendFilter { CodeContentEquals = "CODE_1" }, token);
Check(sendLiteral.Count == 1 && sendLiteral[0].CodeContent == "FULL%CODE_1" && sendFragment.Count == 0,
    "Exact filter trạng thái gửi giữ cùng identity semantics như lịch sử");
var literalId = sendLiteral[0].Id;
var spacesId = spaces.Items.Single().Id;
await exactSend.MarkFailedAsync(literalId, 503, "retry me", token);
await exactSend.MarkFailedAsync(spacesId, 400, "retry too", token);
var reset = await exactSend.ResetToPendingAsync([literalId, spacesId, literalId, long.MaxValue], token);
var resetRows = await exactSend.GetBySendStatusAsync(new ScanSendFilter { SendStatus = ScanSendStatuses.Pending }, token);
Check(reset == 2 && resetRows.Where(row => row.Id == literalId || row.Id == spacesId)
        .All(row => row.SendStatusCode == 0 && row.SendMessage == "" && row.SendAttempts == 1),
    "ResetToPending parameter hóa danh sách Id, không lỗi SQL và không đếm trùng");
// Only failed records are eligible for retry; a previous reset is already Pending.
await exactSend.MarkFailedAsync(literalId, 503, "retry the batch case", token);
var manyIds = Enumerable.Range(10000, 1200).Select(value => (long)value).Append(literalId).ToArray();
Check(await exactSend.ResetToPendingAsync(manyIds, token) == 1
        && await exactSend.ResetToPendingAsync([], token) == 0,
    "ResetToPending chia danh sách lớn thành batch và chấp nhận danh sách rỗng");

var reliablePath = Path.Combine(root, "reliable-queue.db");
using (var writer = new ScanRecordWriter(
    Options.Create(new ScanStoreOptions
    {
        Path = reliablePath,
        QueueCapacity = 1,
        BatchSize = 1,
        FlushIntervalMs = 20,
    }), new ScanStore(reliablePath)))
{
    await writer.VerifyHealthyAsync(token);
    await using var blocker = new SqliteConnection($"Data Source={reliablePath}");
    await blocker.OpenAsync(token);
    await using var lockTransaction = blocker.BeginTransaction(deferred: false);
    var recordCount = 12;
    var producer = Task.Run(() =>
    {
        for (var i = 0; i < recordCount; i++)
            writer.Enqueue(new ScanRecordEntity
            {
                ReceivedUtc = DateTime.UtcNow,
                CodeContent = $"QUEUE-{i:D2}",
                Raw = $"QUEUE-{i:D2}",
                OrderNo = "PO-QUEUE",
                Status = ScanStatuses.Pass,
            });
    }, token);
    await Task.Delay(TimeSpan.FromSeconds(6), token); // Cross SQLite's 5s busy_timeout; the writer retries.
    Check(!producer.IsCompleted, "Queue capacity 1 tạo backpressure trong lúc SQLite bị khóa, không trả thành công giả");
    await lockTransaction.RollbackAsync(token);
    await producer.WaitAsync(token);
    await writer.FlushAsync(token);
    var durable = await new ScanRecordQuery(new ScanStore(reliablePath)).GetAsync(pageSize: 100, cancellationToken: token);
    Check(durable.TotalCount == recordCount
        && durable.Items.Select(item => item.CodeContent).ToHashSet(StringComparer.Ordinal)
            .SetEquals(Enumerable.Range(0, recordCount).Select(i => $"QUEUE-{i:D2}")),
        "Queue capacity 1 ghi bền vững toàn bộ CodeContent sau khi khóa SQLite được giải phóng");
}

SqliteConnection.ClearAllPools();
Directory.Delete(root, recursive: true);
Console.WriteLine($"Đã qua {checks} kiểm thử scan CodeContent; chỉ dùng SQLite tạm.");
