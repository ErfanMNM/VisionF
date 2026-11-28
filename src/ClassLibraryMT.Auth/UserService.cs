using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ClassLibraryMT.Auth;

/// <summary>
/// Cài đặt <see cref="IUserService"/> lưu trữ tài khoản trong SQLite.
///
/// Mật khẩu KHÔNG BAO GIỜ được lưu dạng plaintext — chỉ lưu chuỗi đã băm
/// PBKDF2 (xem <see cref="PasswordHasher"/>).
/// </summary>
internal sealed class UserService : IUserService
{
    private readonly AuthStore _store;
    private readonly AuthOptions _opt;
    private readonly ILogger<UserService> _logger;

    /// <summary>
    /// Khởi tạo service.
    /// </summary>
    /// <param name="options">Cấu hình xác thực.</param>
    /// <param name="logger">Logger.</param>
    public UserService(IOptions<AuthOptions> options, ILogger<UserService> logger)
    {
        _opt = options.Value;
        _logger = logger;
        _store = new AuthStore(_opt.ResolveFullPath());
    }

    /// <summary>Đường dẫn file database đang dùng.</summary>
    public string DatabasePath => _store.DatabasePath;

    public async Task<AuthResult> AuthenticateAsync(
        string username, string password, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrEmpty(password))
        {
            return AuthResult.Fail("Vui lòng nhập tên đăng nhập và mật khẩu.");
        }

        var name = username.Trim();

        await using var connection = await _store.OpenAsync(ct).ConfigureAwait(false);

        var user = await AuthStore
            .ReadUserAsync(connection, "Username = $u COLLATE NOCASE", "$u", name, ct)
            .ConfigureAwait(false);

        // Thông báo chung cho mọi trường hợp thất bại -> không lộ cho attacker
        // biết tài khoản nào tồn tại (trừ trường hợp bị khoá, cần báo rõ).
        if (user is null)
        {
            return AuthResult.Fail("Tên đăng nhập hoặc mật khẩu không đúng.");
        }

        // Tài khoản đã bị khoá trước đó.
        if (user.LockedUntilUtc is { } until && until > DateTime.UtcNow)
        {
            var remain = (int)Math.Ceiling((until - DateTime.UtcNow).TotalMinutes);
            return AuthResult.Fail($"Tài khoản đang bị khoá. Thử lại sau {remain} phút.");
        }

        // Đã hết thời gian khoá -> mở khoá và reset bộ đếm.
        if (user.LockedUntilUtc is not null && user.LockedUntilUtc <= DateTime.UtcNow)
        {
            await ClearLockAsync(connection, user.Id, ct).ConfigureAwait(false);
            user.FailedAttempts = 0;
            user.IsLocked = false;
        }

        if (!user.IsActive)
        {
            return AuthResult.Fail("Tài khoản đã bị vô hiệu hoá. Liên hệ quản trị viên.");
        }

        // Đọc hash (không nằm trong UserAccount vì không nên lọt ra ngoài).
        var hash = await ReadHashAsync(connection, user.Id, ct).ConfigureAwait(false);

        if (!PasswordHasher.Verify(password, hash))
        {
            await RegisterFailureAsync(connection, user, ct).ConfigureAwait(false);

            // Cập nhật bộ đếm trong object để tính thông báo chính xác.
            user.FailedAttempts = Math.Min(user.FailedAttempts + 1, _opt.MaxFailedAttempts);
            user.IsLocked = user.FailedAttempts >= _opt.MaxFailedAttempts;

            _logger.LogWarning(
                "Dang nhap sai mat khau cho tai khoan {Username} (lan {Attempts}).",
                user.Username,
                user.FailedAttempts);

            if (user.IsLocked)
            {
                return AuthResult.Fail(
                    $"Sai mật khẩu quá {_opt.MaxFailedAttempts} lần. Tài khoản bị khoá {_opt.LockoutMinutes} phút.");
            }

            var remaining = _opt.MaxFailedAttempts - user.FailedAttempts;
            return AuthResult.Fail($"Mật khẩu không đúng. Còn {remaining} lần thử trước khi bị khoá.");
        }

