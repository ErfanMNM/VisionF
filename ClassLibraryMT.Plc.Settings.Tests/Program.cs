using System.Globalization;
using ClassLibraryMT.Communications.Plc;
using ClassLibraryMT.Plc;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClassLibraryMT.Plc.Settings.Tests;

/// <summary>
/// Test cho lib ClassLibraryMT.Plc: schema, CRUD tham so, recipe, doc/ghi PLC.
///
/// Khong can PLC that: dung <see cref="FakePlcClient"/> de mo phong
/// doc/ghi o nho.
/// </summary>
internal static class Program
{
    private static async Task<int> Main()
    {
        Console.WriteLine("=== ClassLibraryMT.Plc.Tests ===");
        Console.WriteLine();

        Console.WriteLine("--- Schema ---");
        await TestRunner.RunAsync("Tao schema 4 bang + index", TestSchema);

        Console.WriteLine();
        Console.WriteLine("--- CRUD tham so ---");
        await TestRunner.RunAsync("Them / sua / xoa tham so", TestParameterCrud);
        await TestRunner.RunAsync("Tu choi trung ten tham so", TestDuplicateParameterName);
        await TestRunner.RunAsync("Tu choi dia chi khong hop le", TestInvalidAddress);
        await TestRunner.RunAsync("Tu choi kieu du lieu / dia chi khong khop", TestTypeAddressMismatch);

        Console.WriteLine();
        Console.WriteLine("--- Recipe ---");
        await TestRunner.RunAsync("Tao / xoa recipe", TestRecipeCrud);
        await TestRunner.RunAsync("Tu choi tao recipe trung ten", TestDuplicateRecipeName);
        await TestRunner.RunAsync("Luu / doc gia tri recipe", TestRecipeValues);

        Console.WriteLine();
        Console.WriteLine("--- Doc / ghi PLC ---");
        await TestRunner.RunAsync("Doc dung kieu Int16 / Int32 / Float / Bool", TestReadAllTypes);
        await TestRunner.RunAsync("Ghi dung kieu Int16 / Int32 / Float / Bool", TestWriteAllTypes);
        await TestRunner.RunAsync("Tu choi doc tham so chi ghi", TestReadWriteOnlyParam);
        await TestRunner.RunAsync("Tu choi ghi tham so chi doc", TestWriteReadOnlyParam);
        await TestRunner.RunAsync("Tu choi gia tri sai kieu", TestInvalidValue);
        await TestRunner.RunAsync("Ap dung recipe xuong PLC", TestApplyRecipe);

        return TestRunner.Summary();
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    /// <summary>Tao thu muc tam cho database.</summary>
    private static string CreateTempDir()
    {
        var dir = Path.Combine(
            Path.GetTempPath(),
            "VisionF.PlcTests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>Xoa thu muc tam.</summary>
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
            // Bo qua: file dang khoa.
        }
    }

    /// <summary>Tao store tro toi database tam.</summary>
    private static PlcStore CreateStore(string dir) =>
        new(Path.Combine(dir, "plc.db"));

    /// <summary>Tao service voi client PLC gia lap.</summary>
    private static PlcSettingsService CreateService(
        string dir,
        FakePlcClient? client = null)
    {
        var fake = client ?? new FakePlcClient();
        return new PlcSettingsService(
            CreateStore(dir),
            new FakeFactory(fake),
            NullLogger<PlcSettingsService>.Instance);
    }

    /// <summary>Tao tham so mau.</summary>
    private static PlcParameter MakeParameter(
        string name = "Delay Reject",
        string address = "D12",
        PlcValueType type = PlcValueType.Int16,
        PlcAccessMode mode = PlcAccessMode.ReadWrite,
        int sortOrder = 0) =>
        new()
        {
            Name = name,
            Address = address,
            ValueType = type,
            AccessMode = mode,
            SortOrder = sortOrder,
        };

    /// <summary>
    /// Tim tham so theo ten trong danh sach.
    ///
    /// Danh sach duoc sap xep theo SortOrder roi Name, nen phai tra theo ten
    /// chu khong dua vao thu tu hien thi.
    /// </summary>
    private static PlcParameter Find(IReadOnlyList<PlcParameter> parameters, string name) =>
        parameters.FirstOrDefault(p => p.Name == name)
        ?? throw new Exception($"Khong tim thay tham so '{name}'.");

    // ------------------------------------------------------------------
    // 1. Schema
    // ------------------------------------------------------------------

    private static async Task TestSchema()
    {
        var dir = CreateTempDir();

        try
        {
            var store = CreateStore(dir);

            await using (var connection = await store.OpenAsync())
            {
                var tables = await GetTableNamesAsync(connection);

                foreach (var expected in new[] { "SchemaVersion", "PlcParams", "Recipes", "RecipeValues" })
                {
                    TestRunner.IsTrue(tables.Contains(expected), $"Bang '{expected}' phai ton tai");
                }
            }

            // Dong lai va mo lai: schema phai giu nguyen (CREATE IF NOT EXISTS).
            await using (var connection = await store.OpenAsync())
            {
                var columns = await GetColumnNamesAsync(connection, "PlcParams");

                foreach (var expected in new[]
                {
                    "Id", "Name", "Address", "ValueType", "AccessMode",
                    "Description", "SortOrder", "UpdatedUtc",
                })
                {
                    TestRunner.IsTrue(columns.Contains(expected), $"Cot '{expected}' phai ton tai");
                }
            }

            // Index tren RecipeValues.
            await using (var connection = await store.OpenAsync())
            {
                var indexes = await GetIndexNamesAsync(connection, "RecipeValues");
                TestRunner.IsTrue(
                    indexes.Contains("IX_RecipeValues_ParameterId"),
                    "Index IX_RecipeValues_ParameterId phai ton tai");
            }
        }
        finally
        {
            Cleanup(dir);
        }
    }

    // ------------------------------------------------------------------
    // 2. CRUD tham so
    // ------------------------------------------------------------------

    private static async Task TestParameterCrud()
    {
        var dir = CreateTempDir();

        try
        {
            var service = CreateService(dir);

            // Them.
            var param = MakeParameter();
            var addResult = await service.SaveParameterAsync(param);
            TestRunner.IsTrue(addResult.Ok, "Them tham so thanh cong");

            var all = await service.GetParametersAsync();
            TestRunner.AreEqual(1, all.Count, "Co 1 tham so");

            var saved = all[0];
            TestRunner.AreEqual("Delay Reject", saved.Name, "Ten tham so");
            TestRunner.AreEqual("D12", saved.Address, "Dia chi o nho");
            TestRunner.AreEqual(PlcValueType.Int16, saved.ValueType, "Kieu du lieu");
            TestRunner.AreEqual(PlcAccessMode.ReadWrite, saved.AccessMode, "Che do truy cap");
            TestRunner.IsTrue(saved.CanRead, "ReadWrite duoc doc");
            TestRunner.IsTrue(saved.CanWrite, "ReadWrite duoc ghi");

            // Sua.
            saved.Name = "Delay Reject 2";
            saved.Address = "D20";
            saved.ValueType = PlcValueType.Int32;
            saved.AccessMode = PlcAccessMode.Read;

            var updateResult = await service.SaveParameterAsync(saved);
            TestRunner.IsTrue(updateResult.Ok, "Sua tham so thanh cong");

            var afterUpdate = await service.GetParameterByIdAsync(saved.Id);
            TestRunner.IsNotNull(afterUpdate, "Doc lai tham so sau khi sua");
            TestRunner.AreEqual("Delay Reject 2", afterUpdate!.Name, "Ten da doi");
            TestRunner.AreEqual("D20", afterUpdate.Address, "Dia chi da doi");
            TestRunner.AreEqual(PlcValueType.Int32, afterUpdate.ValueType, "Kieu da doi");
            TestRunner.AreEqual(PlcAccessMode.Read, afterUpdate.AccessMode, "Che do da doi");
            TestRunner.IsFalse(afterUpdate.CanWrite, "Chi doc -> khong ghi duoc");

            // Xoa.
            var deleteResult = await service.DeleteParameterAsync(saved.Id);
            TestRunner.IsTrue(deleteResult.Ok, "Xoa tham so thanh cong");

            var afterDelete = await service.GetParametersAsync();
            TestRunner.AreEqual(0, afterDelete.Count, "Khong con tham so");

            // Xoa tham so khong ton tai.
            var deleteMissing = await service.DeleteParameterAsync(9999);
            TestRunner.IsFalse(deleteMissing.Ok, "Xoa tham so khong ton tai bi tu choi");
        }
        finally
        {
            Cleanup(dir);
        }
    }

    private static async Task TestDuplicateParameterName()
    {
        var dir = CreateTempDir();

        try
        {
            var service = CreateService(dir);

            await service.SaveParameterAsync(MakeParameter("Delay Reject"));

            var duplicate = await service.SaveParameterAsync(MakeParameter("Delay Reject", "D30"));
            TestRunner.IsFalse(duplicate.Ok, "Them trung ten bi tu choi");
            TestRunner.Contains(duplicate.Message, "ton tai", "Thong bao bao trung ten");

            var all = await service.GetParametersAsync();
            TestRunner.AreEqual(1, all.Count, "Chi co 1 tham so");
        }
        finally
        {
            Cleanup(dir);
        }
    }

    private static async Task TestInvalidAddress()
    {
        var dir = CreateTempDir();

        try
        {
            var service = CreateService(dir);

            // Khong bat dau bang D/DM.
            var bad1 = await service.SaveParameterAsync(MakeParameter("A", "X12"));
            TestRunner.IsFalse(bad1.Ok, "Dia chi khong bat dau bang D bi tu choi");

            // Vuot gioi han vung D (32767).
            var bad2 = await service.SaveParameterAsync(MakeParameter("B", "D32768"));
            TestRunner.IsFalse(bad2.Ok, "Dia chi vuot 32767 bi tu choi");

            // Bit ngoai 0-15.
            var bad3 = await service.SaveParameterAsync(
                MakeParameter("C", "D12.16", PlcValueType.Bool));
            TestRunner.IsFalse(bad3.Ok, "Bit ngoai 0-15 bi tu choi");

            // Rong.
            var bad4 = await service.SaveParameterAsync(MakeParameter("D", ""));
            TestRunner.IsFalse(bad4.Ok, "Dia chi rong bi tu choi");

            // Ten rong.
            var bad5 = await service.SaveParameterAsync(MakeParameter("  ", "D12"));
            TestRunner.IsFalse(bad5.Ok, "Ten rong bi tu choi");

            // Hop le: DM12 va D32767.
            var ok1 = await service.SaveParameterAsync(MakeParameter("E", "DM12"));
            TestRunner.IsTrue(ok1.Ok, "Dia chi DM12 hop le");

            var ok2 = await service.SaveParameterAsync(MakeParameter("F", "D32767"));
            TestRunner.IsTrue(ok2.Ok, "Dia chi D32767 hop le");
        }
        finally
        {
            Cleanup(dir);
        }
    }

    private static async Task TestTypeAddressMismatch()
    {
        var dir = CreateTempDir();

        try
        {
            var service = CreateService(dir);

            // Bool nhung dia chi khong co phan bit.
            var bad1 = await service.SaveParameterAsync(
                MakeParameter("A", "D12", PlcValueType.Bool));
            TestRunner.IsFalse(bad1.Ok, "Bool + dia chi khong phai bit bi tu choi");

            // Int16 nhung dia chi co phan bit.
            var bad2 = await service.SaveParameterAsync(
                MakeParameter("B", "D12.05", PlcValueType.Int16));
            TestRunner.IsFalse(bad2.Ok, "Int16 + dia chi bit bi tu choi");

            // Hop le: Bool + dia chi bit.
            var ok = await service.SaveParameterAsync(
                MakeParameter("C", "D12.05", PlcValueType.Bool));
            TestRunner.IsTrue(ok.Ok, "Bool + dia chi bit hop le");
        }
        finally
        {
            Cleanup(dir);
        }
    }

    // ------------------------------------------------------------------
    // 3. Recipe
    // ------------------------------------------------------------------

    private static async Task TestRecipeCrud()
    {
        var dir = CreateTempDir();

        try
        {
            var service = CreateService(dir);

            // Recipe dau tien la mac dinh.
            var first = await service.CreateRecipeAsync("Default");
            TestRunner.IsTrue(first.Ok, "Tao recipe mac dinh thanh cong");

            var recipes = await service.GetRecipesAsync();
            TestRunner.AreEqual(1, recipes.Count, "Co 1 recipe");
            TestRunner.IsTrue(recipes[0].IsDefault, "Recipe dau tien la mac dinh");

            // Khong xoa duoc recipe mac dinh.
            var deleteDefault = await service.DeleteRecipeAsync(recipes[0].Id);
            TestRunner.IsFalse(deleteDefault.Ok, "Xoa recipe mac dinh bi tu choi");
            TestRunner.Contains(deleteDefault.Message, "mac dinh", "Thong bao bao recipe mac dinh");

            // Tao recipe thu hai va xoa duoc.
            var second = await service.CreateRecipeAsync("Line 1");
            TestRunner.IsTrue(second.Ok, "Tao recipe thu hai thanh cong");

            var all = await service.GetRecipesAsync();
            TestRunner.AreEqual(2, all.Count, "Co 2 recipe");

            var nonDefault = all.First(r => !r.IsDefault);
            var deleteSecond = await service.DeleteRecipeAsync(nonDefault.Id);
            TestRunner.IsTrue(deleteSecond.Ok, "Xoa recipe thuong thanh cong");

            var afterDelete = await service.GetRecipesAsync();
            TestRunner.AreEqual(1, afterDelete.Count, "Con 1 recipe");

            // Xoa recipe khong ton tai.
            var deleteMissing = await service.DeleteRecipeAsync(9999);
            TestRunner.IsFalse(deleteMissing.Ok, "Xoa recipe khong ton tai bi tu choi");
        }
        finally
        {
            Cleanup(dir);
        }
    }

    private static async Task TestDuplicateRecipeName()
    {
        var dir = CreateTempDir();

        try
        {
            var service = CreateService(dir);

            await service.CreateRecipeAsync("Line 1");
            var duplicate = await service.CreateRecipeAsync("Line 1");

            TestRunner.IsFalse(duplicate.Ok, "Tao recipe trung ten bi tu choi");
            TestRunner.Contains(duplicate.Message, "ton tai", "Thong bao bao trung ten");

            // Ten rong.
            var empty = await service.CreateRecipeAsync("   ");
            TestRunner.IsFalse(empty.Ok, "Tao recipe ten rong bi tu choi");
        }
        finally
        {
            Cleanup(dir);
        }
    }

    private static async Task TestRecipeValues()
    {
        var dir = CreateTempDir();

        try
        {
            var service = CreateService(dir);

            await service.SaveParameterAsync(MakeParameter("Delay Reject", "D12"));
            await service.SaveParameterAsync(
                MakeParameter("Reject Strength", "D14", PlcValueType.Int32));

            var parameters = await service.GetParametersAsync();
            TestRunner.AreEqual(2, parameters.Count, "Co 2 tham so");

            await service.CreateRecipeAsync("Line 1");
            var recipe = (await service.GetRecipesAsync()).First();

            var values = new Dictionary<long, string>
            {
                [Find(parameters, "Delay Reject").Id] = "2000",
                [Find(parameters, "Reject Strength").Id] = "20",
            };

            var save = await service.SaveRecipeValuesAsync(recipe.Id, values);
            TestRunner.IsTrue(save.Ok, "Luu gia tri recipe thanh cong");

            var loaded = await service.GetRecipeValuesAsync(recipe.Id);
            TestRunner.AreEqual(2, loaded.Count, "Doc lai 2 gia tri");
            TestRunner.AreEqual(
                "2000", loaded[Find(parameters, "Delay Reject").Id], "Gia tri tham so 1");
            TestRunner.AreEqual(
                "20", loaded[Find(parameters, "Reject Strength").Id], "Gia tri tham so 2");

            // Ghi de: chi giu 1 gia tri.
            var overwrite = await service.SaveRecipeValuesAsync(
                recipe.Id,
                new Dictionary<long, string> { [Find(parameters, "Delay Reject").Id] = "3000" });
            TestRunner.IsTrue(overwrite.Ok, "Ghi de gia tri thanh cong");

            var afterOverwrite = await service.GetRecipeValuesAsync(recipe.Id);
            TestRunner.AreEqual(1, afterOverwrite.Count, "Chi con 1 gia tri");
            TestRunner.AreEqual(
                "3000", afterOverwrite[Find(parameters, "Delay Reject").Id], "Gia tri da doi");

            // Recipe khong ton tai.
            var bad = await service.SaveRecipeValuesAsync(
                9999,
                new Dictionary<long, string> { [Find(parameters, "Delay Reject").Id] = "1" });
            TestRunner.IsFalse(bad.Ok, "Luu vao recipe khong ton tai bi tu choi");
        }
        finally
        {
            Cleanup(dir);
        }
    }

    // ------------------------------------------------------------------
    // 4. Doc / ghi PLC
    // ------------------------------------------------------------------

    private static async Task TestReadAllTypes()
    {
        var dir = CreateTempDir();

        try
        {
            var client = new FakePlcClient();
            var service = CreateService(dir, client);

            await service.SaveParameterAsync(
                MakeParameter("I16", "D10", PlcValueType.Int16));
            await service.SaveParameterAsync(
                MakeParameter("I32", "D20", PlcValueType.Int32));
            await service.SaveParameterAsync(
                MakeParameter("F32", "D30", PlcValueType.Float));
            await service.SaveParameterAsync(
                MakeParameter("BIT", "D40.05", PlcValueType.Bool));

            client.Values["D10"] = 123;
            client.Values["D20"] = -45678;
            client.Values["D30"] = 12.5f;
            client.Values["D40.05"] = true;

            var parameters = await service.GetParametersAsync();
            TestRunner.AreEqual(4, parameters.Count, "Co 4 tham so");

            var results = await service.ReadAllAsync();

            TestRunner.AreEqual(4, results.Count, "Doc 4 tham so");
            TestRunner.IsTrue(results.All(r => r.IsSuccess), "Tat ca doc thanh cong");

            // ReadAllAsync tra ket qua theo thu tu danh sach tham so.
            var i16 = await service.ReadParameterAsync(Find(parameters, "I16"));
            var i32 = await service.ReadParameterAsync(Find(parameters, "I32"));
            var f32 = await service.ReadParameterAsync(Find(parameters, "F32"));
            var bit = await service.ReadParameterAsync(Find(parameters, "BIT"));

            TestRunner.AreEqual("123", i16.Value, "Gia tri Int16");
            TestRunner.AreEqual("-45678", i32.Value, "Gia tri Int32");
            TestRunner.AreEqual("12.5", f32.Value, "Gia tri Float");
            TestRunner.AreEqual("true", bit.Value, "Gia tri Bool");
        }
        finally
        {
            Cleanup(dir);
        }
    }

    private static async Task TestWriteAllTypes()
    {
        var dir = CreateTempDir();

        try
        {
            var client = new FakePlcClient();
            var service = CreateService(dir, client);

            var p16 = MakeParameter("I16", "D10", PlcValueType.Int16);
            var p32 = MakeParameter("I32", "D20", PlcValueType.Int32);
            var pf = MakeParameter("F32", "D30", PlcValueType.Float);
            var pb = MakeParameter("BIT", "D40.05", PlcValueType.Bool);

            foreach (var p in new[] { p16, p32, pf, pb })
            {
                await service.SaveParameterAsync(p);
            }

            var w16 = await service.WriteParameterAsync(p16, "123");
            TestRunner.IsTrue(w16.IsSuccess, "Ghi Int16 thanh cong");
            TestRunner.AreEqual((short)123, client.Values["D10"], "Gia tri da ghi D10");

            var w32 = await service.WriteParameterAsync(p32, "-45678");
            TestRunner.IsTrue(w32.IsSuccess, "Ghi Int32 thanh cong");
            TestRunner.AreEqual(-45678, client.Values["D20"], "Gia tri da ghi D20");

            var wf = await service.WriteParameterAsync(pf, "12.5");
            TestRunner.IsTrue(wf.IsSuccess, "Ghi Float thanh cong");
            TestRunner.AreEqual(12.5f, client.Values["D30"], "Gia tri da ghi D30");

            var wb = await service.WriteParameterAsync(pb, "true");
            TestRunner.IsTrue(wb.IsSuccess, "Ghi Bool thanh cong");
            TestRunner.AreEqual(true, client.Values["D40.05"], "Gia tri da ghi D40.05");
        }
        finally
        {
            Cleanup(dir);
        }
    }

    private static async Task TestReadWriteOnlyParam()
    {
        var dir = CreateTempDir();

        try
        {
            var service = CreateService(dir);

            var param = MakeParameter("WriteOnly", "D10", PlcValueType.Int16, PlcAccessMode.Write);
            await service.SaveParameterAsync(param);

            var read = await service.ReadParameterAsync(param);
            TestRunner.IsFalse(read.IsSuccess, "Doc tham so chi ghi bi tu choi");
            TestRunner.Contains(read.Message, "chi ghi", "Thong bao bao chi ghi");
        }
        finally
        {
            Cleanup(dir);
        }
    }

    private static async Task TestWriteReadOnlyParam()
    {
        var dir = CreateTempDir();

        try
        {
            var service = CreateService(dir);

            var param = MakeParameter("ReadOnly", "D10", PlcValueType.Int16, PlcAccessMode.Read);
            await service.SaveParameterAsync(param);

            var write = await service.WriteParameterAsync(param, "1");
            TestRunner.IsFalse(write.IsSuccess, "Ghi tham so chi doc bi tu choi");
            TestRunner.Contains(write.Message, "chi doc", "Thong bao bao chi doc");
        }
        finally
        {
            Cleanup(dir);
        }
    }

    private static async Task TestInvalidValue()
    {
        var dir = CreateTempDir();

        try
        {
            var service = CreateService(dir);

            var p16 = MakeParameter("I16", "D10", PlcValueType.Int16);
            var pf = MakeParameter("F32", "D30", PlcValueType.Float);
            var pb = MakeParameter("BIT", "D40.05", PlcValueType.Bool);

            await service.SaveParameterAsync(p16);
            await service.SaveParameterAsync(pf);
            await service.SaveParameterAsync(pb);

            var bad1 = await service.WriteParameterAsync(p16, "abc");
            TestRunner.IsFalse(bad1.IsSuccess, "Ghi chuoi khong phai so bi tu choi");

            var bad2 = await service.WriteParameterAsync(p16, "");
            TestRunner.IsFalse(bad2.IsSuccess, "Ghi gia tri rong bi tu choi");

            var bad3 = await service.WriteParameterAsync(pf, "abc");
            TestRunner.IsFalse(bad3.IsSuccess, "Ghi float sai dinh dang bi tu choi");

            var bad4 = await service.WriteParameterAsync(pb, "xyz");
            TestRunner.IsFalse(bad4.IsSuccess, "Ghi bool sai dinh dang bi tu choi");

            // "1"/"0" hop le cho bool.
            var ok = await service.WriteParameterAsync(pb, "1");
            TestRunner.IsTrue(ok.IsSuccess, "Ghi bool bang '1' hop le");
        }
        finally
        {
            Cleanup(dir);
        }
    }

    private static async Task TestApplyRecipe()
    {
        var dir = CreateTempDir();

        try
        {
            var client = new FakePlcClient();
            var service = CreateService(dir, client);

            var p1 = MakeParameter("I16", "D10", PlcValueType.Int16);
            var p2 = MakeParameter("F32", "D30", PlcValueType.Float);
            var p3 = MakeParameter("ReadOnly", "D40", PlcValueType.Int16, PlcAccessMode.Read);

            await service.SaveParameterAsync(p1);
            await service.SaveParameterAsync(p2);
            await service.SaveParameterAsync(p3);

            var parameters = await service.GetParametersAsync();

            await service.CreateRecipeAsync("Line 1");
            var recipe = (await service.GetRecipesAsync()).First();

            // Map gia tri theo TEN tham so (danh sach bi sap xep theo SortOrder roi Name).
            var values = new Dictionary<long, string>
            {
                [Find(parameters, "I16").Id] = "111",
                [Find(parameters, "F32").Id] = "2.5",
                [Find(parameters, "ReadOnly").Id] = "999",
            };

            await service.SaveRecipeValuesAsync(recipe.Id, values);

            var results = await service.ApplyRecipeAsync(recipe.Id);

            // Chi 2 tham so duoc ghi (thu 3 chi doc -> bo qua).
            TestRunner.AreEqual(2, results.Count, "Chi ghi 2 tham so co quyen ghi");
            TestRunner.IsTrue(results.All(r => r.IsSuccess), "Tat ca ghi thanh cong");

            TestRunner.AreEqual((short)111, client.Values["D10"], "Gia tri D10 theo recipe");
            TestRunner.AreEqual(2.5f, client.Values["D30"], "Gia tri D30 theo recipe");
            TestRunner.IsFalse(client.Values.ContainsKey("D40"), "Tham so chi doc khong duoc ghi");

            // Recipe khong ton tai.
            var missing = await service.ApplyRecipeAsync(9999);
            TestRunner.AreEqual(1, missing.Count, "Recipe khong ton tai tra ve 1 loi");
            TestRunner.IsFalse(missing[0].IsSuccess, "Recipe khong ton tai -> that bai");
        }
        finally
        {
            Cleanup(dir);
        }
    }

    // ------------------------------------------------------------------
    // Helpers de doc schema
    // ------------------------------------------------------------------

    private static async Task<HashSet<string>> GetTableNamesAsync(SqliteConnection connection)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table';";

        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            names.Add(reader.GetString(0));
        }

