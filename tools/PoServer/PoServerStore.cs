using System.Globalization;
using System.Text.Json;
using ClassLibraryMT.Po;
using ClassLibraryMT.PoServer;
using Microsoft.Data.Sqlite;

namespace PoServer;

/// <summary>Persistent test-server orders; each PO owns exact, complete CodeContent values.</summary>
public sealed class PoServerStore
{
    private readonly string _dbPath;

    public PoServerStore(string dbPath)
    {
        _dbPath = Path.GetFullPath(dbPath);
        Directory.CreateDirectory(Path.GetDirectoryName(_dbPath)!);
        EnsureSchema();
    }

    public string DbPath => _dbPath;

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _dbPath, ForeignKeys = true, DefaultTimeout = 5,
        }.ToString());
        connection.Open();
        using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA busy_timeout=5000; PRAGMA journal_mode=WAL;";
        pragma.ExecuteNonQuery();
        return connection;
    }

    private void EnsureSchema()
    {
        using var db = Open();
        using (var schema = db.CreateCommand())
        {
            schema.CommandText = """
                CREATE TABLE IF NOT EXISTS Orders (
                    OrderNo TEXT NOT NULL PRIMARY KEY,
                    OrderQty INTEGER NOT NULL DEFAULT 0,
                    CreatedUtc TEXT NOT NULL,
                    IssuedUtc TEXT NULL,
                    CompletedUtc TEXT NULL,
                    Status TEXT NOT NULL DEFAULT 'Available',
                    Additional TEXT NOT NULL DEFAULT '{}'
                );
                CREATE TABLE IF NOT EXISTS Codes (
                    OrderNo TEXT NOT NULL,
                    Code TEXT NOT NULL,
                    Gtin TEXT NOT NULL DEFAULT '',
                    Serial TEXT NOT NULL DEFAULT '',
                    ActivatedUtc TEXT NULL,
                    PRIMARY KEY (OrderNo, Code),
                    FOREIGN KEY (OrderNo) REFERENCES Orders(OrderNo) ON DELETE CASCADE
                );
                """;
            schema.ExecuteNonQuery();
        }

        bool hasAdditional;
        using (var columns = db.CreateCommand())
        {
            columns.CommandText = "PRAGMA table_info(Orders);";
            using var reader = columns.ExecuteReader();
            hasAdditional = false;
            while (reader.Read()) hasAdditional |= reader.GetString(1) == "Additional";
        }
        if (!hasAdditional)
        {
            using var add = db.CreateCommand();
            add.CommandText = "ALTER TABLE Orders ADD COLUMN Additional TEXT NOT NULL DEFAULT '{}';";
            add.ExecuteNonQuery();
        }

        // The old sample server made Code globally unique. Rebuild only that table,
        // retaining the original full code, legacy metadata and activation timestamps.
        var keyColumns = new List<(int Position, string Name)>();
        using (var columns = db.CreateCommand())
        {
            columns.CommandText = "PRAGMA table_info(Codes);";
            using var reader = columns.ExecuteReader();
            while (reader.Read())
                if (reader.GetInt32(5) > 0) keyColumns.Add((reader.GetInt32(5), reader.GetString(1)));
        }
        if (!keyColumns.OrderBy(x => x.Position).Select(x => x.Name).SequenceEqual(["OrderNo", "Code"]))
        {
            using var tx = db.BeginTransaction(deferred: false);
            using var migrate = db.CreateCommand();
            migrate.Transaction = tx;
            migrate.CommandText = """
                CREATE TABLE Codes_CodeContentMigration (
                    OrderNo TEXT NOT NULL, Code TEXT NOT NULL,
                    Gtin TEXT NOT NULL DEFAULT '', Serial TEXT NOT NULL DEFAULT '',
                    ActivatedUtc TEXT NULL, PRIMARY KEY (OrderNo, Code),
                    FOREIGN KEY (OrderNo) REFERENCES Orders(OrderNo) ON DELETE CASCADE
                );
                INSERT INTO Codes_CodeContentMigration (OrderNo,Code,Gtin,Serial,ActivatedUtc)
                    SELECT OrderNo,Code,Gtin,Serial,ActivatedUtc FROM Codes;
                DROP TABLE Codes;
                ALTER TABLE Codes_CodeContentMigration RENAME TO Codes;
                """;
            migrate.ExecuteNonQuery();
            tx.Commit();
        }
        using var index = db.CreateCommand();
        index.CommandText = "CREATE INDEX IF NOT EXISTS IX_Codes_OrderNo ON Codes(OrderNo);";
        index.ExecuteNonQuery();
    }

    public PoPushResponse ImportOrder(string orderNo, int orderQty, IReadOnlyList<string> codeContents,
        IReadOnlyDictionary<string, string>? additional = null)
    {
        orderNo = (orderNo ?? string.Empty).Trim();
        if (orderNo.Length is 0 or > 200)
            throw new ArgumentException("poName phải có 1-200 ký tự.");
        ArgumentNullException.ThrowIfNull(codeContents);
        if (orderQty <= 0 || orderQty != codeContents.Count || codeContents.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("orderQty phải bằng số dòng CodeContent không rỗng trước lọc trùng.");

        using var db = Open();
        using var tx = db.BeginTransaction(deferred: false);
        string? canonical = null;
        string? status = null;
        var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        using (var existing = db.CreateCommand())
        {
            existing.Transaction = tx;
            existing.CommandText = "SELECT OrderNo,Status,Additional FROM Orders WHERE OrderNo=$order COLLATE NOCASE;";
            existing.Parameters.AddWithValue("$order", orderNo);
            using var reader = existing.ExecuteReader();
            if (reader.Read())
            {
                canonical = reader.GetString(0);
                status = reader.GetString(1);
                var old = JsonSerializer.Deserialize(reader.GetString(2), PoServerJsonContext.Default.DictionaryStringString);
                if (old is not null) foreach (var pair in old) metadata[pair.Key] = pair.Value;
                if (reader.Read()) throw new InvalidOperationException("Có nhiều PO khác nhau chỉ bởi hoa thường; hãy đối soát DB thử nghiệm.");
            }
        }
        if (status is "Completed" or "Closed" or "Cancelled")
            throw new InvalidOperationException($"PO '{canonical}' đã kết thúc; không nhận thêm mã.");
        orderNo = canonical ?? orderNo;
        if (additional is not null)
            foreach (var pair in additional) if (!string.IsNullOrWhiteSpace(pair.Value)) metadata[pair.Key] = pair.Value;

        if (canonical is null)
        {
            using var create = db.CreateCommand();
            create.Transaction = tx;
            create.CommandText = "INSERT INTO Orders(OrderNo,OrderQty,CreatedUtc,Status) VALUES($order,0,$utc,'Available');";
            create.Parameters.AddWithValue("$order", orderNo);
            create.Parameters.AddWithValue("$utc", DateTime.UtcNow.ToString("O"));
            create.ExecuteNonQuery();
        }
        var accepted = 0;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        using (var insert = db.CreateCommand())
        {
            insert.Transaction = tx;
            insert.CommandText = "INSERT OR IGNORE INTO Codes(OrderNo,Code) VALUES($order,$code);";
            insert.Parameters.AddWithValue("$order", orderNo);
            var value = insert.Parameters.Add("$code", SqliteType.Text);
            foreach (var code in codeContents)
            {
                if (!seen.Add(code)) continue;
                value.Value = code;
                accepted += insert.ExecuteNonQuery();
            }
        }
        using (var update = db.CreateCommand())
        {
            update.Transaction = tx;
            update.CommandText = """
                UPDATE Orders SET OrderQty=(SELECT COUNT(*) FROM Codes WHERE OrderNo=$order),Additional=$additional
                WHERE OrderNo=$order;
                """;
            update.Parameters.AddWithValue("$order", orderNo);
            update.Parameters.AddWithValue("$additional", JsonSerializer.Serialize(metadata, PoServerJsonContext.Default.DictionaryStringString));
            update.ExecuteNonQuery();
        }
        tx.Commit();
        return new PoPushResponse
        {
            Success = true, PoName = orderNo, Accepted = accepted,
            IgnoredDuplicates = codeContents.Count - accepted,
            Message = $"Đã nhận {accepted} mã mới vào PO {orderNo}.",
        };
    }

    public List<ProductionOrder> ListOrders(bool includeCompleted = false)
    {
        using var db = Open();
        using var command = db.CreateCommand();
        command.CommandText = "SELECT OrderNo,OrderQty,CreatedUtc,Additional FROM Orders"
            + (includeCompleted ? "" : " WHERE Status NOT IN ('Completed','Closed','Cancelled')")
            + " ORDER BY CreatedUtc DESC;";
        var result = new List<ProductionOrder>();
        using var reader = command.ExecuteReader();
        while (reader.Read()) result.Add(new ProductionOrder
        {
            OrderNo = reader.GetString(0), OrderQty = reader.GetInt32(1), CreatedUtc = ParseUtc(reader.GetString(2)),
            Additional = JsonSerializer.Deserialize(reader.GetString(3), PoServerJsonContext.Default.DictionaryStringString) ?? [],
        });
        return result;
    }

    public List<PoCode> GetCodes(string orderNo)
    {
        using var db = Open();
        using var command = db.CreateCommand();
        command.CommandText = "SELECT Code,ActivatedUtc FROM Codes WHERE OrderNo=$order COLLATE NOCASE ORDER BY Code COLLATE BINARY;";
        command.Parameters.AddWithValue("$order", orderNo);
        var codes = new List<PoCode>();
        using var reader = command.ExecuteReader();
        while (reader.Read()) codes.Add(new PoCode
        {
            CodeContent = reader.GetString(0), ActivatedUtc = reader.IsDBNull(1) ? null : ParseUtc(reader.GetString(1)),
        });
        return codes;
    }

    public int Activate(string orderNo, IReadOnlyList<string> codeContents)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(orderNo);
        ArgumentNullException.ThrowIfNull(codeContents);
        if (codeContents.Count == 0 || codeContents.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("codeContents phải chứa mã đầy đủ không rỗng.");
        using var db = Open();
        using var tx = db.BeginTransaction(deferred: false);
        var unique = codeContents.Distinct(StringComparer.Ordinal).ToArray();
        using (var exists = db.CreateCommand())
        {
            exists.Transaction = tx;
            exists.CommandText = "SELECT COUNT(*) FROM Codes WHERE OrderNo=$order COLLATE NOCASE AND Code=$code COLLATE BINARY;";
            exists.Parameters.AddWithValue("$order", orderNo);
            var code = exists.Parameters.Add("$code", SqliteType.Text);
            foreach (var value in unique)
            {
                code.Value = value;
                if (Convert.ToInt64(exists.ExecuteScalar()) != 1)
                    throw new ArgumentException("Có CodeContent không thuộc PO; chưa áp dụng lượt kích hoạt này.");
            }
        }
        var count = 0;
        using (var activate = db.CreateCommand())
        {
            activate.Transaction = tx;
            activate.CommandText = "UPDATE Codes SET ActivatedUtc=$utc WHERE OrderNo=$order COLLATE NOCASE AND Code=$code COLLATE BINARY AND ActivatedUtc IS NULL;";
            activate.Parameters.AddWithValue("$order", orderNo);
            activate.Parameters.AddWithValue("$utc", DateTime.UtcNow.ToString("O"));
            var code = activate.Parameters.Add("$code", SqliteType.Text);
            foreach (var value in unique) { code.Value = value; count += activate.ExecuteNonQuery(); }
        }
        using (var complete = db.CreateCommand())
        {
            complete.Transaction = tx;
            complete.CommandText = """
                UPDATE Orders SET Status='Completed',CompletedUtc=COALESCE(CompletedUtc,$utc)
                WHERE OrderNo=$order COLLATE NOCASE AND NOT EXISTS
                    (SELECT 1 FROM Codes WHERE OrderNo=Orders.OrderNo AND ActivatedUtc IS NULL);
                """;
            complete.Parameters.AddWithValue("$order", orderNo);
            complete.Parameters.AddWithValue("$utc", DateTime.UtcNow.ToString("O"));
            complete.ExecuteNonQuery();
        }
        tx.Commit();
        return count;
    }

    public void MarkIssued(string orderNo)
    {
        using var db = Open();
        using var command = db.CreateCommand();
        command.CommandText = "UPDATE Orders SET IssuedUtc=COALESCE(IssuedUtc,$utc),Status=CASE WHEN Status='Available' THEN 'Downloaded' ELSE Status END WHERE OrderNo=$order COLLATE NOCASE;";
        command.Parameters.AddWithValue("$utc", DateTime.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$order", orderNo);
        command.ExecuteNonQuery();
    }

    private static DateTime ParseUtc(string value) => DateTime.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}
