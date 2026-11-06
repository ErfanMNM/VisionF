using System.Data;
using System.Globalization;
using Microsoft.Data.Sqlite;

namespace ClassLibraryMT.Plc;

/// <summary>
/// Lop luu tru tham so PLC va recipe vao SQLite.
///
/// Chiu trách nhiệm:
/// - Tao schema (bang <c>PlcParams</c>, <c>Recipes</c>, <c>RecipeValues</c>,
///   <c>SchemaVersion</c>) neu chua co.
/// - Migration: them cot moi vao bang da ton tai (kiem tra <c>PRAGMA table_info</c>).
/// - Thiet lap PRAGMA (WAL, busy_timeout...).
///
/// Moi thao tac mo connection rieng (SQLite nhe, khong can connection pool phuc tap).
/// </summary>
internal sealed class PlcStore
{
    /// <summary>Phien ban schema hien tai.</summary>
    public const int CurrentSchemaVersion = 1;

    private readonly string _dbPath;

    /// <summary>
    /// Khoi tao lop luu tru.
    /// </summary>
    /// <param name="dbPath">Duong dan tuyet doi toi file database.</param>
    public PlcStore(string dbPath)
    {
        _dbPath = dbPath;
    }

    /// <summary>Duong dan file database dang dung.</summary>
    public string DatabasePath => _dbPath;

