using ClassLibraryMT.Po;
using Microsoft.Data.Sqlite;

namespace PoServer;

/// <summary>
/// Lưu trữ PO + tập mã của server giả lập bằng SQLite (file <c>logs/po-server.db</c>).
///
/// <para>
/// Bảng <c>Orders</c>: <c>OrderNo</c> (PK), <c>OrderQty</c>, <c>CreatedUtc</c>,
/// <c>IssuedUtc</c>, <c>CompletedUtc</c>, <c>Status</c>.
/// </para>
///
/// <para>
/// Bảng <c>Codes</c>: <c>OrderNo</c>, <c>Code</c> (PK), <c>Gtin</c>,
/// <c>Serial</c>, <c>ActivatedUtc</c>.
/// </para>
/// </summary>
public sealed class PoServerStore
{
    /// <summary>GTIN mặc định cho mã mẫu.</summary>
    public const string DefaultGtin = "08934567890123";

    private readonly string _dbPath;

    public PoServerStore(string dbPath)
    {
        _dbPath = dbPath;
        EnsureSchema();
    }

    /// <summary>Đường dẫn file database.</summary>
    public string DbPath => _dbPath;

    /// <summary>Tạo schema nếu chưa có.</summary>
    private void EnsureSchema()
    {
        var dir = Path.GetDirectoryName(_dbPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        using var connection = new SqliteConnection($"Data Source={_dbPath}");
        connection.Open();

        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS Orders (
                OrderNo      TEXT NOT NULL PRIMARY KEY,
                OrderQty     INTEGER NOT NULL DEFAULT 0,
                CreatedUtc   TEXT NOT NULL,
                IssuedUtc    TEXT NULL,
                CompletedUtc TEXT NULL,
                Status       TEXT NOT NULL DEFAULT 'Available'
            );

            CREATE TABLE IF NOT EXISTS Codes (
                OrderNo     TEXT NOT NULL,
                Code        TEXT NOT NULL PRIMARY KEY,
                Gtin        TEXT NOT NULL DEFAULT '',
                Serial      TEXT NOT NULL DEFAULT '',
                ActivatedUtc TEXT NULL,
                FOREIGN KEY (OrderNo) REFERENCES Orders (OrderNo) ON DELETE CASCADE
            );

            CREATE INDEX IF NOT EXISTS IX_Codes_OrderNo ON Codes (OrderNo);
            """;
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Tạo PO mới kèm tập mã sinh tự động theo format TXNG Digital Link.
    /// </summary>
    public ProductionOrder CreateOrder(string orderNo, int orderQty, string gtin = DefaultGtin)
    {
        orderNo = (orderNo ?? "").Trim();
        if (orderNo.Length == 0)
            throw new ArgumentException("orderNo không được để trống.", nameof(orderNo));

        if (orderQty <= 0)
            orderQty = 100;

        var now = DateTime.UtcNow;

        using var connection = new SqliteConnection($"Data Source={_dbPath}");
        connection.Open();

        using var tx = connection.BeginTransaction();

        using (var existing = connection.CreateCommand())
        {
            existing.Transaction = tx;
            existing.CommandText = "SELECT COUNT(*) FROM Orders WHERE OrderNo = $orderNo;";
            existing.Parameters.AddWithValue("$orderNo", orderNo);
            if (Convert.ToInt64(existing.ExecuteScalar()) > 0)
            {
                tx.Rollback();
                throw new InvalidOperationException($"PO '{orderNo}' đã tồn tại.");
            }
        }

        using (var insertOrder = connection.CreateCommand())
        {
            insertOrder.Transaction = tx;
            insertOrder.CommandText = """
                INSERT INTO Orders (OrderNo, OrderQty, CreatedUtc, Status)
                VALUES ($orderNo, $qty, $createdUtc, 'Available');
                """;
            insertOrder.Parameters.AddWithValue("$orderNo", orderNo);
            insertOrder.Parameters.AddWithValue("$qty", orderQty);
            insertOrder.Parameters.AddWithValue("$createdUtc", now.ToString("O"));
            insertOrder.ExecuteNonQuery();
        }

        using (var insertCode = connection.CreateCommand())
        {
            insertCode.Transaction = tx;
            insertCode.CommandText = """
                INSERT INTO Codes (OrderNo, Code, Gtin, Serial, ActivatedUtc)
                VALUES ($orderNo, $code, $gtin, $serial, NULL);
                """;
            var pOrder = insertCode.Parameters.Add("$orderNo", SqliteType.Text);
            var pCode = insertCode.Parameters.Add("$code", SqliteType.Text);
            var pGtin = insertCode.Parameters.Add("$gtin", SqliteType.Text);
            var pSerial = insertCode.Parameters.Add("$serial", SqliteType.Text);

            for (var i = 1; i <= orderQty; i++)
            {
                var serial = $"PO{orderNo}{i:D6}";
                pOrder.Value = orderNo;
                pCode.Value = BuildDigitalLink(gtin, serial);
                pGtin.Value = gtin;
                pSerial.Value = serial;
                insertCode.ExecuteNonQuery();
            }
        }

        tx.Commit();

        return new ProductionOrder
        {
            OrderNo = orderNo,
            OrderQty = orderQty,
            CreatedUtc = now,
        };
    }

    /// <summary>
    /// Danh sách PO còn hiệu lực (chưa hoàn tất / chưa đóng).
    /// </summary>
    public List<ProductionOrder> ListOrders(bool includeCompleted = false)
    {
        using var connection = new SqliteConnection($"Data Source={_dbPath}");
        connection.Open();

        using var cmd = connection.CreateCommand();
        cmd.CommandText = includeCompleted
            ? "SELECT OrderNo, OrderQty, CreatedUtc FROM Orders ORDER BY CreatedUtc DESC;"
            : """
              SELECT OrderNo, OrderQty, CreatedUtc FROM Orders
              WHERE Status NOT IN ('Completed', 'Closed')
              ORDER BY CreatedUtc DESC;
              """;

        var list = new List<ProductionOrder>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(new ProductionOrder
            {
                OrderNo = reader.GetString(0),
                OrderQty = reader.GetInt32(1),
                CreatedUtc = DateTime.Parse(
                    reader.GetString(2), null, System.Globalization.DateTimeStyles.RoundtripKind),
            });
        }

        return list;
    }

    /// <summary>
    /// Lấy tập mã của một PO.
    /// </summary>
    public List<PoCode> GetCodes(string orderNo)
    {
        using var connection = new SqliteConnection($"Data Source={_dbPath}");
        connection.Open();

        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT Code, Gtin, Serial, ActivatedUtc FROM Codes
            WHERE OrderNo = $orderNo ORDER BY Serial;
            """;
        cmd.Parameters.AddWithValue("$orderNo", orderNo);

        var list = new List<PoCode>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(new PoCode
            {
                Code = reader.GetString(0),
                Gtin = reader.GetString(1),
                Serial = reader.GetString(2),
                ActivatedUtc = reader.IsDBNull(3)
                    ? null
                    : DateTime.Parse(
                        reader.GetString(3), null,
                        System.Globalization.DateTimeStyles.RoundtripKind),
            });
        }

        return list;
    }

