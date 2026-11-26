using System.Text;
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
    public const int CurrentSchemaVersion = 2;

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

    /// <summary>Key lưu thời điểm huỷ sản xuất.</summary>
    public const string MetaCancelledUtc = "cancelledUtc";

    /// <summary>Key lưu số lượng kế hoạch của PO, độc lập với số mã thực nhận.</summary>
    public const string MetaOrderQty = "orderQty";

    /// <summary>Key lưu GTIN chuẩn 14 chữ số của PO.</summary>
    public const string MetaGtin = "gtin";

    /// <summary>Key lưu các trường mở rộng của PO dưới dạng JSON.</summary>
    public const string MetaAdditional = "additional";

    /// <summary>Key marking an unresolved PLC Pass safety halt for this PO.</summary>
    public const string MetaHalted = "halted";

    private readonly string _dbRoot;
    private readonly SemaphoreSlim _mutationGate = new(1, 1);

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
    /// Khóa thao tác đổi trạng thái sản xuất và nhận/merge PO. Dùng chung để
    /// một lần nhận không thể chen vào giữa lúc bắt đầu hoặc kết thúc sản xuất.
    /// </summary>
    public async ValueTask<IDisposable> AcquireMutationLockAsync(CancellationToken ct = default)
    {
        await _mutationGate.WaitAsync(ct).ConfigureAwait(false);
        return new MutationGateLease(_mutationGate);
    }

    private sealed class MutationGateLease(SemaphoreSlim gate) : IDisposable
    {
        private SemaphoreSlim? _gate = gate;

        public void Dispose() => Interlocked.Exchange(ref _gate, null)?.Release();
    }

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
    /// Tìm mã đã tồn tại trong mọi PO trên máy, trừ PO được chỉ định loại trừ.
    ///
    /// <para>
    /// Chẩn đoán tương thích cũ: so khớp toàn bộ CodeContent, phân biệt hoa thường.
    /// Luồng sản xuất không dùng kết quả này để lọc mã giữa các PO.
    /// </para>
    ///
    /// <para>
    /// <b>Hiệu năng:</b> không nạp toàn bộ mã vào RAM. Mỗi file DB PO được
    /// <c>ATTACH</c> vào một connection và so khớp theo từng lô khoá (mặc định
    /// 400 khoá/lô) qua khóa <c>PoCodes.Code</c>.
    /// </para>
    /// </summary>
    /// <param name="codeKeys">
    /// Danh sách CodeContent đầy đủ cần tra cứu chẩn đoán.
    /// </param>
    /// <param name="excludeOrderNo">Tên PO cần loại trừ (PO đang kiểm tra).</param>
    /// <param name="maxSamples">Số mã ví dụ tối đa trả về.</param>
    /// <param name="ct">Token hủy.</param>
    /// <returns>Kết quả trùng mã.</returns>
    [Obsolete("Cross-PO diagnostic only. Production matching is isolated per PO using CodeContent.")]
    public async Task<DuplicateCodeResult> FindDuplicateCodesAsync(
        IReadOnlyList<string> codeKeys,
        string? excludeOrderNo = null,
        int maxSamples = 5,
        CancellationToken ct = default)
    {
        if (codeKeys is null || codeKeys.Count == 0 || !Directory.Exists(_dbRoot))
        {
            return new DuplicateCodeResult();
        }

        // Loại bỏ mã trùng ngay trong chính payload (đã de-dup ở tầng trên).
        var unique = new HashSet<string>(codeKeys.Count, StringComparer.Ordinal);
        foreach (var key in codeKeys)
        {
            if (!string.IsNullOrEmpty(key))
            {
                unique.Add(key);
            }
        }

        if (unique.Count == 0)
        {
            return new DuplicateCodeResult();
        }

        var exclude = excludeOrderNo?.Trim() ?? string.Empty;
        var samples = new List<string>();
        var duplicateKeys = new HashSet<string>(StringComparer.Ordinal);
        var firstOwner = string.Empty;

        // Danh sách file DB PO cần so khớp (bỏ qua file của chính PO đang kiểm tra).
        var files = new List<string>();
        foreach (var file in ListDbFiles())
        {
            if (!string.IsNullOrEmpty(exclude)
                && string.Equals(
                    Path.GetFileNameWithoutExtension(file),
                    exclude,
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            files.Add(file);
        }

        if (files.Count == 0)
        {
            return new DuplicateCodeResult();
        }

        // Connection chính: dùng để tạo bảng tạm chứa khoá cần kiểm tra.
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(ct).ConfigureAwait(false);

        await using (var pragma = connection.CreateCommand())
        {
            pragma.CommandText = """
                PRAGMA busy_timeout=5000;
                PRAGMA synchronous=NORMAL;
                PRAGMA temp_store=MEMORY;
                """;
            await pragma.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await using (var schema = connection.CreateCommand())
        {
            schema.CommandText = """
                CREATE TEMP TABLE ProbeKeys (Key TEXT NOT NULL PRIMARY KEY);
                """;
            await schema.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        var keys = unique.ToList();
        const int batchSize = 400;

        // Nạp khoá cần kiểm tra vào bảng tạm MỘT LẦN (dùng chung cho mọi
        // file DB PO). Dùng multi-row VALUES để mỗi câu lệnh nạp được cả lô
        // khoá (SQLite chỉ cho tối đa 999 biến -> lô 400 khoá an toàn).
        foreach (var batch in Chunk(keys, batchSize))
        {
            await using var fill = connection.CreateCommand();

            var fillSql = new StringBuilder("INSERT OR IGNORE INTO ProbeKeys (Key) VALUES ");

            for (var i = 0; i < batch.Count; i++)
            {
                if (i > 0)
                {
                    fillSql.Append(',');
                }

                fillSql.Append("($k").Append(i).Append(')');
            }

            fillSql.Append(';');

            fill.CommandText = fillSql.ToString();

            for (var i = 0; i < batch.Count; i++)
            {
                fill.Parameters.AddWithValue("$k" + i, batch[i]);
            }

            await fill.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        for (var fileIndex = 0; fileIndex < files.Count; fileIndex++)
        {
            var file = files[fileIndex];
            ct.ThrowIfCancellationRequested();

            // Tên file DB đã được chuẩn hoá (sanitize) — dùng làm tên alias.
            var alias = "po_" + fileIndex.ToString(System.Globalization.CultureInfo.InvariantCulture);

            try
            {
                await using (var attach = connection.CreateCommand())
                {
                    attach.CommandText = $"ATTACH DATABASE $path AS {alias};";
                    attach.Parameters.AddWithValue("$path", file);
                    await attach.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw new IOException($"Không thể kiểm tra mã trùng trong file PO '{file}'.", ex);
            }

            try
            {
                // Lấy mọi khóa trùng để PO nhận lại chỉ thêm phần mã mới.
                await using (var query = connection.CreateCommand())
                {
                    query.CommandText = $"""
                        SELECT DISTINCT c.Code
                        FROM {alias}.PoCodes AS c
                        INNER JOIN ProbeKeys AS p
                            ON p.Key = c.Code COLLATE BINARY;
                        """;
                    await using var reader = await query.ExecuteReaderAsync(ct).ConfigureAwait(false);
                    while (await reader.ReadAsync(ct).ConfigureAwait(false))
                    {
                        var key = reader.GetString(0);
                        if (!duplicateKeys.Add(key))
                            continue;

                        if (samples.Count < maxSamples)
                        {
                            samples.Add(key);
                        }
                    }
                }

                // Tên PO chứa mã trùng (lấy từ PoMeta của file đó).
                if (duplicateKeys.Count > 0 && string.IsNullOrEmpty(firstOwner))
                {
                    await using (var query = connection.CreateCommand())
                    {
                        query.CommandText = $"""
                            SELECT Value FROM {alias}.PoMeta WHERE Key = $key LIMIT 1;
                            """;
                        query.Parameters.AddWithValue("$key", MetaOrderNo);
                        var value = await query.ExecuteScalarAsync(ct).ConfigureAwait(false);
                        firstOwner = value as string ?? string.Empty;
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw new IOException($"Không thể đọc mã trong file PO '{file}'.", ex);
            }
            finally
            {
                try
                {
                    await using var detach = connection.CreateCommand();
                    detach.CommandText = $"DETACH DATABASE {alias};";
                    await detach.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // Bỏ qua lỗi DETACH.
                }
            }
        }

        return new DuplicateCodeResult
        {
            TotalDuplicates = duplicateKeys.Count,
            SampleCodes = samples,
            ExistingOrderNo = firstOwner,
            DuplicateKeys = duplicateKeys,
        };
    }

    /// <summary>Chia danh sách thành các lô có kích thước tối đa cho trước.</summary>
    private static IEnumerable<List<T>> Chunk<T>(IReadOnlyList<T> source, int size)
    {
        for (var offset = 0; offset < source.Count; offset += size)
        {
            var take = Math.Min(size, source.Count - offset);
            var chunk = new List<T>(take);
            for (var i = 0; i < take; i++)
            {
                chunk.Add(source[offset + i]);
            }

            yield return chunk;
        }
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
        var existingPath = ResolvePath(order.OrderNo, order.CreatedUtc);
        if (existingPath is not null)
        {
            await EnsurePoDbAtPathAsync(existingPath, order, codes, ct).ConfigureAwait(false);
            return;
        }

        // Chỉ công bố DB mới sau khi toàn bộ metadata và mã đã commit.
        // File tạm không có đuôi .db để lượt kiểm tra khác không nhìn thấy.
        var path = GetDbPath(order.OrderNo, order.CreatedUtc);
        var pendingPath = path + ".pending-" + Guid.NewGuid().ToString("N");
        try
        {
            await EnsurePoDbAtPathAsync(pendingPath, order, codes, ct, pooling: false).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            File.Move(pendingPath, path);
        }
        finally
        {
            foreach (var suffix in new[] { "", "-wal", "-shm" })
            {
                try { File.Delete(pendingPath + suffix); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }

    private static async Task EnsurePoDbAtPathAsync(
        string path, ProductionOrder order, IReadOnlyList<PoCode> codes,
        CancellationToken ct, bool pooling = true)
    {
        await using var connection = await OpenAsync(path, ct, pooling).ConfigureAwait(false);

        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct)
            .ConfigureAwait(false);

        await using (var meta = connection.CreateCommand())
        {
            meta.Transaction = transaction;
            meta.CommandText = """
                INSERT INTO PoMeta (Key, Value) VALUES ($key, $value), ($createdKey, $createdValue)
                ON CONFLICT(Key) DO NOTHING;
                """;
            meta.Parameters.AddWithValue("$key", MetaOrderNo);
            meta.Parameters.AddWithValue("$value", order.OrderNo);
            meta.Parameters.AddWithValue("$createdKey", MetaCreatedUtc);
            meta.Parameters.AddWithValue("$createdValue", order.CreatedUtc.ToString("O"));
            await meta.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        // Giữ nguyên trạng thái local đã có (Running/Completed/Cancelled).
        // Tải lại PO từ server không được làm mất trạng thái huỷ hoặc hoàn tất.

        // Planned là mục tiêu tích lũy, không phải actual hay số mã của batch.
        // Giữ target lớn nhất để replay/append không cộng hai lần hoặc hạ target.
        await using (var planned = connection.CreateCommand())
        {
            planned.Transaction = transaction;
            planned.CommandText = """
                INSERT INTO PoMeta (Key, Value) VALUES ($key, $value)
                ON CONFLICT(Key) DO UPDATE SET Value =
                    CAST(MAX(CAST(PoMeta.Value AS INTEGER), CAST(excluded.Value AS INTEGER)) AS TEXT);
                """;
            planned.Parameters.AddWithValue("$key", MetaOrderQty);
            planned.Parameters.AddWithValue("$value", order.OrderQty.ToString(System.Globalization.CultureInfo.InvariantCulture));
            await planned.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        var gtin = order.Gtin;
        if (string.IsNullOrEmpty(gtin) && order.Additional is not null)
            gtin = order.Additional.FirstOrDefault(e => string.Equals(e.Key, MetaGtin,
                StringComparison.OrdinalIgnoreCase)).Value;
        if (PoCodeParser.TryNormalizeGtin(gtin, out var normalizedGtin))
        {
            await using var existingGtin = connection.CreateCommand();
            existingGtin.Transaction = transaction;
            existingGtin.CommandText = "SELECT Value FROM PoMeta WHERE Key=$key COLLATE NOCASE LIMIT 1;";
            existingGtin.Parameters.AddWithValue("$key", MetaGtin);
            var storedGtin = await existingGtin.ExecuteScalarAsync(ct).ConfigureAwait(false) as string;
            if (PoCodeParser.TryNormalizeGtin(storedGtin, out var normalizedStoredGtin)
                && !string.Equals(normalizedGtin, normalizedStoredGtin, StringComparison.Ordinal))
                throw new InvalidDataException("Không thể đổi GTIN của PO đã lưu.");
            await SetMetaAsync(connection, MetaGtin, normalizedGtin, ct, transaction).ConfigureAwait(false);
        }

        // Lưu các trường mở rộng (tên sản phẩm, khách hàng...) để hỗ trợ tìm kiếm.
        if (order.Additional is { Count: > 0 })
        {
            await SetMetaAsync(
                connection,
                MetaAdditional,
                System.Text.Json.JsonSerializer.Serialize(
                    order.Additional,
                    PoJsonContext.Default.DictionaryStringString),
                ct, transaction).ConfigureAwait(false);
        }

        // Insert tập mã (bỏ qua mã đã tồn tại — idempotent).
        //
        // Ghi theo LÔ (multi-row VALUES) thay vì từng dòng một: với PO vài
        // chục nghìn mã, cách ghi từng dòng sẽ tốn hàng trăm nghìn vòng
        // round-trip xuống SQLite. Chia lô 180 mã / lệnh để không vượt quá
        // giới hạn số biến của SQLite (mặc định 999 — 5 biến cho mỗi mã).
        if (codes.Count > 0)
        {
            const int batchSize = 180;

            for (var offset = 0; offset < codes.Count; offset += batchSize)
            {
                var take = Math.Min(batchSize, codes.Count - offset);

                var sql = new StringBuilder(
                    "INSERT OR IGNORE INTO PoCodes "
                    + "(Code, Gtin, Serial, ScannedUtc, Status, ActivatedUtc) VALUES ");

                // Mỗi lô tạo một câu lệnh RIÊNG: nếu dùng chung một
                // SqliteCommand thì tham số của các lô trước sẽ còn lại và
                // làm lệnh chậm dần (và vượt giới hạn biến của SQLite).
                await using var insert = connection.CreateCommand();
                insert.Transaction = transaction;

                // Mỗi mã dùng 3 tham số riêng ($c{n}, $g{n}, $s{n}) — tên
                // phải KHỚP với tên trong SQL.
                for (var i = 0; i < take; i++)
                {
                    var n = offset + i;

                    var pCode = insert.Parameters.Add("$c" + n, SqliteType.Text);
                    var pGtin = insert.Parameters.Add("$g" + n, SqliteType.Text);
                    var pSerial = insert.Parameters.Add("$s" + n, SqliteType.Text);
                    var pScanned = insert.Parameters.Add("$seen" + n, SqliteType.Text);
                    var pActivated = insert.Parameters.Add("$activated" + n, SqliteType.Text);

                    var code = codes[n];

                    pCode.Value = code.CodeContent;
                    pGtin.Value = string.Empty;
                    pSerial.Value = string.Empty;
                    pScanned.Value = code.ScannedUtc?.ToString("O") is { } scanned
                        ? scanned
                        : DBNull.Value;
                    pActivated.Value = code.ActivatedUtc?.ToString("O") is { } activated
                        ? activated
                        : DBNull.Value;

                    if (i > 0)
                    {
                        sql.Append(',');
                    }

                    sql.Append("($c").Append(n)
                       .Append(", $g").Append(n)
                       .Append(", $s").Append(n)
                       .Append(", $seen").Append(n)
                       .Append(", CASE WHEN $activated").Append(n).Append(" IS NOT NULL THEN 'Activated' WHEN $seen")
                       .Append(n).Append(" IS NOT NULL THEN 'Scanned' ELSE 'Pending' END, $activated")
                       .Append(n).Append(')');
                }

                insert.CommandText = sql.ToString();
                await insert.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
        }

        await transaction.CommitAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Liệt kê tất cả PO đã tồn tại trong các file DB local (theo tháng).
    ///
    /// <para>
    /// Đọc metadata từ bảng <c>PoMeta</c> của mỗi file — không cần gọi mạng.
    /// Đây là nguồn danh sách PO cho UI trong mô hình local-first.
    /// </para>
    /// </summary>
    public async Task<IReadOnlyList<LocalPoInfo>> ListLocalOrdersAsync(
        CancellationToken ct = default,
        bool strict = false)
    {
        var result = new List<LocalPoInfo>();

        foreach (var file in ListDbFiles())
        {
            try
            {
                var connectionString = new SqliteConnectionStringBuilder
                {
                    DataSource = file,
                    Mode = SqliteOpenMode.ReadOnly,
                }.ToString();
                await using var connection = new SqliteConnection(connectionString);
                await connection.OpenAsync(ct).ConfigureAwait(false);

                string orderNo = string.Empty;
                string status = ProductionOrderStatus.Downloaded.ToString();
                DateTime createdUtc = DateTime.MinValue;
                int orderQty = 0;
                string gtin = string.Empty;
                bool hasOrderQty = false;
                DateTime? startedUtc = null;
                DateTime? completedUtc = null;
                DateTime? cancelledUtc = null;
                Dictionary<string, string> additional = [];

                await using (var meta = connection.CreateCommand())
                {
                    meta.CommandText = "SELECT Key, Value FROM PoMeta;";
                    await using var reader = await meta.ExecuteReaderAsync(ct).ConfigureAwait(false);
                    while (await reader.ReadAsync(ct).ConfigureAwait(false))
                    {
                        var key = reader.GetString(0);
                        var value = reader.GetString(1);

                        if (string.Equals(key, MetaOrderNo, StringComparison.OrdinalIgnoreCase))
                            orderNo = value;
                        else if (string.Equals(key, MetaStatus, StringComparison.OrdinalIgnoreCase))
                            status = value;
                        else if (string.Equals(key, MetaCreatedUtc, StringComparison.OrdinalIgnoreCase))
                            createdUtc = ParseUtc(value);
                        else if (string.Equals(key, MetaOrderQty, StringComparison.OrdinalIgnoreCase))
                            hasOrderQty = int.TryParse(value, out orderQty) && orderQty >= 0;
                        else if (string.Equals(key, MetaGtin, StringComparison.OrdinalIgnoreCase))
                            gtin = value;
                        else if (string.Equals(key, MetaStartedUtc, StringComparison.OrdinalIgnoreCase))
                            startedUtc = ParseUtc(value);
                        else if (string.Equals(key, MetaCompletedUtc, StringComparison.OrdinalIgnoreCase))
                            completedUtc = ParseUtc(value);
                        else if (string.Equals(key, MetaCancelledUtc, StringComparison.OrdinalIgnoreCase))
                            cancelledUtc = ParseUtc(value);
                        else if (string.Equals(key, MetaAdditional, StringComparison.OrdinalIgnoreCase))
                            additional = ParseAdditional(value);
                    }
                }

                // Tương thích PO cũ: đọc GTIN từ additional trước, rồi chỉ suy
                // ra từ mã cũ nếu toàn bộ mã hợp lệ và cùng đúng một GTIN.
                if (string.IsNullOrEmpty(gtin))
                    gtin = additional.FirstOrDefault(e => string.Equals(e.Key, MetaGtin,
                        StringComparison.OrdinalIgnoreCase)).Value ?? string.Empty;
                if (!PoCodeParser.TryNormalizeGtin(gtin, out var normalizedGtin))
                {
                    normalizedGtin = string.Empty;
                    var candidate = string.Empty;
                    var consistent = true;
                    await using var legacyCodes = connection.CreateCommand();
                    legacyCodes.CommandText = "SELECT Code FROM PoCodes;";
                    await using var legacyReader = await legacyCodes.ExecuteReaderAsync(ct).ConfigureAwait(false);
                    while (await legacyReader.ReadAsync(ct).ConfigureAwait(false))
                    {
                        if (!PoCodeParser.TryGetGtin(legacyReader.GetString(0), out var parsed)
                            || (candidate.Length > 0 && !string.Equals(candidate, parsed, StringComparison.Ordinal)))
                        {
                            consistent = false;
                            break;
                        }
                        candidate = parsed;
                    }
                    if (consistent) normalizedGtin = candidate;
                }

                if (string.IsNullOrWhiteSpace(orderNo))
                {
                    if (strict)
                        throw new InvalidDataException($"File PO '{file}' thiếu mã lệnh sản xuất.");
                    continue;
                }
                if (strict && (!Enum.TryParse<ProductionOrderStatus>(status, true, out var parsedStatus)
                    || !Enum.IsDefined(parsedStatus)))
                    throw new InvalidDataException($"File PO '{file}' có trạng thái không hợp lệ.");

                // Số mã thực tế trong file (có thể nhiều hơn orderQty nếu server
                // bổ sung mã sau lần cào đầu tiên).
                int totalCodes = 0;
                int scannedCodes = 0;
                await using (var count = connection.CreateCommand())
                {
                    count.CommandText = """
                        SELECT COUNT(*), SUM(CASE WHEN ScannedUtc IS NOT NULL OR ActivatedUtc IS NOT NULL THEN 1 ELSE 0 END)
                        FROM PoCodes;
                        """;
                    await using var reader = await count.ExecuteReaderAsync(ct).ConfigureAwait(false);
                    if (await reader.ReadAsync(ct).ConfigureAwait(false))
                    {
                        totalCodes = reader.GetInt32(0);
                        if (!reader.IsDBNull(1))
                        {
                            scannedCodes = reader.GetInt32(1);
                        }
                    }
                }

                result.Add(new LocalPoInfo
                {
                    OrderNo = orderNo,
                    OrderQty = hasOrderQty ? orderQty : totalCodes,
                    Gtin = normalizedGtin,
                    CreatedUtc = createdUtc,
                    Status = ParseStatus(status),
                    StartedUtc = startedUtc,
                    CompletedUtc = completedUtc,
                    CancelledUtc = cancelledUtc,
                    TotalCodes = totalCodes,
                    ScannedCodes = scannedCodes,
                    DbPath = file,
                    Additional = additional,
                });
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                if (strict)
                    throw new IOException($"Không thể kiểm tra trạng thái PO trong '{file}'.", ex);
                // Danh sách UI vẫn có thể hiển thị những file đọc được.
            }
        }

        // Mới nhất trước.
        return result
            .OrderByDescending(p => p.CreatedUtc == default ? p.StartedUtc : p.CreatedUtc)
            .ToList();
    }

    /// <summary>
    /// Kiểm tra PO dở dang từ DB và lịch sử, kể cả trước khi khôi phục cache.
    /// Lỗi đọc dữ liệu được trả ra để bên nhận không bỏ qua kiểm tra.
    /// Gọi trong khóa <see cref="AcquireMutationLockAsync"/> khi chuẩn bị ghi dữ liệu.
    /// </summary>
    public async Task<string?> GetUnfinishedOrderNoAsync(
        PoHistoryStore history, CancellationToken ct = default)
    {
        var orders = await ListLocalOrdersAsync(ct, strict: true).ConfigureAwait(false);
        var unfinished = orders.FirstOrDefault(order => order.IsUnfinished);
        if (unfinished is not null)
            return unfinished.OrderNo;

        if (!File.Exists(history.DbPath))
            return null;
        var finishedOrders = orders.Where(order => order.IsFinished)
            .Select(order => order.OrderNo).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var runs = await history.GetRunsAsync(ct).ConfigureAwait(false);
        // Local kết thúc có thể đã commit trước khi ứng dụng dừng đột ngột,
        // trong khi lịch sử chưa cập nhật. Không để bản ghi cũ khóa nhận mã.
        return runs.FirstOrDefault(run =>
            string.Equals(run.Status, "Running", StringComparison.OrdinalIgnoreCase)
            && !finishedOrders.Contains(run.OrderNo))?.OrderNo;
    }

    /// <summary>
    /// Đọc tập mã kèm cờ trạng thái để nạp vào cache RAM.
    /// </summary>
    /// <returns>Cặp (khóa mã, cờ trạng thái) cho từng mã của PO.</returns>
    public async Task<IReadOnlyList<KeyValuePair<string, PoCodeCache.Flags>>> GetCodesWithFlagsAsync(
        string orderNo,
        DateTime createdUtc,
        CancellationToken ct = default)
    {
        var path = ResolvePath(orderNo, createdUtc);
        if (path is null)
            return [];

        await using var connection = await OpenAsync(path, ct).ConfigureAwait(false);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT Code, ScannedUtc, ActivatedUtc, PendingPassUtc FROM PoCodes;";

        var list = new List<KeyValuePair<string, PoCodeCache.Flags>>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var flags = PoCodeCache.Flags.None;

            if (!reader.IsDBNull(1) || !reader.IsDBNull(2))
            {
                flags |= PoCodeCache.Flags.Scanned;
            }

            if (!reader.IsDBNull(2))
            {
                flags |= PoCodeCache.Flags.Activated;
            }

            if (!reader.IsDBNull(3))
            {
                flags |= PoCodeCache.Flags.PendingPass;
            }

            list.Add(new KeyValuePair<string, PoCodeCache.Flags>(
                reader.GetString(0),
                flags));
        }

        return list;
    }

    /// <summary>
    /// Đánh dấu PO đã bị huỷ — không được chạy lại.
    /// </summary>
    public async Task MarkCancelledAsync(
        string orderNo,
        DateTime createdUtc,
        CancellationToken ct = default)
    {
        var path = ResolvePath(orderNo, createdUtc);
        if (path is null)
            return;

        await using var connection = await OpenAsync(path, ct).ConfigureAwait(false);
        await SetMetaAsync(
            connection, MetaCancelledUtc, DateTime.UtcNow.ToString("O"), ct)
            .ConfigureAwait(false);
        await SetMetaAsync(
            connection, MetaStatus, ProductionOrderStatus.Cancelled.ToString(), ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Kiểm tra PO đã bị huỷ hay chưa.
    /// </summary>
    public async Task<bool> IsCancelledAsync(
        string orderNo,
        DateTime createdUtc,
        CancellationToken ct = default)
    {
        var meta = await GetMetaAsync(orderNo, createdUtc, ct).ConfigureAwait(false);
        if (meta.TryGetValue(MetaCancelledUtc, out _))
        {
            return true;
        }

        return meta.TryGetValue(MetaStatus, out var status)
            && string.Equals(
                status, ProductionOrderStatus.Cancelled.ToString(),
                StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Đọc toàn bộ mã của PO (kèm trạng thái).
    /// </summary>
    public async Task<IReadOnlyList<PoCode>> GetCodesAsync(
        string orderNo,
        DateTime createdUtc,
        CancellationToken ct = default)
    {        var path = ResolvePath(orderNo, createdUtc);
        if (path is null)
            return [];

        await using var connection = await OpenAsync(path, ct).ConfigureAwait(false);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT Code, ScannedUtc, ActivatedUtc
            FROM PoCodes
            ORDER BY Code;
            """;

        var list = new List<PoCode>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            list.Add(new PoCode
            {
                CodeContent = reader.GetString(0),
                ScannedUtc = reader.IsDBNull(1)
                    ? null
                    : DateTime.Parse(reader.GetString(1), null, System.Globalization.DateTimeStyles.RoundtripKind),
                ActivatedUtc = reader.IsDBNull(2)
                    ? null
                    : DateTime.Parse(reader.GetString(2), null, System.Globalization.DateTimeStyles.RoundtripKind),
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
            SELECT Code, ScannedUtc, ActivatedUtc FROM PoCodes
            WHERE ScannedUtc IS NOT NULL OR ActivatedUtc IS NOT NULL;
            """;

        var list = new List<PoCode>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            list.Add(new PoCode
            {
                CodeContent = reader.GetString(0),
                ScannedUtc = reader.IsDBNull(1)
                    ? null
                    : DateTime.Parse(reader.GetString(1), null, System.Globalization.DateTimeStyles.RoundtripKind),
                ActivatedUtc = reader.IsDBNull(2)
                    ? null
                    : DateTime.Parse(reader.GetString(2), null, System.Globalization.DateTimeStyles.RoundtripKind),
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
        cmd.CommandText = "SELECT COUNT(*) FROM PoCodes WHERE ScannedUtc IS NOT NULL OR ActivatedUtc IS NOT NULL;";
        var result = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return result is long l ? (int)l : 0;
    }

    /// <summary>Counts the committed code rows in one PO database.</summary>
    public async Task<int> CountCodesAsync(
        string orderNo,
        DateTime createdUtc,
        CancellationToken ct = default)
    {
        var path = ResolvePath(orderNo, createdUtc);
        if (path is null)
            return 0;

        await using var connection = await OpenAsync(path, ct).ConfigureAwait(false);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM PoCodes;";
        var result = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return result is long count ? (int)count : 0;
    }

    /// <summary>
    /// Đánh dấu một mã đã được quét (Pass) trong PO.
    /// </summary>
    /// <param name="orderNo">Mã PO.</param>
    /// <param name="createdUtc">Thời điểm tạo PO (xác định file DB).</param>
    /// <param name="codeContent">Complete code content.</param>
    /// <param name="ct">Token hủy.</param>
    public async Task<bool> MarkScannedAsync(
        string orderNo,
        DateTime createdUtc,
        string codeContent,
        CancellationToken ct = default)
    {
        var path = ResolvePath(orderNo, createdUtc);
        if (path is null)
            return false;

        await using var connection = await OpenAsync(path, ct).ConfigureAwait(false);
        await using var tx = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
        await using var cmd = connection.CreateCommand();
        cmd.Transaction = (SqliteTransaction)tx;
        cmd.CommandText = """
            UPDATE PoCodes
            SET ScannedUtc = $scannedUtc, Status = 'Scanned'
            WHERE Code = $code AND ScannedUtc IS NULL;
            """;
        cmd.Parameters.AddWithValue("$scannedUtc", DateTime.UtcNow.ToString("O"));
        cmd.Parameters.AddWithValue("$code", codeContent);
        var changed = await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false) > 0;
        ct.ThrowIfCancellationRequested();
        await tx.CommitAsync(CancellationToken.None).ConfigureAwait(false);
        return changed;
    }

    /// <summary>Atomically mark a PO complete in its local database.</summary>
    public async Task MarkCompletedAsync(
        string orderNo,
        DateTime createdUtc,
        CancellationToken ct = default)
    {
        var path = ResolvePath(orderNo, createdUtc)
            ?? throw new InvalidOperationException($"PO database for '{orderNo}' was not found.");

        await using var connection = await OpenAsync(path, ct).ConfigureAwait(false);
        await using var tx = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
        await SetMetaAsync(connection, MetaStatus, ProductionOrderStatus.Completed.ToString(), ct,
            (SqliteTransaction)tx).ConfigureAwait(false);
        await SetMetaAsync(connection, MetaCompletedUtc, DateTime.UtcNow.ToString("O"), ct,
            (SqliteTransaction)tx).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        await tx.CommitAsync(CancellationToken.None).ConfigureAwait(false);
    }

    /// <summary>Persist an unresolved Pass intent before writing Pass to the PLC.</summary>
    public async Task<bool> BeginPendingPassAsync(
        string orderNo,
        DateTime createdUtc,
        string codeContent,
        CancellationToken ct = default)
    {
        var path = ResolvePath(orderNo, createdUtc);
        if (path is null)
            return false;

        await using var connection = await OpenAsync(path, ct).ConfigureAwait(false);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            UPDATE PoCodes
            SET PendingPassUtc = $pendingUtc
            WHERE Code = $code AND ScannedUtc IS NULL
              AND ActivatedUtc IS NULL AND PendingPassUtc IS NULL;
            """;
        cmd.Parameters.AddWithValue("$pendingUtc", DateTime.UtcNow.ToString("O"));
        cmd.Parameters.AddWithValue("$code", codeContent);
        return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false) > 0;
    }

    /// <summary>Clear a known failed PLC Pass intent so the code remains available.</summary>
    public async Task<bool> ClearPendingPassAsync(
        string orderNo,
        DateTime createdUtc,
        string codeContent,
        CancellationToken ct = default)
    {
        var path = ResolvePath(orderNo, createdUtc);
        if (path is null)
            return false;

        await using var connection = await OpenAsync(path, ct).ConfigureAwait(false);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            UPDATE PoCodes SET PendingPassUtc = NULL
            WHERE Code = $code AND PendingPassUtc IS NOT NULL AND ScannedUtc IS NULL;
            """;
        cmd.Parameters.AddWithValue("$code", codeContent);
        return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false) > 0;
    }

    /// <summary>Atomically convert a pending Pass intent into committed scanned state.</summary>
    public async Task<bool> CommitPendingPassAsync(
        string orderNo,
        DateTime createdUtc,
        string codeContent,
        CancellationToken ct = default)
    {
        var path = ResolvePath(orderNo, createdUtc);
        if (path is null)
            return false;

        await using var connection = await OpenAsync(path, ct).ConfigureAwait(false);
        await using var tx = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
        await using var cmd = connection.CreateCommand();
        cmd.Transaction = (SqliteTransaction)tx;
        cmd.CommandText = """
            UPDATE PoCodes
            SET PendingPassUtc = NULL, ScannedUtc = $scannedUtc, Status = 'Scanned'
            WHERE Code = $code AND PendingPassUtc IS NOT NULL AND ScannedUtc IS NULL;
            """;
        cmd.Parameters.AddWithValue("$scannedUtc", DateTime.UtcNow.ToString("O"));
        cmd.Parameters.AddWithValue("$code", codeContent);
        var changed = await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false) > 0;
        ct.ThrowIfCancellationRequested();
        await tx.CommitAsync(CancellationToken.None).ConfigureAwait(false);
        return changed;
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
            WHERE Code = $code AND ScannedUtc IS NOT NULL;
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
            SELECT Code FROM PoCodes
            WHERE ScannedUtc IS NOT NULL AND ActivatedUtc IS NULL
            ORDER BY Code;
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
        CancellationToken ct,
        SqliteTransaction? transaction = null)
    {
        await using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
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
    private static async Task<SqliteConnection> OpenAsync(string path, CancellationToken ct, bool pooling = true)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Pooling = pooling,
        }.ToString());
        try
        {
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
                        ActivatedUtc TEXT NULL,
                        PendingPassUtc TEXT NULL
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

            var hasPendingPassColumn = false;
            await using (var columns = connection.CreateCommand())
            {
                columns.CommandText = "PRAGMA table_info(PoCodes);";
                await using var reader = await columns.ExecuteReaderAsync(ct).ConfigureAwait(false);
                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    if (string.Equals(reader.GetString(1), "PendingPassUtc", StringComparison.OrdinalIgnoreCase))
                    {
                        hasPendingPassColumn = true;
                        break;
                    }
                }
            }

            if (!hasPendingPassColumn)
            {
                await using var migration = connection.CreateCommand();
                migration.CommandText = "ALTER TABLE PoCodes ADD COLUMN PendingPassUtc TEXT NULL;";
                await migration.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
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

    /// <summary>
    /// Legacy compatibility key for callers predating full CodeContent identity.
    /// </summary>
    public static string BuildCodeKey(string gtin, string serial) => $"{gtin}|{serial}";

    /// <summary>Parse chuỗi thời gian UTC theo format "O".</summary>
    private static DateTime ParseUtc(string value) =>
        DateTime.TryParse(
            value,
            null,
            System.Globalization.DateTimeStyles.RoundtripKind,
            out var parsed)
            ? parsed
            : DateTime.MinValue;

    /// <summary>Parse chuỗi trạng thái PO (fallback <c>Downloaded</c>).</summary>
    private static ProductionOrderStatus ParseStatus(string value) =>
        Enum.TryParse<ProductionOrderStatus>(value, ignoreCase: true, out var status)
            ? status
            : ProductionOrderStatus.Downloaded;

    /// <summary>
    /// Parse JSON các trường mở rộng của PO; dữ liệu hỏng thì bỏ qua.
    /// </summary>
    private static Dictionary<string, string> ParseAdditional(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return [];

        try
        {
            return System.Text.Json.JsonSerializer.Deserialize(
                value,
                PoJsonContext.Default.DictionaryStringString) ?? [];
        }
        catch (System.Text.Json.JsonException)
        {
            return [];
        }
    }
}

/// <summary>
/// Thông tin một PO đọc được từ file DB local.
/// </summary>
public sealed class LocalPoInfo
{
    /// <summary>Mã PO.</summary>
    public string OrderNo { get; set; } = string.Empty;

    /// <summary>Số lượng kế hoạch của PO, không phải số mã thực nhận.</summary>
    public int OrderQty { get; set; }

    /// <summary>GTIN chuẩn 14 chữ số; rỗng nếu PO legacy không xác định được duy nhất.</summary>
    public string Gtin { get; set; } = string.Empty;

    /// <summary>Thời điểm tạo PO (từ server).</summary>
    public DateTime CreatedUtc { get; set; }

    /// <summary>Trạng thái PO.</summary>
    public ProductionOrderStatus Status { get; set; }

    /// <summary>Thời điểm bắt đầu sản xuất (null nếu chưa).</summary>
    public DateTime? StartedUtc { get; set; }

    /// <summary>Thời điểm hoàn tất (null nếu chưa).</summary>
    public DateTime? CompletedUtc { get; set; }

    /// <summary>Thời điểm huỷ (null nếu chưa).</summary>
    public DateTime? CancelledUtc { get; set; }

    /// <summary>Tổng số mã thực tế trong file DB.</summary>
    public int TotalCodes { get; set; }

    /// <summary>Số mã đã quét trong file DB.</summary>
    public int ScannedCodes { get; set; }

    /// <summary>Đường dẫn file DB của PO.</summary>
    public string DbPath { get; set; } = string.Empty;

    /// <summary>Các trường mở rộng của PO (tên sản phẩm, khách hàng...).</summary>
    public Dictionary<string, string> Additional { get; set; } = [];

    /// <summary>True khi PO đã bị huỷ (không được chạy lại).</summary>
    public bool IsCancelled => Status == ProductionOrderStatus.Cancelled;

    /// <summary>PO đã kết thúc, không nhận bổ sung mã hoặc chạy lại.</summary>
    public bool IsFinished => Status is ProductionOrderStatus.Cancelled
        or ProductionOrderStatus.Completed or ProductionOrderStatus.Closed
        || CompletedUtc.HasValue || CancelledUtc.HasValue;

    /// <summary>PO còn đang chạy hoặc đã bắt đầu nhưng chưa kết thúc.</summary>
    public bool IsUnfinished => !IsFinished && (Status == ProductionOrderStatus.Running
        || StartedUtc.HasValue || ScannedCodes > 0);
}

/// <summary>
/// Kết quả tìm mã trùng giữa PO mới và các PO đã có trên máy.
/// </summary>
public sealed class DuplicateCodeResult
{
    /// <summary>Tổng số mã đã tồn tại trong PO khác.</summary>
    public int TotalDuplicates { get; set; }

    /// <summary>
    /// Vài mã trùng ví dụ (khoá chuẩn <c>"{Gtin}|{Serial}"</c>) để hiển thị
    /// trong thông báo lỗi.
    /// </summary>
    public IReadOnlyList<string> SampleCodes { get; set; } = [];

    /// <summary>Tên PO đang chứa các mã trùng (rỗng nếu chưa xác định).</summary>
    public string ExistingOrderNo { get; set; } = string.Empty;

    /// <summary>Các khóa chuẩn trùng dạng GTIN|Serial.</summary>
    public IReadOnlySet<string> DuplicateKeys { get; set; } = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>True nếu có ít nhất một mã trùng.</summary>
    public bool HasDuplicates => TotalDuplicates > 0;
}
