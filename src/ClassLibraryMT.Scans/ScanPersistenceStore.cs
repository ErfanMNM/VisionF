using System.Globalization;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;

namespace ClassLibraryMT.Scans;

/// <summary>SQLite scan history plus durable, idempotent intent for the separate PO database.</summary>
internal sealed class ScanPersistenceStore(ScanStore store) : IScanPersistenceStore
{
    public async Task PersistBatchAsync(IReadOnlyList<ScanPersistenceEnvelope> batch, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(batch);
        if (batch.Count == 0) return;
        foreach (var envelope in batch) Validate(envelope);

        await using var connection = await store.OpenAsync(ct).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
        await using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = """
            INSERT INTO ScanRecords
                (EventId, ReceivedUtc, Code, Gtin, Serial, Status, ProcessingTimeMs,
                 PlcResult, BatchNo, Raw, OrderNo, OrderCreatedUtc, PersistenceReady, PoAccepted, PlcAckTimeMs)
            VALUES ($event, $utc, $code, $gtin, $serial, $status, $processing,
                    $plc, $batch, $raw, $order, $created, $ready, $accepted, $ack)
            ON CONFLICT(EventId) DO NOTHING;
            """;
        foreach (var envelope in batch)
        {
            var record = envelope.Record;
            var hash = ScanPersistenceIdentity.Hash(envelope);
            await using (var receipt = connection.CreateCommand())
            {
                receipt.Transaction = transaction;
                receipt.CommandText = "SELECT IdentityHash FROM ScanPersistenceReceipts WHERE EventId = $event;";
                receipt.Parameters.AddWithValue("$event", envelope.EventId);
                if (await receipt.ExecuteScalarAsync(ct).ConfigureAwait(false) is byte[] previous)
                {
                    if (!CryptographicOperations.FixedTimeEquals(hash, previous))
                        throw new InvalidOperationException("EventId was reused for a different scan attempt.");
                    // The atomic receipt proves the event already committed. It
                    // may still have pending PO work, or its detail may have
                    // expired. Neither case permits recreating or replacing it.
                    continue;
                }
            }
            insert.Parameters.Clear();
            insert.Parameters.AddWithValue("$event", envelope.EventId);
            insert.Parameters.AddWithValue("$utc", ScanStore.FormatUtc(record.ReceivedUtc));
            insert.Parameters.AddWithValue("$code", record.CodeContent);
            insert.Parameters.AddWithValue("$gtin", record.Gtin);
            insert.Parameters.AddWithValue("$serial", record.Serial);
            insert.Parameters.AddWithValue("$status", record.Status);
            insert.Parameters.AddWithValue("$processing", record.ProcessingTimeMs);
            insert.Parameters.AddWithValue("$ack", record.PlcAckTimeMs);
            insert.Parameters.AddWithValue("$plc", record.PlcResult);
            insert.Parameters.AddWithValue("$batch", string.IsNullOrWhiteSpace(record.BatchNo) ? ScanBatchDefaults.NoBatch : record.BatchNo);
            insert.Parameters.AddWithValue("$raw", record.Raw);
            insert.Parameters.AddWithValue("$order", record.OrderNo ?? string.Empty);
            insert.Parameters.AddWithValue("$created", envelope.OrderCreatedUtc is { } created ? ScanStore.FormatUtc(created) : DBNull.Value);
            insert.Parameters.AddWithValue("$ready", envelope.Accepted ? 0 : 1);
            insert.Parameters.AddWithValue("$accepted", envelope.Accepted ? 1 : 0);
            var inserted = await insert.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            if (inserted == 0)
            {
                // A retry may follow an uncertain commit or a later Checkee ACK.
                // Do not replace the row, its primary key, readiness, or send state.
                await VerifySameAttemptAsync(connection, transaction, envelope, ct).ConfigureAwait(false);
            }

            if (envelope.Accepted)
            {
                await using var intent = connection.CreateCommand();
                intent.Transaction = transaction;
                intent.CommandText = """
                    INSERT INTO ScanPoOutbox (EventId, OrderNo, OrderCreatedUtc, CodeContent, ScannedUtc)
                    VALUES ($event, $order, $created, $code, $utc)
                    ON CONFLICT(EventId) DO NOTHING;
                    """;
                intent.Parameters.AddWithValue("$event", envelope.EventId);
                intent.Parameters.AddWithValue("$order", record.OrderNo);
                intent.Parameters.AddWithValue("$created", ScanStore.FormatUtc(envelope.OrderCreatedUtc!.Value));
                intent.Parameters.AddWithValue("$code", record.CodeContent);
                intent.Parameters.AddWithValue("$utc", ScanStore.FormatUtc(record.ReceivedUtc));
                await intent.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
            await using var remember = connection.CreateCommand();
            remember.Transaction = transaction;
            remember.CommandText = "INSERT INTO ScanPersistenceReceipts (EventId, IdentityHash) VALUES ($event, $hash);";
            remember.Parameters.AddWithValue("$event", envelope.EventId);
            remember.Parameters.AddWithValue("$hash", hash);
            await remember.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        await transaction.CommitAsync(ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ScanPersistenceEnvelope>> GetPendingAsync(CancellationToken ct = default)
    {
        await using var connection = await store.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        // Read the captured outbox values rather than current application state.
        command.CommandText = """
            SELECT o.EventId, o.OrderNo, o.OrderCreatedUtc, o.CodeContent, o.ScannedUtc,
                   r.Id, r.Gtin, r.Serial, r.Status, r.ProcessingTimeMs, r.PlcResult, r.BatchNo, r.Raw, r.PlcAckTimeMs
            FROM ScanPoOutbox o JOIN ScanRecords r ON r.EventId = o.EventId
            WHERE o.AppliedUtc IS NULL
            ORDER BY r.Id;
            """;
        var result = new List<ScanPersistenceEnvelope>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            result.Add(new ScanPersistenceEnvelope(reader.GetString(0), new ScanRecordEntity
            {
                Id = reader.GetInt64(5), OrderNo = reader.GetString(1), CodeContent = reader.GetString(3),
                ReceivedUtc = ParseUtc(reader.GetString(4)), Gtin = reader.GetString(6), Serial = reader.GetString(7),
                Status = reader.GetString(8), ProcessingTimeMs = reader.GetDouble(9), PlcResult = reader.GetString(10),
                BatchNo = reader.GetString(11), Raw = reader.GetString(12),
                PlcAckTimeMs = reader.GetDouble(13),
            }, ParseUtc(reader.GetString(2)), true));
        }
        return result;
    }

    public async Task CompleteAsync(IReadOnlyList<string> eventIds, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(eventIds);
        if (eventIds.Count == 0) return;
        await using var connection = await store.OpenAsync(ct).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE ScanPoOutbox SET AppliedUtc = COALESCE(AppliedUtc, $utc) WHERE EventId = $event;
            UPDATE ScanRecords SET PersistenceReady = 1
            WHERE EventId = $event AND EXISTS
                (SELECT 1 FROM ScanPoOutbox WHERE EventId = $event AND AppliedUtc IS NOT NULL);
            """;
        command.Parameters.AddWithValue("$utc", ScanStore.FormatUtc(DateTime.UtcNow));
        var eventParameter = command.Parameters.Add("$event", SqliteType.Text);
        foreach (var id in eventIds.Distinct(StringComparer.Ordinal))
        {
            eventParameter.Value = id;
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        await transaction.CommitAsync(ct).ConfigureAwait(false);
    }

    public async Task VerifyHealthyAsync(CancellationToken ct = default)
    {
        await using var connection = await store.OpenAsync(ct).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
        await transaction.RollbackAsync(ct).ConfigureAwait(false);
    }

    public Task<int> PruneAsync(DateTime cutoff, CancellationToken ct = default) => store.PruneAsync(cutoff, ct);

    private static void Validate(ScanPersistenceEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentNullException.ThrowIfNull(envelope.Record);
        ArgumentException.ThrowIfNullOrWhiteSpace(envelope.EventId);
        if (envelope.Accepted && (string.IsNullOrWhiteSpace(envelope.Record.OrderNo) || !envelope.OrderCreatedUtc.HasValue))
            throw new ArgumentException("An accepted scan must capture its PO database identity.", nameof(envelope));
    }

    private static async Task VerifySameAttemptAsync(SqliteConnection connection, SqliteTransaction transaction,
        ScanPersistenceEnvelope envelope, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT COUNT(*) FROM ScanRecords
            WHERE EventId = $event AND ReceivedUtc = $utc AND Code = $code COLLATE BINARY
                AND OrderNo = $order COLLATE BINARY AND OrderCreatedUtc IS $created AND PoAccepted = $accepted;
            """;
        command.Parameters.AddWithValue("$event", envelope.EventId);
        command.Parameters.AddWithValue("$utc", ScanStore.FormatUtc(envelope.Record.ReceivedUtc));
        command.Parameters.AddWithValue("$code", envelope.Record.CodeContent);
        command.Parameters.AddWithValue("$order", envelope.Record.OrderNo ?? string.Empty);
        command.Parameters.AddWithValue("$created", envelope.OrderCreatedUtc is { } created ? ScanStore.FormatUtc(created) : DBNull.Value);
        command.Parameters.AddWithValue("$accepted", envelope.Accepted ? 1 : 0);
        if (Convert.ToInt64(await command.ExecuteScalarAsync(ct).ConfigureAwait(false), CultureInfo.InvariantCulture) != 1)
            throw new InvalidOperationException("EventId was reused for a different scan attempt.");
    }

    private static DateTime ParseUtc(string value) => DateTime.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}
