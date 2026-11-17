using System.Data;
using System.Globalization;
using Microsoft.Data.Sqlite;

namespace ClassLibraryMT.Scans;

/// <summary>
/// Cài đặt <see cref="IScanSendStatusStore"/> trên nền <see cref="ScanStore"/>.
///
/// <para>
/// Dùng chung file DB với bảng ghi quét mã (cùng <c>logs/scans.db</c>) để
/// trạng thái gửi Checkee nằm sát bản ghi gốc — không cần DB riêng, không cần
/// đồng bộ.
/// </para>
/// </summary>
internal sealed class ScanSendStatusStore : IScanSendStatusStore
{
    private readonly ScanStore _store;

    public ScanSendStatusStore(ScanStore store)
    {
        _store = store;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<ScanRecordEntity>> GetPendingAsync(
        bool onlyPass = true,
        int limit = 100,
        CancellationToken cancellationToken = default)
    {
        limit = Math.Clamp(limit, 1, 5000);

        await using var connection = await _store
            .OpenAsync(cancellationToken)
            .ConfigureAwait(false);

        // SendStatus rỗng = bản ghi tạo trước khi có tính năng Checkee ->
        // coi như chưa gửi (Pending).
        var where = "WHERE (SendStatus = '' OR SendStatus IS NULL OR SendStatus = $pending)";
        var parameters = new List<(string Name, object Value)>
        {
            ("$pending", ScanSendStatuses.Pending),
        };

        if (onlyPass)
        {
            where += " AND Status = $pass";
            parameters.Add(("$pass", ScanStatuses.Pass));
        }

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"""
            SELECT Id, ReceivedUtc, Code, Gtin, Serial, Status,
                   ProcessingTimeMs, PlcResult, BatchNo, Raw, OrderNo,
                   SendStatus, SendStatusCode, SendMessage, SendAttempts,
                   SendTimeUtc, SendAttemptUtc
            FROM ScanRecords
            {where}
            ORDER BY ReceivedUtc ASC, Id ASC
            LIMIT $limit;
            """;
        AddParameters(cmd, parameters);
        cmd.Parameters.AddWithValue("$limit", limit);

        var items = new List<ScanRecordEntity>();
        await using var reader = await cmd
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            items.Add(MapRecord(reader));
        }

        return items;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<ScanRecordEntity>> GetBySendStatusAsync(
        ScanSendFilter? filter = null,
        CancellationToken cancellationToken = default)
    {
        var where = "WHERE 1=1";
        var parameters = new List<(string Name, object Value)>();

        if (filter is not null)
        {
            if (!string.IsNullOrWhiteSpace(filter.SendStatus))
            {
                where += " AND SendStatus = $sendStatus";
                parameters.Add(("$sendStatus", filter.SendStatus));
            }

            if (!string.IsNullOrWhiteSpace(filter.CodeContains))
            {
                where += " AND (Code LIKE $code OR Serial LIKE $code OR Raw LIKE $code)";
                parameters.Add(("$code", $"%{filter.CodeContains}%"));
            }

            if (filter.OnlyPass == true)
            {
                where += " AND Status = $pass";
                parameters.Add(("$pass", ScanStatuses.Pass));
            }
        }

        var limit = Math.Clamp(filter?.Limit ?? 200, 1, 5000);

        await using var connection = await _store
            .OpenAsync(cancellationToken)
            .ConfigureAwait(false);

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"""
            SELECT Id, ReceivedUtc, Code, Gtin, Serial, Status,
                   ProcessingTimeMs, PlcResult, BatchNo, Raw, OrderNo,
                   SendStatus, SendStatusCode, SendMessage, SendAttempts,
                   SendTimeUtc, SendAttemptUtc
            FROM ScanRecords
            {where}
            ORDER BY ReceivedUtc DESC, Id DESC
            LIMIT $limit;
            """;
        AddParameters(cmd, parameters);
        cmd.Parameters.AddWithValue("$limit", limit);

        var items = new List<ScanRecordEntity>();
        await using var reader = await cmd
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            items.Add(MapRecord(reader));
        }

        return items;
    }

    /// <inheritdoc/>
    public Task MarkSentAsync(
        long id,
        string message,
        CancellationToken cancellationToken = default) =>
        UpdateSendStateAsync(
            id,
            ScanSendStatuses.Sent,
            statusCode: 200,
            message,
            setSendTime: true,
            cancellationToken);

