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
using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
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

// Dedicated concurrent clients share a start gate, not an initialization lock. No OpenAsync
// is allowed to warm up the fixture before the competing clients are released together.
// Independent instances exercise the database-level guarantee, not just singleton DI usage.
await ParallelSchemaInitializationAsync("parallel-shared-fresh.db", sharedInstance: true, legacy: false);
await ParallelSchemaInitializationAsync("parallel-independent-fresh.db", sharedInstance: false, legacy: false);
await ParallelSchemaInitializationAsync("parallel-independent-legacy.db", sharedInstance: false, legacy: true);

async Task ParallelSchemaInitializationAsync(string fileName, bool sharedInstance, bool legacy)
{
    var parallelPath = Path.Combine(root, fileName);
    const string legacyContent = "  Legacy/Việt\u001dFULL-Code  ";
    if (legacy)
    {
        await using var seed = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = parallelPath, Pooling = false }.ToString());
        await seed.OpenAsync(token);
        await using var command = seed.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode=WAL;
            CREATE TABLE ScanRecords (
                Id INTEGER PRIMARY KEY AUTOINCREMENT, ReceivedUtc TEXT NOT NULL, Code TEXT NOT NULL,
                Status TEXT NOT NULL DEFAULT 'Fail',
                ProcessingTimeMs INTEGER NOT NULL DEFAULT 0, Raw TEXT NOT NULL DEFAULT '',
                SendStatus TEXT NOT NULL DEFAULT '', SendAttempts INTEGER NOT NULL DEFAULT 0,
                SendMessage TEXT NOT NULL DEFAULT '');
            INSERT INTO ScanRecords (Id,ReceivedUtc,Code,Status,ProcessingTimeMs,Raw,SendStatus,SendAttempts,SendMessage)
            VALUES (41,'2026-11-27T00:00:00.0000000Z','legacy-tail','Pass',0.375,$raw,'Sent',3,'legacy ACK');
            """;
        command.Parameters.AddWithValue("$raw", legacyContent);
        await command.ExecuteNonQueryAsync(token);
    }

    const int clientCount = 12;
    using var ready = new CountdownEvent(clientCount);
    using var start = new ManualResetEventSlim();
    var common = new ScanStore(parallelPath);
    var clients = Enumerable.Range(0, clientCount).Select(_ => Task.Factory.StartNew(() =>
    {
        var client = sharedInstance ? common : new ScanStore(parallelPath);
        ready.Signal();
        start.Wait(token);
        using var connection = client.OpenAsync(token).GetAwaiter().GetResult();
        var columns = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        using (var schema = connection.CreateCommand())
        {
            schema.CommandText = "PRAGMA table_info(ScanRecords);";
            using var reader = schema.ExecuteReader();
            while (reader.Read()) columns.Add(reader.GetString(1), reader.GetString(2));
        }
        var expectedColumns = new[] { "EventId", "OrderCreatedUtc", "PersistenceReady", "PoAccepted", "PlcAckTimeMs" };
        if (expectedColumns.Any(column => !columns.ContainsKey(column)) || columns["ProcessingTimeMs"] != "REAL")
            throw new InvalidOperationException("Parallel opener observed an incomplete/reverted migration: " + fileName);
        using var version = connection.CreateCommand();
        version.CommandText = "SELECT COUNT(*) FROM SchemaVersion WHERE Version=$version;";
        version.Parameters.AddWithValue("$version", ScanStore.CurrentSchemaVersion);
        if (Convert.ToInt64(version.ExecuteScalar()) != 1)
            throw new InvalidOperationException("Parallel opener observed missing schema commit marker.");
        return columns.Count;
    }, token, TaskCreationOptions.LongRunning, TaskScheduler.Default)).ToArray();
    try { ready.Wait(token); }
    finally { start.Set(); }
    var completed = await Task.WhenAll(clients).WaitAsync(token);
    Check(completed.Length == clientCount && completed.Distinct().Count() == 1,
        "Mở schema đồng thời hoàn tất đầy đủ, không duplicate column/rebuild race: " + fileName);

    await using var verify = await new ScanStore(parallelPath).OpenAsync(token);
    await using (var schema = verify.CreateCommand())
    {
        schema.CommandText = """
            SELECT COUNT(*) FROM sqlite_master
            WHERE type='index' AND name='IX_ScanRecords_EventId'
                AND NOT EXISTS (SELECT 1 FROM sqlite_master WHERE type='table' AND name='ScanRecords_Legacy');
            """;
        Check(Convert.ToInt64(await schema.ExecuteScalarAsync(token)) == 1,
            "Parallel migration giữ unique EventId index và không để bảng Legacy trung gian: " + fileName);
    }
    if (legacy)
    {
        await using var history = verify.CreateCommand();
        history.CommandText = "SELECT Id,Raw,ProcessingTimeMs,SendStatus,SendAttempts,SendMessage,Status FROM ScanRecords;";
        await using var reader = await history.ExecuteReaderAsync(token);
        Check(await reader.ReadAsync(token) && reader.GetInt64(0) == 41 && reader.GetString(1) == legacyContent
            && reader.GetDouble(2) == 0.375 && reader.GetString(3) == ScanSendStatuses.Sent
            && reader.GetInt32(4) == 3 && reader.GetString(5) == "legacy ACK" && reader.GetString(6) == ScanStatuses.Pass
            && !await reader.ReadAsync(token),
            "Parallel INTEGER→REAL migration bảo toàn primary key, raw GS/Unicode, timing, Pass và Sent ACK đúng một dòng");
    }
}

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
