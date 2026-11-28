using Microsoft.Data.Sqlite;

namespace ClassLibraryMT.Po;

/// <summary>
/// Lưu lịch sử các lần chạy Production Order vào một DB SQLite riêng.
///
/// <para>
/// DB nằm ở <c>{DbRoot}/po-history.db</c> — cùng thư mục gốc với các thư mục
/// tháng chứa file DB của từng PO, nhưng là file riêng biệt (không phải thư
/// mục) để không bị quét nhầm khi duyệt PO.
/// </para>
///
/// <para>
/// Bảng <c>PoRuns</c> lưu 1 dòng cho mỗi lần chạy PO:
/// <list type="bullet">
/// <item><c>StartedUtc</c> — lúc bắt đầu sản xuất.</item>
/// <item><c>CompletedUtc</c> — lúc chạy đủ 100% (tự động hoàn tất).</item>
/// <item><c>CancelledUtc</c> — lúc người dùng bấm "Huỷ PO".</item>
/// </list>
/// </para>
///
/// <para>
/// Lịch sử này là nguồn để khôi phục PO đang chạy khi mở máy: nếu còn dòng
/// nào ở trạng thái <c>Running</c> thì app tiếp tục PO đó.
/// </para>
/// </summary>
public sealed class PoHistoryStore
{
    /// <summary>Phiên bản schema hiện tại.</summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>Tên file DB lịch sử (đặt trong thư mục gốc của PO).</summary>
    public const string DbFileName = "po-history.db";

    private readonly string _dbPath;
    private readonly Lock _gate = new();

    /// <summary>
    /// Khởi tạo store.
    /// </summary>
    /// <param name="dbRoot">Thư mục gốc lưu file PO (đường dẫn tuyệt đối).</param>
    public PoHistoryStore(string dbRoot)
    {
        if (string.IsNullOrWhiteSpace(dbRoot))
            throw new ArgumentException("DbRoot không được để trống.", nameof(dbRoot));

        _dbPath = Path.Combine(dbRoot, DbFileName);
    }

    /// <summary>Đường dẫn file DB lịch sử.</summary>
    public string DbPath => _dbPath;

