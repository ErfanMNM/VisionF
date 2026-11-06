using System.Data;
using System.Globalization;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ClassLibraryMT.Logging;

/// <summary>
/// Worker nền ghi log vào SQLite.
///
/// Mọi lệnh ghi chỉ enqueue vào <see cref="Channel{T}"/> (không block UI thread).
/// Worker gom batch rồi insert trong một transaction.
///
/// Tính năng:
/// - WAL mode + busy_timeout để đọc không bị chặn bởi worker ghi.
/// - Flush theo thời gian (<see cref="SqliteLoggingOptions.FlushIntervalMs"/>) hoặc
///   khi batch đủ (<see cref="SqliteLoggingOptions.BatchSize"/>).
/// - Prune theo retention mỗi <see cref="SqliteLoggingOptions.PruneIntervalMs"/>.
/// - Khi <see cref="Dispose"/> thì flush toàn bộ log còn trong queue trước khi đóng.
/// </summary>
/// <remarks>
/// Được đăng ký singleton trong DI. Khi container dispose thì worker dừng và
/// flush nốt các bản ghi còn trong hàng đợi.
/// </remarks>
public sealed class SqliteLogWriter : IDisposable
{
    private readonly SqliteLoggingOptions _opt;
    private readonly Channel<object> _queue;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _worker;
    private readonly string _dbPath;
    private bool _disposed;

    /// <summary>Số bản ghi đã ghi thành công (dùng cho test/thống kê).</summary>
    private long _writtenCount;

    /// <summary>
    /// Khởi tạo worker ghi log.
    ///
    /// Cố ý KHÔNG nhận <c>ILogger</c>: writer nằm trong chuỗi
    /// <c>ILoggerProvider</c> nên phụ thuộc vào <c>ILogger</c> sẽ gây vòng
    /// phụ thuộc vòng tròn trong DI. Lỗi ghi ra stderr qua
    /// <see cref="SqliteDiagnostics"/>.
    /// </summary>
    /// <param name="options">Cấu hình ghi log.</param>
    public SqliteLogWriter(IOptions<SqliteLoggingOptions> options)
    {
        _opt = options.Value;
        _dbPath = _opt.ResolveFullPath();

        // Bounded channel: nếu ứng dụng log quá nhanh, hàng đợi tới giới hạn
        // thì bản ghi mới bị drop thay vì làm treo app.
        _queue = Channel.CreateBounded<object>(new BoundedChannelOptions(20_000)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
        });

