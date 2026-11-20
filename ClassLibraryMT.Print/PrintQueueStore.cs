using ClassLibraryMT.Po;
using Microsoft.Data.Sqlite;

namespace ClassLibraryMT.Print;

/// <summary>Trạng thái in nằm riêng trong từng DB PO, không dùng ActivatedUtc.</summary>
public sealed class PrintQueueStore
{
    private readonly PoStore _poStore;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public PrintQueueStore(PoStore poStore) => _poStore = poStore;

    public async Task<PrintCodeBatch?> ClaimNextAsync(string orderNo, DateTime createdUtc, string printerId, int quantity, CancellationToken ct = default)
    {
        if (quantity <= 0) return null;
        var path = ResolvePoPath(orderNo, createdUtc);
        if (path is null) return null;
        quantity = Math.Min(quantity, 1000);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var db = await OpenAsync(path, ct).ConfigureAwait(false);
            await using var tx = db.BeginTransaction(deferred: false);
            var batchId = Guid.NewGuid().ToString("N");
            var items = new List<PrintQueueItem>();
            await using (var select = db.CreateCommand())
            {
                select.Transaction = (SqliteTransaction)tx;
                select.CommandText = """
                    SELECT pc.Code, pc.Gtin, pc.Serial,
                           COALESCE(ps.State, 0), COALESCE(ps.Attempts, 0)
                    FROM PoCodes pc
                    LEFT JOIN PrintCodeStates ps ON ps.Code = pc.Code
                    WHERE pc.ActivatedUtc IS NULL
                      AND COALESCE(ps.State, 0) = $pending
                    ORDER BY pc.Serial, pc.Code
                    LIMIT $quantity;
                    """;
                select.Parameters.AddWithValue("$pending", (int)PrintCodeState.Pending);
                select.Parameters.AddWithValue("$quantity", quantity);
                await using var reader = await select.ExecuteReaderAsync(ct).ConfigureAwait(false);
                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    items.Add(new PrintQueueItem
                    {
                        Code = reader.GetString(0), Gtin = reader.GetString(1), Serial = reader.GetString(2),
                        State = PrintCodeState.Pending, Attempts = reader.GetInt32(4) + 1,
                    });
                }
            }
            if (items.Count == 0) { await tx.RollbackAsync(ct).ConfigureAwait(false); return null; }
            foreach (var item in items)
            {
                await using var upsert = db.CreateCommand();
                upsert.Transaction = (SqliteTransaction)tx;
                upsert.CommandText = """
                    INSERT INTO PrintCodeStates(Code, State, BatchId, PrinterId, SentUtc, Attempts, LastError)
                    VALUES ($code, $sent, $batch, $printer, $utc, $attempts, NULL)
                    ON CONFLICT(Code) DO UPDATE SET
                        State = excluded.State, BatchId = excluded.BatchId, PrinterId = excluded.PrinterId,
                        SentUtc = excluded.SentUtc, Attempts = excluded.Attempts, LastError = NULL;
                    """;
                upsert.Parameters.AddWithValue("$code", item.Code);
                upsert.Parameters.AddWithValue("$sent", (int)PrintCodeState.Sent);
                upsert.Parameters.AddWithValue("$batch", batchId);
                upsert.Parameters.AddWithValue("$printer", printerId);
                upsert.Parameters.AddWithValue("$utc", DateTime.UtcNow.ToString("O"));
                upsert.Parameters.AddWithValue("$attempts", item.Attempts);
                await upsert.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
            await tx.CommitAsync(ct).ConfigureAwait(false);
            return new PrintCodeBatch(batchId, orderNo, createdUtc, printerId, items);
        }
        finally { _gate.Release(); }
    }

    public async Task MarkPrintedAsync(string orderNo, DateTime createdUtc, string batchId, IReadOnlyCollection<string> codes, string response, CancellationToken ct = default)
        => await UpdateStateAsync(orderNo, createdUtc, batchId, codes, PrintCodeState.Printed, "PrintedUtc", response, ct).ConfigureAwait(false);

    public async Task RecordSendResponseAsync(string orderNo, DateTime createdUtc, string batchId, string code, string response, CancellationToken ct = default)
    {
        var path = ResolvePoPath(orderNo, createdUtc);
        if (path is null) return;
        await using var db = await OpenAsync(path, ct).ConfigureAwait(false);
        await using var cmd = db.CreateCommand();
        cmd.CommandText = "UPDATE PrintCodeStates SET LastResponse = $response, LastError = $error WHERE Code = $code AND BatchId = $batch AND State = $sent;";
        cmd.Parameters.AddWithValue("$response", response);
        cmd.Parameters.AddWithValue("$error", response.StartsWith("MFD|OK", StringComparison.OrdinalIgnoreCase) ? DBNull.Value : response);
        cmd.Parameters.AddWithValue("$code", code);
        cmd.Parameters.AddWithValue("$batch", batchId);
        cmd.Parameters.AddWithValue("$sent", (int)PrintCodeState.Sent);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task<string?> MarkNextPrintedAsync(string orderNo, DateTime createdUtc, string printerId, string response, CancellationToken ct = default)
    {
        var path = ResolvePoPath(orderNo, createdUtc);
        if (path is null) return null;
        await using var db = await OpenAsync(path, ct).ConfigureAwait(false);
        await using var tx = db.BeginTransaction(deferred: false);
        string? code;
        await using (var select = db.CreateCommand())
        {
            select.Transaction = (SqliteTransaction)tx;
            select.CommandText = "SELECT Code FROM PrintCodeStates WHERE State = $sent AND PrinterId = $printer AND LastResponse LIKE 'MFD|OK%' ORDER BY SentUtc, Code LIMIT 1;";
            select.Parameters.AddWithValue("$sent", (int)PrintCodeState.Sent);
            select.Parameters.AddWithValue("$printer", printerId);
            code = await select.ExecuteScalarAsync(ct).ConfigureAwait(false) as string;
        }
        if (code is null) { await tx.RollbackAsync(ct).ConfigureAwait(false); return null; }
        await using (var update = db.CreateCommand())
        {
            update.Transaction = (SqliteTransaction)tx;
            update.CommandText = "UPDATE PrintCodeStates SET State = $printed, PrintedUtc = $utc, LastResponse = $response WHERE Code = $code AND State = $sent;";
            update.Parameters.AddWithValue("$printed", (int)PrintCodeState.Printed);
            update.Parameters.AddWithValue("$utc", DateTime.UtcNow.ToString("O"));
            update.Parameters.AddWithValue("$response", response);
            update.Parameters.AddWithValue("$code", code);
            update.Parameters.AddWithValue("$sent", (int)PrintCodeState.Sent);
            if (await update.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 0)
            {
                await tx.RollbackAsync(ct).ConfigureAwait(false);
                return null;
            }
        }
        await tx.CommitAsync(ct).ConfigureAwait(false);
        return code;
    }

    public async Task<string?> MarkNextPrintedAnyAsync(string printerId, string response, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            string? selectedPath = null;
            string? selectedCode = null;
            string? selectedBatch = null;
            string? selectedUtc = null;
            foreach (var path in _poStore.ListDbFiles())
            {
                await using var db = await OpenAsync(path, ct).ConfigureAwait(false);
                await using var select = db.CreateCommand();
                select.CommandText = "SELECT Code, BatchId, SentUtc FROM PrintCodeStates WHERE State = $sent AND PrinterId = $printer AND LastResponse LIKE 'MFD|OK%' ORDER BY SentUtc, Code LIMIT 1;";
                select.Parameters.AddWithValue("$sent", (int)PrintCodeState.Sent);
                select.Parameters.AddWithValue("$printer", printerId);
                await using var reader = await select.ExecuteReaderAsync(ct).ConfigureAwait(false);
                if (!await reader.ReadAsync(ct).ConfigureAwait(false)) continue;
                var sentUtc = reader.IsDBNull(2) ? string.Empty : reader.GetString(2);
                if (selectedUtc is null || string.CompareOrdinal(sentUtc, selectedUtc) < 0)
                {
                    selectedPath = path;
                    selectedCode = reader.GetString(0);
                    selectedBatch = reader.IsDBNull(1) ? null : reader.GetString(1);
                    selectedUtc = sentUtc;
                }
            }
            if (selectedPath is null || selectedCode is null || selectedBatch is null) return null;

            await using var target = await OpenAsync(selectedPath, ct).ConfigureAwait(false);
            await using var tx = target.BeginTransaction(deferred: false);
            await using var update = target.CreateCommand();
            update.Transaction = (SqliteTransaction)tx;
            update.CommandText = "UPDATE PrintCodeStates SET State = $printed, PrintedUtc = $utc, LastResponse = $response WHERE Code = $code AND BatchId = $batch AND State = $sent;";
            update.Parameters.AddWithValue("$printed", (int)PrintCodeState.Printed);
            update.Parameters.AddWithValue("$utc", DateTime.UtcNow.ToString("O"));
            update.Parameters.AddWithValue("$response", response);
            update.Parameters.AddWithValue("$code", selectedCode);
            update.Parameters.AddWithValue("$batch", selectedBatch);
            update.Parameters.AddWithValue("$sent", (int)PrintCodeState.Sent);
            if (await update.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 0)
            {
                await tx.RollbackAsync(ct).ConfigureAwait(false);
                return null;
            }
            await tx.CommitAsync(ct).ConfigureAwait(false);
            return selectedCode;
        }
        finally { _gate.Release(); }
    }

    public async Task<int> ReconcileActivatedAcrossOrdersAsync(CancellationToken ct = default)
    {
        var total = 0;
        foreach (var path in _poStore.ListDbFiles())
        {
            await using var db = await OpenAsync(path, ct).ConfigureAwait(false);
            await using var cmd = db.CreateCommand();
            cmd.CommandText = """
                UPDATE PrintCodeStates
                SET State = $state, ActiveObservedUtc = $utc,
                    LastError = 'Mã đã active nhưng chưa có xác nhận từ máy in.'
                WHERE State = $sent
                  AND EXISTS (SELECT 1 FROM PoCodes pc WHERE pc.Code = PrintCodeStates.Code AND pc.ActivatedUtc IS NOT NULL);
                """;
            cmd.Parameters.AddWithValue("$state", (int)PrintCodeState.NoResponse);
            cmd.Parameters.AddWithValue("$sent", (int)PrintCodeState.Sent);
            cmd.Parameters.AddWithValue("$utc", DateTime.UtcNow.ToString("O"));
            total += await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        return total;
    }

    public async Task<int> MarkNoResponseForActivatedAsync(string orderNo, DateTime createdUtc, CancellationToken ct = default)
    {
        var path = ResolvePoPath(orderNo, createdUtc);
        if (path is null) return 0;
        await using var db = await OpenAsync(path, ct).ConfigureAwait(false);
        await using var cmd = db.CreateCommand();
        cmd.CommandText = """
            UPDATE PrintCodeStates
            SET State = $state, ActiveObservedUtc = $utc,
                LastError = 'Mã đã active nhưng chưa có xác nhận từ máy in.'
            WHERE State = $sent
              AND EXISTS (SELECT 1 FROM PoCodes pc WHERE pc.Code = PrintCodeStates.Code AND pc.ActivatedUtc IS NOT NULL);
            """;
        cmd.Parameters.AddWithValue("$state", (int)PrintCodeState.NoResponse);
        cmd.Parameters.AddWithValue("$sent", (int)PrintCodeState.Sent);
        cmd.Parameters.AddWithValue("$utc", DateTime.UtcNow.ToString("O"));
        return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task<PrintQueueSummary> GetSummaryAsync(string orderNo, DateTime createdUtc, CancellationToken ct = default)
    {
        var path = ResolvePoPath(orderNo, createdUtc);
        if (path is null) return new(0, 0, 0, 0);
        await using var db = await OpenAsync(path, ct).ConfigureAwait(false);
        await using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT pc.Code, COALESCE(ps.State, 0)
            FROM PoCodes pc LEFT JOIN PrintCodeStates ps ON ps.Code = pc.Code
            WHERE ps.Code IS NOT NULL OR pc.ActivatedUtc IS NULL;
            """;
        var counts = new int[4];
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var state = reader.GetInt32(1);
            if (state is >= 0 and < 4) counts[state]++;
        }
        return new(counts[(int)PrintCodeState.Pending], counts[(int)PrintCodeState.Sent], counts[(int)PrintCodeState.Printed], counts[(int)PrintCodeState.NoResponse]);
    }

    private async Task UpdateStateAsync(string orderNo, DateTime createdUtc, string batchId, IReadOnlyCollection<string> codes, PrintCodeState state, string timeColumn, string response, CancellationToken ct)
    {
        if (codes.Count == 0) return;
        var path = ResolvePoPath(orderNo, createdUtc);
        if (path is null) return;
        await using var db = await OpenAsync(path, ct).ConfigureAwait(false);
        await using var tx = await db.BeginTransactionAsync(ct).ConfigureAwait(false);
        foreach (var code in codes)
        {
            await using var cmd = db.CreateCommand();
            cmd.Transaction = (SqliteTransaction)tx;
            cmd.CommandText = $"UPDATE PrintCodeStates SET State = $state, {timeColumn} = $utc, LastResponse = $response WHERE Code = $code AND BatchId = $batch;";
            cmd.Parameters.AddWithValue("$state", (int)state);
            cmd.Parameters.AddWithValue("$utc", DateTime.UtcNow.ToString("O"));
            cmd.Parameters.AddWithValue("$response", response);
            cmd.Parameters.AddWithValue("$code", code);
            cmd.Parameters.AddWithValue("$batch", batchId);
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        await tx.CommitAsync(ct).ConfigureAwait(false);
    }

    private static async Task<SqliteConnection> OpenAsync(string path, CancellationToken ct)
    {
        var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWrite, Cache = SqliteCacheMode.Shared }.ToString());
        await db.OpenAsync(ct).ConfigureAwait(false);
        await using var pragma = db.CreateCommand();
        pragma.CommandText = "PRAGMA busy_timeout=5000; PRAGMA journal_mode=WAL;";
        await pragma.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        await using var schema = db.CreateCommand();
        schema.CommandText = """
            CREATE TABLE IF NOT EXISTS PrintCodeStates (
                Code TEXT NOT NULL PRIMARY KEY,
                State INTEGER NOT NULL DEFAULT 0,
                BatchId TEXT NULL,
                PrinterId TEXT NULL,
                SentUtc TEXT NULL,
                PrintedUtc TEXT NULL,
                ActiveObservedUtc TEXT NULL,
                Attempts INTEGER NOT NULL DEFAULT 0,
                LastResponse TEXT NULL,
                LastError TEXT NULL
            );
            CREATE INDEX IF NOT EXISTS IX_PrintCodeStates_State ON PrintCodeStates(State);
            """;
        await schema.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        return db;
    }

    private string? ResolvePoPath(string orderNo, DateTime createdUtc)
    {
        var expected = _poStore.GetDbPath(orderNo, createdUtc);
        return File.Exists(expected) ? expected : _poStore.FindExistingDbPath(orderNo);
    }
}