        return names;
    }

    private static async Task<HashSet<string>> GetColumnNamesAsync(
        SqliteConnection connection, string table)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info({table});";

        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            names.Add(reader.GetString(1));
        }

        return names;
    }

    private static async Task<HashSet<string>> GetIndexNamesAsync(
        SqliteConnection connection, string table)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"PRAGMA index_list({table});";

        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            names.Add(reader.GetString(1));
        }

        return names;
    }
}

/// <summary>
/// Client PLC gia lap: luon thanh cong, luu gia tri vao dictionary theo dia chi.
/// </summary>
internal sealed class FakePlcClient : IPlcMemoryClient
{
    /// <summary>Gia tri theo dia chi o nho.</summary>
    public Dictionary<string, object> Values { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Dia chi da goi doc (de kiem tra).</summary>
    public List<string> ReadAddresses { get; } = [];

    /// <summary>Dia chi da goi ghi (de kiem tra).</summary>
    public List<string> WrittenAddresses { get; } = [];

    public PlcResult<short> ReadInt16(string address)
    {
        ReadAddresses.Add(address);
        var value = Values.TryGetValue(address, out var v) ? Convert.ToInt16(v, CultureInfo.InvariantCulture) : (short)0;
        return PlcResult<short>.Ok(value, "OK", TimeSpan.Zero);
    }

    public PlcResult<int> ReadInt32(string address)
    {
        ReadAddresses.Add(address);
        var value = Values.TryGetValue(address, out var v) ? Convert.ToInt32(v, CultureInfo.InvariantCulture) : 0;
        return PlcResult<int>.Ok(value, "OK", TimeSpan.Zero);
    }

    public PlcResult<float> ReadFloat(string address)
    {
        ReadAddresses.Add(address);
        var value = Values.TryGetValue(address, out var v) ? Convert.ToSingle(v, CultureInfo.InvariantCulture) : 0f;
        return PlcResult<float>.Ok(value, "OK", TimeSpan.Zero);
    }

    public PlcResult<bool> ReadBool(string address)
    {
        ReadAddresses.Add(address);
        var value = Values.TryGetValue(address, out var v) && Convert.ToBoolean(v, CultureInfo.InvariantCulture);
        return PlcResult<bool>.Ok(value, "OK", TimeSpan.Zero);
    }

    public PlcResult WriteInt16(string address, short value)
    {
        WrittenAddresses.Add(address);
        Values[address] = value;
        return PlcResult.Ok("OK", TimeSpan.Zero);
    }

    public PlcResult WriteInt32(string address, int value)
    {
        WrittenAddresses.Add(address);
        Values[address] = value;
        return PlcResult.Ok("OK", TimeSpan.Zero);
    }

    public PlcResult WriteFloat(string address, float value)
    {
        WrittenAddresses.Add(address);
        Values[address] = value;
        return PlcResult.Ok("OK", TimeSpan.Zero);
    }

    public PlcResult WriteBool(string address, bool value)
    {
        WrittenAddresses.Add(address);
        Values[address] = value;
        return PlcResult.Ok("OK", TimeSpan.Zero);
    }

    public void Dispose()
    {
        // Khong can dispose gi.
    }
}

/// <summary>Factory tra ve cung mot client gia lap cho moi lan tao.</summary>
internal sealed class FakeFactory : IPlcMemoryClientFactory
{
    private readonly IPlcMemoryClient _client;

    public FakeFactory(IPlcMemoryClient client) => _client = client;

    public IPlcMemoryClient Create() => _client;
}
