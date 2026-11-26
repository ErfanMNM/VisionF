using System.Data;
using System.Globalization;
using Microsoft.Data.Sqlite;

namespace ClassLibraryMT.Scans;

/// <summary>
/// Lớp lưu trữ bản ghi quét mã vào SQLite.
///
/// Chịu trách nhiệm:
/// - Tạo schema (bảng <c>ScanRecords</c>, <c>SchemaVersion</c>) nếu chưa có.
/// - Migration: thêm cột mới vào bảng đã tồn tại (kiểm tra <c>PRAGMA table_info</c>).
/// - Thiết lập PRAGMA (WAL, busy_timeout...).
///
/// Mỗi thao tác mở connection riêng (SQLite nhẹ, không cần connection pool phức tạp).
/// </summary>
internal sealed class ScanStore
{
    /// <summary>Phiên bản schema hiện tại.</summary>
    public const int CurrentSchemaVersion = 6;

    private readonly string _dbPath;

    /// <summary>
    /// Khởi tạo lớp lưu trữ.
    /// </summary>
    /// <param name="dbPath">Đường dẫn tuyệt đối tới file database.</param>
    public ScanStore(string dbPath)
    {
        _dbPath = dbPath;
    }

    /// <summary>Đường dẫn file database đang dùng.</summary>
    public string DatabasePath => _dbPath;