        // Đăng nhập thành công: reset bộ đếm, ghi thời điểm đăng nhập.
        var now = DateTime.UtcNow;

        await using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = """
                UPDATE Users
                SET FailedAttempts = 0,
                    LockedUntilUtc = NULL,
                    LastLoginUtc = $lastLogin,
                    UpdatedUtc = $updated
                WHERE Id = $id;
                """;
            cmd.Parameters.AddWithValue("$lastLogin", AuthStore.FormatUtc(now));
            cmd.Parameters.AddWithValue("$updated", AuthStore.FormatUtc(now));
            cmd.Parameters.AddWithValue("$id", user.Id);
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        user.FailedAttempts = 0;
        user.IsLocked = false;
        user.LockedUntilUtc = null;
        user.LastLoginUtc = now;

        _logger.LogInformation("Dang nhap thanh cong: {Username} ({Role}).", user.Username, user.RoleName);

        return AuthResult.Success(user);
    }

    /// <inheritdoc />
    public async Task<Result> VerifyPasswordAsync(
        string username,
        string password,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrEmpty(password))
        {
            return Result.Fail("Vui lòng nhập mật khẩu.");
        }

        await using var connection = await _store.OpenAsync(ct).ConfigureAwait(false);

        var user = await AuthStore
            .ReadUserAsync(connection, "Username = $u COLLATE NOCASE", "$u", username.Trim(), ct)
            .ConfigureAwait(false);

        // Thông báo chung cho mọi trường hợp thất bại -> không lộ tài khoản nào tồn tại.
        if (user is null)
        {
            return Result.Fail("Mật khẩu không đúng.");
        }

        var hash = await ReadHashAsync(connection, user.Id, ct).ConfigureAwait(false);

        if (!PasswordHasher.Verify(password, hash))
        {
            return Result.Fail("Mật khẩu không đúng.");
        }

        return Result.Success("Mật khẩu chính xác.");
    }

    public async Task<IReadOnlyList<UserAccount>> GetAllAsync(CancellationToken ct = default)
    {
        await using var connection = await _store.OpenAsync(ct).ConfigureAwait(false);

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT Id, Username, DisplayName, PasswordHash, Role, IsActive,
                   MustChangePassword, FailedAttempts, LockedUntilUtc, LastLoginUtc,
                   CreatedUtc, UpdatedUtc
            FROM Users
            ORDER BY Username COLLATE NOCASE;
            """;

        var users = new List<UserAccount>();

        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            // MapUser đã tự tính IsLocked dựa trên LockedUntilUtc.
            users.Add(AuthStore.MapUser(reader));
        }

        return users;
    }

    public async Task<UserAccount?> GetByIdAsync(long id, CancellationToken ct = default)
    {
        await using var connection = await _store.OpenAsync(ct).ConfigureAwait(false);

        return await AuthStore
            .ReadUserAsync(connection, "Id = $id", "$id", id, ct)
            .ConfigureAwait(false);
    }

    public async Task<UserAccount?> GetByUsernameAsync(
        string username, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(username))
        {
            return null;
        }

        await using var connection = await _store.OpenAsync(ct).ConfigureAwait(false);

        return await AuthStore
            .ReadUserAsync(
                connection, "Username = $u COLLATE NOCASE", "$u", username.Trim(), ct)
            .ConfigureAwait(false);
    }

    public async Task<Result> CreateAsync(
        string username,
        string displayName,
        string password,
        UserRole role,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(username))
        {
            return Result.Fail("Tên đăng nhập không được để trống.");
        }

        if (username.Trim().Length < 3)
        {
            return Result.Fail("Tên đăng nhập phải có ít nhất 3 ký tự.");
        }

        if (!PasswordHasher.ValidateStrength(password, out var pwError))
        {
            return Result.Fail(pwError);
        }

        var name = username.Trim();

        await using var connection = await _store.OpenAsync(ct).ConfigureAwait(false);

        // Kiểm tra trùng tên đăng nhập (không phân biệt hoa thường).
        var existing = await AuthStore
            .ReadUserAsync(connection, "Username = $u COLLATE NOCASE", "$u", name, ct)
            .ConfigureAwait(false);

        if (existing is not null)
        {
            return Result.Fail($"Tên đăng nhập \"{name}\" đã tồn tại.");
        }

        var now = DateTime.UtcNow;
        var hash = PasswordHasher.Hash(password, _opt.Pbkdf2Iterations);

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO Users
                (Username, DisplayName, PasswordHash, Role, IsActive,
                 MustChangePassword, FailedAttempts, LockedUntilUtc, LastLoginUtc,
                 CreatedUtc, UpdatedUtc)
            VALUES
                ($username, $displayName, $hash, $role, 1,
                 0, 0, NULL, NULL,
                 $createdUtc, $updatedUtc);
            """;
        cmd.Parameters.AddWithValue("$username", name);
        cmd.Parameters.AddWithValue(
            "$displayName",
            string.IsNullOrWhiteSpace(displayName) ? name : displayName.Trim());
        cmd.Parameters.AddWithValue("$hash", hash);
        cmd.Parameters.AddWithValue("$role", (int)role);
        cmd.Parameters.AddWithValue("$createdUtc", AuthStore.FormatUtc(now));
        cmd.Parameters.AddWithValue("$updatedUtc", AuthStore.FormatUtc(now));

        try
        {
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
        {
            // UNIQUE constraint bị chặn bởi race condition giữa 2 lần tạo.
            return Result.Fail($"Tên đăng nhập \"{name}\" đã tồn tại.");
        }

        _logger.LogInformation("Da tao tai khoan {Username} voi vai tro {Role}.", name, role);

        return Result.Success($"Đã tạo tài khoản \"{name}\".");
    }

    public async Task<Result> UpdateAsync(
        long id,
        string? displayName,
        UserRole? role,
        bool? isActive,
        CancellationToken ct = default)
    {
        await using var connection = await _store.OpenAsync(ct).ConfigureAwait(false);

        var user = await AuthStore
            .ReadUserAsync(connection, "Id = $id", "$id", id, ct)
            .ConfigureAwait(false);

        if (user is null)
        {
            return Result.Fail("Không tìm thấy tài khoản.");
        }

        var now = DateTime.UtcNow;

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            UPDATE Users
            SET DisplayName = $displayName,
                Role = $role,
                IsActive = $isActive,
                UpdatedUtc = $updated
            WHERE Id = $id;
            """;
        cmd.Parameters.AddWithValue(
            "$displayName",
            displayName is null ? user.DisplayName : displayName.Trim());
        cmd.Parameters.AddWithValue("$role", (int)(role ?? user.Role));
        cmd.Parameters.AddWithValue("$isActive", (isActive ?? user.IsActive) ? 1 : 0);
        cmd.Parameters.AddWithValue("$updated", AuthStore.FormatUtc(now));
        cmd.Parameters.AddWithValue("$id", id);

        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

        _logger.LogInformation("Da cap nhat tai khoan {Username}.", user.Username);

        return Result.Success($"Đã cập nhật tài khoản \"{user.Username}\".");
    }

    public async Task<Result> ResetPasswordAsync(
        long id, string newPassword, CancellationToken ct = default)
    {
        if (!PasswordHasher.ValidateStrength(newPassword, out var pwError))
        {
            return Result.Fail(pwError);
        }

        await using var connection = await _store.OpenAsync(ct).ConfigureAwait(false);

        var user = await AuthStore
            .ReadUserAsync(connection, "Id = $id", "$id", id, ct)
            .ConfigureAwait(false);

        if (user is null)
        {
            return Result.Fail("Không tìm thấy tài khoản.");
        }

        var now = DateTime.UtcNow;
        var hash = PasswordHasher.Hash(newPassword, _opt.Pbkdf2Iterations);

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            UPDATE Users
            SET PasswordHash = $hash,
                MustChangePassword = 0,
                FailedAttempts = 0,
                LockedUntilUtc = NULL,
                UpdatedUtc = $updated
            WHERE Id = $id;
            """;
        cmd.Parameters.AddWithValue("$hash", hash);
        cmd.Parameters.AddWithValue("$updated", AuthStore.FormatUtc(now));
        cmd.Parameters.AddWithValue("$id", id);

        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

        _logger.LogInformation("Da dat lai mat khau cho tai khoan {Username}.", user.Username);

        return Result.Success($"Đã đặt lại mật khẩu cho \"{user.Username}\".");
    }

    public async Task<Result> SetPasswordAsync(
        long id, string currentPassword, string newPassword, CancellationToken ct = default)
    {
        if (!PasswordHasher.ValidateStrength(newPassword, out var pwError))
        {
            return Result.Fail(pwError);
        }

        await using var connection = await _store.OpenAsync(ct).ConfigureAwait(false);

        var user = await AuthStore
            .ReadUserAsync(connection, "Id = $id", "$id", id, ct)
            .ConfigureAwait(false);

        if (user is null)
        {
            return Result.Fail("Không tìm thấy tài khoản.");
        }

        var hash = await ReadHashAsync(connection, id, ct).ConfigureAwait(false);

        if (!PasswordHasher.Verify(currentPassword, hash))
        {
            return Result.Fail("Mật khẩu hiện tại không đúng.");
        }

        var now = DateTime.UtcNow;
        var newHash = PasswordHasher.Hash(newPassword, _opt.Pbkdf2Iterations);

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            UPDATE Users
            SET PasswordHash = $hash,
                MustChangePassword = 0,
                UpdatedUtc = $updated
            WHERE Id = $id;
            """;
        cmd.Parameters.AddWithValue("$hash", newHash);
        cmd.Parameters.AddWithValue("$updated", AuthStore.FormatUtc(now));
        cmd.Parameters.AddWithValue("$id", id);

        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

        _logger.LogInformation("Nguoi dung {Username} da doi mat khau.", user.Username);

        return Result.Success("Đã đổi mật khẩu thành công.");
    }

    public async Task<Result> DeleteAsync(long id, CancellationToken ct = default)
    {
        await using var connection = await _store.OpenAsync(ct).ConfigureAwait(false);

        var user = await AuthStore
            .ReadUserAsync(connection, "Id = $id", "$id", id, ct)
            .ConfigureAwait(false);

        if (user is null)
        {
            return Result.Fail("Không tìm thấy tài khoản.");
        }

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "DELETE FROM Users WHERE Id = $id;";
        cmd.Parameters.AddWithValue("$id", id);

        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

        _logger.LogInformation("Da xoa tai khoan {Username}.", user.Username);

        return Result.Success($"Đã xoá tài khoản \"{user.Username}\".");
    }

    public async Task<bool> EnsureSeedAdminAsync(CancellationToken ct = default)
    {
        await using var connection = await _store.OpenAsync(ct).ConfigureAwait(false);

        // Đếm số tài khoản: nếu đã có thì không seed lại (tránh reset mật khẩu
        // mà người dùng đã đổi).
        await using (var countCmd = connection.CreateCommand())
        {
            countCmd.CommandText = "SELECT COUNT(*) FROM Users;";
            var count = Convert.ToInt64(
                await countCmd.ExecuteScalarAsync(ct).ConfigureAwait(false),
                CultureInfo.InvariantCulture);

            if (count > 0)
            {
                return false;
            }
        }

        var username = string.IsNullOrWhiteSpace(_opt.SeedAdminUser)
            ? "admin"
            : _opt.SeedAdminUser.Trim();
        var password = string.IsNullOrEmpty(_opt.SeedAdminPassword)
            ? "admin"
            : _opt.SeedAdminPassword;

        var now = DateTime.UtcNow;
        var hash = PasswordHasher.Hash(password, _opt.Pbkdf2Iterations);

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO Users
                (Username, DisplayName, PasswordHash, Role, IsActive,
                 MustChangePassword, FailedAttempts, LockedUntilUtc, LastLoginUtc,
                 CreatedUtc, UpdatedUtc)
            VALUES
                ($username, $displayName, $hash, $role, 1,
                 1, 0, NULL, NULL,
                 $createdUtc, $updatedUtc);
            """;
        cmd.Parameters.AddWithValue("$username", username);
        cmd.Parameters.AddWithValue("$displayName", "Quản trị viên");
        cmd.Parameters.AddWithValue("$hash", hash);
        cmd.Parameters.AddWithValue("$role", (int)UserRole.Administrator);
        cmd.Parameters.AddWithValue("$createdUtc", AuthStore.FormatUtc(now));
        cmd.Parameters.AddWithValue("$updatedUtc", AuthStore.FormatUtc(now));

        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

        _logger.LogWarning(
            "Da tao tai khoan quan tri mac dinh {Username}. Hay doi mat khau ngay lap tuc.",
            username);

        return true;
    }

    /// <summary>
    /// Đọc chuỗi hash mật khẩu (nội bộ, không lộ ra ngoài).
    /// </summary>
    private static async Task<string?> ReadHashAsync(
        SqliteConnection connection, long id, CancellationToken ct)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT PasswordHash FROM Users WHERE Id = $id;";
        cmd.Parameters.AddWithValue("$id", id);

        var value = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return value as string;
    }

    /// <summary>
    /// Ghi nhận một lần đăng nhập sai: tăng bộ đếm và khoá nếu đạt ngưỡng.
    /// </summary>
    private async Task RegisterFailureAsync(
        SqliteConnection connection, UserAccount user, CancellationToken ct)
    {
        var attempts = user.FailedAttempts + 1;
        DateTime? lockedUntil = null;

        if (attempts >= _opt.MaxFailedAttempts)
        {
            // Khoá tài khoản trong mot khoang thoi gian. Giu nguyen so lan sai
            // de hien thi cho nguoi dung (khong reset ve 0).
            lockedUntil = DateTime.UtcNow.AddMinutes(_opt.LockoutMinutes);
        }

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            UPDATE Users
            SET FailedAttempts = $attempts,
                LockedUntilUtc = $lockedUntil,
                UpdatedUtc = $updated
            WHERE Id = $id;
            """;
        cmd.Parameters.AddWithValue("$attempts", attempts);
        cmd.Parameters.AddWithValue(
            "$lockedUntil",
            lockedUntil is null ? DBNull.Value : AuthStore.FormatUtc(lockedUntil.Value));
        cmd.Parameters.AddWithValue("$updated", AuthStore.FormatUtc(DateTime.UtcNow));
        cmd.Parameters.AddWithValue("$id", user.Id);

        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Mở khoá tài khoản (hết thời gian chờ) và reset bộ đếm sai.
    /// </summary>
    private static async Task ClearLockAsync(
        SqliteConnection connection, long id, CancellationToken ct)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            UPDATE Users
            SET FailedAttempts = 0,
                LockedUntilUtc = NULL,
                UpdatedUtc = $updated
            WHERE Id = $id;
            """;
        cmd.Parameters.AddWithValue("$updated", AuthStore.FormatUtc(DateTime.UtcNow));
        cmd.Parameters.AddWithValue("$id", id);

        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }
}
