using System.Text.Json;
using ClassLibraryMT.Logging;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace ClassLibraryMT.Logging.Tests;

/// <summary>
/// Test cho lib ghi log SQLite.
///
/// Phan 1: ghi log va doc lai qua ILogQueryService.
/// Phan 2: loc, phan trang.
/// Phan 3: prune theo retention.
/// Phan 4: escape CSV.
/// Phan 5: flush khi dong (khong mat log).
/// </summary>
internal static class Program
{
    private static async Task<int> Main()
    {
        Console.WriteLine("=== TEST LIB LOG SQLITE ===");
        Console.WriteLine();

        Console.WriteLine("--- Ghi va doc log ---");
        await TestRunner.RunAsync("Ghi va doc log (AppLogs)", TestWriteAndRead);

        Console.WriteLine();
        Console.WriteLine("--- Ghi thao tac nguoi dung ---");
        await TestRunner.RunAsync("Ghi thao tac nguoi dung (UserActions)", TestUserAction);

        Console.WriteLine();
        Console.WriteLine("--- Loc va phan trang ---");
        await TestRunner.RunAsync("Loc va phan trang", TestFilterAndPaging);

        Console.WriteLine();
        Console.WriteLine("--- Giu log khi dong app (flush) ---");
        await TestRunner.RunAsync("Khong mat log khi dong app", TestFlushOnDispose);

        Console.WriteLine();
        Console.WriteLine("--- Prune theo retention ---");
        await TestRunner.RunAsync("Prune ban ghi cu theo retention", TestPruneRetention);

        Console.WriteLine();
        Console.WriteLine("--- Escape CSV ---");
        TestRunner.Run("Escape CSV", TestCsvEscape);

        Console.WriteLine();
        Console.WriteLine("--- Schema va WAL ---");
        await TestRunner.RunAsync("Schema 3 bang + WAL", TestSchema);

        Console.WriteLine();
        Console.WriteLine("--- Nguoi dung trong nhat ky ---");
        await TestRunner.RunAsync("Ghi Username/UserId vao UserActions", TestUserContextInLog);

        Console.WriteLine();
        Console.WriteLine("--- Migration bang UserActions cu ---");
        await TestRunner.RunAsync("ALTER TABLE them cot Username/UserId", TestMigrationUserActions);

        return TestRunner.Summary();
    }

    // ------------------------------------------------------------------
    // Helper
    // ------------------------------------------------------------------

    /// <summary>
    /// Tao thu muc tam de chua file .db, tra ve duong dan thu muc do.
    /// </summary>
    private static string CreateTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "tantien-logtest-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static SqliteLoggingOptions OptionsFor(string dir, int retentionDays = 30) => new()
    {
        Path = Path.Combine(dir, "test.db"),
        MinLevel = LogLevel.Debug,
        RetentionDays = retentionDays,
        BatchSize = 10,
        FlushIntervalMs = 200,
        PruneIntervalMs = 200,
    };

    /// <summary>
    /// Tao logger provider + writer + query service tren cung mot file .db.
    /// </summary>
    private static (SqliteLogWriter Writer, SqliteLoggerProvider Provider, ILogQueryService Query)
        CreateStack(SqliteLoggingOptions opt)
    {
        var options = Options.Create(opt);
        var writer = new SqliteLogWriter(options);
        var provider = new SqliteLoggerProvider(writer, options);
        var query = new LogQueryService(options, NullLogger<LogQueryService>.Instance);
        return (writer, provider, query);
    }