        _worker = Task.Run(() => RunAsync(_cts.Token));
    }

    /// <summary>Đường dẫn file database đang dùng.</summary>
    public string DatabasePath => _dbPath;

    /// <summary>Số bản ghi đã ghi thành công.</summary>
    public long WrittenCount => Interlocked.Read(ref _writtenCount);

    /// <summary>Enqueue một bản ghi log kỹ thuật.</summary>
    public void Enqueue(AppLogEntry entry)
    {
        if (_disposed) return;
        _queue.Writer.TryWrite(entry);
    }

    /// <summary>Enqueue một thao tác người dùng.</summary>
    public void Enqueue(UserActionEntry entry)
    {
        if (_disposed) return;
        _queue.Writer.TryWrite(entry);
    }

    /// <summary>Chờ cho tới khi hàng đợi rỗng và dữ liệu đã nằm trong database.</summary>
    public async Task FlushAsync(CancellationToken cancellationToken = default)
    {
        if (_disposed) return;

        // Yêu cầu flush có phản hồi: worker chỉ hoàn thành yêu cầu sau khi đã
        // ghi xong batch vào database. Nếu chỉ chờ "channel rỗng" thì sẽ bị
        // race: worker đã gom bản ghi vào danh sách cục bộ nhưng chưa kịp insert.
        var request = new FlushRequest();

        await _queue.Writer.WriteAsync(request, cancellationToken).ConfigureAwait(false);
        await request.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task RunAsync(CancellationToken ct)
    {
        var pending = new List<object>(capacity: _opt.BatchSize);
        var lastFlush = Environment.TickCount64;
        var lastPrune = Environment.TickCount64;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                // Chờ có phần tử mới, nhưng không lâu hơn 1/4 chu kỳ flush
                // để batch luôn được đẩy xuống đúng nhịp.
                var waitMs = Math.Max(50, _opt.FlushIntervalMs / 4);
                using var waitCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                waitCts.CancelAfter(waitMs);

                bool hasItem;
                try
                {
                    hasItem = await _queue.Reader.WaitToReadAsync(waitCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    // Hết thời gian chờ -> vòng lặp tiếp theo kiểm tra nhịp flush.
                    // (Không phải lỗi: token bị hủy do CancelAfter, không phải do ct.)
                    hasItem = false;
                }

                if (hasItem)
                {
                    // Gom tối đa BatchSize phần tử đang có sẵn (không chặn).
                    while (pending.Count < _opt.BatchSize && _queue.Reader.TryRead(out var item))
                    {
                        if (item is FlushRequest request)
                        {
                            // Yêu cầu flush: ghi nốt phần đang dở rồi báo hoàn thành.
                            await FlushBatchAsync(pending, ct).ConfigureAwait(false);
                            request.Complete();
                            continue;
                        }

                        pending.Add(item);
                    }
                }

                var now = Environment.TickCount64;

                // Đủ batch -> flush ngay.
                if (pending.Count >= _opt.BatchSize)
                {
                    await FlushBatchAsync(pending, ct).ConfigureAwait(false);
                    lastFlush = now;
                }
                // Hoặc đã quá chu kỳ flush -> flush phần đang dở.
                else if (pending.Count > 0 && now - lastFlush >= _opt.FlushIntervalMs)
                {
                    await FlushBatchAsync(pending, ct).ConfigureAwait(false);
                    lastFlush = now;
                }

                // Chu kỳ prune: xoá bản ghi cũ hơn retention.
                if (_opt.RetentionDays > 0 && now - lastPrune >= _opt.PruneIntervalMs)
                {
                    await PruneAsync(ct).ConfigureAwait(false);
                    lastPrune = now;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // Worker không được chết vì lỗi ghi DB. Log ra stderr và tiếp tục.
                SqliteDiagnostics.Error("Loi trong worker ghi log SQLite.", ex);
                try
                {
                    await Task.Delay(500, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }

        // Tắt: flush nốt phần đang dở + hoàn thành mọi yêu cầu còn treo.
        try
        {
            while (_queue.Reader.TryRead(out var item))
            {
                if (item is FlushRequest request)
                {
                    request.Complete();
                    continue;
                }

                pending.Add(item);
            }

            if (pending.Count > 0)
            {
                await FlushBatchAsync(pending, CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            SqliteDiagnostics.Error("Khong flush duoc log con lai khi dong worker.", ex);
        }
    }

    private async Task FlushBatchAsync(List<object> pending, CancellationToken ct)
    {
        if (pending.Count == 0) return;

        // Tách thành 2 nhóm để insert theo đúng bảng.
        var appLogs = new List<AppLogEntry>(pending.Count);
        var userActions = new List<UserActionEntry>(pending.Count);

        foreach (var item in pending)
        {
            switch (item)
            {
                case AppLogEntry log:
                    appLogs.Add(log);
                    break;
                case UserActionEntry action:
                    userActions.Add(action);
                    break;
            }
        }

        pending.Clear();

        try
        {
            await using var connection = await OpenConnectionAsync(ct).ConfigureAwait(false);
            await using var transaction = (SqliteTransaction)await connection
                .BeginTransactionAsync(ct).ConfigureAwait(false);

            if (appLogs.Count > 0)
            {
                await InsertAppLogsAsync(connection, transaction, appLogs, ct).ConfigureAwait(false);
            }

            if (userActions.Count > 0)
            {
                await InsertUserActionsAsync(connection, transaction, userActions, ct).ConfigureAwait(false);
            }

            await transaction.CommitAsync(ct).ConfigureAwait(false);

            Interlocked.Add(ref _writtenCount, appLogs.Count + userActions.Count);
        }
        catch (Exception ex)
        {
            // Mất batch này cũng được: log kỹ thuật không quan trọng bằng việc app chạy ổn.
            SqliteDiagnostics.Error(
                $"Ghi batch log SQLite that bai ({appLogs.Count + userActions.Count} ban ghi).", ex);
        }
    }

    private async Task<SqliteConnection> OpenConnectionAsync(CancellationToken ct)
    {
        var directory = Path.GetDirectoryName(_dbPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var connection = new SqliteConnection($"Data Source={_dbPath}");
        await connection.OpenAsync(ct).ConfigureAwait(false);

        // WAL: cho phép đọc song song với ghi.
        await ExecutePragmaAsync(connection, "PRAGMA journal_mode=WAL;", ct).ConfigureAwait(false);
        await ExecutePragmaAsync(connection, "PRAGMA busy_timeout=5000;", ct).ConfigureAwait(false);
        await ExecutePragmaAsync(connection, "PRAGMA synchronous=NORMAL;", ct).ConfigureAwait(false);
        await ExecutePragmaAsync(connection, "PRAGMA foreign_keys=ON;", ct).ConfigureAwait(false);

        await EnsureSchemaAsync(connection, ct).ConfigureAwait(false);

        return connection;
    }

    private static async Task ExecutePragmaAsync(SqliteConnection connection, string sql, CancellationToken ct)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Tạo bảng và index nếu chưa có. Chạy mỗi lần mở connection (cheap vì
    /// dùng <c>IF NOT EXISTS</c>).
    /// </summary>
    private static async Task EnsureSchemaAsync(SqliteConnection connection, CancellationToken ct)
    {
        const string schema = """
            CREATE TABLE IF NOT EXISTS SchemaVersion (
                Version     INTEGER NOT NULL PRIMARY KEY,
                AppliedUtc  TEXT    NOT NULL
            );

            CREATE TABLE IF NOT EXISTS AppLogs (
                Id             INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                TimestampUtc   TEXT    NOT NULL,
                Level          TEXT    NOT NULL,
                Category       TEXT    NOT NULL DEFAULT '',
                EventId        INTEGER NOT NULL DEFAULT 0,
                Message        TEXT    NOT NULL DEFAULT '',
                Exception      TEXT    NULL,
                PropertiesJson TEXT    NULL
            );

            CREATE TABLE IF NOT EXISTS UserActions (
                Id           INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                TimestampUtc TEXT    NOT NULL,
                Action       TEXT    NOT NULL,
                Target       TEXT    NOT NULL DEFAULT '',
                DetailJson   TEXT    NULL,
                Result       TEXT    NOT NULL DEFAULT '',
                Username     TEXT    NOT NULL DEFAULT '',
                UserId       INTEGER NULL
            );

            CREATE INDEX IF NOT EXISTS IX_UserActions_TimestampUtc ON UserActions (TimestampUtc DESC);
            CREATE INDEX IF NOT EXISTS IX_UserActions_Action       ON UserActions (Action);
            CREATE INDEX IF NOT EXISTS IX_AppLogs_TimestampUtc     ON AppLogs (TimestampUtc DESC);
            CREATE INDEX IF NOT EXISTS IX_AppLogs_Level            ON AppLogs (Level);
            """;

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = schema;
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

        // Migration: file app.db đã tồn tại từ trước khi thêm cột Username /
        // UserId -> ALTER TABLE thêm vào, KHÔNG xoá DB (để giữ lịch sử).
        // Phải chạy TRƯỚC khi tạo index trên cột mới.
        await EnsureUserActionColumnsAsync(connection, ct).ConfigureAwait(false);

        // Index trên cột Username (tạo sau migration để chắc chắn cột tồn tại).
        await using (var indexCmd = connection.CreateCommand())
        {
            indexCmd.CommandText =
                "CREATE INDEX IF NOT EXISTS IX_UserActions_Username ON UserActions (Username);";
            await indexCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        // Ghi version hiện tại nếu chưa có.
        await using var versionCmd = connection.CreateCommand();
        versionCmd.CommandText = """
            INSERT OR IGNORE INTO SchemaVersion (Version, AppliedUtc)
            VALUES (2, $appliedUtc);
            """;
        versionCmd.Parameters.AddWithValue("$appliedUtc", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        await versionCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Thêm cột mới vào bảng <c>UserActions</c> nếu bảng cũ chưa có.
    /// </summary>
    private static async Task EnsureUserActionColumnsAsync(
        SqliteConnection connection, CancellationToken ct)
    {
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        await using (var pragma = connection.CreateCommand())
        {
            pragma.CommandText = "PRAGMA table_info(UserActions);";

            await using var reader = await pragma.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                // PRAGMA table_info: cid | name | type | notnull | dflt_value | pk
                columns.Add(reader.GetString(1));
            }
        }

        if (!columns.Contains("Username"))
        {
            await using var alter = connection.CreateCommand();
            alter.CommandText = "ALTER TABLE UserActions ADD COLUMN Username TEXT NOT NULL DEFAULT '';";
            await alter.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        if (!columns.Contains("UserId"))
        {
            await using var alter = connection.CreateCommand();
            alter.CommandText = "ALTER TABLE UserActions ADD COLUMN UserId INTEGER NULL;";
            await alter.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
    }

    private static async Task InsertAppLogsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        List<AppLogEntry> entries,
        CancellationToken ct)
    {
        await using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = """
            INSERT INTO AppLogs (TimestampUtc, Level, Category, EventId, Message, Exception, PropertiesJson)
            VALUES ($ts, $level, $category, $eventId, $message, $exception, $props);
            """;

        var pTs = cmd.Parameters.Add("$ts", SqliteType.Text);
        var pLevel = cmd.Parameters.Add("$level", SqliteType.Text);
        var pCategory = cmd.Parameters.Add("$category", SqliteType.Text);
        var pEventId = cmd.Parameters.Add("$eventId", SqliteType.Integer);
        var pMessage = cmd.Parameters.Add("$message", SqliteType.Text);
        var pException = cmd.Parameters.Add("$exception", SqliteType.Text);
        var pProps = cmd.Parameters.Add("$props", SqliteType.Text);

        foreach (var e in entries)
        {
            pTs.Value = e.TimestampUtc.ToString("O", CultureInfo.InvariantCulture);
            pLevel.Value = e.Level;
            pCategory.Value = e.Category;
            pEventId.Value = e.EventId;
            pMessage.Value = e.Message;
            pException.Value = (object?)e.Exception ?? DBNull.Value;
            pProps.Value = (object?)e.PropertiesJson ?? DBNull.Value;

            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
    }

    private static async Task InsertUserActionsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        List<UserActionEntry> entries,
        CancellationToken ct)
    {
        await using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = """
            INSERT INTO UserActions (TimestampUtc, Action, Target, DetailJson, Result, Username, UserId)
            VALUES ($ts, $action, $target, $detail, $result, $username, $userId);
            """;

        var pTs = cmd.Parameters.Add("$ts", SqliteType.Text);
        var pAction = cmd.Parameters.Add("$action", SqliteType.Text);
        var pTarget = cmd.Parameters.Add("$target", SqliteType.Text);
        var pDetail = cmd.Parameters.Add("$detail", SqliteType.Text);
        var pResult = cmd.Parameters.Add("$result", SqliteType.Text);
        var pUsername = cmd.Parameters.Add("$username", SqliteType.Text);
        var pUserId = cmd.Parameters.Add("$userId", SqliteType.Integer);

        foreach (var e in entries)
        {
            pTs.Value = e.TimestampUtc.ToString("O", CultureInfo.InvariantCulture);
            pAction.Value = e.Action;
            pTarget.Value = e.Target;
            pDetail.Value = (object?)e.DetailJson ?? DBNull.Value;
            pResult.Value = e.Result;
            pUsername.Value = e.Username;
            pUserId.Value = (object?)e.UserId ?? DBNull.Value;

            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Xoá các bản ghi cũ hơn thời hạn retention.
    /// </summary>
    private async Task PruneAsync(CancellationToken ct)
    {
        try
        {
            var cutoff = DateTime.UtcNow.AddDays(-_opt.RetentionDays);

            await using var connection = await OpenConnectionAsync(ct).ConfigureAwait(false);

            // Mỗi bảng một lệnh riêng: nếu gộp chung, lỗi ở bảng đầu sẽ làm
            // cả lệnh bị huỷ và bảng sau không được dọn.
            var removed = 0;

            foreach (var table in new[] { "AppLogs", "UserActions" })
            {
                await using var cmd = connection.CreateCommand();
                cmd.CommandText = $"DELETE FROM {table} WHERE TimestampUtc < $cutoff;";
                cmd.Parameters.AddWithValue(
                    "$cutoff", cutoff.ToString("O", CultureInfo.InvariantCulture));

                removed += await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            if (removed > 0)
            {
                Console.WriteLine(
                    $"[ClassLibraryMT.Logging] Da don {removed} ban ghi log cu hon {_opt.RetentionDays} ngay.");
            }
        }
        catch (Exception ex)
        {
            SqliteDiagnostics.Error("Khong prune duoc log cu.", ex);
        }
    }

    /// <summary>
    /// Dừng worker và flush toàn bộ log còn trong hàng đợi.
    ///
    /// Idempotent — có thể được gọi nhiều lần (DI container + thao tác tắt app).
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try
        {
            // Đóng channel để worker biết hết việc -> flush nốt rồi thoát.
            _queue.Writer.TryComplete();
            _worker.Wait(TimeSpan.FromSeconds(5));
        }
        catch (Exception ex)
        {
            SqliteDiagnostics.Error("Loi khi dong worker ghi log SQLite.", ex);
        }
        finally
        {
            // Giải phóng mọi yêu cầu flush còn treo để không ai chờ mãi.
            while (_queue.Reader.TryRead(out var item))
            {
                if (item is FlushRequest request)
                {
                    request.Complete();
                }
            }

            _cts.Cancel();
            _cts.Dispose();
        }
    }

    /// <summary>
    /// Yêu cầu flush kèm phản hồi: worker chỉ gọi <see cref="Complete"/> sau khi
    /// đã ghi xong batch vào database, nên <c>FlushAsync</c> chỉ trả về khi dữ
    /// liệu thực sự nằm trong file.
    /// </summary>
    private sealed class FlushRequest
    {
        private readonly TaskCompletionSource _tcs =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Chờ worker hoàn tất việc ghi.</summary>
        public Task WaitAsync(CancellationToken cancellationToken) =>
            _tcs.Task.WaitAsync(cancellationToken);

        /// <summary>Báo cho bên chờ biết đã ghi xong.</summary>
        public void Complete() => _tcs.TrySetResult();
    }
}
