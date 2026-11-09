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
    public const int CurrentSchemaVersion = 3;

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
        await connection.OpenAsync(ct).ConfigureAwait(false);

        await ExecutePragmaAsync(connection, "PRAGMA journal_mode=WAL;", ct).ConfigureAwait(false);
        await ExecutePragmaAsync(connection, "PRAGMA busy_timeout=5000;", ct).ConfigureAwait(false);
        await ExecutePragmaAsync(connection, "PRAGMA synchronous=NORMAL;", ct).ConfigureAwait(false);
        await ExecutePragmaAsync(connection, "PRAGMA foreign_keys=ON;", ct).ConfigureAwait(false);

        await EnsureSchemaAsync(connection, ct).ConfigureAwait(false);

        return connection;
    }

    /// <summary>
    /// Tạo bảng/index nếu chưa có và chạy migration cho các phiên bản cũ.
    /// </summary>
    private static async Task EnsureSchemaAsync(SqliteConnection connection, CancellationToken ct)
    {
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
                OrderNo          TEXT    NOT NULL DEFAULT ''
            );

            CREATE INDEX IF NOT EXISTS IX_ScanRecords_ReceivedUtc ON ScanRecords (ReceivedUtc DESC);
            CREATE INDEX IF NOT EXISTS IX_ScanRecords_Status      ON ScanRecords (Status);
            CREATE INDEX IF NOT EXISTS IX_ScanRecords_BatchNo     ON ScanRecords (BatchNo);
            """;

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = schema;
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

        // Migration: bảng đã tồn tại từ phiên bản cũ có thể thiếu cột.
        await EnsureColumnAsync(connection, "ScanRecords", "Gtin", "TEXT NOT NULL DEFAULT ''", ct)
            .ConfigureAwait(false);
        await EnsureColumnAsync(connection, "ScanRecords", "Serial", "TEXT NOT NULL DEFAULT ''", ct)
            .ConfigureAwait(false);
        await EnsureColumnAsync(connection, "ScanRecords", "ProcessingTimeMs", "REAL NOT NULL DEFAULT 0", ct)
            .ConfigureAwait(false);
        await EnsureColumnAsync(connection, "ScanRecords", "PlcResult", "TEXT NOT NULL DEFAULT 'Skipped'", ct)
            .ConfigureAwait(false);
        await EnsureColumnAsync(connection, "ScanRecords", "BatchNo", "TEXT NOT NULL DEFAULT 'NoBatch'", ct)
            .ConfigureAwait(false);
        await EnsureColumnAsync(connection, "ScanRecords", "Raw", "TEXT NOT NULL DEFAULT ''", ct)
            .ConfigureAwait(false);

        // Migration v3: cột OrderNo — biết mã quét thuộc PO nào (dùng cho cơ
        // chế chống trùng lịch sử toàn cục và để truy vết báo cáo).
        await EnsureColumnAsync(connection, "ScanRecords", "OrderNo", "TEXT NOT NULL DEFAULT ''", ct)
            .ConfigureAwait(false);

        // Migration v2: cột ProcessingTimeMs đổi từ INTEGER sang REAL để giữ
        // được phần thập phân (ví dụ 0.333 ms). SQLite là kiểu động nên dữ
        // liệu cũ vẫn đọc được, nhưng khai báo kiểu trong schema cho đúng.
        await MigrateProcessingTimeToRealAsync(connection, ct).ConfigureAwait(false);

        // Tạo index SAU khi migration xong — index IX_ScanRecords_OrderNo tham
        // chiếu cột OrderNo, tạo trước khi cột tồn tại sẽ báo lỗi SQLite
        // ("no such column") với các DB đã tồn tại từ phiên bản cũ.
        await using (var indexCmd = connection.CreateCommand())
        {
            indexCmd.CommandText =
                "CREATE INDEX IF NOT EXISTS IX_ScanRecords_OrderNo ON ScanRecords (OrderNo);";
            await indexCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await using var versionCmd = connection.CreateCommand();
        versionCmd.CommandText = """
            INSERT OR IGNORE INTO SchemaVersion (Version, AppliedUtc)
            VALUES ($version, $appliedUtc);
            """;
        versionCmd.Parameters.AddWithValue("$version", CurrentSchemaVersion);
        versionCmd.Parameters.AddWithValue(
            "$appliedUtc", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        await versionCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Đảm bảo một cột có trong bảng (thêm bằng ALTER TABLE nếu thiếu).
    /// </summary>
    private static async Task EnsureColumnAsync(
        SqliteConnection connection,
        string table,
        string column,
        string definition,
        CancellationToken ct)
    {
        var existing = await GetColumnNamesAsync(connection, table, ct).ConfigureAwait(false);
        if (existing.Contains(column))
        {
            return;
        }

        await using var alter = connection.CreateCommand();
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
        CancellationToken ct)
    {
        // Lấy kiểu khai báo hiện tại của cột.
        var columnType = await GetColumnTypeAsync(
            connection, "ScanRecords", "ProcessingTimeMs", ct).ConfigureAwait(false);

        if (columnType is null || columnType.Contains("REAL", StringComparison.OrdinalIgnoreCase))
        {
            // Chưa có cột, hoặc đã là REAL -> không cần migrate.
            return;
        }

        await using var transaction = (SqliteTransaction)await connection
            .BeginTransactionAsync(ct).ConfigureAwait(false);

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
                    OrderNo          TEXT    NOT NULL DEFAULT ''
                );
                INSERT INTO ScanRecords
                    (Id, ReceivedUtc, Code, Gtin, Serial, Status,
                     ProcessingTimeMs, PlcResult, BatchNo, Raw, OrderNo)
                SELECT Id, ReceivedUtc, Code, Gtin, Serial, Status,
                       ProcessingTimeMs, PlcResult, BatchNo, Raw, OrderNo
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

        await transaction.CommitAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Lấy kiểu khai báo của một cột (ví dụ <c>INTEGER</c>, <c>REAL</c>).
    /// </summary>
    /// <param name="connection">Connection đã mở.</param>
    /// <param name="table">Tên bảng.</param>
    /// <param name="column">Tên cột.</param>
    /// <param name="ct">Token hủy.</param>
    /// <returns>Kiểu của cột, hoặc null nếu cột không tồn tại.</returns>
    private static async Task<string?> GetColumnTypeAsync(
        SqliteConnection connection,
        string table,
        string column,
        CancellationToken ct)
    {
        await using var cmd = connection.CreateCommand();
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
        string table,
        CancellationToken ct)
    {
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        await using var cmd = connection.CreateCommand();
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
                (ReceivedUtc, Code, Gtin, Serial, Status, ProcessingTimeMs, PlcResult, BatchNo, Raw, OrderNo)
            VALUES
                ($receivedUtc, $code, $gtin, $serial, $status, $processingTimeMs, $plcResult, $batchNo, $raw, $orderNo);
            """;

        var pReceived = cmd.Parameters.Add("$receivedUtc", SqliteType.Text);
        var pCode = cmd.Parameters.Add("$code", SqliteType.Text);
        var pGtin = cmd.Parameters.Add("$gtin", SqliteType.Text);
        var pSerial = cmd.Parameters.Add("$serial", SqliteType.Text);
        var pStatus = cmd.Parameters.Add("$status", SqliteType.Text);
        var pProcessing = cmd.Parameters.Add("$processingTimeMs", SqliteType.Real);
        var pPlc = cmd.Parameters.Add("$plcResult", SqliteType.Text);
        var pBatch = cmd.Parameters.Add("$batchNo", SqliteType.Text);
        var pRaw = cmd.Parameters.Add("$raw", SqliteType.Text);
        var pOrder = cmd.Parameters.Add("$orderNo", SqliteType.Text);

        foreach (var r in records)
        {
            pReceived.Value = FormatUtc(r.ReceivedUtc);
            pCode.Value = r.Code;
            pGtin.Value = r.Gtin;
            pSerial.Value = r.Serial;
            pStatus.Value = r.Status;
            pProcessing.Value = r.ProcessingTimeMs;
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

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "DELETE FROM ScanRecords WHERE ReceivedUtc < $cutoff;";
        cmd.Parameters.AddWithValue("$cutoff", FormatUtc(cutoff));

        return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
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
                where += " AND Status = $status";
                parameters.Add(("$status", ScanStatuses.ToStorage(filter.Status.Value)));
            }

            if (!string.IsNullOrWhiteSpace(filter.CodeContains))
            {
                where += " AND (Code LIKE $code OR Serial LIKE $code OR Raw LIKE $code)";
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
                       ProcessingTimeMs, PlcResult, BatchNo, Raw, OrderNo
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
    /// Lấy tập serial đã từng xuất hiện trong lịch sử quét với kết quả Pass.
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
    /// <returns>Danh sách serial đã từng quét (không trùng lặp).</returns>
    public async Task<IReadOnlyList<string>> GetSeenSerialsAsync(
        string orderNo,
        CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct).ConfigureAwait(false);

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT DISTINCT Serial FROM ScanRecords
            WHERE Status = $pass
              AND Serial <> ''
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

    /// <summary>
    /// Chuyển dòng dữ liệu thành <see cref="ScanRecordEntity"/>.
    /// </summary>
    private static ScanRecordEntity MapRecord(IDataRecord record) => new()
    {
        Id = record.GetInt64(0),
        ReceivedUtc = ParseUtc(record.GetString(1)),
        Code = record.GetString(2),
        Gtin = record.GetString(3),
        Serial = record.GetString(4),
        Status = record.GetString(5),
        ProcessingTimeMs = record.GetDouble(6),
        PlcResult = record.GetString(7),
        BatchNo = record.GetString(8),
        Raw = record.GetString(9),
        OrderNo = record.IsDBNull(10) ? string.Empty : record.GetString(10),
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