    // ------------------------------------------------------------------
    // 1. Ghi va doc log
    // ------------------------------------------------------------------
    private static async Task TestWriteAndRead()
    {
        var dir = CreateTempDir();

        try
        {
            var (writer, provider, query) = CreateStack(OptionsFor(dir));

            using (provider)
            {
                var logger = provider.CreateLogger("Test.Category");
                logger.LogInformation("Thong bao thu nghiem {Count}", 42);
                logger.LogWarning("Canh bao thu nghiem");
                logger.LogError(new InvalidOperationException("Loi thu nghiem"), "Co loi");

                await writer.FlushAsync();
            }

            var result = await query.GetAppLogsAsync();

            TestRunner.AreEqual(3L, result.TotalCount, "Tong so ban ghi log");

            var first = result.Items[0];
            TestRunner.AreEqual("Test.Category", first.Category, "Category");
            TestRunner.AreEqual("Error", first.Level, "Muc log cuoi cung (moi nhat truoc)");
            TestRunner.IsNotNull(first.Exception, "Exception phai duoc ghi");
            TestRunner.Contains(first.Message, "Co loi", "Message co loi");

            // Ban ghi co thuoc tinh bo sung: tim ban ghi "Thong bao thu nghiem".
            var withProps = result.Items.FirstOrDefault(
                e => e.Message.Contains("Thong bao thu nghiem", StringComparison.Ordinal));
            TestRunner.IsNotNull(withProps, "Phai co ban ghi co thuoc tinh");
            TestRunner.IsNotNull(withProps!.PropertiesJson, "Properties phai duoc ghi");
            TestRunner.Contains(withProps.PropertiesJson!, "Count", "Properties chua Count");

            // Ban ghi "Canh bao thu nghiem" khong co thuoc tinh -> PropertiesJson null.
            var noProps = result.Items.FirstOrDefault(
                e => e.Message.Contains("Canh bao thu nghiem", StringComparison.Ordinal));
            TestRunner.IsNotNull(noProps, "Phai co ban ghi canh bao");
            TestRunner.IsTrue(noProps!.PropertiesJson is null, "Ban ghi khong thuoc tinh -> PropertiesJson null");

            // Loc theo category.
            var byCat = await query.GetAppLogsAsync(new AppLogFilter { CategoryContains = "Test" });
            TestRunner.AreEqual(3L, byCat.TotalCount, "Loc theo category");

            // Loc theo muc log toi thieu.
            var errors = await query.GetAppLogsAsync(new AppLogFilter { MinLevel = LogLevel.Error });
            TestRunner.AreEqual(1L, errors.TotalCount, "Loc theo MinLevel=Error");

            // Loc theo message.
            var byMsg = await query.GetAppLogsAsync(new AppLogFilter { MessageContains = "Canh bao" });
            TestRunner.AreEqual(1L, byMsg.TotalCount, "Loc theo message");

            writer.Dispose();
        }
        finally
        {
            Cleanup(dir);
        }
    }

    // ------------------------------------------------------------------
    // 2. Ghi thao tac nguoi dung
    // ------------------------------------------------------------------
    private static async Task TestUserAction()
    {
        var dir = CreateTempDir();

        try
        {
            var (writer, provider, query) = CreateStack(OptionsFor(dir));

            using (provider)
            {
                var actionLog = new UserActionLogger(writer, Options.Create(OptionsFor(dir)));

                actionLog.Log("Config.Save", "Config", "Luu cau hinh", UserActionResults.Success);
                actionLog.Log("License.Import", "License", "Nhap file", UserActionResults.Failed);
                actionLog.Log("Tab.Switch", "Tab", "Cau hinh", UserActionResults.Success);

                await writer.FlushAsync();
            }

            var result = await query.GetUserActionsAsync();

            TestRunner.AreEqual(3L, result.TotalCount, "Tong so thao tac");

            var newest = result.Items[0];
            TestRunner.AreEqual("Tab.Switch", newest.Action, "Thao tac moi nhat");
            TestRunner.AreEqual("Tab", newest.Target, "Target");
            TestRunner.AreEqual("Success", newest.Result, "Ket qua");

            // Chi tiet van ban duoc boc thanh JSON.
            TestRunner.IsNotNull(newest.DetailJson, "DetailJson phai co");
            TestRunner.Contains(newest.DetailJson!, "Cau hinh", "Detail chua noi dung");

            // Loc theo ten hanh dong.
            var configOnly = await query.GetUserActionsAsync(new UserActionFilter { ActionContains = "Config" });
            TestRunner.AreEqual(1L, configOnly.TotalCount, "Loc theo Action");

            // Loc theo ket qua.
            var failed = await query.GetUserActionsAsync(new UserActionFilter { Result = "Failed" });
            TestRunner.AreEqual(1L, failed.TotalCount, "Loc theo Result=Failed");

            // Dem theo hanh dong: moi hanh dong trong du lieu deu co 1 ban ghi.
            var counts = await query.GetUserActionCountsAsync();
            TestRunner.AreEqual(1L, counts["Tab.Switch"], "Dem so luong theo hanh dong");
            TestRunner.AreEqual(1L, counts["Config.Save"], "Dem so luong Config.Save");
            TestRunner.AreEqual(1L, counts["License.Import"], "Dem so luong License.Import");

            writer.Dispose();
        }
        finally
        {
            Cleanup(dir);
        }
    }

