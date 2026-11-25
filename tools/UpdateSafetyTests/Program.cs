using ClassLibraryMT.Logging;
using ClassLibraryMT.Scans;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

var root = Path.Combine(Path.GetTempPath(), "visionf-update-safety-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var passed = 0;
using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));

async Task Test(string name, Func<Task> action)
{
    await action();
    passed++;
    Console.WriteLine("[OK] " + name);
}

static async Task MustFail(Func<Task> action)
{
    try { await action(); }
    catch (Exception error) when (error is IOException or ObjectDisposedException) { return; }
    throw new Exception("Đã chấp nhận writer lỗi làm điều kiện cài cập nhật.");
}

static async Task Execute(string path, string sql)
{
    await using var connection = new SqliteConnection("Data Source=" + path);
    await connection.OpenAsync();
    using var command = connection.CreateCommand();
    command.CommandText = sql;
    await command.ExecuteNonQueryAsync();
}

ScanRecordWriter NewScans(string path, int capacity = 20000) => new(
    Options.Create(new ScanStoreOptions { Path = path, QueueCapacity = capacity, BatchSize = 1000, FlushIntervalMs = 1000 }),
    new ScanStore(path));

SqliteLogWriter NewLogs(string path) => new(Options.Create(new SqliteLoggingOptions
    { Path = path, RetentionDays = 0, BatchSize = 1000, FlushIntervalMs = 1000 }));

try
{
    await Test("Writer rỗng vẫn phải mở được DB; có bản ghi thì kiểm tra sau ghi", async () =>
    {
        var scanPath = Path.Combine(root, "good-scans.db");
        var logPath = Path.Combine(root, "good-log.db");
        using var scans = NewScans(scanPath);
        using var logs = NewLogs(logPath);
        await scans.VerifyHealthyAsync(timeout.Token);
        await logs.VerifyHealthyAsync(timeout.Token);
        if (!File.Exists(scanPath) || !File.Exists(logPath)) throw new Exception("Chưa mở DB thật.");
        scans.Enqueue(new ScanRecordEntity { CodeContent = "UPDATE-SAFETY", ReceivedUtc = DateTime.UtcNow });
        logs.Enqueue(new AppLogEntry { Message = "Kiểm tra cập nhật", TimestampUtc = DateTime.UtcNow });
        await scans.VerifyHealthyAsync(timeout.Token);
        await logs.VerifyHealthyAsync(timeout.Token);
        if (scans.WrittenCount != 1 || logs.WrittenCount != 1) throw new Exception("Chưa ghi hết dữ liệu.");
    });

    await Test("Từ chối DB không mở được kể cả khi chưa có log hoặc lượt quét", async () =>
    {
        var invalid = Path.Combine(root, "directory-as-database");
        Directory.CreateDirectory(invalid);
        using var scans = NewScans(invalid);
        using var logs = NewLogs(invalid);
        await MustFail(() => scans.VerifyHealthyAsync(timeout.Token));
        await MustFail(() => logs.VerifyHealthyAsync(timeout.Token));
    });

    await Test("Ghi batch scan lỗi thì Flush cũ không ném; Verify chặn kể cả đã sửa DB", async () =>
    {
        var path = Path.Combine(root, "failed-scan-batch.db");
        using var scans = NewScans(path);
        await scans.VerifyHealthyAsync(timeout.Token);
        await Execute(path, "CREATE TRIGGER fail_write BEFORE INSERT ON ScanRecords BEGIN SELECT RAISE(ABORT, 'test failure'); END;");
        scans.Enqueue(new ScanRecordEntity { CodeContent = "FAILED", ReceivedUtc = DateTime.UtcNow });
        await scans.FlushAsync(timeout.Token);
        await Execute(path, "DROP TRIGGER fail_write;");
        await MustFail(() => scans.VerifyHealthyAsync(timeout.Token));
    });

    await Test("Ghi batch log lỗi thì Flush cũ không ném; Verify chặn kể cả đã sửa DB", async () =>
    {
        var path = Path.Combine(root, "failed-log-batch.db");
        using var logs = NewLogs(path);
        await logs.VerifyHealthyAsync(timeout.Token);
        await Execute(path, "CREATE TRIGGER fail_write BEFORE INSERT ON AppLogs BEGIN SELECT RAISE(ABORT, 'test failure'); END;");
        logs.Enqueue(new AppLogEntry { Message = "FAILED", TimestampUtc = DateTime.UtcNow });
        await logs.FlushAsync(timeout.Token);
        await Execute(path, "DROP TRIGGER fail_write;");
        await MustFail(() => logs.VerifyHealthyAsync(timeout.Token));
    });

    await Test("Tràn hàng đợi scan được ghi nhận để chặn cập nhật", async () =>
    {
        using var scans = NewScans(Path.Combine(root, "overflow-scans.db"), capacity: 1);
        await scans.VerifyHealthyAsync(timeout.Token);
        for (var i = 0; i < 5000; i++) scans.Enqueue(new ScanRecordEntity { CodeContent = i.ToString() });
        await MustFail(() => scans.VerifyHealthyAsync(timeout.Token));
    });

    await Test("Tràn hàng đợi log được ghi nhận để chặn cập nhật", async () =>
    {
        var path = Path.Combine(root, "overflow-log.db");
        using var logs = NewLogs(path);
        await logs.VerifyHealthyAsync(timeout.Token);
        await using var blocker = new SqliteConnection("Data Source=" + path);
        await blocker.OpenAsync(timeout.Token);
        using var transaction = blocker.BeginTransaction();
        for (var i = 0; i < 30000; i++) logs.Enqueue(new AppLogEntry { Message = i.ToString() });
        transaction.Rollback();
        await MustFail(() => logs.VerifyHealthyAsync(timeout.Token));
    });

    await Test("Writer đã dispose tuyệt đối không được báo khỏe", async () =>
    {
        var scans = NewScans(Path.Combine(root, "disposed-scans.db"));
        var logs = NewLogs(Path.Combine(root, "disposed-log.db"));
        scans.Dispose();
        logs.Dispose();
        await MustFail(() => scans.VerifyHealthyAsync(timeout.Token));
        await MustFail(() => logs.VerifyHealthyAsync(timeout.Token));
    });
    Console.WriteLine($"Đạt {passed} kiểm thử an toàn bàn giao cập nhật.");
}
finally
{
    SqliteConnection.ClearAllPools();
    Directory.Delete(root, recursive: true);
}