    /// <summary>
    /// Ghi 1 lần chạy PO mới (khi bắt đầu sản xuất).
    ///
    /// <para>
    /// Nếu đang có một lần chạy <c>Running</c> của cùng PO thì đóng lại lần
    /// đó (đánh dấu huỷ) trước khi ghi lần mới — tránh tồn tại 2 dòng
    /// <c>Running</c> cùng lúc làm hỏng logic auto-resume.
    /// </para>
    /// </summary>
    public async Task StartRunAsync(
        string orderNo,
        int orderQty,
        DateTime createdUtc,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(orderNo))
            return;

        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        await using var tx = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);

        // Đóng lần chạy đang mở (nếu có) của chính PO này.
        await using (var close = connection.CreateCommand())
        {
            close.Transaction = (SqliteTransaction)tx;
            close.CommandText = """
                UPDATE PoRuns
                SET Status = 'Cancelled', CancelledUtc = $now
                WHERE OrderNo = $orderNo AND Status = 'Running';
                """;
            close.Parameters.AddWithValue("$orderNo", orderNo);
            close.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
            await close.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await using (var insert = connection.CreateCommand())
        {
            insert.Transaction = (SqliteTransaction)tx;
            insert.CommandText = """
                INSERT INTO PoRuns
                    (OrderNo, OrderQty, CreatedUtc, StartedUtc, CompletedUtc, CancelledUtc, Status)
                VALUES
                    ($orderNo, $orderQty, $createdUtc, $startedUtc, NULL, NULL, 'Running');
                """;
            insert.Parameters.AddWithValue("$orderNo", orderNo);
            insert.Parameters.AddWithValue("$orderQty", orderQty);
            insert.Parameters.AddWithValue("$createdUtc", createdUtc.ToString("O"));
            insert.Parameters.AddWithValue("$startedUtc", DateTime.UtcNow.ToString("O"));
            await insert.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await tx.CommitAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Đóng lần chạy đang mở của một PO với trạng thái <c>Completed</c>.
    /// </summary>
    public async Task CompleteRunAsync(string orderNo, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(orderNo))
            return;

        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            UPDATE PoRuns
            SET Status = 'Completed', CompletedUtc = $now
            WHERE OrderNo = $orderNo AND Status = 'Running';
            """;
        cmd.Parameters.AddWithValue("$orderNo", orderNo);
        cmd.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Đóng lần chạy đang mở của một PO với trạng thái <c>Cancelled</c>.
    /// </summary>
    public async Task CancelRunAsync(string orderNo, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(orderNo))
            return;

        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            UPDATE PoRuns
            SET Status = 'Cancelled', CancelledUtc = $now
            WHERE OrderNo = $orderNo AND Status = 'Running';
            """;
        cmd.Parameters.AddWithValue("$orderNo", orderNo);
        cmd.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Lấy lần chạy đang ở trạng thái <c>Running</c> (mới nhất) — dùng cho
    /// auto-resume khi mở máy.
    /// </summary>
    /// <returns>Lần chạy đang mở, hoặc <c>null</c> nếu không có.</returns>
    public async Task<PoRunRecord?> GetRunningRunAsync(CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT OrderNo, OrderQty, CreatedUtc, StartedUtc
            FROM PoRuns
            WHERE Status = 'Running'
            ORDER BY StartedUtc DESC
            LIMIT 1;
            """;

        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
            return null;

        return new PoRunRecord
        {
            OrderNo = reader.GetString(0),
            OrderQty = reader.GetInt32(1),
            CreatedUtc = ParseUtc(reader.GetString(2)),
            StartedUtc = ParseUtc(reader.GetString(3)),
        };
    }

    /// <summary>
    /// Lấy toàn bộ lịch sử chạy PO (mới nhất trước) để hiển thị.
    /// </summary>
    public async Task<IReadOnlyList<PoRunRecord>> GetRunsAsync(CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT OrderNo, OrderQty, CreatedUtc, StartedUtc, CompletedUtc, CancelledUtc, Status
            FROM PoRuns
            ORDER BY StartedUtc DESC;
            """;

        var list = new List<PoRunRecord>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            list.Add(new PoRunRecord
            {
                OrderNo = reader.GetString(0),
                OrderQty = reader.GetInt32(1),
                CreatedUtc = ParseUtc(reader.GetString(2)),
                StartedUtc = ParseUtc(reader.GetString(3)),
                CompletedUtc = reader.IsDBNull(4) ? null : ParseUtc(reader.GetString(4)),
                CancelledUtc = reader.IsDBNull(5) ? null : ParseUtc(reader.GetString(5)),
                Status = reader.GetString(6),
            });
        }

        return list;
    }

    /// <summary>
    /// Mở connection, tạo schema nếu chưa có.
    /// </summary>
    private async Task<SqliteConnection> OpenAsync(CancellationToken ct)
    {
        var dir = Path.GetDirectoryName(_dbPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        var connection = new SqliteConnection($"Data Source={_dbPath}");
        await connection.OpenAsync(ct).ConfigureAwait(false);

        await using (var pragma = connection.CreateCommand())
        {
            pragma.CommandText = """
                PRAGMA journal_mode=WAL;
                PRAGMA busy_timeout=5000;
                PRAGMA synchronous=NORMAL;
                """;
            await pragma.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await using (var schema = connection.CreateCommand())
        {
            schema.CommandText = """
                CREATE TABLE IF NOT EXISTS PoRuns (
                    RunId        INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                    OrderNo      TEXT NOT NULL,
                    OrderQty     INTEGER NOT NULL DEFAULT 0,
                    CreatedUtc   TEXT NOT NULL,
                    StartedUtc   TEXT NOT NULL,
                    CompletedUtc TEXT NULL,
                    CancelledUtc TEXT NULL,
                    Status       TEXT NOT NULL DEFAULT 'Running'
                );

                CREATE INDEX IF NOT EXISTS IX_PoRuns_OrderNo ON PoRuns (OrderNo);
                CREATE INDEX IF NOT EXISTS IX_PoRuns_Status  ON PoRuns (Status);
                """;
            await schema.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        return connection;
    }

    /// <summary>Parse chuỗi thời gian UTC theo format "O".</summary>
    private static DateTime ParseUtc(string value) =>
        DateTime.TryParse(
            value,
            null,
            System.Globalization.DateTimeStyles.RoundtripKind,
            out var parsed)
            ? parsed
            : DateTime.MinValue;
}

/// <summary>
/// Một dòng trong lịch sử chạy PO.
/// </summary>
public sealed class PoRunRecord
{
    /// <summary>Mã lệnh sản xuất.</summary>
    public string OrderNo { get; set; } = string.Empty;

    /// <summary>Tổng số mã của PO khi bắt đầu.</summary>
    public int OrderQty { get; set; }

    /// <summary>Thời điểm tạo PO (từ server).</summary>
    public DateTime CreatedUtc { get; set; }

    /// <summary>Thời điểm bắt đầu sản xuất.</summary>
    public DateTime StartedUtc { get; set; }

    /// <summary>Thời điểm hoàn tất (null nếu chưa).</summary>
    public DateTime? CompletedUtc { get; set; }

    /// <summary>Thời điểm huỷ (null nếu chưa).</summary>
    public DateTime? CancelledUtc { get; set; }

    /// <summary>Trạng thái: Running / Completed / Cancelled.</summary>
    public string Status { get; set; } = "Running";
}