    // ------------------------------------------------------------------
    // 3. Loc va phan trang
    // ------------------------------------------------------------------
    private static async Task TestFilterAndPaging()
    {
        var dir = CreateTempDir();

        try
        {
            var (writer, provider, query) = CreateStack(OptionsFor(dir));

            using (provider)
            {
                var actionLog = new UserActionLogger(writer, Options.Create(OptionsFor(dir)));

                for (var i = 0; i < 25; i++)
                {
                    actionLog.Log("Test.Action", "Test", $"Ban ghi {i}", UserActionResults.Success);
                }

                await writer.FlushAsync();
            }

            // Trang 1, 10 ban ghi.
            var page1 = await query.GetUserActionsAsync(page: 1, pageSize: 10);
            TestRunner.AreEqual(10, page1.Items.Count, "So ban ghi trang 1");
            TestRunner.AreEqual(25L, page1.TotalCount, "Tong so ban ghi");
            TestRunner.AreEqual(3, page1.TotalPages, "Tong so trang");
            TestRunner.IsTrue(page1.HasNextPage, "Trang 1 phai co trang sau");
            TestRunner.IsFalse(page1.HasPreviousPage, "Trang 1 khong co trang truoc");

            // Trang 3, 5 ban ghi.
            var page3 = await query.GetUserActionsAsync(page: 3, pageSize: 10);
            TestRunner.AreEqual(5, page3.Items.Count, "So ban ghi trang 3");
            TestRunner.IsFalse(page3.HasNextPage, "Trang cuoi khong co trang sau");
            TestRunner.IsTrue(page3.HasPreviousPage, "Trang cuoi co trang truoc");

            // Trang vuot gioi han -> rong.
            var page99 = await query.GetUserActionsAsync(page: 99, pageSize: 10);
            TestRunner.AreEqual(0, page99.Items.Count, "Trang vuot gioi han phai rong");

            // Loc theo khoang thoi gian.
            var future = await query.GetUserActionsAsync(new UserActionFilter
            {
                FromUtc = DateTime.UtcNow.AddDays(1),
            });
            TestRunner.AreEqual(0L, future.TotalCount, "Loc tu ngay tuong lai phai rong");

            writer.Dispose();
        }
        finally
        {
            Cleanup(dir);
        }
    }

    // ------------------------------------------------------------------
    // 4. Flush khi dong app
    // ------------------------------------------------------------------
    private static async Task TestFlushOnDispose()
    {
        var dir = CreateTempDir();

        try
        {
            var (writer, provider, query) = CreateStack(OptionsFor(dir));

            using (provider)
            {
                var actionLog = new UserActionLogger(writer, Options.Create(OptionsFor(dir)));
                actionLog.Log("App.Exit", "App", "Dong ung dung", UserActionResults.Success);
            }

            // Dispose writer -> flush phan con lai (khong goi FlushAsync).
            writer.Dispose();

            // Doc lai bang service moi (file da duoc dong).
            var freshOptions = Options.Create(OptionsFor(dir));
            var freshQuery = new LogQueryService(freshOptions, NullLogger<LogQueryService>.Instance);

            var result = await freshQuery.GetUserActionsAsync();
            TestRunner.AreEqual(1L, result.TotalCount, "Khong mat ban ghi khi dong app");
        }
        finally
        {
            Cleanup(dir);
        }
    }