    /// <summary>
    /// Mo connection da thiet lap PRAGMA va dam bao schema ton tai.
    /// </summary>
    /// <param name="ct">Token huy.</param>
    /// <returns>Connection san dung (caller dispose).</returns>
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
    /// Tao bang/index neu chua co va chay migration cho cac phien ban cu.
    /// </summary>
    private static async Task EnsureSchemaAsync(SqliteConnection connection, CancellationToken ct)
    {
        const string schema = """
            CREATE TABLE IF NOT EXISTS SchemaVersion (
                Version     INTEGER NOT NULL PRIMARY KEY,
                AppliedUtc  TEXT    NOT NULL
            );

            CREATE TABLE IF NOT EXISTS PlcParams (
                Id           INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                Name         TEXT    NOT NULL UNIQUE,
                Address      TEXT    NOT NULL,
                ValueType    INTEGER NOT NULL DEFAULT 0,
                AccessMode   INTEGER NOT NULL DEFAULT 0,
                Description  TEXT    NULL,
                SortOrder    INTEGER NOT NULL DEFAULT 0,
                UpdatedUtc   TEXT    NOT NULL
            );

            CREATE TABLE IF NOT EXISTS Recipes (
                Id          INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                Name        TEXT    NOT NULL UNIQUE,
                IsDefault   INTEGER NOT NULL DEFAULT 0,
                CreatedUtc  TEXT    NOT NULL,
                UpdatedUtc  TEXT    NOT NULL
            );

            CREATE TABLE IF NOT EXISTS RecipeValues (
                RecipeId    INTEGER NOT NULL,
                ParameterId INTEGER NOT NULL,
                Value       TEXT    NOT NULL,
                UpdatedUtc  TEXT    NOT NULL,
                PRIMARY KEY (RecipeId, ParameterId),
                FOREIGN KEY (RecipeId)    REFERENCES Recipes (Id)    ON DELETE CASCADE,
                FOREIGN KEY (ParameterId) REFERENCES PlcParams (Id) ON DELETE CASCADE
            );

            CREATE INDEX IF NOT EXISTS IX_PlcParams_SortOrder ON PlcParams (SortOrder, Name);
            CREATE INDEX IF NOT EXISTS IX_RecipeValues_ParameterId ON RecipeValues (ParameterId);
            """;

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = schema;
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

        // Migration: bang da ton tai tu phien ban cu co the thieu cot.
        await EnsureColumnAsync(connection, "PlcParams", "Description", "TEXT NULL", ct)
            .ConfigureAwait(false);
        await EnsureColumnAsync(connection, "PlcParams", "SortOrder", "INTEGER NOT NULL DEFAULT 0", ct)
            .ConfigureAwait(false);
        await EnsureColumnAsync(connection, "PlcParams", "UpdatedUtc", "TEXT NOT NULL DEFAULT ''", ct)
            .ConfigureAwait(false);
        await EnsureColumnAsync(connection, "Recipes", "IsDefault", "INTEGER NOT NULL DEFAULT 0", ct)
            .ConfigureAwait(false);
        await EnsureColumnAsync(connection, "Recipes", "CreatedUtc", "TEXT NOT NULL DEFAULT ''", ct)
            .ConfigureAwait(false);
        await EnsureColumnAsync(connection, "Recipes", "UpdatedUtc", "TEXT NOT NULL DEFAULT ''", ct)
            .ConfigureAwait(false);
        await EnsureColumnAsync(connection, "RecipeValues", "UpdatedUtc", "TEXT NOT NULL DEFAULT ''", ct)
            .ConfigureAwait(false);

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
    /// Dam bao mot cot co trong bang (them bang ALTER TABLE neu thieu).
    /// </summary>
    private static async Task EnsureColumnAsync(
        SqliteConnection connection,
        string table,
        string column,
        string definition,
        CancellationToken ct)
    {
        var existing = await GetColumnNamesAsync(connection, table, ct).ConfigureAwait(false);
        if (existing.Contains(column))
        {
            return;
        }

        await using var alter = connection.CreateCommand();
        alter.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {definition};";
        await alter.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Lay danh sach ten cot cua mot bang (dung cho migration).
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
    /// Chay mot cau lenh PRAGMA.
    /// </summary>
    private static async Task ExecutePragmaAsync(
        SqliteConnection connection, string sql, CancellationToken ct)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------
    // Doc / ghi tham so
    // ------------------------------------------------------------------

    /// <summary>
    /// Doc tat ca tham so, sap xep theo <c>SortOrder</c> roi <c>Name</c>.
    /// </summary>
    /// <param name="ct">Token huy.</param>
    /// <returns>Danh sach tham so.</returns>
    public async Task<IReadOnlyList<PlcParameter>> GetParametersAsync(CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct).ConfigureAwait(false);

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT Id, Name, Address, ValueType, AccessMode, Description, SortOrder, UpdatedUtc
            FROM PlcParams
            ORDER BY SortOrder ASC, Name COLLATE NOCASE ASC;
            """;

        var list = new List<PlcParameter>();

        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            list.Add(MapParameter(reader));
        }

        return list;
    }

    /// <summary>
    /// Doc mot tham so theo Id.
    /// </summary>
    /// <param name="id">Id tham so.</param>
    /// <param name="ct">Token huy.</param>
    /// <returns>Tham so hoac null.</returns>
    public async Task<PlcParameter?> GetParameterByIdAsync(long id, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct).ConfigureAwait(false);

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT Id, Name, Address, ValueType, AccessMode, Description, SortOrder, UpdatedUtc
            FROM PlcParams
            WHERE Id = $id;
            """;
        cmd.Parameters.AddWithValue("$id", id);

        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await reader.ReadAsync(ct).ConfigureAwait(false) ? MapParameter(reader) : null;
    }

    /// <summary>
    /// Them hoac cap nhat mot tham so (Id = 0 -> them).
    /// </summary>
    /// <param name="parameter">Tham so can luu.</param>
    /// <param name="ct">Token huy.</param>
    /// <returns>Ket qua.</returns>
    public async Task<Result> SaveParameterAsync(PlcParameter parameter, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(parameter);

        await using var connection = await OpenAsync(ct).ConfigureAwait(false);

        var now = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);

        await using var cmd = connection.CreateCommand();
        if (parameter.Id <= 0)
        {
            cmd.CommandText = """
                INSERT INTO PlcParams
                    (Name, Address, ValueType, AccessMode, Description, SortOrder, UpdatedUtc)
                VALUES
                    ($name, $address, $valueType, $accessMode, $description, $sortOrder, $updatedUtc);
                """;
            cmd.Parameters.AddWithValue("$name", parameter.Name.Trim());
            cmd.Parameters.AddWithValue("$address", parameter.Address.Trim());
            cmd.Parameters.AddWithValue("$valueType", (int)parameter.ValueType);
            cmd.Parameters.AddWithValue("$accessMode", (int)parameter.AccessMode);
            cmd.Parameters.AddWithValue("$description", (object?)parameter.Description ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$sortOrder", parameter.SortOrder);
            cmd.Parameters.AddWithValue("$updatedUtc", now);
        }
        else
        {
            cmd.CommandText = """
                UPDATE PlcParams
                SET Name = $name,
                    Address = $address,
                    ValueType = $valueType,
                    AccessMode = $accessMode,
                    Description = $description,
                    SortOrder = $sortOrder,
                    UpdatedUtc = $updatedUtc
                WHERE Id = $id;
                """;
            cmd.Parameters.AddWithValue("$name", parameter.Name.Trim());
            cmd.Parameters.AddWithValue("$address", parameter.Address.Trim());
            cmd.Parameters.AddWithValue("$valueType", (int)parameter.ValueType);
            cmd.Parameters.AddWithValue("$accessMode", (int)parameter.AccessMode);
            cmd.Parameters.AddWithValue("$description", (object?)parameter.Description ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$sortOrder", parameter.SortOrder);
            cmd.Parameters.AddWithValue("$updatedUtc", now);
            cmd.Parameters.AddWithValue("$id", parameter.Id);
        }

        try
        {
            var affected = await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

            if (parameter.Id > 0 && affected == 0)
            {
                return Result.Fail("Khong tim thay tham so can cap nhat.");
            }

            return Result.Success();
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
        {
            // SQLITE_CONSTRAINT: trung ten / trung khoa.
            return Result.Fail($"Tham so '{parameter.Name}' da ton tai.");
        }
    }

    /// <summary>
    /// Xoa mot tham so (keo theo cac gia tri recipe lien quan qua FK ON DELETE CASCADE).
    /// </summary>
    /// <param name="id">Id tham so.</param>
    /// <param name="ct">Token huy.</param>
    /// <returns>Ket qua.</returns>
    public async Task<Result> DeleteParameterAsync(long id, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct).ConfigureAwait(false);

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "DELETE FROM PlcParams WHERE Id = $id;";
        cmd.Parameters.AddWithValue("$id", id);

        var affected = await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        return affected > 0
            ? Result.Success()
            : Result.Fail("Khong tim thay tham so can xoa.");
    }

    // ------------------------------------------------------------------
    // Doc / ghi recipe
    // ------------------------------------------------------------------

    /// <summary>
    /// Doc tat ca recipe.
    /// </summary>
    /// <param name="ct">Token huy.</param>
    /// <returns>Danh sach recipe.</returns>
    public async Task<IReadOnlyList<PlcRecipe>> GetRecipesAsync(CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct).ConfigureAwait(false);

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT Id, Name, IsDefault, CreatedUtc, UpdatedUtc
            FROM Recipes
            ORDER BY IsDefault DESC, Name COLLATE NOCASE ASC;
            """;

        var list = new List<PlcRecipe>();

        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            list.Add(MapRecipe(reader));
        }

        return list;
    }

    /// <summary>
    /// Doc mot recipe theo Id.
    /// </summary>
    /// <param name="id">Id recipe.</param>
    /// <param name="ct">Token huy.</param>
    /// <returns>Recipe hoac null.</returns>
    public async Task<PlcRecipe?> GetRecipeByIdAsync(long id, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct).ConfigureAwait(false);

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT Id, Name, IsDefault, CreatedUtc, UpdatedUtc
            FROM Recipes
            WHERE Id = $id;
            """;
        cmd.Parameters.AddWithValue("$id", id);

        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await reader.ReadAsync(ct).ConfigureAwait(false) ? MapRecipe(reader) : null;
    }

    /// <summary>
    /// Tao recipe moi. Neu chua co recipe nao, recipe dau tien se la mac dinh.
    /// </summary>
    /// <param name="name">Ten recipe.</param>
    /// <param name="ct">Token huy.</param>
    /// <returns>Ket qua.</returns>
    public async Task<Result> CreateRecipeAsync(string name, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return Result.Fail("Ten recipe khong duoc de trong.");
        }

        await using var connection = await OpenAsync(ct).ConfigureAwait(false);

        // Recipe dau tien la mac dinh.
        await using (var countCmd = connection.CreateCommand())
        {
            countCmd.CommandText = "SELECT COUNT(*) FROM Recipes;";
            var count = Convert.ToInt64(await countCmd.ExecuteScalarAsync(ct).ConfigureAwait(false));
            var isDefault = count == 0 ? 1 : 0;

            var now = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);

            await using var insert = connection.CreateCommand();
            insert.CommandText = """
                INSERT INTO Recipes (Name, IsDefault, CreatedUtc, UpdatedUtc)
                VALUES ($name, $isDefault, $createdUtc, $updatedUtc);
                """;
            insert.Parameters.AddWithValue("$name", name.Trim());
            insert.Parameters.AddWithValue("$isDefault", isDefault);
            insert.Parameters.AddWithValue("$createdUtc", now);
            insert.Parameters.AddWithValue("$updatedUtc", now);

            try
            {
                await insert.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
            catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
            {
                return Result.Fail($"Recipe '{name.Trim()}' da ton tai.");
            }
        }

        return Result.Success();
    }