    /// <summary>
    /// Đánh dấu một danh sách mã của PO đã được kích hoạt.
    /// </summary>
    public int Activate(string orderNo, IReadOnlyList<string> serials)
    {
        if (serials.Count == 0)
            return 0;

        var now = DateTime.UtcNow.ToString("O");

        using var connection = new SqliteConnection($"Data Source={_dbPath}");
        connection.Open();

        using var tx = connection.BeginTransaction();

        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            UPDATE Codes SET ActivatedUtc = $activatedUtc
            WHERE OrderNo = $orderNo AND (Serial = $serial OR Code = $serial)
              AND ActivatedUtc IS NULL;
            """;
        var pActivated = cmd.Parameters.Add("$activatedUtc", SqliteType.Text);
        var pOrder = cmd.Parameters.Add("$orderNo", SqliteType.Text);
        var pSerial = cmd.Parameters.Add("$serial", SqliteType.Text);

        var count = 0;
        foreach (var serial in serials)
        {
            pActivated.Value = now;
            pOrder.Value = orderNo;
            pSerial.Value = serial;
            count += cmd.ExecuteNonQuery();
        }

        tx.Commit();
        return count;
    }

    /// <summary>
    /// Đánh dấu PO đã được cấp (issued) cho SCADA.
    /// </summary>
    public void MarkIssued(string orderNo)
    {
        using var connection = new SqliteConnection($"Data Source={_dbPath}");
        connection.Open();

        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            UPDATE Orders SET IssuedUtc = $issuedUtc, Status = 'Downloaded'
            WHERE OrderNo = $orderNo AND IssuedUtc IS NULL;
            """;
        cmd.Parameters.AddWithValue("$issuedUtc", DateTime.UtcNow.ToString("O"));
        cmd.Parameters.AddWithValue("$orderNo", orderNo);
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Sinh link TXNG Digital Link theo chuẩn <c>/01/{gtin}/21/{serial}</c>.
    /// </summary>
    public static string BuildDigitalLink(string gtin, string serial)
        => $"https://digital-link.txng.vn/01/{gtin}/21/{serial}";
}
