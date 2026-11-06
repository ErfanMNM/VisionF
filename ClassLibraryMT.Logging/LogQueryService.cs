using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ClassLibraryMT.Logging;

/// <summary>
/// Cài đặt <see cref="ILogQueryService"/> đọc trực tiếp từ SQLite.
///
/// Mỗi lần truy vấn mở connection riêng (WAL cho phép đọc song song với
/// worker đang ghi) nên không cần lock với writer.
/// </summary>
internal sealed class LogQueryService : ILogQueryService
{
    private readonly SqliteLoggingOptions _opt;
    private readonly ILogger<LogQueryService> _logger;

    public LogQueryService(IOptions<SqliteLoggingOptions> options, ILogger<LogQueryService> logger)
    {
        _opt = options.Value;
        _logger = logger;
    }

    private string DbPath => _opt.ResolveFullPath();

    public async Task<PagedResult<UserActionEntry>> GetUserActionsAsync(
        UserActionFilter? filter = null,
        int page = 1,
        int pageSize = 200,
        CancellationToken cancellationToken = default)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 5000);

        if (!File.Exists(DbPath))
        {
            return new PagedResult<UserActionEntry> { Page = page, PageSize = pageSize };
        }

        var where = new StringBuilder("WHERE 1=1");
        var parameters = new List<(string Name, object Value)>();

        if (filter is not null)
        {
            if (filter.FromUtc.HasValue)
            {
                where.Append(" AND TimestampUtc >= $from");
                parameters.Add(("$from", filter.FromUtc.Value.ToString("O", CultureInfo.InvariantCulture)));
            }

            if (filter.ToUtc.HasValue)
            {
                where.Append(" AND TimestampUtc <= $to");
                parameters.Add(("$to", filter.ToUtc.Value.ToString("O", CultureInfo.InvariantCulture)));
            }

            if (!string.IsNullOrWhiteSpace(filter.ActionContains))
            {
                where.Append(" AND Action LIKE $action");
                parameters.Add(("$action", $"%{filter.ActionContains}%"));
            }

            if (!string.IsNullOrWhiteSpace(filter.Result))
            {
                where.Append(" AND Result = $result");
                parameters.Add(("$result", filter.Result));
            }
        }

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);

        // Tổng số bản ghi khớp bộ lọc.
        long total;
        await using (var countCmd = connection.CreateCommand())
        {
            countCmd.CommandText = $"SELECT COUNT(*) FROM UserActions {where};";
            AddParameters(countCmd, parameters);
            total = Convert.ToInt64(
                await countCmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
                CultureInfo.InvariantCulture);
        }

        var items = new List<UserActionEntry>();
        await using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = $"""
                SELECT Id, TimestampUtc, Action, Target, DetailJson, Result
                FROM UserActions
                {where}
                ORDER BY TimestampUtc DESC, Id DESC
                LIMIT $limit OFFSET $offset;
                """;
            AddParameters(cmd, parameters);
            cmd.Parameters.AddWithValue("$limit", pageSize);
            cmd.Parameters.AddWithValue("$offset", (page - 1) * pageSize);

            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                items.Add(new UserActionEntry
                {
                    Id = reader.GetInt64(0),
                    TimestampUtc = ParseUtc(reader.GetString(1)),
                    Action = reader.GetString(2),
                    Target = reader.IsDBNull(3) ? string.Empty : reader.GetString(3),
                    DetailJson = reader.IsDBNull(4) ? null : reader.GetString(4),
                    Result = reader.IsDBNull(5) ? string.Empty : reader.GetString(5),
                });
            }
        }

        return new PagedResult<UserActionEntry>
        {
            Items = items,
            TotalCount = total,
            Page = page,
            PageSize = pageSize,
        };
    }

    public async Task<PagedResult<AppLogEntry>> GetAppLogsAsync(
        AppLogFilter? filter = null,
        int page = 1,
        int pageSize = 200,
        CancellationToken cancellationToken = default)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 5000);

        if (!File.Exists(DbPath))
        {
            return new PagedResult<AppLogEntry> { Page = page, PageSize = pageSize };
        }

        var where = new StringBuilder("WHERE 1=1");
        var parameters = new List<(string Name, object Value)>();

        if (filter is not null)
        {
            if (filter.FromUtc.HasValue)
            {
                where.Append(" AND TimestampUtc >= $from");
                parameters.Add(("$from", filter.FromUtc.Value.ToString("O", CultureInfo.InvariantCulture)));
            }

            if (filter.ToUtc.HasValue)
            {
                where.Append(" AND TimestampUtc <= $to");
                parameters.Add(("$to", filter.ToUtc.Value.ToString("O", CultureInfo.InvariantCulture)));
            }

            if (filter.MinLevel.HasValue)
            {
                // Muc log luu dang chuoi; so sanh theo thu tu uy tin.
                // SQLite khong ho tro "IN ($param)" voi mot tham so duy nhat,
                // nen phai tao mot tham so rieng cho tung muc.
                var levels = LevelsAtOrAbove(filter.MinLevel.Value);
                var names = new List<string>(levels.Length);

                for (var i = 0; i < levels.Length; i++)
                {
                    var name = $"$level{i}";
                    names.Add(name);
                    parameters.Add((name, levels[i]));
                }

                where.Append(" AND Level IN (").Append(string.Join(", ", names)).Append(')');
            }

            if (!string.IsNullOrWhiteSpace(filter.CategoryContains))
            {
                where.Append(" AND Category LIKE $category");
                parameters.Add(("$category", $"%{filter.CategoryContains}%"));
            }

            if (!string.IsNullOrWhiteSpace(filter.MessageContains))
            {
                where.Append(" AND Message LIKE $message");
                parameters.Add(("$message", $"%{filter.MessageContains}%"));
            }
        }

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);

        long total;
        await using (var countCmd = connection.CreateCommand())
        {
            countCmd.CommandText = $"SELECT COUNT(*) FROM AppLogs {where};";
            AddParameters(countCmd, parameters);
            total = Convert.ToInt64(
                await countCmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
                CultureInfo.InvariantCulture);
        }

        var items = new List<AppLogEntry>();
        await using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = $"""
                SELECT Id, TimestampUtc, Level, Category, EventId, Message, Exception, PropertiesJson
                FROM AppLogs
                {where}
                ORDER BY TimestampUtc DESC, Id DESC
                LIMIT $limit OFFSET $offset;
                """;
            AddParameters(cmd, parameters);
            cmd.Parameters.AddWithValue("$limit", pageSize);
            cmd.Parameters.AddWithValue("$offset", (page - 1) * pageSize);

            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                items.Add(new AppLogEntry
                {
                    Id = reader.GetInt64(0),
                    TimestampUtc = ParseUtc(reader.GetString(1)),
                    Level = reader.GetString(2),
                    Category = reader.IsDBNull(3) ? string.Empty : reader.GetString(3),
                    EventId = reader.GetInt32(4),
                    Message = reader.IsDBNull(5) ? string.Empty : reader.GetString(5),
                    Exception = reader.IsDBNull(6) ? null : reader.GetString(6),
                    PropertiesJson = reader.IsDBNull(7) ? null : reader.GetString(7),
                });
            }
        }

        return new PagedResult<AppLogEntry>
        {
            Items = items,
            TotalCount = total,
            Page = page,
            PageSize = pageSize,
        };
    }

    public async Task<Dictionary<string, long>> GetUserActionCountsAsync(CancellationToken cancellationToken = default)
    {
        var counts = new Dictionary<string, long>(StringComparer.Ordinal);

        if (!File.Exists(DbPath))
        {
            return counts;
        }

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT Action, COUNT(*) FROM UserActions GROUP BY Action ORDER BY COUNT(*) DESC;";

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            counts[reader.GetString(0)] = reader.GetInt64(1);
        }

        return counts;
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken ct)
    {
        var connection = new SqliteConnection($"Data Source={DbPath}");
        await connection.OpenAsync(ct).ConfigureAwait(false);

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "PRAGMA busy_timeout=5000;";
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

        return connection;
    }

    private static void AddParameters(SqliteCommand cmd, List<(string Name, object Value)> parameters)
    {
        foreach (var (name, value) in parameters)
        {
            cmd.Parameters.AddWithValue(name, value);
        }
    }

    private static DateTime ParseUtc(string value) =>
        DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)
            ? parsed
            : DateTime.MinValue;

    /// <summary>
    /// Danh sách mức log từ mức cho trở lên, dùng cho điều kiện <c>IN</c>.
    /// </summary>
    private static string[] LevelsAtOrAbove(LogLevel minLevel) => minLevel switch
    {
        LogLevel.Trace => ["Trace", "Debug", "Information", "Warning", "Error", "Critical"],
        LogLevel.Debug => ["Debug", "Information", "Warning", "Error", "Critical"],
        LogLevel.Information => ["Information", "Warning", "Error", "Critical"],
        LogLevel.Warning => ["Warning", "Error", "Critical"],
        LogLevel.Error => ["Error", "Critical"],
        LogLevel.Critical => ["Critical"],
        _ => ["Trace", "Debug", "Information", "Warning", "Error", "Critical"],
    };
}
