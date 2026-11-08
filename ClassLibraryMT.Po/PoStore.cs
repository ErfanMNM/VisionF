using Microsoft.Data.Sqlite;

namespace ClassLibraryMT.Po;

/// <summary>
/// Quản lý các file database riêng cho từng lệnh sản xuất.
///
/// <para>
/// Cấu trúc thư mục: <c>{DbRoot}/{yyyy-MM}/{OrderNo}.db</c> — mỗi PO một
/// file SQLite riêng, nhờ vậy việc kiểm tra trùng chỉ diễn ra trong phạm vi
/// 1 PO và không ảnh hưởng tới lịch sử quét tổng (<c>logs/scans.db</c>).
/// </para>
///
/// <para>
/// Mỗi file PO chứa 2 bảng:
/// <list type="bullet">
/// <item><c>PoCodes</c> — tập mã của PO kèm trạng thái quét / kích hoạt.</item>
/// <item><c>PoMeta</c> — các cặp key/value lưu trạng thái PO.</item>
/// </list>
/// </para>
/// </summary>
public sealed class PoStore
{
    /// <summary>Phiên bản schema hiện tại của file PO.</summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>Key lưu mã lệnh sản xuất trong bảng meta.</summary>
    public const string MetaOrderNo = "orderNo";

    /// <summary>Key lưu trạng thái PO trong bảng meta.</summary>
    public const string MetaStatus = "status";

    /// <summary>Key lưu thời điểm tạo PO.</summary>
    public const string MetaCreatedUtc = "createdUtc";

    /// <summary>Key lưu thời điểm bắt đầu sản xuất.</summary>
    public const string MetaStartedUtc = "startedUtc";

    /// <summary>Key lưu thời điểm hoàn tất sản xuất.</summary>
    public const string MetaCompletedUtc = "completedUtc";

    private readonly string _dbRoot;

    /// <summary>
    /// Khởi tạo store.
    /// </summary>
    /// <param name="dbRoot">Thư mục gốc lưu file PO (đường dẫn tuyệt đối).</param>
    public PoStore(string dbRoot)
    {
        _dbRoot = dbRoot;
    }

    /// <summary>Thư mục gốc đang dùng.</summary>
    public string DbRoot => _dbRoot;

    /// <summary>
    /// Tính đường dẫn file DB cho một PO (chưa tạo file).
    /// </summary>
    public string GetDbPath(string orderNo, DateTime createdUtc)
    {
        var folder = Path.Combine(_dbRoot, createdUtc.ToString("yyyy-MM"));
        return Path.Combine(folder, SanitizeFileName(orderNo) + ".db");
    }

    /// <summary>
    /// Tìm file DB của một PO theo thư mục tháng (dùng khi rebuild cache lúc
    /// khởi động: PO có thể thuộc tháng trước).
    /// </summary>
    public string? FindExistingDbPath(string orderNo)
    {
        if (!Directory.Exists(_dbRoot))
            return null;

        var safe = SanitizeFileName(orderNo);
        foreach (var dir in Directory.EnumerateDirectories(_dbRoot))
        {
            var candidate = Path.Combine(dir, safe + ".db");
            if (File.Exists(candidate))
                return candidate;
        }

        return null;
    }

    /// <summary>
    /// Liệt kê tất cả file DB của PO đang tồn tại.
    /// </summary>
    public IReadOnlyList<string> ListDbFiles()
    {
        if (!Directory.Exists(_dbRoot))
            return [];

        var result = new List<string>();
        foreach (var dir in Directory.EnumerateDirectories(_dbRoot))
        {
            result.AddRange(Directory.EnumerateFiles(dir, "*.db"));
        }

        return result;
    }