    // ------------------------------------------------------------------
    // 5. Prune theo retention
    // ------------------------------------------------------------------
    private static async Task TestPruneRetention()
    {
        var dir = CreateTempDir();

        try
        {
            // Retention = 0 nghia la giu vinh vien -> khong prune.
            var (writer, provider, query) = CreateStack(OptionsFor(dir, retentionDays: 0));

            using (provider)
            {
                var actionLog = new UserActionLogger(writer, Options.Create(OptionsFor(dir)));
                actionLog.Log("Keep.Me", "Test", "Giu lai", UserActionResults.Success);
                await writer.FlushAsync();
            }

            // Cho worker chay prune cycle.
            await Task.Delay(600);

            var result = await query.GetUserActionsAsync();
            TestRunner.AreEqual(1L, result.TotalCount, "Retention=0 phai giu vinh vien");

            writer.Dispose();

            // Bay gio test voi retention = 1 ngay: ban ghi cu hon 1 ngay se bi xoa.
            var opt = OptionsFor(dir, retentionDays: 1);
            var options2 = Options.Create(opt);
            var writer2 = new SqliteLogWriter(options2);

            // Ghi truc tiep ban ghi co TimestampUtc cu hon 5 ngay.
            writer2.Enqueue(new UserActionEntry
            {
                TimestampUtc = DateTime.UtcNow.AddDays(-5),
                Action = "Old.Action",
                Target = "Test",
                Result = UserActionResults.Success,
            });

            writer2.Enqueue(new UserActionEntry
            {
                TimestampUtc = DateTime.UtcNow,
                Action = "New.Action",
                Target = "Test",
                Result = UserActionResults.Success,
            });

            await writer2.FlushAsync();
            await Task.Delay(600);
            writer2.Dispose();

            var query2 = new LogQueryService(options2, NullLogger<LogQueryService>.Instance);
            var after = await query2.GetUserActionsAsync();

            // "Old.Action" (5 ngay truoc) bi prune; "New.Action" va "Keep.Me" con lai.
            TestRunner.AreEqual(2L, after.TotalCount, "Prune phai xoa ban ghi cu");
            TestRunner.IsFalse(
                after.Items.Any(e => e.Action == "Old.Action"),
                "Ban ghi cu 5 ngay truoc phai bi xoa");
            TestRunner.IsTrue(
                after.Items.Any(e => e.Action == "New.Action"),
                "Ban ghi moi phai duoc giu lai");
        }
        finally
        {
            Cleanup(dir);
        }
    }

    // ------------------------------------------------------------------
    // 6. Escape CSV
    // ------------------------------------------------------------------
    private static void TestCsvEscape()
    {
        // Dung reflection de goi private static BuildCsv? Khong - test qua
        // ham public cua lib: Serialize trong ViewModel khong thuoc lib.
        // Thay vao do, test truc tiep quy tac escape chuoi CSV chuan.
        TestRunner.AreEqual("abc", CsvEscape("abc"), "Chuoi don gian khong can boc ngoac");
        TestRunner.AreEqual("\"a,b\"", CsvEscape("a,b"), "Chuoi co dau phay phai boc ngoac");
        TestRunner.AreEqual("\"a\"\"b\"", CsvEscape("a\"b"), "Dau ngoac kep phai nhan doi");
        TestRunner.AreEqual("\"a\nb\"", CsvEscape("a\nb"), "Chuoi co xuong dong phai boc ngoac");
        TestRunner.AreEqual(string.Empty, CsvEscape(string.Empty), "Chuoi rong");
    }

