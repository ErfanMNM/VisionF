using ClassLibraryMT.Scans;
using Microsoft.Data.Sqlite;

// This executable has no app config, devices, HTTP, production DB discovery or real PO fixtures.
var sandbox = Path.Combine(Path.GetTempPath(), "VisionF-TEST-ASYNC-PO-20261127-Outbox-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(sandbox);
var path = Path.Combine(sandbox, "history.db");
var checks = 0;
void Check(bool valid, string message)
{
    if (!valid) throw new InvalidOperationException(message);
    checks++;
    Console.WriteLine("[OK] " + message);
}
using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
var ct = timeout.Token;
var created = new DateTime(2026, 11, 27, 1, 2, 3, DateTimeKind.Utc).AddTicks(1234567);
var scanned = created.AddMinutes(1).AddTicks(7654321);
const string order = "TEST-ASYNC-PO-20261127-Outbox";
const string gtin = "09506000134352"; // Valid checksum, normalized 14-digit PO GTIN.
const string code = "https://id.example.invalid/01/09506000134352/21/FULL-Code%2F42";
const string invalidCode = "  Việt/GS1/01/PART\u001d21/FULL-CODE  ";
ScanPersistenceEnvelope Attempt(string eventId, string status, bool accepted, string content = code) => new(eventId,
    new ScanRecordEntity
    {
        OrderNo = order, OrderCreatedUtc = created, PoAccepted = accepted, Gtin = gtin,
        ReceivedUtc = scanned, CodeContent = content, Raw = content,
        Status = status, PlcResult = ScanPlcResults.Failed, ProcessingTimeMs = 0.345, PlcAckTimeMs = 2.456,
    }, created, accepted);
var first = Attempt("scan-event-1", ScanStatuses.Pass, true);
var duplicate = Attempt("scan-event-2", ScanStatuses.Duplicate, false);
var batch = new[]
{
    first, duplicate, Attempt("scan-event-3", ScanStatuses.NoRead, false, ""),
    Attempt("scan-event-4", ScanStatuses.InvalidStructure, false, invalidCode),
    Attempt("scan-event-5", ScanStatuses.Overload, false, "busy"),
};
var rawStore = new ScanStore(path);
IScanPersistenceStore persistence = new ScanPersistenceStore(rawStore);
await persistence.VerifyHealthyAsync(ct);
await persistence.PersistBatchAsync(batch, ct);
await persistence.PersistBatchAsync(batch, ct);
var query = new ScanRecordQuery(rawStore);
var history = await query.GetAsync(pageSize: 100, cancellationToken: ct);
Check(history.TotalCount == batch.Length, "Retry same EventIds preserves one history per camera attempt");
Check(history.Items.Count(row => row.CodeContent == code) == 2, "Same full code on different attempts preserves Pass and Duplicate history");
Check(history.Items.Any(row => row.Status == ScanStatuses.NoRead)
    && history.Items.Any(row => row.Status == ScanStatuses.InvalidStructure)
    && history.Items.Any(row => row.Status == ScanStatuses.Overload), "NoRead, invalid and overload all have scan history");
Check(history.Items.Single(row => row.Status == ScanStatuses.InvalidStructure).CodeContent == invalidCode,
    "Rejected structure retains original whitespace, Unicode and GS separator without claiming accepted Pass");

// Recreate store as on process restart, before the separate PO commit has completed.
persistence = new ScanPersistenceStore(new ScanStore(path));
var pending = await persistence.GetPendingAsync(ct);
Check(pending.Count == 1 && pending[0].EventId == first.EventId && pending[0].Accepted, "Restart replays durable PO intent once");
Check(pending[0].Record.OrderNo == order && pending[0].OrderCreatedUtc == created
    && pending[0].Record.CodeContent == code && pending[0].Record.ReceivedUtc == scanned,
    "Replay preserves captured PO identity, original scan timestamp and full ordinal code");
Check(pending[0].Record.ProcessingTimeMs == 0.345 && pending[0].Record.PlcAckTimeMs == 2.456
    && history.Items.All(row => row.PlcAckTimeMs == 2.456),
    "History and replay keep separate camera-to-call and camera-to-ACK measurements");
var sender = new ScanSendStatusStore(rawStore);
Check((await sender.GetPendingAsync(true, 100, ct)).Count == 0, "Accepted history is ineligible for Checkee until PO application completes");
Check((await sender.GetPendingAsync(false, 100, ct)).Count == 4, "OnlyPass=false still excludes uncommitted PO intents");
Check((await sender.GetBySendStatusAsync(cancellationToken: ct)).All(row => row.Status != ScanStatuses.Pass),
    "Manual send query also gates unresolved PO intents");
var blockedRow = history.Items.Single(row => row.Status == ScanStatuses.Pass);
await sender.MarkSentAsync(blockedRow.Id, "must not apply", ct);
await sender.MarkFailedAsync(blockedRow.Id, 503, "must not apply", ct);
await sender.MarkPendingAsync(blockedRow.Id, "must not apply", ct);
var blocked = (await query.GetAsync(new ScanRecordFilter { Status = ScanStatus.Pass }, cancellationToken: ct)).Items.Single();
Check(blocked.SendStatus == "" && blocked.SendAttempts == 0, "Manual send state methods cannot acknowledge unresolved PO records");
try
{
    await sender.BeginSendAttemptAsync([blockedRow], gtin, "TEST-BATCH", "TEST-TYPE", ct);
    throw new InvalidOperationException("Expected blocked Checkee intent");
}
catch (InvalidOperationException ex) when (ex.Message.Contains("chưa đồng bộ", StringComparison.Ordinal)) { }
Check((await sender.GetRecentSendAttemptsAsync(cancellationToken: ct)).Count == 0,
    "Claim rejects unresolved record and rolls back its HTTP send intent");
await using (var connection = new SqliteConnection("Data Source=" + path))
{
    await connection.OpenAsync(ct);
    await using var set = connection.CreateCommand();
    set.CommandText = "UPDATE ScanRecords SET SendStatus='Failed' WHERE Id=$id;";
    set.Parameters.AddWithValue("$id", blockedRow.Id);
    await set.ExecuteNonQueryAsync(ct);
}
Check(await sender.ResetToPendingAsync([blockedRow.Id], ct) == 0,
    "Manual retry cannot reset an unresolved PO record even if its send status is Failed");
await using (var connection = new SqliteConnection("Data Source=" + path))
{
    await connection.OpenAsync(ct);
    await using var set = connection.CreateCommand();
    set.CommandText = "UPDATE ScanRecords SET SendStatus='' WHERE Id=$id;";
    set.Parameters.AddWithValue("$id", blockedRow.Id);
    await set.ExecuteNonQueryAsync(ct);
}

var rolledBack = Attempt("transaction-before-collision", ScanStatuses.Pass, true, "new-code");
try
{
    await persistence.PersistBatchAsync([rolledBack, Attempt(first.EventId, ScanStatuses.Pass, true, "different-code")], ct);
    throw new InvalidOperationException("Expected EventId collision");
}
catch (InvalidOperationException ex) when (ex.Message.Contains("EventId was reused", StringComparison.Ordinal)) { }
Check((await query.GetAsync(cancellationToken: ct)).TotalCount == batch.Length
    && (await persistence.GetPendingAsync(ct)).Count == 1, "EventId collision rolls back both new history and PO intent in whole batch");

await persistence.CompleteAsync([first.EventId], ct); // Represents successful separate PO batch apply.
Check((await persistence.GetPendingAsync(ct)).Count == 0, "Completing PO intent removes it from replay");
var ready = (await sender.GetPendingAsync(true, 100, ct)).Single();
Check(ready.Id == blockedRow.Id && ready.CodeContent == code, "PO completion makes original history eligible without changing record identity");
Check(ready.PoAccepted && ready.Status == ScanStatuses.Pass && ready.OrderNo == order
    && ready.OrderCreatedUtc == created && ready.Gtin == gtin,
    "Ready Checkee fixture carries accepted Pass, immutable PO identity and normalized matching GTIN");
var sendAttempt = await sender.BeginSendAttemptAsync([ready], gtin, "TEST-BATCH", "TEST-TYPE", ct);
await sender.CompleteSendAttemptAsync(sendAttempt, CheckeeSendAttemptStatuses.Sent, 200, "TEST ACK", ct);
await persistence.PersistBatchAsync(batch, ct); // Lost worker acknowledgement after both DB commits.
await persistence.CompleteAsync([first.EventId], ct);
var afterRetry = (await query.GetAsync(new ScanRecordFilter { Status = ScanStatus.Pass }, cancellationToken: ct)).Items.Single();
Check(afterRetry.Id == ready.Id && afterRetry.SendStatus == ScanSendStatuses.Sent && afterRetry.SendAttempts == 1
    && afterRetry.SendMessage == "TEST ACK", "Replay after Checkee ACK never overwrites Sent, attempts, primary key or message");
Check((await persistence.GetPendingAsync(ct)).Count == 0 && (await query.GetAsync(cancellationToken: ct)).TotalCount == batch.Length,
    "Idempotent completion and reinsert never reactivate completed outbox or duplicate history");

var retained = Attempt("pending-retention", ScanStatuses.Pass, true, "retained-code");
await persistence.PersistBatchAsync([retained], ct);
await persistence.PruneAsync(scanned.AddDays(1), ct);
Check((await query.GetAsync(cancellationToken: ct)).TotalCount == 1
    && (await persistence.GetPendingAsync(ct)).Single().EventId == retained.EventId,
    "Retention never prunes history needed by an unresolved PO intent");
await using (var connection = new SqliteConnection("Data Source=" + path))
{
    await connection.OpenAsync(ct);
    await using var count = connection.CreateCommand();
    count.CommandText = "SELECT COUNT(*) FROM ScanPoOutbox WHERE AppliedUtc IS NOT NULL;";
    Check(Convert.ToInt64(await count.ExecuteScalarAsync(ct)) == 0,
        "Retention prunes completed PO outbox details with their history in one transaction");
}
await persistence.PersistBatchAsync(batch, ct);
Check((await query.GetAsync(cancellationToken: ct)).TotalCount == 1
    && (await persistence.GetPendingAsync(ct)).Single().EventId == retained.EventId
    && (await sender.GetPendingAsync(true, 100, ct)).Count == 0,
    "Complete then prune then retry EventId neither recreates blocked history nor resends an ACKed attempt");
try
{
    await persistence.PersistBatchAsync([Attempt(first.EventId, ScanStatuses.Pass, true, "post-retention-collision")], ct);
    throw new InvalidOperationException("Expected receipt identity collision");
}
catch (InvalidOperationException ex) when (ex.Message.Contains("EventId was reused", StringComparison.Ordinal)) { }
Check((await query.GetAsync(cancellationToken: ct)).TotalCount == 1,
    "Compact receipt still rejects EventId identity reuse after detailed history expires");

// Build an old database from scratch only inside this test's sandbox.
var legacyPath = Path.Combine(sandbox, "legacy-v5-integer.db");
await using (var legacy = new SqliteConnection("Data Source=" + legacyPath))
{
    await legacy.OpenAsync(ct);
    await using var command = legacy.CreateCommand();
    command.CommandText = """
        CREATE TABLE ScanRecords (
            Id INTEGER PRIMARY KEY AUTOINCREMENT, ReceivedUtc TEXT NOT NULL, Code TEXT NOT NULL DEFAULT '',
            Status TEXT NOT NULL DEFAULT 'Fail', ProcessingTimeMs INTEGER NOT NULL DEFAULT 0,
            SendStatus TEXT NOT NULL DEFAULT '', SendAttempts INTEGER NOT NULL DEFAULT 0,
            SendMessage TEXT NOT NULL DEFAULT ''
        );
        INSERT INTO ScanRecords (ReceivedUtc, Code, Status, SendStatus, SendAttempts, SendMessage)
        VALUES ('2026-11-27T00:00:00.0000000Z', 'TEST-LEGACY-SENT', 'Pass', 'Sent', 3, 'legacy ACK'),
               ('2026-11-27T00:00:01.0000000Z', 'TEST-LEGACY-FAILED', 'Pass', 'Failed', 4, 'legacy failure'),
               ('2026-11-27T00:00:02.0000000Z', 'TEST-LEGACY-PENDING', 'Pass', '', 0, '');
        """;
    await command.ExecuteNonQueryAsync(ct);
}
var legacyStore = new ScanStore(legacyPath);
var legacyPersistence = new ScanPersistenceStore(legacyStore);
await legacyPersistence.VerifyHealthyAsync(ct);
var legacyRows = (await new ScanRecordQuery(legacyStore).GetAsync(cancellationToken: ct)).Items;
Check(legacyRows.Count == 3 && legacyRows.Single(row => row.CodeContent == "TEST-LEGACY-SENT") is
    { SendStatus: ScanSendStatuses.Sent, SendAttempts: 3, SendMessage: "legacy ACK" }, "Legacy INTEGER migration preserves Sent state, attempts and ACK");
Check(legacyRows.Single(row => row.CodeContent == "TEST-LEGACY-FAILED") is
    { SendStatus: ScanSendStatuses.Failed, SendAttempts: 4, SendMessage: "legacy failure" }, "Legacy migration preserves Failed state and attempt history");
Check((await new ScanSendStatusStore(legacyStore).GetPendingAsync(true, 100, ct)).Single().CodeContent == "TEST-LEGACY-PENDING",
    "Legacy rows default ready while existing Sent and Failed stay excluded from auto send");
await legacyPersistence.PersistBatchAsync([Attempt("post-legacy-upgrade", ScanStatuses.Pass, true)], ct);
Check((await legacyPersistence.GetPendingAsync(ct)).Count == 1, "Outbox works immediately after legacy table rebuild");

Console.WriteLine($"PASS: {checks} sandbox outbox checks. Artifacts: {sandbox}");