    /// <summary>
    /// Mở connection đã thiết lập PRAGMA và đảm bảo schema tồn tại.
    /// </summary>
    /// <param name="ct">Token hủy.</param>
    /// <returns>Connection sẵn dùng (caller dispose).</returns>
    public async Task<SqliteConnection> OpenAsync(CancellationToken ct = default)
    {
        var directory = Path.GetDirectoryName(_dbPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var connection = new SqliteConnection($"Data Source={_dbPath}");
        try
        {
            await connection.OpenAsync(ct).ConfigureAwait(false);
            await ExecutePragmaAsync(connection, "PRAGMA busy_timeout=5000;", ct).ConfigureAwait(false);
            await ExecutePragmaAsync(connection, "PRAGMA journal_mode=WAL;", ct).ConfigureAwait(false);
            await ExecutePragmaAsync(connection, "PRAGMA synchronous=NORMAL;", ct).ConfigureAwait(false);
            await ExecutePragmaAsync(connection, "PRAGMA foreign_keys=ON;", ct).ConfigureAwait(false);
            await EnsureSchemaAsync(connection, ct).ConfigureAwait(false);
            return connection;
        }
        catch
        {
            // Open/PRAGMA/schema có thể bị hủy khi writer dừng. Caller chưa
            // nhận connection nên store phải đóng handle trong đường lỗi.
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Tạo bảng/index nếu chưa có và chạy migration cho các phiên bản cũ.
    /// </summary>
    private static async Task EnsureSchemaAsync(SqliteConnection connection, CancellationToken ct)
    {
        // Schema reads and DDL must share one database write reservation. An instance-only
        // semaphore cannot protect another ScanStore/process opening the same file. Acquire
        // BEFORE checking columns/types so the waiting initializer rechecks the committed schema.
        ct.ThrowIfCancellationRequested();
        await using var transaction = connection.BeginTransaction(deferred: false); // BEGIN IMMEDIATE
        ct.ThrowIfCancellationRequested();
        const string schema = """
            CREATE TABLE IF NOT EXISTS SchemaVersion (
                Version     INTEGER NOT NULL PRIMARY KEY,
                AppliedUtc  TEXT    NOT NULL
            );

            CREATE TABLE IF NOT EXISTS ScanRecords (
                Id               INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                ReceivedUtc      TEXT    NOT NULL,
                Code             TEXT    NOT NULL DEFAULT '',
                Gtin             TEXT    NOT NULL DEFAULT '',
                Serial           TEXT    NOT NULL DEFAULT '',
                Status           TEXT    NOT NULL DEFAULT 'Fail',
                ProcessingTimeMs REAL    NOT NULL DEFAULT 0,
                PlcResult        TEXT    NOT NULL DEFAULT 'Skipped',
                BatchNo          TEXT    NOT NULL DEFAULT 'NoBatch',
                Raw              TEXT    NOT NULL DEFAULT '',
                OrderNo          TEXT    NOT NULL DEFAULT '',
                SendStatus       TEXT    NOT NULL DEFAULT '',
                SendStatusCode   INTEGER NOT NULL DEFAULT 0,
                SendMessage      TEXT    NOT NULL DEFAULT '',
                SendAttempts     INTEGER NOT NULL DEFAULT 0,
                SendTimeUtc      TEXT    NULL,
                SendAttemptUtc   TEXT    NULL
            );

            CREATE TABLE IF NOT EXISTS CheckeeSendAttempts (
                Id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                StartedUtc TEXT NOT NULL,
                CompletedUtc TEXT NULL,
                ProductGtin TEXT NOT NULL,
                BatchCode TEXT NOT NULL,
                SerialType TEXT NOT NULL,
                Status TEXT NOT NULL DEFAULT 'InProgress',
                StatusCode INTEGER NOT NULL DEFAULT 0,
                Message TEXT NOT NULL DEFAULT ''
            );
            CREATE TABLE IF NOT EXISTS CheckeeSendAttemptItems (
                AttemptId INTEGER NOT NULL REFERENCES CheckeeSendAttempts(Id) ON DELETE CASCADE,
                Position INTEGER NOT NULL,
                ScanRecordId INTEGER NOT NULL,
                CodeContent TEXT NOT NULL,
                OrderNo TEXT NOT NULL,
                Status TEXT NOT NULL DEFAULT 'InProgress',
                StatusCode INTEGER NOT NULL DEFAULT 0,
                Message TEXT NOT NULL DEFAULT '',
                PRIMARY KEY (AttemptId, Position)
            );
            """;

        await using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = schema;
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

        // Migration: bảng đã tồn tại từ phiên bản cũ có thể thiếu cột.
        await EnsureColumnAsync(connection, transaction, "ScanRecords", "Gtin", "TEXT NOT NULL DEFAULT ''", ct)
            .ConfigureAwait(false);
        await EnsureColumnAsync(connection, transaction, "ScanRecords", "Serial", "TEXT NOT NULL DEFAULT ''", ct)
            .ConfigureAwait(false);
        await EnsureColumnAsync(connection, transaction, "ScanRecords", "ProcessingTimeMs", "REAL NOT NULL DEFAULT 0", ct)
            .ConfigureAwait(false);
        await EnsureColumnAsync(connection, transaction, "ScanRecords", "PlcResult", "TEXT NOT NULL DEFAULT 'Skipped'", ct)
            .ConfigureAwait(false);
        await EnsureColumnAsync(connection, transaction, "ScanRecords", "BatchNo", "TEXT NOT NULL DEFAULT 'NoBatch'", ct)
            .ConfigureAwait(false);
        await EnsureColumnAsync(connection, transaction, "ScanRecords", "Raw", "TEXT NOT NULL DEFAULT ''", ct)
            .ConfigureAwait(false);

        // Migration v3: cột OrderNo — biết mã quét thuộc PO nào (dùng cho cơ
        // chế chống trùng lịch sử toàn cục và để truy vết báo cáo).
        await EnsureColumnAsync(connection, transaction, "ScanRecords", "OrderNo", "TEXT NOT NULL DEFAULT ''", ct)
            .ConfigureAwait(false);

        // Migration v4: các cột trạng thái gửi mã lên Checkee. Dữ liệu cũ
        // được giữ nguyên — cột mới có giá trị mặc định (chưa gửi).
        await EnsureColumnAsync(connection, transaction, "ScanRecords", "SendStatus", "TEXT NOT NULL DEFAULT ''", ct)
            .ConfigureAwait(false);
        await EnsureColumnAsync(connection, transaction, "ScanRecords", "SendStatusCode", "INTEGER NOT NULL DEFAULT 0", ct)
            .ConfigureAwait(false);
        await EnsureColumnAsync(connection, transaction, "ScanRecords", "SendMessage", "TEXT NOT NULL DEFAULT ''", ct)
            .ConfigureAwait(false);
        await EnsureColumnAsync(connection, transaction, "ScanRecords", "SendAttempts", "INTEGER NOT NULL DEFAULT 0", ct)
            .ConfigureAwait(false);
        await EnsureColumnAsync(connection, transaction, "ScanRecords", "SendTimeUtc", "TEXT NULL", ct)
            .ConfigureAwait(false);
        await EnsureColumnAsync(connection, transaction, "ScanRecords", "SendAttemptUtc", "TEXT NULL", ct)
            .ConfigureAwait(false);

        // Migration v2: cột ProcessingTimeMs đổi từ INTEGER sang REAL để giữ
        // được phần thập phân (ví dụ 0.333 ms). SQLite là kiểu động nên dữ
        // liệu cũ vẫn đọc được, nhưng khai báo kiểu trong schema cho đúng.
        await MigrateProcessingTimeToRealAsync(connection, transaction, ct).ConfigureAwait(false);

        // Add v6 only AFTER the legacy table rebuild, so upgrading an INTEGER
        // ProcessingTimeMs database cannot discard persistence metadata.
        await EnsureColumnAsync(connection, transaction, "ScanRecords", "EventId", "TEXT NULL", ct)
            .ConfigureAwait(false);
        await EnsureColumnAsync(connection, transaction, "ScanRecords", "OrderCreatedUtc", "TEXT NULL", ct)
            .ConfigureAwait(false);
        await EnsureColumnAsync(connection, transaction, "ScanRecords", "PersistenceReady", "INTEGER NOT NULL DEFAULT 1", ct)
            .ConfigureAwait(false);
        await EnsureColumnAsync(connection, transaction, "ScanRecords", "PoAccepted", "INTEGER NOT NULL DEFAULT 0", ct)
            .ConfigureAwait(false);
        await EnsureColumnAsync(connection, transaction, "ScanRecords", "PlcAckTimeMs", "REAL NOT NULL DEFAULT 0", ct)
            .ConfigureAwait(false);

        // Tạo index SAU khi migration xong — index IX_ScanRecords_OrderNo tham
        // chiếu cột OrderNo, tạo trước khi cột tồn tại sẽ báo lỗi SQLite
        // ("no such column") với các DB đã tồn tại từ phiên bản cũ.
        await using (var indexCmd = connection.CreateCommand())
        {
            indexCmd.Transaction = transaction;
            indexCmd.CommandText = """
                CREATE INDEX IF NOT EXISTS IX_ScanRecords_ReceivedUtc ON ScanRecords (ReceivedUtc DESC);
                CREATE INDEX IF NOT EXISTS IX_ScanRecords_Status ON ScanRecords (Status);
                CREATE INDEX IF NOT EXISTS IX_ScanRecords_BatchNo ON ScanRecords (BatchNo);
                CREATE INDEX IF NOT EXISTS IX_ScanRecords_OrderNo ON ScanRecords (OrderNo);
                CREATE INDEX IF NOT EXISTS IX_ScanRecords_SendStatus ON ScanRecords (SendStatus);
                CREATE UNIQUE INDEX IF NOT EXISTS IX_ScanRecords_EventId ON ScanRecords (EventId);
                -- Small idempotency receipts intentionally outlive detailed history.
                CREATE TABLE IF NOT EXISTS ScanPersistenceReceipts (
                    EventId TEXT NOT NULL PRIMARY KEY,
                    IdentityHash BLOB NOT NULL
                );
                CREATE TABLE IF NOT EXISTS ScanPoOutbox (
                    EventId TEXT NOT NULL PRIMARY KEY,
                    OrderNo TEXT NOT NULL,
                    OrderCreatedUtc TEXT NOT NULL,
                    CodeContent TEXT NOT NULL,
                    ScannedUtc TEXT NOT NULL,
                    AppliedUtc TEXT NULL
                );
                CREATE INDEX IF NOT EXISTS IX_ScanPoOutbox_Pending ON ScanPoOutbox (AppliedUtc);
                """;
            await indexCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await using var versionCmd = connection.CreateCommand();
        versionCmd.Transaction = transaction;
        versionCmd.CommandText = """
            INSERT OR IGNORE INTO SchemaVersion (Version, AppliedUtc)
            VALUES ($version, $appliedUtc);
            """;
        versionCmd.Parameters.AddWithValue("$version", CurrentSchemaVersion);
        versionCmd.Parameters.AddWithValue(
            "$appliedUtc", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        await versionCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        await transaction.CommitAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Đảm bảo một cột có trong bảng (thêm bằng ALTER TABLE nếu thiếu).
    /// </summary>
    private static async Task EnsureColumnAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string table,
        string column,
        string definition,
        CancellationToken ct)
    {
        var existing = await GetColumnNamesAsync(connection, transaction, table, ct).ConfigureAwait(false);
        if (existing.Contains(column))
        {
            return;
        }

        await using var alter = connection.CreateCommand();
        alter.Transaction = transaction;
        alter.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {definition};";
        await alter.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Migration v2: đổi kiểu cột <c>ProcessingTimeMs</c> từ INTEGER sang REAL.
    ///
    /// SQLite có kiểu động (dynamic typing) nên dữ liệu cũ vẫn đọc được bình
    /// thường, nhưng khai báo kiểu trong schema cần được cập nhật cho khớp với
    /// code C#. Bảng cũ được đổi tên tạm, tạo bảng mới đúng kiểu, rồi chép
    /// dữ liệu sang — cách này hoạt động trên mọi phiên bản SQLite và giữ nguyên
    /// dữ liệu đã ghi.
    ///
    /// Cột không tồn tại thì bỏ qua (sẽ được <see cref="EnsureColumnAsync"/> tạo).
    /// </summary>
    private static async Task MigrateProcessingTimeToRealAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken ct)
    {
        // Lấy kiểu khai báo hiện tại của cột.
        var columnType = await GetColumnTypeAsync(
            connection, transaction, "ScanRecords", "ProcessingTimeMs", ct).ConfigureAwait(false);

        if (columnType is null || columnType.Contains("REAL", StringComparison.OrdinalIgnoreCase))
        {
            // Chưa có cột, hoặc đã là REAL -> không cần migrate.
            return;
        }

        await using (var cmd = connection.CreateCommand())
        {
            cmd.Transaction = transaction;
            cmd.CommandText = """
                ALTER TABLE ScanRecords RENAME TO ScanRecords_Legacy;
                CREATE TABLE ScanRecords (
                    Id               INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                    ReceivedUtc      TEXT    NOT NULL,
                    Code             TEXT    NOT NULL DEFAULT '',
                    Gtin             TEXT    NOT NULL DEFAULT '',
                    Serial           TEXT    NOT NULL DEFAULT '',
                    Status           TEXT    NOT NULL DEFAULT 'Fail',
                    ProcessingTimeMs REAL    NOT NULL DEFAULT 0,
                    PlcResult        TEXT    NOT NULL DEFAULT 'Skipped',
                    BatchNo          TEXT    NOT NULL DEFAULT 'NoBatch',
                    Raw              TEXT    NOT NULL DEFAULT '',
                    OrderNo          TEXT    NOT NULL DEFAULT '',
                    SendStatus       TEXT    NOT NULL DEFAULT '',
                    SendStatusCode   INTEGER NOT NULL DEFAULT 0,
                    SendMessage      TEXT    NOT NULL DEFAULT '',
                    SendAttempts     INTEGER NOT NULL DEFAULT 0,
                    SendTimeUtc      TEXT    NULL,
                    SendAttemptUtc   TEXT    NULL
                );
                INSERT INTO ScanRecords
                    (Id, ReceivedUtc, Code, Gtin, Serial, Status,
                     ProcessingTimeMs, PlcResult, BatchNo, Raw, OrderNo,
                     SendStatus, SendStatusCode, SendMessage, SendAttempts, SendTimeUtc, SendAttemptUtc)
                SELECT Id, ReceivedUtc, Code, Gtin, Serial, Status,
                       ProcessingTimeMs, PlcResult, BatchNo, Raw, OrderNo,
                       SendStatus, SendStatusCode, SendMessage, SendAttempts, SendTimeUtc, SendAttemptUtc
                FROM ScanRecords_Legacy;
                DROP TABLE ScanRecords_Legacy;
                """;

            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        // Tạo lại index (DROP TABLE đã xoá kèm index).
        await using (var indexCmd = connection.CreateCommand())
        {
            indexCmd.Transaction = transaction;
            indexCmd.CommandText = """
                CREATE INDEX IF NOT EXISTS IX_ScanRecords_ReceivedUtc ON ScanRecords (ReceivedUtc DESC);
                CREATE INDEX IF NOT EXISTS IX_ScanRecords_Status      ON ScanRecords (Status);
                CREATE INDEX IF NOT EXISTS IX_ScanRecords_BatchNo     ON ScanRecords (BatchNo);
                CREATE INDEX IF NOT EXISTS IX_ScanRecords_OrderNo     ON ScanRecords (OrderNo);
                """;

            await indexCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Lấy kiểu khai báo của một cột (ví dụ <c>INTEGER</c>, <c>REAL</c>).
    /// </summary>
    /// <param name="connection">Connection đã mở.</param>
    /// <param name="transaction">Transaction giữ khóa schema trong lúc đọc metadata.</param>
    /// <param name="table">Tên bảng.</param>
    /// <param name="column">Tên cột.</param>
    /// <param name="ct">Token hủy.</param>
    /// <returns>Kiểu của cột, hoặc null nếu cột không tồn tại.</returns>
    private static async Task<string?> GetColumnTypeAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string table,
        string column,
        CancellationToken ct)
    {
        await using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = $"PRAGMA table_info({table});";

        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            // PRAGMA table_info: cid | name | type | notnull | dflt_value | pk
            if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase))
            {
                return reader.GetString(2);
            }
        }

        return null;
    }

    /// <summary>
    /// Lấy danh sách tên cột của một bảng (dùng cho migration).
    /// </summary>
    private static async Task<HashSet<string>> GetColumnNamesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string table,
        CancellationToken ct)
    {
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        await using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = $"PRAGMA table_info({table});";

        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            // PRAGMA table_info: cid | name | type | notnull | dflt_value | pk
            columns.Add(reader.GetString(1));
        }

        return columns;
    }

    /// <summary>
    /// Chạy một câu lệnh PRAGMA.
    /// </summary>
    private static async Task ExecutePragmaAsync(
        SqliteConnection connection,
        string sql,
        CancellationToken ct)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Ghi một loạt bản ghi trong **một transaction** (nhanh hơn nhiều lần
    /// insert rời rạc).
    /// </summary>
    /// <param name="connection">Connection đã mở.</param>
    /// <param name="records">Danh sách bản ghi.</param>
    /// <param name="ct">Token hủy.</param>
    public async Task InsertBatchAsync(
        SqliteConnection connection,
        IReadOnlyList<ScanRecordEntity> records,
        CancellationToken ct)
    {
        if (records.Count == 0)
        {
            return;
        }

        await using var transaction = (SqliteTransaction)await connection
            .BeginTransactionAsync(ct).ConfigureAwait(false);

        await using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = """
            INSERT INTO ScanRecords
                (ReceivedUtc, Code, Gtin, Serial, Status, ProcessingTimeMs, PlcAckTimeMs, PlcResult, BatchNo, Raw, OrderNo)
            VALUES
                ($receivedUtc, $code, $gtin, $serial, $status, $processingTimeMs, $plcAckTimeMs, $plcResult, $batchNo, $raw, $orderNo);
            """;

        var pReceived = cmd.Parameters.Add("$receivedUtc", SqliteType.Text);
        var pCode = cmd.Parameters.Add("$code", SqliteType.Text);
        var pGtin = cmd.Parameters.Add("$gtin", SqliteType.Text);
        var pSerial = cmd.Parameters.Add("$serial", SqliteType.Text);
        var pStatus = cmd.Parameters.Add("$status", SqliteType.Text);
        var pProcessing = cmd.Parameters.Add("$processingTimeMs", SqliteType.Real);
        var pPlcAck = cmd.Parameters.Add("$plcAckTimeMs", SqliteType.Real);
        var pPlc = cmd.Parameters.Add("$plcResult", SqliteType.Text);
        var pBatch = cmd.Parameters.Add("$batchNo", SqliteType.Text);
        var pRaw = cmd.Parameters.Add("$raw", SqliteType.Text);
        var pOrder = cmd.Parameters.Add("$orderNo", SqliteType.Text);

        foreach (var r in records)
        {
            pReceived.Value = FormatUtc(r.ReceivedUtc);
            pCode.Value = r.CodeContent;
            pGtin.Value = r.Gtin;
            pSerial.Value = r.Serial;
            pStatus.Value = r.Status;
            pProcessing.Value = r.ProcessingTimeMs;
            pPlcAck.Value = r.PlcAckTimeMs;
            pPlc.Value = r.PlcResult;
            pBatch.Value = string.IsNullOrWhiteSpace(r.BatchNo)
                ? ScanBatchDefaults.NoBatch
                : r.BatchNo;
            pRaw.Value = r.Raw;
            pOrder.Value = r.OrderNo ?? string.Empty;

            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await transaction.CommitAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Xoá các bản ghi cũ hơn thời hạn retention.
    /// </summary>
    /// <param name="cutoff">Mốc thời gian (UTC) — xoá bản ghi cũ hơn mốc này.</param>
    /// <param name="ct">Token hủy.</param>
    /// <returns>Số bản ghi đã xoá.</returns>
    public async Task<int> PruneAsync(DateTime cutoff, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
        // Preserve an EventId receipt before deleting detail. This also upgrades
        // rows/outbox from the first v6 implementation without resurrecting them
        // when an old batch retries after retention.
        var receipts = new List<(string EventId, byte[] Hash)>();
        await using (var candidates = connection.CreateCommand())
        {
            candidates.Transaction = transaction;
            candidates.CommandText = """
                SELECT EventId, OrderNo, OrderCreatedUtc, Code, ReceivedUtc, PoAccepted
                FROM ScanRecords WHERE ReceivedUtc < $cutoff AND PersistenceReady = 1 AND EventId IS NOT NULL
                UNION ALL
                SELECT o.EventId, o.OrderNo, o.OrderCreatedUtc, o.CodeContent, o.ScannedUtc, 1
                FROM ScanPoOutbox o WHERE o.AppliedUtc IS NOT NULL
                    AND NOT EXISTS (SELECT 1 FROM ScanRecords r WHERE r.EventId = o.EventId);
                """;
            candidates.Parameters.AddWithValue("$cutoff", FormatUtc(cutoff));
            await using var reader = await candidates.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
                receipts.Add((reader.GetString(0), ScanPersistenceIdentity.Hash(reader.GetString(1),
                    reader.IsDBNull(2) ? null : reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetInt32(5) != 0)));
        }
        await using (var receipt = connection.CreateCommand())
        {
            receipt.Transaction = transaction;
            receipt.CommandText = "INSERT INTO ScanPersistenceReceipts (EventId, IdentityHash) VALUES ($event, $hash) ON CONFLICT(EventId) DO NOTHING;";
            var eventParameter = receipt.Parameters.Add("$event", SqliteType.Text);
            var hashParameter = receipt.Parameters.Add("$hash", SqliteType.Blob);
            foreach (var item in receipts)
            {
                eventParameter.Value = item.EventId; hashParameter.Value = item.Hash;
                await receipt.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
        }
        await using (var pruneOutbox = connection.CreateCommand())
        {
            pruneOutbox.Transaction = transaction;
            pruneOutbox.CommandText = """
                DELETE FROM ScanPoOutbox WHERE AppliedUtc IS NOT NULL AND
                    (NOT EXISTS (SELECT 1 FROM ScanRecords r WHERE r.EventId = ScanPoOutbox.EventId)
                     OR EventId IN (SELECT EventId FROM ScanRecords WHERE ReceivedUtc < $cutoff AND PersistenceReady = 1));
                """;
            pruneOutbox.Parameters.AddWithValue("$cutoff", FormatUtc(cutoff));
            await pruneOutbox.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        await using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        // An unapplied PO intent must retain its history even if retention is
        // reached while the PO database is unavailable.
        cmd.CommandText = "DELETE FROM ScanRecords WHERE ReceivedUtc < $cutoff AND PersistenceReady = 1;";
        cmd.Parameters.AddWithValue("$cutoff", FormatUtc(cutoff));

        var removed = await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        await transaction.CommitAsync(ct).ConfigureAwait(false);
        return removed;
    }

    /// <summary>
    /// Truy vấn bản ghi theo bộ lọc, có phân trang.
    /// </summary>
    /// <param name="filter">Bộ lọc (null = không lọc).</param>
    /// <param name="page">Số trang, bắt đầu từ 1.</param>
    /// <param name="pageSize">Số bản ghi mỗi trang.</param>
    /// <param name="ct">Token hủy.</param>
    /// <returns>Kết quả có phân trang.</returns>
    public async Task<PagedScanResult<ScanRecordEntity>> QueryAsync(
        ScanRecordFilter? filter,
        int page,
        int pageSize,
        CancellationToken ct)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 5000);

        var where = "WHERE 1=1";
        var parameters = new List<(string Name, object Value)>();

        if (filter is not null)
        {
            if (filter.FromUtc.HasValue)
            {
                where += " AND ReceivedUtc >= $from";
                parameters.Add(("$from", FormatUtc(filter.FromUtc.Value)));
            }

            if (filter.ToUtc.HasValue)
            {
                where += " AND ReceivedUtc <= $to";
                parameters.Add(("$to", FormatUtc(filter.ToUtc.Value)));
            }

            if (filter.Status.HasValue)
            {
                if (filter.Status.Value == ScanStatus.Fail)
                {
                    // Fail là bộ lọc tổng hợp các mã bị loại, gồm dữ liệu cũ.
                    where += " AND Status IN ($status, $duplicate, $invalidForOrder, $noRead, $invalidStructure)";
                    parameters.Add(("$status", ScanStatuses.Fail));
                    parameters.Add(("$duplicate", ScanStatuses.Duplicate));
                    parameters.Add(("$invalidForOrder", ScanStatuses.InvalidForOrder));
                    parameters.Add(("$noRead", ScanStatuses.NoRead));
                    parameters.Add(("$invalidStructure", ScanStatuses.InvalidStructure));
                }
                else
                {
                    where += " AND Status = $status";
                    parameters.Add(("$status", ScanStatuses.ToStorage(filter.Status.Value)));
                }
            }

            if (filter.CodeContentEquals is not null)
            {
                // Raw contains the full content for historical rows whose Code held only Serial.
                where += " AND (CASE WHEN Raw <> '' THEN Raw ELSE Code END) = $codeContent COLLATE BINARY";
                parameters.Add(("$codeContent", filter.CodeContentEquals));
            }

            if (!string.IsNullOrWhiteSpace(filter.CodeContains))
            {
                where += " AND (Code LIKE $code OR Raw LIKE $code)";
                parameters.Add(("$code", $"%{filter.CodeContains}%"));
            }

            if (!string.IsNullOrWhiteSpace(filter.BatchNo))
            {
                where += " AND BatchNo = $batchNo";
                parameters.Add(("$batchNo", filter.BatchNo));
            }
        }

        await using var connection = await OpenAsync(ct).ConfigureAwait(false);

        long total;
        await using (var countCmd = connection.CreateCommand())
        {
            countCmd.CommandText = $"SELECT COUNT(*) FROM ScanRecords {where};";
            AddParameters(countCmd, parameters);
            total = Convert.ToInt64(
                await countCmd.ExecuteScalarAsync(ct).ConfigureAwait(false),
                CultureInfo.InvariantCulture);
        }

        var items = new List<ScanRecordEntity>();

        await using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = $"""
                SELECT Id, ReceivedUtc, Code, Gtin, Serial, Status,
                       ProcessingTimeMs, PlcResult, BatchNo, Raw, OrderNo,
                       SendStatus, SendStatusCode, SendMessage, SendAttempts,
                       SendTimeUtc, SendAttemptUtc, PlcAckTimeMs
                FROM ScanRecords
                {where}
                ORDER BY ReceivedUtc DESC, Id DESC
                LIMIT $limit OFFSET $offset;
                """;
            AddParameters(cmd, parameters);
            cmd.Parameters.AddWithValue("$limit", pageSize);
            cmd.Parameters.AddWithValue("$offset", (page - 1) * pageSize);

            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                items.Add(MapRecord(reader));
            }
        }

        return new PagedScanResult<ScanRecordEntity>
        {
            Items = items,
            TotalCount = total,
            Page = page,
            PageSize = pageSize,
        };
    }

    /// <summary>
    /// Đếm số bản ghi theo từng trạng thái.
    /// </summary>
    /// <param name="ct">Token hủy.</param>
    /// <returns>Dictionary trạng thái → số lượng.</returns>
    public async Task<Dictionary<string, long>> CountByStatusAsync(CancellationToken ct)
    {
        var counts = new Dictionary<string, long>(StringComparer.Ordinal);

        await using var connection = await OpenAsync(ct).ConfigureAwait(false);

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT Status, COUNT(*) FROM ScanRecords GROUP BY Status;";

        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            counts[reader.GetString(0)] = Convert.ToInt64(
                reader.GetValue(1), CultureInfo.InvariantCulture);
        }

        return counts;
    }

    /// <summary>
    /// Lấy tập nội dung mã đầy đủ đã xuất hiện trong lịch sử với kết quả Pass.
    ///
    /// <para>
    /// Dùng cho cơ chế chống trùng lịch sử toàn cục: trước khi gửi mã lên
    /// server, mỗi mã của PO đang chạy được kiểm tra xem đã từng được quét
    /// trong hệ thống hay chưa. Bản ghi thuộc chính PO đang chạy được bỏ qua
    /// (mã của PO đó hợp lệ).
    /// </para>
    /// </summary>
    /// <param name="orderNo">Mã PO hiện tại (bỏ qua bản ghi của PO này).</param>
    /// <param name="ct">Token hủy.</param>
    /// <returns>Danh sách mã nguyên văn; không cắt, trim hay so sánh không phân biệt hoa thường.</returns>
    public async Task<IReadOnlyList<string>> GetSeenCodeContentsAsync(
        string orderNo,
        CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct).ConfigureAwait(false);

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT DISTINCT CASE WHEN Raw <> '' THEN Raw ELSE Code END AS CodeContent
            FROM ScanRecords
            WHERE Status = $pass
              AND (Raw <> '' OR Code <> '')
              AND (OrderNo <> $orderNo OR OrderNo = '');
            """;
        cmd.Parameters.AddWithValue("$pass", ScanStatuses.Pass);
        cmd.Parameters.AddWithValue("$orderNo", orderNo ?? string.Empty);

        var list = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            list.Add(reader.GetString(0));
        }

        return list;
    }

    /// <summary>Tương thích tên cũ; không còn trả serial đã tách.</summary>
    [Obsolete("Use GetSeenCodeContentsAsync; production identity is the complete code.")]
    public Task<IReadOnlyList<string>> GetSeenSerialsAsync(
        string orderNo,
        CancellationToken ct = default) =>
        GetSeenCodeContentsAsync(orderNo, ct);

    /// <summary>
    /// Chuyển dòng dữ liệu thành <see cref="ScanRecordEntity"/>.
    /// </summary>
    private static ScanRecordEntity MapRecord(IDataRecord record) => new()
    {
        Id = record.GetInt64(0),
        ReceivedUtc = ParseUtc(record.GetString(1)),
        // Older versions stored only Serial in Code, while Raw retained the full camera payload.
        CodeContent = record.GetString(9) is { Length: > 0 } raw ? raw : record.GetString(2),
        Gtin = record.GetString(3),
        Serial = record.GetString(4),
        Status = record.GetString(5),
        ProcessingTimeMs = record.GetDouble(6),
        PlcResult = record.GetString(7),
        BatchNo = record.GetString(8),
        Raw = record.GetString(9),
        OrderNo = record.IsDBNull(10) ? string.Empty : record.GetString(10),
        SendStatus = record.IsDBNull(11) ? string.Empty : record.GetString(11),
        SendStatusCode = record.IsDBNull(12) ? 0 : record.GetInt32(12),
        SendMessage = record.IsDBNull(13) ? string.Empty : record.GetString(13),
        SendAttempts = record.IsDBNull(14) ? 0 : record.GetInt32(14),
        SendTimeUtc = record.IsDBNull(15) ? null : ParseUtc(record.GetString(15)),
        SendAttemptUtc = record.IsDBNull(16) ? null : ParseUtc(record.GetString(16)),
        PlcAckTimeMs = record.GetDouble(17),
    };

    /// <summary>
    /// Thêm tham số vào command.
    /// </summary>
    private static void AddParameters(SqliteCommand cmd, List<(string Name, object Value)> parameters)
    {
        foreach (var (name, value) in parameters)
        {
            cmd.Parameters.AddWithValue(name, value);
        }
    }

    /// <summary>
    /// Parse chuỗi thời gian UTC theo format "O".
    /// </summary>
    private static DateTime ParseUtc(string value) =>
        DateTime.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind,
            out var parsed)
            ? parsed
            : DateTime.MinValue;

    /// <summary>
    /// Định dạng thời gian UTC theo format "O" (dùng chung cho database).
    /// </summary>
    /// <param name="value">Thời gian cần định dạng.</param>
    /// <returns>Chuỗi đã định dạng.</returns>
    public static string FormatUtc(DateTime value) =>
        value.ToString("O", CultureInfo.InvariantCulture);
}