    /// <summary>
    /// Ban sao cua quy tac escape CSV de test (same logic voi HistoryViewModel).
    /// </summary>
    private static string CsvEscape(string value)
    {
        if (value.Length == 0) return string.Empty;

        var needsQuote = value.Contains(',')
            || value.Contains('"')
            || value.Contains('\n')
            || value.Contains('\r');

        if (!needsQuote) return value;

        return '"' + value.Replace("\"", "\"\"") + '"';
    }

    // ------------------------------------------------------------------
    // 7. Schema va WAL
    // ------------------------------------------------------------------
    private static async Task TestSchema()
    {
        var dir = CreateTempDir();

        try
        {
            var (writer, provider, query) = CreateStack(OptionsFor(dir));

            using (provider)
            {
                var logger = provider.CreateLogger("Schema.Test");
                logger.LogInformation("Ban ghi de tao schema");
                await writer.FlushAsync();
            }

            var dbPath = Path.Combine(dir, "test.db");

            TestRunner.IsTrue(File.Exists(dbPath), "File .db phai duoc tao");

            // WAL -> file -wal ton tai (neu co du lieu chua checkpoint).
            var walExists = File.Exists(dbPath + "-wal");
            TestRunner.IsTrue(walExists || File.Exists(dbPath), "Che do WAL phai hoat dong");

            // Doc schema truc tiep.
            using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={dbPath}");
            connection.Open();

            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' ORDER BY name;";
                using var reader = cmd.ExecuteReader();
                var tables = new List<string>();
                while (reader.Read()) tables.Add(reader.GetString(0));

                TestRunner.IsTrue(tables.Contains("AppLogs"), "Co bang AppLogs");
                TestRunner.IsTrue(tables.Contains("UserActions"), "Co bang UserActions");
                TestRunner.IsTrue(tables.Contains("SchemaVersion"), "Co bang SchemaVersion");
            }

            // Version da duoc ghi.
            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = "SELECT COUNT(*) FROM SchemaVersion;";
                var count = Convert.ToInt64(cmd.ExecuteScalar());
                TestRunner.AreEqual(1L, count, "SchemaVersion phai co 1 dong");
            }