    /// <summary>
    /// Xoa mot recipe (khong xoa recipe mac dinh).
    /// </summary>
    /// <param name="id">Id recipe.</param>
    /// <param name="ct">Token huy.</param>
    /// <returns>Ket qua.</returns>
    public async Task<Result> DeleteRecipeAsync(long id, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct).ConfigureAwait(false);

        await using (var check = connection.CreateCommand())
        {
            check.CommandText = "SELECT IsDefault FROM Recipes WHERE Id = $id;";
            check.Parameters.AddWithValue("$id", id);

            var value = await check.ExecuteScalarAsync(ct).ConfigureAwait(false);
            if (value is null)
            {
                return Result.Fail("Khong tim thay recipe can xoa.");
            }

            if (Convert.ToInt64(value) != 0)
            {
                return Result.Fail("Khong the xoa recipe mac dinh.");
            }
        }

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "DELETE FROM Recipes WHERE Id = $id;";
        cmd.Parameters.AddWithValue("$id", id);

        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        return Result.Success();
    }

    /// <summary>
    /// Luu gia tri cac tham so cho mot recipe (ghi de, xoa het gia tri cu).
    /// </summary>
    /// <param name="recipeId">Id recipe.</param>
    /// <param name="values">Danh sach gia tri theo ParameterId.</param>
    /// <param name="ct">Token huy.</param>
    /// <returns>Ket qua.</returns>
    public async Task<Result> SaveRecipeValuesAsync(
        long recipeId,
        IReadOnlyDictionary<long, string> values,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(values);

        await using var connection = await OpenAsync(ct).ConfigureAwait(false);

        // Recipe phai ton tai.
        await using (var check = connection.CreateCommand())
        {
            check.CommandText = "SELECT COUNT(*) FROM Recipes WHERE Id = $id;";
            check.Parameters.AddWithValue("$id", recipeId);

            var count = Convert.ToInt64(await check.ExecuteScalarAsync(ct).ConfigureAwait(false));
            if (count == 0)
            {
                return Result.Fail("Khong tim thay recipe.");
            }
        }

        var now = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);

        // Ghi trong transaction: xoa gia tri cu + ghi gia tri moi la mot lan
        // -> khong de lai recipe o trang thai nua dua (dang xoa het nhung chua ghi xong).
        await using var transaction = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);

        try
        {
            await using (var delete = connection.CreateCommand())
            {
                delete.Transaction = (SqliteTransaction)transaction;
                delete.CommandText = "DELETE FROM RecipeValues WHERE RecipeId = $recipeId;";
                delete.Parameters.AddWithValue("$recipeId", recipeId);
                await delete.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            foreach (var (parameterId, value) in values)
            {
                await using var insert = connection.CreateCommand();
                insert.Transaction = (SqliteTransaction)transaction;
                insert.CommandText = """
                    INSERT INTO RecipeValues (RecipeId, ParameterId, Value, UpdatedUtc)
                    VALUES ($recipeId, $parameterId, $value, $updatedUtc);
                    """;
                insert.Parameters.AddWithValue("$recipeId", recipeId);
                insert.Parameters.AddWithValue("$parameterId", parameterId);
                insert.Parameters.AddWithValue("$value", value);
                insert.Parameters.AddWithValue("$updatedUtc", now);

                try
                {
                    await insert.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                }
                catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
                {
                    // Tham so khong con ton tai -> bo qua gia tri nay.
                }
            }

            await using (var touch = connection.CreateCommand())
            {
                touch.Transaction = (SqliteTransaction)transaction;
                touch.CommandText = "UPDATE Recipes SET UpdatedUtc = $updatedUtc WHERE Id = $id;";
                touch.Parameters.AddWithValue("$updatedUtc", now);
                touch.Parameters.AddWithValue("$id", recipeId);
                await touch.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            await transaction.CommitAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            await transaction.RollbackAsync(ct).ConfigureAwait(false);
            throw;
        }

        return Result.Success();
    }

    /// <summary>
    /// Doc gia tri cac tham so cua mot recipe.
    /// </summary>
    /// <param name="recipeId">Id recipe.</param>
    /// <param name="ct">Token huy.</param>
    /// <returns>Danh sach gia tri theo ParameterId.</returns>
    public async Task<IReadOnlyDictionary<long, string>> GetRecipeValuesAsync(
        long recipeId,
        CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct).ConfigureAwait(false);

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT ParameterId, Value
            FROM RecipeValues
            WHERE RecipeId = $recipeId
            ORDER BY ParameterId ASC;
            """;
        cmd.Parameters.AddWithValue("$recipeId", recipeId);

        var values = new Dictionary<long, string>();

        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            values[reader.GetInt64(0)] = reader.GetString(1);
        }

        return values;
    }

    // ------------------------------------------------------------------
    // Mapping
    // ------------------------------------------------------------------

    /// <summary>
    /// Chuyen dong du lieu thanh <see cref="PlcParameter"/>.
    /// </summary>
    private static PlcParameter MapParameter(IDataRecord record) => new()
    {
        Id = record.GetInt64(0),
        Name = record.GetString(1),
        Address = record.GetString(2),
        ValueType = (PlcValueType)record.GetInt32(3),
        AccessMode = (PlcAccessMode)record.GetInt32(4),
        Description = record.IsDBNull(5) ? null : record.GetString(5),
        SortOrder = record.GetInt32(6),
        UpdatedUtc = ParseUtc(record.GetString(7)),
    };

    /// <summary>
    /// Chuyen dong du lieu thanh <see cref="PlcRecipe"/>.
    /// </summary>
    private static PlcRecipe MapRecipe(IDataRecord record) => new()
    {
        Id = record.GetInt64(0),
        Name = record.GetString(1),
        IsDefault = record.GetInt32(2) != 0,
        CreatedUtc = ParseUtc(record.GetString(3)),
        UpdatedUtc = ParseUtc(record.GetString(4)),
    };

    /// <summary>
    /// Parse chuoi thoi gian UTC theo format "O".
    /// </summary>
    private static DateTime ParseUtc(string value) =>
        DateTime.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind,
            out var parsed)
            ? parsed
            : DateTime.MinValue;

    /// <summary>
    /// Dinh dang thoi gian UTC theo format "O" (dung chung cho database).
    /// </summary>
    /// <param name="value">Thoi gian can dinh dang.</param>
    /// <returns>Chuoi da dinh dang.</returns>
    public static string FormatUtc(DateTime value) =>
        value.ToString("O", CultureInfo.InvariantCulture);
}
