using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace TanTienQR.Services;

/// <summary>Separate audit SQLite file; never shares the production outbox transaction.</summary>
internal sealed class ManualActivationHistoryStore
{
    private readonly string _connectionString;
    private readonly SemaphoreSlim _gate = new(1);
    private bool _initialized;

    public ManualActivationHistoryStore(IOptions<ManualActivationOptions> options)
    {
        var path = options.Value.ResolveHistoryPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path, Pooling = false, DefaultTimeout = 3,
        }.ToString();
    }

    private async Task InitializeAsync(SqliteConnection db, CancellationToken ct)
    {
        if (_initialized) return;
        await using var command = db.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS ManualActivationAudit (
                Sequence INTEGER PRIMARY KEY AUTOINCREMENT,
                Id TEXT NOT NULL UNIQUE, CodeContent TEXT NOT NULL, Source TEXT NOT NULL,
                Status TEXT NOT NULL, Message TEXT NOT NULL, OrderNo TEXT NOT NULL,
                ReceivedUtc TEXT NOT NULL, Success INTEGER NOT NULL);
            UPDATE ManualActivationAudit SET Status='Interrupted',
                Message='Phiên trước dừng trước khi ghi kết quả; kiểm tra lịch sử quét trước khi thử lại.'
                WHERE Status='Pending';
            """;
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        _initialized = true;
    }

    public async Task SaveAsync(ManualActivationResult result, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var db = new SqliteConnection(_connectionString);
            await db.OpenAsync(ct).ConfigureAwait(false);
            await InitializeAsync(db, ct).ConfigureAwait(false);
            await using var command = db.CreateCommand();
            command.CommandText = """
                INSERT INTO ManualActivationAudit
                    (Id,CodeContent,Source,Status,Message,OrderNo,ReceivedUtc,Success)
                VALUES ($id,$code,$source,$status,$message,$order,$received,$success)
                ON CONFLICT(Id) DO UPDATE SET Status=excluded.Status,Message=excluded.Message,
                    OrderNo=excluded.OrderNo,Success=excluded.Success;
                """;
            command.Parameters.AddWithValue("$id", result.Id);
            command.Parameters.AddWithValue("$code", result.CodeContent);
            command.Parameters.AddWithValue("$source", result.Source);
            command.Parameters.AddWithValue("$status", result.Status);
            command.Parameters.AddWithValue("$message", result.Message);
            command.Parameters.AddWithValue("$order", result.OrderNo);
            command.Parameters.AddWithValue("$received", result.ReceivedUtc.ToString("O", CultureInfo.InvariantCulture));
            command.Parameters.AddWithValue("$success", result.Success ? 1 : 0);
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    public async Task<IReadOnlyList<ManualActivationResult>> GetRecentAsync(int limit = 10, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var db = new SqliteConnection(_connectionString);
            await db.OpenAsync(ct).ConfigureAwait(false);
            await InitializeAsync(db, ct).ConfigureAwait(false);
            await using var command = db.CreateCommand();
            command.CommandText = "SELECT Id,CodeContent,Source,Status,Message,OrderNo,ReceivedUtc,Success FROM ManualActivationAudit ORDER BY Sequence DESC LIMIT $limit;";
            command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 100));
            await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            var results = new List<ManualActivationResult>();
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
                results.Add(new ManualActivationResult
                {
                    Id = reader.GetString(0), CodeContent = reader.GetString(1), Source = reader.GetString(2),
                    Status = reader.GetString(3), Message = reader.GetString(4), OrderNo = reader.GetString(5),
                    ReceivedUtc = DateTime.Parse(reader.GetString(6), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                    Success = reader.GetInt32(7) != 0,
                });
            return results;
        }
        finally { _gate.Release(); }
    }
}