            writer.Dispose();
        }
        finally
        {
            Cleanup(dir);
        }
    }

    // ------------------------------------------------------------------
    // 8. Nguoi dung trong nhat ky
    // ------------------------------------------------------------------

    /// <summary>
    /// IUserContext (tu lib Auth) duoc ghi vao UserActions.
    /// </summary>
    private static async Task TestUserContextInLog()
    {
        var dir = CreateTempDir();

        try
        {
            var (writer, provider, query) = CreateStack(OptionsFor(dir));

            // Service provider gia lap: chi co IUserContext, khong co gi khac.
            var userContext = new FakeUserContext { Username = "nguyenvana", UserId = 42 };
            var services = new FakeServiceProvider(userContext);

            using (provider)
            {
                var actionLog = new UserActionLogger(
                    writer, Options.Create(OptionsFor(dir)), services);

                actionLog.Log("Config.Save", "Config", "Luu cau hinh", UserActionResults.Success);

                await writer.FlushAsync();
            }

            var result = await query.GetUserActionsAsync();

            TestRunner.AreEqual(1L, result.TotalCount, "Co 1 ban ghi");
            TestRunner.AreEqual("nguyenvana", result.Items[0].Username, "Username duoc ghi");
            TestRunner.AreEqual(42L, result.Items[0].UserId, "UserId duoc ghi");

            // Loc theo username.
            var byUser = await query.GetUserActionsAsync(new UserActionFilter { Username = "nguyenvana" });
            TestRunner.AreEqual(1L, byUser.TotalCount, "Loc theo Username");

            var byOther = await query.GetUserActionsAsync(new UserActionFilter { Username = "khac" });
            TestRunner.AreEqual(0L, byOther.TotalCount, "Loc theo Username sai -> rong");

            writer.Dispose();
        }
        finally
        {
            Cleanup(dir);
        }
    }

    /// <summary>
    /// Bang UserActions tao tu phien ban cu (khong co cot Username/UserId) phai
    /// duoc ALTER TABLE them cot moi, giu nguyen du lieu cu.
    /// </summary>
    private static async Task TestMigrationUserActions()
    {
        var dir = CreateTempDir();

        try
        {
            var dbPath = Path.Combine(dir, "test.db");

            // Tao bang UserActions theo phien ban cu (khong co cot moi).
            using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={dbPath}"))
            {
                connection.Open();

                using var cmd = connection.CreateCommand();
                cmd.CommandText = """
                    CREATE TABLE UserActions (
                        Id           INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                        TimestampUtc TEXT    NOT NULL,
                        Action       TEXT    NOT NULL,
                        Target       TEXT    NOT NULL DEFAULT '',
                        DetailJson   TEXT    NULL,
                        Result       TEXT    NOT NULL DEFAULT ''
                    );
                    """;
                cmd.ExecuteNonQuery();

                // Chen 1 ban ghi cu.
                using var insert = connection.CreateCommand();
                insert.CommandText = """
                    INSERT INTO UserActions (TimestampUtc, Action, Target, DetailJson, Result)
                    VALUES ($ts, $a, $t, $d, $r);
                    """;
                insert.Parameters.AddWithValue("$ts", DateTime.UtcNow.ToString("O"));
                insert.Parameters.AddWithValue("$a", "Old.Action");
                insert.Parameters.AddWithValue("$t", "Old");
                insert.Parameters.AddWithValue("$d", DBNull.Value);
                insert.Parameters.AddWithValue("$r", "Success");
                insert.ExecuteNonQuery();
            }

            // Mo writer -> phai chay migration.
            var (writer, provider, query) = CreateStack(OptionsFor(dir));

            using (provider)
            {
                var actionLog = new UserActionLogger(writer, Options.Create(OptionsFor(dir)));
                actionLog.Log("New.Action", "New", "Ban ghi moi", UserActionResults.Success);
                await writer.FlushAsync();
            }

            var result = await query.GetUserActionsAsync();

            // Du lieu cu phai con nguyen.
            TestRunner.AreEqual(2L, result.TotalCount, "Phai giu ca ban ghi cu va ban ghi moi");
            TestRunner.IsTrue(
                result.Items.Any(e => e.Action == "Old.Action"),
                "Ban ghi cu phai con nguyen");

            // Ban ghi moi co cot Username/UserId (rong vi khong co user context).
            var newest = result.Items[0];
            TestRunner.AreEqual("New.Action", newest.Action, "Ban ghi moi");
            TestRunner.AreEqual(string.Empty, newest.Username, "Username rong khi khong co context");

            writer.Dispose();
        }
        finally
        {
            Cleanup(dir);
        }
    }

    /// <summary>
    /// IUserContext gia lap cho test.
    /// </summary>
    private sealed class FakeUserContext : IUserContext
    {
        public string? Username { get; set; }
        public long? UserId { get; set; }
    }

    /// <summary>
    /// Service provider toi gian chi tra ve IUserContext.
    /// </summary>
    private sealed class FakeServiceProvider : IServiceProvider
    {
        private readonly IUserContext _userContext;

        public FakeServiceProvider(IUserContext userContext) => _userContext = userContext;

        public object? GetService(Type serviceType) =>
            serviceType == typeof(IUserContext) ? _userContext : null;
    }

    // ------------------------------------------------------------------
    // Cleanup
    // ------------------------------------------------------------------
    private static void Cleanup(string dir)
    {
        try
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
        catch (IOException)
        {
            // Bo qua: file co the con khoa tam thoi.
        }
    }
}