    /// <inheritdoc/>
    public Task MarkFailedAsync(
        long id,
        int statusCode,
        string message,
        CancellationToken cancellationToken = default) =>
        UpdateSendStateAsync(
            id,
            ScanSendStatuses.Failed,
            statusCode,
            message,
            setSendTime: false,
            cancellationToken);

    /// <inheritdoc/>
    public Task MarkPendingAsync(
        long id,
        string message,
        CancellationToken cancellationToken = default) =>
        UpdateSendStateAsync(
            id,
            ScanSendStatuses.Pending,
            statusCode: 0,
            message,
            setSendTime: false,
            cancellationToken);

    /// <summary>
    /// Cập nhật trạng thái gửi của một bản ghi.
    /// </summary>
    private async Task UpdateSendStateAsync(
        long id,
        string status,
        int statusCode,
        string message,
        bool setSendTime,
        CancellationToken ct)
    {
        await using var connection = await _store.OpenAsync(ct).ConfigureAwait(false);

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            UPDATE ScanRecords
            SET SendStatus = $status,
                SendStatusCode = $statusCode,
                SendMessage = $message,
                SendAttempts = SendAttempts + 1,
                SendAttemptUtc = $attemptUtc,
                SendTimeUtc = CASE WHEN $setSendTime = 1 THEN $attemptUtc ELSE SendTimeUtc END
            WHERE Id = $id;
            """;

        cmd.Parameters.AddWithValue("$status", status);
        cmd.Parameters.AddWithValue("$statusCode", statusCode);
        cmd.Parameters.AddWithValue("$message", message ?? string.Empty);
        cmd.Parameters.AddWithValue(
            "$attemptUtc",
            ScanStore.FormatUtc(DateTime.UtcNow));
        cmd.Parameters.AddWithValue("$setSendTime", setSendTime ? 1 : 0);
        cmd.Parameters.AddWithValue("$id", id);

        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<int> ResetToPendingAsync(
        IReadOnlyList<long> ids,
        CancellationToken cancellationToken = default)
    {
        if (ids is null || ids.Count == 0)
        {
            return 0;
        }

        await using var connection = await _store
            .OpenAsync(cancellationToken)
            .ConfigureAwait(false);

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            UPDATE ScanRecords
            SET SendStatus = $pending,
                SendStatusCode = 0,
                SendMessage = ''
            WHERE Id IN $ids;
            """;

        cmd.Parameters.AddWithValue("$pending", ScanSendStatuses.Pending);
        cmd.Parameters.AddWithValue("$ids", ids.ToArray());

        return await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<Dictionary<string, long>> CountBySendStatusAsync(
        CancellationToken cancellationToken = default)
    {
        var counts = new Dictionary<string, long>(StringComparer.Ordinal)
        {
            [ScanSendStatuses.Pending] = 0,
            [ScanSendStatuses.Sent] = 0,
            [ScanSendStatuses.Failed] = 0,
        };

        await using var connection = await _store
            .OpenAsync(cancellationToken)
            .ConfigureAwait(false);

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT SendStatus, COUNT(*) FROM ScanRecords GROUP BY SendStatus;
            """;

        await using var reader = await cmd
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var status = reader.IsDBNull(0)
                ? string.Empty
                : reader.GetString(0);

            var count = Convert.ToInt64(
                reader.GetValue(1), CultureInfo.InvariantCulture);

            // Bản ghi cũ (SendStatus rỗng) -> tính vào nhóm "chưa gửi".
            var key = string.IsNullOrWhiteSpace(status)
                ? ScanSendStatuses.Pending
                : ScanSendStatuses.Normalize(status);

            counts[key] = counts.TryGetValue(key, out var current)
                ? current + count
                : count;
        }

        return counts;
    }

    /// <summary>
    /// Thêm tham số vào command.
    /// </summary>
    private static void AddParameters(
        SqliteCommand cmd,
        List<(string Name, object Value)> parameters)
    {
        foreach (var (name, value) in parameters)
        {
            cmd.Parameters.AddWithValue(name, value);
        }
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
        SendStatus = record.IsDBNull(11) ? string.Empty : record.GetString(11),
        SendStatusCode = record.IsDBNull(12) ? 0 : record.GetInt32(12),
        SendMessage = record.IsDBNull(13) ? string.Empty : record.GetString(13),
        SendAttempts = record.IsDBNull(14) ? 0 : record.GetInt32(14),
        SendTimeUtc = record.IsDBNull(15) ? null : ParseUtc(record.GetString(15)),
        SendAttemptUtc = record.IsDBNull(16) ? null : ParseUtc(record.GetString(16)),
    };

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
}