    /// <summary>
    /// Tạo (nếu chưa có) file DB của PO và nạp tập mã lần đầu.
    /// </summary>
    /// <param name="order">Thông tin PO.</param>
    /// <param name="codes">Tập mã của PO.</param>
    /// <param name="ct">Token hủy.</param>
    public async Task EnsurePoDbAsync(
        ProductionOrder order,
        IReadOnlyList<PoCode> codes,
        CancellationToken ct = default)
    {
        var path = GetDbPath(order.OrderNo, order.CreatedUtc);
        await using var connection = await OpenAsync(path, ct).ConfigureAwait(false);

        await using (var meta = connection.CreateCommand())
        {
            meta.CommandText = """
                INSERT INTO PoMeta (Key, Value) VALUES ($key, $value)
                ON CONFLICT(Key) DO NOTHING;
                """;
            meta.Parameters.AddWithValue("$key", MetaOrderNo);
            meta.Parameters.AddWithValue("$value", order.OrderNo);
            await meta.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await SetMetaAsync(connection, MetaStatus, ProductionOrderStatus.Downloaded.ToString(), ct)
            .ConfigureAwait(false);
        await SetMetaAsync(connection, MetaCreatedUtc, order.CreatedUtc.ToString("O"), ct)
            .ConfigureAwait(false);

        // Insert tập mã (bỏ qua mã đã tồn tại — idempotent).
        if (codes.Count > 0)
        {
            await using var tx = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
            await using var insert = connection.CreateCommand();
            insert.Transaction = (SqliteTransaction)tx;
            insert.CommandText = """
                INSERT OR IGNORE INTO PoCodes (Code, Gtin, Serial, ScannedUtc, Status, ActivatedUtc)
                VALUES ($code, $gtin, $serial, NULL, 'Pending', NULL);
                """;

            var pCode = insert.Parameters.Add("$code", SqliteType.Text);
            var pGtin = insert.Parameters.Add("$gtin", SqliteType.Text);
            var pSerial = insert.Parameters.Add("$serial", SqliteType.Text);

            foreach (var code in codes)
            {
                pCode.Value = code.Code;
                pGtin.Value = code.Gtin;
                pSerial.Value = code.Serial;
                await insert.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            await tx.CommitAsync(ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Đọc toàn bộ mã của PO (kèm trạng thái).
    /// </summary>
    public async Task<IReadOnlyList<PoCode>> GetCodesAsync(
        string orderNo,
        DateTime createdUtc,
        CancellationToken ct = default)
    {
        var path = ResolvePath(orderNo, createdUtc);
        if (path is null)
            return [];

        await using var connection = await OpenAsync(path, ct).ConfigureAwait(false);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT Code, Gtin, Serial, ScannedUtc, Status, ActivatedUtc
            FROM PoCodes
            ORDER BY Serial;
            """;

        var list = new List<PoCode>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            list.Add(new PoCode
            {
                Code = reader.GetString(0),
                Gtin = reader.GetString(1),
                Serial = reader.GetString(2),
                ActivatedUtc = reader.IsDBNull(5)
                    ? null
                    : DateTime.Parse(reader.GetString(5), null, System.Globalization.DateTimeStyles.RoundtripKind),
            });
        }

        return list;
    }

    /// <summary>
    /// Đọc danh sách mã đã quét (Pass) của PO — dùng để rebuild cache RAM.
    /// </summary>
    public async Task<IReadOnlyList<PoCode>> GetScannedCodesAsync(
        string orderNo,
        DateTime createdUtc,
        CancellationToken ct = default)
    {
        var path = ResolvePath(orderNo, createdUtc);
        if (path is null)
            return [];

        await using var connection = await OpenAsync(path, ct).ConfigureAwait(false);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT Code, Gtin, Serial FROM PoCodes WHERE ScannedUtc IS NOT NULL;
            """;

        var list = new List<PoCode>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            list.Add(new PoCode
            {
                Code = reader.GetString(0),
                Gtin = reader.GetString(1),
                Serial = reader.GetString(2),
            });
        }

        return list;
    }

    /// <summary>
    /// Đếm số mã đã quét của PO.
    /// </summary>
    public async Task<int> CountScannedAsync(
        string orderNo,
        DateTime createdUtc,
        CancellationToken ct = default)
    {
        var path = ResolvePath(orderNo, createdUtc);
        if (path is null)
            return 0;

        await using var connection = await OpenAsync(path, ct).ConfigureAwait(false);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM PoCodes WHERE ScannedUtc IS NOT NULL;";
        var result = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return result is long l ? (int)l : 0;
    }

    /// <summary>
    /// Đánh dấu một mã đã được quét (Pass) trong PO.
    /// </summary>
    /// <param name="orderNo">Mã PO.</param>
    /// <param name="createdUtc">Thời điểm tạo PO (xác định file DB).</param>
    /// <param name="code">Khóa mã (định dạng <c>gtin|serial</c>).</param>
    /// <param name="ct">Token hủy.</param>
    public async Task MarkScannedAsync(
        string orderNo,
        DateTime createdUtc,
        string code,
        CancellationToken ct = default)
    {
        var path = ResolvePath(orderNo, createdUtc);
        if (path is null)
            return;

        await using var connection = await OpenAsync(path, ct).ConfigureAwait(false);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            UPDATE PoCodes
            SET ScannedUtc = $scannedUtc, Status = 'Scanned'
            WHERE Gtin || '|' || Serial = $code OR Code = $code OR Serial = $code;
            """;
        cmd.Parameters.AddWithValue("$scannedUtc", DateTime.UtcNow.ToString("O"));
        cmd.Parameters.AddWithValue("$code", code);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Đánh dấu một danh sách mã đã được kích hoạt (server đã nhận OK).
    /// </summary>
    public async Task MarkActivatedAsync(
        string orderNo,
        DateTime createdUtc,
        IReadOnlyList<string> codes,
        CancellationToken ct = default)
    {
        if (codes.Count == 0)
            return;

        var path = ResolvePath(orderNo, createdUtc);
        if (path is null)
            return;

        await using var connection = await OpenAsync(path, ct).ConfigureAwait(false);
        await using var tx = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
        await using var cmd = connection.CreateCommand();
        cmd.Transaction = (SqliteTransaction)tx;
        cmd.CommandText = """
            UPDATE PoCodes
            SET ActivatedUtc = $activatedUtc, Status = CASE WHEN ScannedUtc IS NULL THEN Status ELSE 'Activated' END
            WHERE Code = $code OR Serial = $code;
            """;

        var pActivated = cmd.Parameters.Add("$activatedUtc", SqliteType.Text);
        var pCode = cmd.Parameters.Add("$code", SqliteType.Text);

        foreach (var code in codes)
        {
            pActivated.Value = DateTime.UtcNow.ToString("O");
            pCode.Value = code;
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await tx.CommitAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Đọc danh sách mã CHƯA được kích hoạt của PO.
    /// </summary>
    public async Task<IReadOnlyList<string>> GetPendingActivationCodesAsync(
        string orderNo,
        DateTime createdUtc,
        CancellationToken ct = default)
    {
        var path = ResolvePath(orderNo, createdUtc);
        if (path is null)
            return [];

        await using var connection = await OpenAsync(path, ct).ConfigureAwait(false);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT Serial FROM PoCodes
            WHERE ActivatedUtc IS NULL AND Serial <> ''
            ORDER BY Serial;
            """;

        var list = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            list.Add(reader.GetString(0));
        }

        return list;
    }

    /// <summary>
    /// Đọc metadata của PO (key → value).
    /// </summary>
    public async Task<IReadOnlyDictionary<string, string>> GetMetaAsync(
        string orderNo,
        DateTime createdUtc,
        CancellationToken ct = default)
    {
        var path = ResolvePath(orderNo, createdUtc);
        if (path is null)
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        await using var connection = await OpenAsync(path, ct).ConfigureAwait(false);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT Key, Value FROM PoMeta;";

        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            map[reader.GetString(0)] = reader.GetString(1);
        }

        return map;
    }

    /// <summary>
    /// Ghi một giá trị metadata của PO.
    /// </summary>
    public async Task SetMetaAsync(
        string orderNo,
        DateTime createdUtc,
        string key,
        string value,
        CancellationToken ct = default)
    {
        var path = ResolvePath(orderNo, createdUtc);
        if (path is null)
            return;

        await using var connection = await OpenAsync(path, ct).ConfigureAwait(false);
        await SetMetaAsync(connection, key, value, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Ghi một giá trị metadata (dùng nội bộ với connection đã mở).
    /// </summary>
    private static async Task SetMetaAsync(
        SqliteConnection connection,
        string key,
        string value,
        CancellationToken ct)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO PoMeta (Key, Value) VALUES ($key, $value)
            ON CONFLICT(Key) DO UPDATE SET Value = excluded.Value;
            """;
        cmd.Parameters.AddWithValue("$key", key);
        cmd.Parameters.AddWithValue("$value", value);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Tìm đường dẫn file DB của PO: ưu tiên đường dẫn theo tháng đã biết,
    /// nếu không có thì quét toàn bộ thư mục.
    /// </summary>
    private string? ResolvePath(string orderNo, DateTime createdUtc)
    {
        var direct = GetDbPath(orderNo, createdUtc);
        if (File.Exists(direct))
            return direct;

        return FindExistingDbPath(orderNo);
    }

    /// <summary>
    /// Mở connection tới file DB của PO, tạo schema nếu chưa có.
    /// </summary>
    private static async Task<SqliteConnection> OpenAsync(string path, CancellationToken ct)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        var connection = new SqliteConnection($"Data Source={path}");
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
                CREATE TABLE IF NOT EXISTS PoCodes (
                    Code        TEXT NOT NULL PRIMARY KEY,
                    Gtin        TEXT NOT NULL DEFAULT '',
                    Serial      TEXT NOT NULL DEFAULT '',
                    ScannedUtc  TEXT NULL,
                    Status      TEXT NOT NULL DEFAULT 'Pending',
                    ActivatedUtc TEXT NULL
                );

                CREATE TABLE IF NOT EXISTS PoMeta (
                    Key   TEXT NOT NULL PRIMARY KEY,
                    Value TEXT NOT NULL
                );

                CREATE INDEX IF NOT EXISTS IX_PoCodes_Serial  ON PoCodes (Serial);
                CREATE INDEX IF NOT EXISTS IX_PoCodes_Scanned ON PoCodes (ScannedUtc);
                """;
            await schema.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        return connection;
    }

    /// <summary>
    /// Chuẩn hoá mã PO để dùng làm tên file an toàn trên mọi hệ điều hành.
    /// </summary>
    private static string SanitizeFileName(string orderNo)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = orderNo.Select(c => invalid.Contains(c) ? '_' : c).ToArray();
        var name = new string(chars).Trim();
        return string.IsNullOrEmpty(name) ? "PO" : name;
    }
}
