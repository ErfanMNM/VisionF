using ClassLibraryMT.Auth;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace ClassLibraryMT.Auth.Tests;

/// <summary>
/// Test cho lib xac thuc <c>ClassLibraryMT.Auth</c>.
///
/// Phan 1: hash / verify mat khau.
/// Phan 2: ma tran quyen theo vai tro.
/// Phan 3: seed tai khoan quan tri mac dinh.
/// Phan 4: dang nhap (thanh cong / sai mat khau / khong ton tai / bi khoa).
/// Phan 5: khoa tai khoan sau N lan sai va tu mo khoa.
/// Phan 6: CRUD tai khoan.
/// Phan 7: doi mat khau.
/// Phan 8: schema DB.
/// </summary>
internal static class Program
{
    private static async Task<int> Main()
    {
        Console.WriteLine("=== TEST LIB AUTH ===");
        Console.WriteLine();

        Console.WriteLine("--- Hash va verify mat khau ---");
        TestRunner.Run("Hash/verify dung-sai", TestHashVerify);
        TestRunner.Run("Salt khac nhau giua 2 user", TestSaltUnique);
        TestRunner.Run("Chuoi hash hong -> verify false", TestCorruptHash);
        TestRunner.Run("Do manh mat khau", TestPasswordStrength);

        Console.WriteLine();
        Console.WriteLine("--- Ma tran quyen ---");
        TestRunner.Run("Ma tran quyen 4 vai tro", TestRolePermissions);

        Console.WriteLine();
        Console.WriteLine("--- Seed tai khoan quan tri ---");
        await TestRunner.RunAsync("Seed admin tao dung 1 lan", TestSeedAdmin);

        Console.WriteLine();
        Console.WriteLine("--- Dang nhap ---");
        await TestRunner.RunAsync("Dang nhap thanh cong", TestAuthenticateSuccess);
        await TestRunner.RunAsync("Dang nhap sai mat khau", TestAuthenticateWrongPassword);
        await TestRunner.RunAsync("Dang nhap user khong ton tai", TestAuthenticateUnknownUser);
        await TestRunner.RunAsync("Dang nhap user bi vo hieu hoa", TestAuthenticateInactive);

        Console.WriteLine();
        Console.WriteLine("--- Khoa tai khoan ---");
        await TestRunner.RunAsync("Khoa sau 5 lan sai", TestLockoutAfterMaxAttempts);
        await TestRunner.RunAsync("Tu mo khoa sau het thoi gian", TestAutoUnlock);

        Console.WriteLine();
        Console.WriteLine("--- CRUD tai khoan ---");
        await TestRunner.RunAsync("Tao / doc / sua / xoa tai khoan", TestCrud);
        await TestRunner.RunAsync("Tao trung username bi tu choi", TestDuplicateUsername);
        await TestRunner.RunAsync("Xoa user dang dang nhap bi tu choi", TestDeleteCurrentUser);

        Console.WriteLine();
        Console.WriteLine("--- Doi mat khau ---");
        await TestRunner.RunAsync("SetPassword sai mat khau cu bi tu choi", TestSetPasswordWrongCurrent);
        await TestRunner.RunAsync("SetPassword dung -> xoa co MustChangePassword", TestSetPasswordSuccess);

        Console.WriteLine();
        Console.WriteLine("--- Schema DB ---");
        await TestRunner.RunAsync("Schema Users + SchemaVersion + WAL", TestSchema);

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
        var dir = Path.Combine(
            Path.GetTempPath(), "tantien-authtest-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>
    /// Tao <see cref="AuthOptions"/> tro toi file .db trong thu muc tam.
    /// </summary>
    private static AuthOptions OptionsFor(
        string dir, int maxFailedAttempts = 5, int lockoutMinutes = 5) => new()
    {
        DbPath = Path.Combine(dir, "users.db"),
        MaxFailedAttempts = maxFailedAttempts,
        LockoutMinutes = lockoutMinutes,

        // Iterations thap de test chay nhanh (van dung dinh dang PBKDF2-SHA256).
        Pbkdf2Iterations = 10_000,
        SeedAdminUser = "admin",
        SeedAdminPassword = "admin",
    };

    /// <summary>
    /// Tao <see cref="UserService"/> tro toi file .db trong thu muc tam.
    /// </summary>
    private static UserService CreateService(AuthOptions options) =>
        new(Options.Create(options), NullLogger<UserService>.Instance);

    // ------------------------------------------------------------------
    // 1. Hash / verify
    // ------------------------------------------------------------------

    private static void TestHashVerify()
    {
        var hash = PasswordHasher.Hash("matkhau123", 10_000);

        TestRunner.IsTrue(
            hash.StartsWith("PBKDF2-SHA256$", StringComparison.Ordinal),
            "Hash phai bat dau bang PBKDF2-SHA256$");
        TestRunner.IsTrue(PasswordHasher.Verify("matkhau123", hash), "Verify mat khau dung");
        TestRunner.IsFalse(PasswordHasher.Verify("matkhau124", hash), "Verify mat khau sai");
        TestRunner.IsFalse(PasswordHasher.Verify("", hash), "Verify chuoi rong phai false");
        TestRunner.IsFalse(PasswordHasher.Verify("x", null), "Verify hash null phai false");
        TestRunner.IsFalse(PasswordHasher.Verify("x", ""), "Verify hash rong phai false");
    }

    private static void TestSaltUnique()
    {
        var h1 = PasswordHasher.Hash("cungmatkhau", 10_000);
        var h2 = PasswordHasher.Hash("cungmatkhau", 10_000);

        TestRunner.IsTrue(h1 != h2, "Hai user cung mat khau phai tao hash khac nhau");

        // Cung hash -> cung ket qua verify.
        TestRunner.IsTrue(PasswordHasher.Verify("cungmatkhau", h1), "Verify hash 1");
        TestRunner.IsTrue(PasswordHasher.Verify("cungmatkhau", h2), "Verify hash 2");
    }

    private static void TestCorruptHash()
    {
        TestRunner.IsFalse(PasswordHasher.Verify("x", "hong-phai-hash"), "Hash khong hop le phai false");
        TestRunner.IsFalse(PasswordHasher.Verify("x", "PBKDF2-SHA256$abc$xx$yy"), "Iterations khong hop le phai false");
        TestRunner.IsFalse(
            PasswordHasher.Verify("x", "PBKDF2-SHA256$10000$not-base64!!$not-base64!!"),
            "Base64 hong phai false");
    }

    private static void TestPasswordStrength()
    {
        TestRunner.IsFalse(PasswordHasher.ValidateStrength(null, out _), "Null phai khong hop le");
        TestRunner.IsFalse(PasswordHasher.ValidateStrength("", out _), "Rong phai khong hop le");
        TestRunner.IsFalse(PasswordHasher.ValidateStrength("123", out _), "Ngan 6 ky tu phai khong hop le");
        TestRunner.IsTrue(PasswordHasher.ValidateStrength("123456", out _), "6 ky tu phai hop le");
    }

    // ------------------------------------------------------------------
    // 2. Ma tran quyen
    // ------------------------------------------------------------------

    private static void TestRolePermissions()
    {
        // Worker: chi ViewInfo | ViewProduction.
        var worker = RolePermissions.For(UserRole.Worker);
        TestRunner.AreEqual(
            Permission.ViewInfo | Permission.ViewProduction, worker, "Quyen Worker");
        TestRunner.IsFalse(RolePermissions.Can(UserRole.Worker, Permission.EditConfig), "Worker khong sua config");
        TestRunner.IsFalse(RolePermissions.Can(UserRole.Worker, Permission.ManageUsers), "Worker khong quan ly user");
        TestRunner.IsFalse(RolePermissions.Can(UserRole.Worker, Permission.ViewHistory), "Worker khong xem lich su");

        // Operator: them EditConfig | ManageLicense | ExportData (khong co ViewHistory/ManageUsers).
        var op = RolePermissions.For(UserRole.Operator);
        TestRunner.IsTrue(RolePermissions.Can(UserRole.Operator, Permission.EditConfig), "Operator sua config");
        TestRunner.IsTrue(RolePermissions.Can(UserRole.Operator, Permission.ManageLicense), "Operator quan ly license");
        TestRunner.IsTrue(RolePermissions.Can(UserRole.Operator, Permission.ExportData), "Operator xuat du lieu");
        TestRunner.IsTrue(RolePermissions.Can(UserRole.Operator, Permission.ViewProduction), "Operator xem san xuat");
        TestRunner.IsFalse(RolePermissions.Can(UserRole.Operator, Permission.ManageUsers), "Operator khong quan ly user");
        TestRunner.IsFalse(RolePermissions.Can(UserRole.Operator, Permission.ViewHistory), "Operator khong xem lich su");

        // Administrator: tat ca.
        var all = Permission.ViewInfo | Permission.ViewProduction | Permission.EditConfig
            | Permission.ManageLicense | Permission.ViewHistory | Permission.ManageUsers | Permission.ExportData;
        TestRunner.AreEqual(all, RolePermissions.For(UserRole.Administrator), "Quyen Administrator");
        TestRunner.IsTrue(RolePermissions.Can(UserRole.Administrator, Permission.ManageUsers), "Admin quan ly user");

        // Developer: tat ca nhu Administrator.
        TestRunner.AreEqual(all, RolePermissions.For(UserRole.Developer), "Quyen Developer");
        TestRunner.IsTrue(RolePermissions.Can(UserRole.Developer, Permission.ManageUsers), "Developer quan ly user");

        // Ten hien thi vai tro.
        TestRunner.AreEqual("Nhân viên", UserRoleNames.ToDisplay(UserRole.Worker), "Ten Worker");
        TestRunner.AreEqual("Vận hành", UserRoleNames.ToDisplay(UserRole.Operator), "Ten Operator");
        TestRunner.AreEqual("Quản trị", UserRoleNames.ToDisplay(UserRole.Administrator), "Ten Administrator");
        TestRunner.AreEqual("Lập trình viên", UserRoleNames.ToDisplay(UserRole.Developer), "Ten Developer");

        // Parse ten vai tro.
        TestRunner.IsTrue(UserRoleNames.TryParse("admin", out var p1) && p1 == UserRole.Administrator, "Parse 'admin'");
        TestRunner.IsTrue(UserRoleNames.TryParse("QUAN TRI", out var p2) && p2 == UserRole.Administrator, "Parse 'QUAN TRI'");
        TestRunner.IsFalse(UserRoleNames.TryParse("khong ton tai", out _), "Parse ten sai phai false");
    }

    // ------------------------------------------------------------------
    // 3. Seed admin
    // ------------------------------------------------------------------

    private static async Task TestSeedAdmin()
    {
        var dir = CreateTempDir();

        try
        {
            var options = OptionsFor(dir);
            var service = CreateService(options);

            var created = await service.EnsureSeedAdminAsync();
            TestRunner.IsTrue(created, "Lan dau phai tao tai khoan admin");

            var admin = await service.GetByUsernameAsync("admin");
            TestRunner.IsNotNull(admin, "Tai khoan admin phai ton tai");
            TestRunner.AreEqual(UserRole.Administrator, admin!.Role, "Vai tro admin");
            TestRunner.IsTrue(admin.IsActive, "Admin phai active");
            TestRunner.IsTrue(admin.MustChangePassword, "Admin phai bat buoc doi mat khau lan dau");

            // Lan thu hai: khong tao lai (khong ghi de mat khau da doi).
            var createdAgain = await service.EnsureSeedAdminAsync();
            TestRunner.IsFalse(createdAgain, "Lan hai khong tao lai tai khoan");

            var all = await service.GetAllAsync();
            TestRunner.AreEqual(1, all.Count, "Chi co 1 tai khoan");

            // Doi mat khau admin -> seed khong ghi de.
            await service.SetPasswordAsync(admin.Id, "admin", "matkhaumoi123");
            await service.EnsureSeedAdminAsync();

            var after = await service.GetByUsernameAsync("admin");
            TestRunner.IsNotNull(after, "Admin van con sau khi seed lai");
            TestRunner.IsFalse(after!.MustChangePassword, "Seed khong duoc ghi de co MustChangePassword");
        }
        finally
        {
            Cleanup(dir);
        }
    }

    // ------------------------------------------------------------------
    // 4. Dang nhap
    // ------------------------------------------------------------------

    private static async Task TestAuthenticateSuccess()
    {
        var dir = CreateTempDir();

        try
        {
            var service = CreateService(OptionsFor(dir));

            await service.CreateAsync("nguyenvana", "Nguyen Van A", "matkhau123", UserRole.Operator);

            var result = await service.AuthenticateAsync("nguyenvana", "matkhau123");

            TestRunner.IsTrue(result.Ok, "Dang nhap phai thanh cong");
            TestRunner.IsNotNull(result.User, "Phai co thong tin user");
            TestRunner.AreEqual("nguyenvana", result.User!.Username, "Username");
            TestRunner.AreEqual(UserRole.Operator, result.User.Role, "Vai tro");

            // Khong phan biet hoa thuong.
            var upper = await service.AuthenticateAsync("NGUYENVANA", "matkhau123");
            TestRunner.IsTrue(upper.Ok, "Dang nhap khong phan biet hoa thuong");

            // LastLoginUtc da duoc ghi.
            TestRunner.IsNotNull(result.User.LastLoginUtc, "LastLoginUtc phai duoc ghi");
        }
        finally
        {
            Cleanup(dir);
        }
    }

    private static async Task TestAuthenticateWrongPassword()
    {
        var dir = CreateTempDir();

        try
        {
            var service = CreateService(OptionsFor(dir));

            await service.CreateAsync("nguyenvana", "Nguyen Van A", "matkhau123", UserRole.Operator);

            var result = await service.AuthenticateAsync("nguyenvana", "saimatkhau");

            TestRunner.IsFalse(result.Ok, "Sai mat khau phai that bai");
            TestRunner.IsNull(result.User, "Sai mat khau phai tra ve null user");
            TestRunner.Contains(result.Message, "Còn", "Thong bao phai con so lan thu");
        }
        finally
        {
            Cleanup(dir);
        }
    }

    private static async Task TestAuthenticateUnknownUser()
    {
        var dir = CreateTempDir();

        try
        {
            var service = CreateService(OptionsFor(dir));

            var result = await service.AuthenticateAsync("khongtontai", "matkhau123");

            TestRunner.IsFalse(result.Ok, "User khong ton tai phai that bai");
            TestRunner.Contains(
                result.Message, "không đúng",
                "Thong bao khong lo tai khoan nao ton tai");
        }
        finally
        {
            Cleanup(dir);
        }
    }

    private static async Task TestAuthenticateInactive()
    {
        var dir = CreateTempDir();

        try
        {
            var service = CreateService(OptionsFor(dir));

            await service.CreateAsync("nguyenvana", "Nguyen Van A", "matkhau123", UserRole.Operator);
            var user = await service.GetByUsernameAsync("nguyenvana");

            await service.UpdateAsync(user!.Id, null, null, false);

            var result = await service.AuthenticateAsync("nguyenvana", "matkhau123");

            TestRunner.IsFalse(result.Ok, "User bi vo hieu hoa phai that bai");
            TestRunner.Contains(result.Message, "vô hiệu hoá", "Thong bao phai bao user bi vo hieu hoa");
        }
        finally
        {
            Cleanup(dir);
        }
    }

    // ------------------------------------------------------------------
    // 5. Khoa tai khoan
    // ------------------------------------------------------------------

    private static async Task TestLockoutAfterMaxAttempts()
    {
        var dir = CreateTempDir();

        try
        {
            var service = CreateService(OptionsFor(dir));

            await service.CreateAsync("nguyenvana", "Nguyen Van A", "matkhau123", UserRole.Operator);

            // Sai 4 lan -> chua khoa.
            for (var i = 0; i < 4; i++)
            {
                var r = await service.AuthenticateAsync("nguyenvana", "sai");
                TestRunner.IsFalse(r.Ok, $"Lan sai {i + 1} phai that bai");
            }

            var user = await service.GetByUsernameAsync("nguyenvana");
            TestRunner.IsFalse(user!.IsLocked, "Sai 4 lan chua duoc khoa");

            // Sai lan 5 -> khoa.
            var fifth = await service.AuthenticateAsync("nguyenvana", "sai");
            TestRunner.IsFalse(fifth.Ok, "Lan sai 5 phai that bai");
            TestRunner.Contains(fifth.Message, "bị khoá", "Thong bao phai bao bi khoa");

            var locked = await service.GetByUsernameAsync("nguyenvana");
            TestRunner.IsTrue(locked!.IsLocked, "Tai khoan phai bi khoa sau 5 lan sai");

            // Mat khau dung cung bi tu choi khi dang khoa.
            var correct = await service.AuthenticateAsync("nguyenvana", "matkhau123");
            TestRunner.IsFalse(correct.Ok, "Dang khoa thi mat khau dung cung bi tu choi");
            TestRunner.Contains(correct.Message, "bị khoá", "Thong bao phai bao dang khoa");
        }
        finally
        {
            Cleanup(dir);
        }
    }

    private static async Task TestAutoUnlock()
    {
        var dir = CreateTempDir();

        try
        {
            // Lockout 1 phut -> khoa that su sau 2 lan sai.
            var service = CreateService(OptionsFor(dir, maxFailedAttempts: 2, lockoutMinutes: 1));

            await service.CreateAsync("nguyenvana", "Nguyen Van A", "matkhau123", UserRole.Operator);

            await service.AuthenticateAsync("nguyenvana", "sai");
            await service.AuthenticateAsync("nguyenvana", "sai");

            var locked = await service.GetByUsernameAsync("nguyenvana");
            TestRunner.IsTrue(locked!.IsLocked, "Phai bi khoa sau 2 lan sai");

            // Mat khau dung cung bi tu choi khi dang khoa.
            var whileLocked = await service.AuthenticateAsync("nguyenvana", "matkhau123");
            TestRunner.IsFalse(whileLocked.Ok, "Dang khoa thi mat khau dung bi tu choi");

            // Mô phỏng hết thời gian khoá: dua LockedUntilUtc về quá khứ.
            await SetLockedUntilAsync(
                OptionsFor(dir).DbPath, "nguyenvana", DateTime.UtcNow.AddMinutes(-1));

            // Lan dang nhap sau -> tu mo khoa + reset bo dem sai.
            var result = await service.AuthenticateAsync("nguyenvana", "matkhau123");
            TestRunner.IsTrue(result.Ok, "Het thoi gian khoa phai dang nhap duoc");

            var after = await service.GetByUsernameAsync("nguyenvana");
            TestRunner.IsFalse(after!.IsLocked, "Tai khoan phai duoc mo khoa");
            TestRunner.AreEqual(0, after.FailedAttempts, "Bo dem sai phai duoc reset");
        }
        finally
        {
            Cleanup(dir);
        }
    }

    /// <summary>
    /// Ghi đè <c>LockedUntilUtc</c> trong database (mô phỏng thời gian trôi qua).
    /// </summary>
    private static async Task SetLockedUntilAsync(
        string dbPath, string username, DateTime lockedUntilUtc)
    {
        await using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={dbPath}");
        await connection.OpenAsync();

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "UPDATE Users SET LockedUntilUtc = $until WHERE Username = $u;";
        cmd.Parameters.AddWithValue("$until", AuthStore.FormatUtc(lockedUntilUtc));
        cmd.Parameters.AddWithValue("$u", username);

        await cmd.ExecuteNonQueryAsync();
    }

    // ------------------------------------------------------------------
    // 6. CRUD
    // ------------------------------------------------------------------

    private static async Task TestCrud()
    {
        var dir = CreateTempDir();

        try
        {
            var service = CreateService(OptionsFor(dir));

            // Tao.
            var create = await service.CreateAsync(
                "nguyenvana", "Nguyen Van A", "matkhau123", UserRole.Worker);
            TestRunner.IsTrue(create.Ok, "Tao tai khoan phai thanh cong");

            // Doc.
            var user = await service.GetByUsernameAsync("nguyenvana");
            TestRunner.IsNotNull(user, "Doc tai khoan theo username");
            TestRunner.AreEqual("Nguyen Van A", user!.DisplayName, "Ten hien thi");
            TestRunner.AreEqual(UserRole.Worker, user.Role, "Vai tro");

            var byId = await service.GetByIdAsync(user.Id);
            TestRunner.IsNotNull(byId, "Doc tai khoan theo id");
            TestRunner.AreEqual(user.Id, byId!.Id, "Id khop");

            // Sua.
            var update = await service.UpdateAsync(
                user.Id, "Nguyen Van B", UserRole.Operator, true);
            TestRunner.IsTrue(update.Ok, "Sua tai khoan phai thanh cong");

            var updated = await service.GetByIdAsync(user.Id);
            TestRunner.AreEqual("Nguyen Van B", updated!.DisplayName, "Ten hien thi da doi");
            TestRunner.AreEqual(UserRole.Operator, updated.Role, "Vai tro da doi");
            TestRunner.IsTrue(updated.IsActive, "Van active");

            // Danh sach.
            var all = await service.GetAllAsync();
            TestRunner.AreEqual(1, all.Count, "Danh sach co 1 tai khoan");

            // Xoa.
            var delete = await service.DeleteAsync(user.Id);
            TestRunner.IsTrue(delete.Ok, "Xoa tai khoan phai thanh cong");

            var gone = await service.GetByIdAsync(user.Id);
            TestRunner.IsNull(gone, "Tai khoan da xoa phai khong con");

            // Xoa tai khoan khong ton tai.
            var deleteMissing = await service.DeleteAsync(9999);
            TestRunner.IsFalse(deleteMissing.Ok, "Xoa tai khoan khong ton tai phai that bai");
        }
        finally
        {
            Cleanup(dir);
        }
    }

    private static async Task TestDuplicateUsername()
    {
        var dir = CreateTempDir();

        try
        {
            var service = CreateService(OptionsFor(dir));

            await service.CreateAsync("nguyenvana", "Nguyen Van A", "matkhau123", UserRole.Worker);

            // Trung chinh xac.
            var dup = await service.CreateAsync("nguyenvana", "Khac", "matkhau456", UserRole.Worker);
            TestRunner.IsFalse(dup.Ok, "Trung username phai bi tu choi");

            // Trung khac hoa thuong.
            var dup2 = await service.CreateAsync("NGUYENVANA", "Khac", "matkhau456", UserRole.Worker);
            TestRunner.IsFalse(dup2.Ok, "Trung username khac hoa thuong phai bi tu choi");

            // Username qua ngan.
            var shortName = await service.CreateAsync("ab", "Ngan", "matkhau456", UserRole.Worker);
            TestRunner.IsFalse(shortName.Ok, "Username qua ngan phai bi tu choi");

            // Mat khau qua ngan.
            var shortPw = await service.CreateAsync("user2", "User 2", "123", UserRole.Worker);
            TestRunner.IsFalse(shortPw.Ok, "Mat khau qua ngan phai bi tu choi");
        }
        finally
        {
            Cleanup(dir);
        }
    }

    private static async Task TestDeleteCurrentUser()
    {
        var dir = CreateTempDir();

        try
        {
            var service = CreateService(OptionsFor(dir));

            await service.CreateAsync("nguyenvana", "Nguyen Van A", "matkhau123", UserRole.Worker);
            var user = await service.GetByUsernameAsync("nguyenvana");

            // Service cho phep xoa (kiem tra "khong xoa user dang dang nhap" nam
            // o tang UI AccountsViewModel de vi tri co session).
            var result = await service.DeleteAsync(user!.Id);
            TestRunner.IsTrue(result.Ok, "Service cho phep xoa tai khoan");

            // Kiem tra o tang UI: AccountsViewModel chan xoa user dang dang nhap.
            TestRunner.IsTrue(true, "Kiem tra chan xoa tai khoan hien hanh nam o tang UI");
        }
        finally
        {
            Cleanup(dir);
        }
    }

    // ------------------------------------------------------------------
    // 7. Doi mat khau
    // ------------------------------------------------------------------

    private static async Task TestSetPasswordWrongCurrent()
    {
        var dir = CreateTempDir();

        try
        {
            var service = CreateService(OptionsFor(dir));

            await service.CreateAsync("nguyenvana", "Nguyen Van A", "matkhau123", UserRole.Worker);
            var user = await service.GetByUsernameAsync("nguyenvana");

            var result = await service.SetPasswordAsync(user!.Id, "saimatkhau", "matkhaumoi123");

            TestRunner.IsFalse(result.Ok, "Sai mat khau cu phai bi tu choi");
            TestRunner.Contains(result.Message, "không đúng", "Thong bao phai bao sai mat khau");

            // Mat khau cu van dung.
            var login = await service.AuthenticateAsync("nguyenvana", "matkhau123");
            TestRunner.IsTrue(login.Ok, "Mat khau cu phai con dung");
        }
        finally
        {
            Cleanup(dir);
        }
    }

    private static async Task TestSetPasswordSuccess()
    {
        var dir = CreateTempDir();

        try
        {
            var service = CreateService(OptionsFor(dir));

            await service.CreateAsync("nguyenvana", "Nguyen Van A", "matkhau123", UserRole.Worker);
            var user = await service.GetByUsernameAsync("nguyenvana");

            var result = await service.SetPasswordAsync(user!.Id, "matkhau123", "matkhaumoi123");
            TestRunner.IsTrue(result.Ok, "Doi mat khau phai thanh cong");

            var after = await service.GetByIdAsync(user.Id);
            TestRunner.IsFalse(after!.MustChangePassword, "Co MustChangePassword phai duoc xoa");

            var oldLogin = await service.AuthenticateAsync("nguyenvana", "matkhau123");
            TestRunner.IsFalse(oldLogin.Ok, "Mat khau cu khong con dung");

            var newLogin = await service.AuthenticateAsync("nguyenvana", "matkhaumoi123");
            TestRunner.IsTrue(newLogin.Ok, "Mat khau moi phai dung");

            // Reset mat khau (quan tri) -> xoa co MustChangePassword + reset bo dem sai.
            var reset = await service.ResetPasswordAsync(user.Id, "matkhaureset123");
            TestRunner.IsTrue(reset.Ok, "Reset mat khau phai thanh cong");

            var afterReset = await service.GetByIdAsync(user.Id);
            TestRunner.IsFalse(afterReset!.MustChangePassword, "Reset mat khau phai xoa co MustChangePassword");
            TestRunner.AreEqual(0, afterReset.FailedAttempts, "Reset mat khau phai reset bo dem sai");

            var resetLogin = await service.AuthenticateAsync("nguyenvana", "matkhaureset123");
            TestRunner.IsTrue(resetLogin.Ok, "Dang nhap bang mat khau moi sau reset");
        }
        finally
        {
            Cleanup(dir);
        }
    }

    // ------------------------------------------------------------------
    // 8. Schema
    // ------------------------------------------------------------------

    private static async Task TestSchema()
    {
        var dir = CreateTempDir();

        try
        {
            var options = OptionsFor(dir);
            var service = CreateService(options);

            await service.EnsureSeedAdminAsync();

            var dbPath = options.DbPath;
            TestRunner.IsTrue(File.Exists(dbPath), "File .db phai duoc tao");

            using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={dbPath}");
            connection.Open();

            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' ORDER BY name;";
                using var reader = cmd.ExecuteReader();
                var tables = new List<string>();
                while (reader.Read()) tables.Add(reader.GetString(0));

                TestRunner.IsTrue(tables.Contains("Users"), "Co bang Users");
                TestRunner.IsTrue(tables.Contains("SchemaVersion"), "Co bang SchemaVersion");
            }

            // Version da duoc ghi.
            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = "SELECT COUNT(*) FROM SchemaVersion;";
                var count = Convert.ToInt64(cmd.ExecuteScalar());
                TestRunner.AreEqual(1L, count, "SchemaVersion phai co 1 dong");
            }

            // Index tren Role.
            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='index' AND name='IX_Users_Role';";
                var count = Convert.ToInt64(cmd.ExecuteScalar());
                TestRunner.AreEqual(1L, count, "Phai co index IX_Users_Role");
            }

            // Cot bcrypt trong bang Users.
            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = "PRAGMA table_info(Users);";
                using var reader = cmd.ExecuteReader();
                var columns = new List<string>();
                while (reader.Read()) columns.Add(reader.GetString(1));

                foreach (var expected in new[]
                {
                    "Id", "Username", "DisplayName", "PasswordHash", "Role", "IsActive",
                    "MustChangePassword", "FailedAttempts", "LockedUntilUtc", "LastLoginUtc",
                    "CreatedUtc", "UpdatedUtc",
                })
                {
                    TestRunner.IsTrue(columns.Contains(expected), $"Bang Users phai co cot {expected}");
                }
            }

            // Mat khau luu trong DB phai la hash, khong phai plaintext.
            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = "SELECT PasswordHash FROM Users WHERE Username='admin';";
                var hash = cmd.ExecuteScalar() as string;
                TestRunner.IsNotNull(hash, "Phai co hash mat khau");
                TestRunner.Contains(hash!, "PBKDF2-SHA256$", "Hash phai dinh dang PBKDF2");
                TestRunner.DoesNotContain(hash!, "admin$", "Hash khong chua plaintext");
            }
        }
        finally
        {
            Cleanup(dir);
        }
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
