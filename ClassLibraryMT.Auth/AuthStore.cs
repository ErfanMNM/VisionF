using System.Data;
using System.Globalization;
using Microsoft.Data.Sqlite;

namespace ClassLibraryMT.Auth;

/// <summary>
/// Lớp lưu trữ tài khoản vào SQLite.
///
/// Chịu trách nhiệm:
/// - Tạo schema (bảng <c>Users</c>, <c>SchemaVersion</c>) nếu chưa có.
/// - Migration: thêm cột mới vào bảng đã tồn tại (kiểm tra <c>PRAGMA table_info</c>).
/// - Thiết lập PRAGMA (WAL, busy_timeout...).
///
/// Mỗi thao tác mở connection riêng (SQLite nhẹ, không cần connection pool phức tạp).
/// </summary>
internal sealed class AuthStore
{
    /// <summary>Phiên bản schema hiện tại.</summary>
    public const int CurrentSchemaVersion = 1;

    private readonly string _dbPath;

    /// <summary>
    /// Khởi tạo lớp lưu trữ.
    /// </summary>
    /// <param name="dbPath">Đường dẫn tuyệt đối tới file database.</param>
    public AuthStore(string dbPath)
    {
        _dbPath = dbPath;
    }

    /// <summary>Đường dẫn file database đang dùng.</summary>
    public string DatabasePath => _dbPath;

    /// <summary>
    /// Mở connection đã thiết lập PRAGMA và đảm bảo schema tồn tại.
    /// </summary>
    /// <param name="ct">Token hủy.</param>
    /// <returns>Connection sẵn sàng dùng (caller dispose).</returns>
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

            CREATE TABLE IF NOT EXISTS Users (
                Id                 INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                Username           TEXT    NOT NULL UNIQUE,
                DisplayName        TEXT    NOT NULL DEFAULT '',
                PasswordHash       TEXT    NOT NULL,
                Role               INTEGER NOT NULL DEFAULT 0,
                IsActive           INTEGER NOT NULL DEFAULT 1,
                MustChangePassword INTEGER NOT NULL DEFAULT 0,
                FailedAttempts     INTEGER NOT NULL DEFAULT 0,
                LockedUntilUtc     TEXT    NULL,
                LastLoginUtc       TEXT    NULL,
                CreatedUtc         TEXT    NOT NULL,
                UpdatedUtc         TEXT    NOT NULL
            );

            CREATE INDEX IF NOT EXISTS IX_Users_Role ON Users (Role);
            """;

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = schema;
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

        // Migration: bảng Users đã tồn tại từ phiên bản cũ có thể thiếu cột.
        // Kiểm tra PRAGMA table_info rồi ALTER cho từng cột thiếu.
        var existing = await GetColumnNamesAsync(connection, "Users", ct).ConfigureAwait(false);

        var migrations = new (string Column, string Definition)[]
        {
            ("DisplayName", "TEXT NOT NULL DEFAULT ''"),
            ("MustChangePassword", "INTEGER NOT NULL DEFAULT 0"),
            ("FailedAttempts", "INTEGER NOT NULL DEFAULT 0"),
            ("LockedUntilUtc", "TEXT NULL"),
            ("LastLoginUtc", "TEXT NULL"),
            ("UpdatedUtc", "TEXT NOT NULL DEFAULT ''"),
        };

        foreach (var (column, definition) in migrations)
        {
            if (existing.Contains(column))
            {
                continue;
            }

            await using var alter = connection.CreateCommand();
            alter.CommandText = $"ALTER TABLE Users ADD COLUMN {column} {definition};";
            await alter.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
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
    /// Lấy danh sách tên cột của một bảng (dùng cho migration).
    /// </summary>
    private static async Task<HashSet<string>> GetColumnNamesAsync(
        SqliteConnection connection, string table, CancellationToken ct)
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
        SqliteConnection connection, string sql, CancellationToken ct)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Đọc một tài khoản theo điều kiện (dùng chung cho id / username).
    /// </summary>
    /// <param name="connection">Connection.</param>
    /// <param name="whereClause">Mệnh đề WHERE (không kèm từ khoá WHERE).</param>
    /// <param name="parameter">Tham số cần bind.</param>
    /// <param name="value">Giá trị tham số.</param>
    /// <param name="ct">Token hủy.</param>
    /// <returns>Tài khoản hoặc null.</returns>
    public static async Task<UserAccount?> ReadUserAsync(
        SqliteConnection connection,
        string whereClause,
        string parameter,
        object value,
        CancellationToken ct)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"""
            SELECT Id, Username, DisplayName, PasswordHash, Role, IsActive,
                   MustChangePassword, FailedAttempts, LockedUntilUtc, LastLoginUtc,
                   CreatedUtc, UpdatedUtc
            FROM Users
            WHERE {whereClause}
            LIMIT 1;
            """;
        cmd.Parameters.AddWithValue(parameter, value);

        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            return null;
        }

        return MapUser(reader);
    }

    /// <summary>
    /// Ánh xạ dòng dữ liệu hiện tại thành đối tượng <see cref="UserAccount"/>.
    /// </summary>
    public static UserAccount MapUser(SqliteDataReader reader)
    {
        var lockedUntilRaw = reader.IsDBNull(8) ? null : reader.GetString(8);
        var lastLoginRaw = reader.IsDBNull(9) ? null : reader.GetString(9);
        var lockedUntil = ParseUtc(lockedUntilRaw);

        return new UserAccount
        {
            Id = reader.GetInt64(0),
            Username = reader.GetString(1),
            DisplayName = reader.GetString(2),
            RoleValue = reader.GetInt32(4),
            IsActive = reader.GetInt32(5) != 0,
            MustChangePassword = reader.GetInt32(6) != 0,
            FailedAttempts = reader.GetInt32(7),
            LockedUntilUtc = lockedUntil,

            // Đang bị khoá = còn thời điểm hết hạn khoá trong tương lai.
            IsLocked = lockedUntil is { } until && until > DateTime.UtcNow,
            LastLoginUtc = ParseUtc(lastLoginRaw),
            CreatedUtc = ParseUtc(reader.GetString(10)) ?? DateTime.UtcNow,
            UpdatedUtc = ParseUtc(reader.GetString(11)) ?? DateTime.UtcNow,
        };
    }

    /// <summary>
    /// Parse chuỗi thời gian UTC theo định dạng "O".
    /// </summary>
    /// <param name="text">Chuỗi cần parse.</param>
    /// <returns>Thời điểm hoặc null nếu không hợp lệ.</returns>
    public static DateTime? ParseUtc(string? text) =>
        DateTime.TryParse(
            text,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
            out var parsed)
            ? DateTime.SpecifyKind(parsed, DateTimeKind.Utc)
            : null;

    /// <summary>
    /// Định dạng thời điểm UTC theo chuẩn "O" để lưu vào database.
    /// </summary>
    /// <param name="value">Thời điểm.</param>
    /// <returns>Chuỗi ISO-8601.</returns>
    public static string FormatUtc(DateTime value) =>
        DateTime.SpecifyKind(value, DateTimeKind.Utc).ToString("O", CultureInfo.InvariantCulture);
}
