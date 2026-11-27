using Microsoft.Data.Sqlite;

namespace ClassLibraryMT.Scans;

internal sealed partial class ScanSendStatusStore
{
    public async Task<long> BeginSendAttemptAsync(IReadOnlyList<ScanRecordEntity> records,
        string productGtin, string batchCode, string serialType, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(records);
        if (records.Count == 0) throw new ArgumentException("Lần gửi phải có ít nhất một mã.", nameof(records));
        await using var connection = await _store.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        // Recheck readiness inside the same write transaction as the HTTP
        // intent. A stale/manual selection must not bypass the PO outbox gate.
        foreach (var record in records)
        {
            // ScanRecords giữ khóa so khớp nội bộ; lịch sử lưu GTIN nguyên bản thực gửi.
            if (record.Gtin.Length is not (8 or 12 or 13 or 14)
                || productGtin.Length is not (8 or 12 or 13 or 14)
                || record.Gtin.Any(c => c is < '0' or > '9') || productGtin.Any(c => c is < '0' or > '9')
                || !string.Equals(record.Gtin.TrimStart('0'), productGtin.TrimStart('0'), StringComparison.Ordinal))
                throw new InvalidOperationException("GTIN bản ghi không khớp GTIN của lần gửi.");
            await using var ready = connection.CreateCommand();
            ready.Transaction = transaction;
            ready.CommandText = """
                SELECT COUNT(*) FROM ScanRecords
                WHERE Id = $id AND PersistenceReady = 1
                    AND PoAccepted=1 AND Status='Pass'
                    AND OrderNo=$order COLLATE BINARY AND OrderCreatedUtc=$created
                    AND Gtin=$gtin COLLATE BINARY
                    AND (SendStatus='' OR SendStatus IS NULL OR SendStatus='Pending')
                    AND (CASE WHEN Raw <> '' THEN Raw ELSE Code END) = $code COLLATE BINARY;
                """;
            ready.Parameters.AddWithValue("$id", record.Id);
            ready.Parameters.AddWithValue("$code", record.CodeContent);
            ready.Parameters.AddWithValue("$order", record.OrderNo);
            ready.Parameters.AddWithValue("$created", record.OrderCreatedUtc is { } created
                ? ScanStore.FormatUtc(created) : DBNull.Value);
            ready.Parameters.AddWithValue("$gtin", record.Gtin);
            if (Convert.ToInt64(await ready.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)) != 1)
                throw new InvalidOperationException("Bản ghi chưa đồng bộ PO hoặc không còn khớp; không được gửi Checkee.");
        }
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO CheckeeSendAttempts (StartedUtc, ProductGtin, BatchCode, SerialType)
            VALUES ($utc, $gtin, $batch, $type);
            SELECT last_insert_rowid();
            """;
        command.Parameters.AddWithValue("$utc", ScanStore.FormatUtc(DateTime.UtcNow));
        command.Parameters.AddWithValue("$gtin", productGtin);
        command.Parameters.AddWithValue("$batch", batchCode);
        command.Parameters.AddWithValue("$type", serialType);
        var id = (long)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
        var position = 0;
        foreach (var record in records)
        {
            await using var item = connection.CreateCommand();
            item.Transaction = transaction;
            item.CommandText = """
                INSERT INTO CheckeeSendAttemptItems (AttemptId, Position, ScanRecordId, CodeContent, OrderNo)
                VALUES ($attempt, $position, $record, $code, $order);
                """;
            item.Parameters.AddWithValue("$attempt", id);
            item.Parameters.AddWithValue("$position", position++);
            item.Parameters.AddWithValue("$record", record.Id);
            item.Parameters.AddWithValue("$code", record.CodeContent);
            item.Parameters.AddWithValue("$order", record.OrderNo ?? string.Empty);
            await item.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        // Một process có thể chết sau khi HTTP đã tới server nhưng trước ACK.
        // Đóng tự retry ngay trong intent transaction; Complete sẽ đặt trạng
        // thái cuối (hoặc Pending nếu xác nhận lỗi transport tạm thời).
        await using var hold = connection.CreateCommand();
        hold.Transaction = transaction;
        hold.CommandText = """
            UPDATE ScanRecords SET SendStatus='Failed',
                SendMessage='Lượt gửi chưa xác nhận kết quả; cần đối soát nếu ứng dụng bị gián đoạn.'
            WHERE PersistenceReady = 1 AND Id IN (SELECT ScanRecordId FROM CheckeeSendAttemptItems WHERE AttemptId=$id);
            """;
        hold.Parameters.AddWithValue("$id", id);
        await hold.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        // Chỉ giữ 60 lần gần nhất; chi tiết là snapshot, độc lập retention ScanRecords.
        await using var prune = connection.CreateCommand();
        prune.Transaction = transaction;
        prune.CommandText = "DELETE FROM CheckeeSendAttempts WHERE Id NOT IN (SELECT Id FROM CheckeeSendAttempts ORDER BY Id DESC LIMIT 60);";
        await prune.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return id;
    }

    public async Task CompleteSendAttemptAsync(long attemptId, string status, int statusCode, string message,
        CancellationToken cancellationToken = default)
    {
        if (status is not (CheckeeSendAttemptStatuses.Sent or CheckeeSendAttemptStatuses.Failed
            or CheckeeSendAttemptStatuses.RetryPending or CheckeeSendAttemptStatuses.Unknown))
            throw new ArgumentException("Kết quả lần gửi không hợp lệ.", nameof(status));
        await using var connection = await _store.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var utc = ScanStore.FormatUtc(DateTime.UtcNow);
        await using var complete = connection.CreateCommand();
        complete.Transaction = transaction;
        complete.CommandText = """
            UPDATE CheckeeSendAttempts SET CompletedUtc=$utc, Status=$status, StatusCode=$http, Message=$message
            WHERE Id=$id AND CompletedUtc IS NULL;
            """;
        complete.Parameters.AddWithValue("$utc", utc);
        complete.Parameters.AddWithValue("$status", status);
        complete.Parameters.AddWithValue("$http", statusCode);
        complete.Parameters.AddWithValue("$message", message ?? string.Empty);
        complete.Parameters.AddWithValue("$id", attemptId);
        if (await complete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            throw new InvalidOperationException("Lần gửi không tồn tại hoặc đã được hoàn tất.");

        await using var items = connection.CreateCommand();
        items.Transaction = transaction;
        items.CommandText = "UPDATE CheckeeSendAttemptItems SET Status=$status, StatusCode=$http, Message=$message WHERE AttemptId=$id;";
        items.Parameters.AddWithValue("$status", status);
        items.Parameters.AddWithValue("$http", statusCode);
        items.Parameters.AddWithValue("$message", message ?? string.Empty);
        items.Parameters.AddWithValue("$id", attemptId);
        await items.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        await using var state = connection.CreateCommand();
        state.Transaction = transaction;
        state.CommandText = """
            UPDATE ScanRecords SET SendStatus=$status, SendStatusCode=$http, SendMessage=$message,
                SendAttempts=SendAttempts+1, SendAttemptUtc=$utc,
                SendTimeUtc=CASE WHEN $status='Sent' THEN $utc ELSE SendTimeUtc END
            WHERE PersistenceReady = 1 AND Id IN (SELECT ScanRecordId FROM CheckeeSendAttemptItems WHERE AttemptId=$id);
            """;
        state.Parameters.AddWithValue("$status", status switch
        {
            CheckeeSendAttemptStatuses.Sent => ScanSendStatuses.Sent,
            CheckeeSendAttemptStatuses.Failed or CheckeeSendAttemptStatuses.Unknown => ScanSendStatuses.Failed,
            _ => ScanSendStatuses.Pending,
        });
        state.Parameters.AddWithValue("$http", statusCode);
        state.Parameters.AddWithValue("$message", message ?? string.Empty);
        state.Parameters.AddWithValue("$utc", utc);
        state.Parameters.AddWithValue("$id", attemptId);
        await state.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<CheckeeSendAttempt>> GetRecentSendAttemptsAsync(int limit = 60,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _store.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT a.Id, a.StartedUtc, a.CompletedUtc, a.ProductGtin, a.BatchCode, a.SerialType,
                a.Status, a.StatusCode, a.Message, COUNT(i.Position),
                COALESCE(SUM(CASE WHEN i.Status='Sent' THEN 1 ELSE 0 END),0),
                COALESCE(SUM(CASE WHEN i.Status IN ('Failed','RetryPending') THEN 1 ELSE 0 END),0),
                COALESCE(SUM(CASE WHEN i.Status IN ('Unknown','InProgress') THEN 1 ELSE 0 END),0)
            FROM CheckeeSendAttempts a LEFT JOIN CheckeeSendAttemptItems i ON i.AttemptId=a.Id
            GROUP BY a.Id ORDER BY a.Id DESC LIMIT $limit;
            """;
        cmd.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 60));
        var result = new List<CheckeeSendAttempt>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(new CheckeeSendAttempt
            {
                Id=reader.GetInt64(0), StartedUtc=ParseUtc(reader.GetString(1)),
                CompletedUtc=reader.IsDBNull(2) ? null : ParseUtc(reader.GetString(2)),
                ProductGtin=reader.GetString(3), BatchCode=reader.GetString(4), SerialType=reader.GetString(5),
                Status=reader.GetString(6), StatusCode=reader.GetInt32(7), Message=reader.GetString(8),
                TotalCount=reader.GetInt32(9), OkCount=reader.GetInt32(10), FailCount=reader.GetInt32(11), UnknownCount=reader.GetInt32(12),
            });
        }
        return result;
    }

    public async Task<IReadOnlyList<CheckeeSendAttemptItem>> GetSendAttemptItemsAsync(long attemptId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _store.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT ScanRecordId, CodeContent, OrderNo, Status, StatusCode, Message FROM CheckeeSendAttemptItems WHERE AttemptId=$id ORDER BY Position;";
        cmd.Parameters.AddWithValue("$id", attemptId);
        var result = new List<CheckeeSendAttemptItem>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            result.Add(new CheckeeSendAttemptItem
            {
                ScanRecordId=reader.GetInt64(0), CodeContent=reader.GetString(1), OrderNo=reader.GetString(2),
                Status=reader.GetString(3), StatusCode=reader.GetInt32(4), Message=reader.GetString(5),
            });
        return result;
    }
}
