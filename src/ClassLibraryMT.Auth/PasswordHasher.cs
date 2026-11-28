using System.Security.Cryptography;
using System.Text;

namespace ClassLibraryMT.Auth;

/// <summary>
/// Băm mật khẩu bằng PBKDF2-HMAC-SHA256.
///
/// Cấu trúc chuỗi lưu trữ:
/// <code>
/// PBKDF2-SHA256$&lt;iterations&gt;$&lt;base64 salt&gt;$&lt;base64 hash&gt;
/// </code>
///
/// Mỗi tài khoản có salt riêng (16 byte ngẫu nhiên) nên cùng một mật khẩu
/// vẫn tạo ra hash khác nhau giữa các tài khoản.
/// </summary>
public static class PasswordHasher
{
    /// <summary>Định dạng thuật toán (dùng để tương thích về sau).</summary>
    public const string Algorithm = "PBKDF2-SHA256";

    /// <summary>Số vòng lặp mặc định (OWASP khuyến nghị 600k cho SHA256, 210k là mức cân bằng).</summary>
    public const int DefaultIterations = 210_000;

    /// <summary>Độ dài salt (byte).</summary>
    public const int SaltSize = 16;

    /// <summary>Độ dài hash đầu ra (byte).</summary>
    public const int HashSize = 32;

    /// <summary>Độ dài tối thiểu của mật khẩu được chấp nhận.</summary>
    public const int MinPasswordLength = 6;

    /// <summary>
    /// Băm mật khẩu, sinh salt mới mỗi lần gọi.
    /// </summary>
    /// <param name="password">Mật khẩu plaintext.</param>
    /// <param name="iterations">Số vòng lặp PBKDF2.</param>
    /// <returns>Chuỗi đã băm theo định dạng lưu trữ.</returns>
    public static string Hash(string password, int iterations = DefaultIterations)
    {
        ArgumentNullException.ThrowIfNull(password);

        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var hash = Derive(password, salt, iterations);

        return string.Join(
            '$',
            Algorithm,
            iterations.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Convert.ToBase64String(salt),
            Convert.ToBase64String(hash));
    }

    /// <summary>
    /// Kiểm tra mật khẩu có khớp với chuỗi đã băm hay không.
    ///
    /// Dùng <see cref="CryptographicOperations.FixedTimeEquals"/> để chống
    /// timing attack (thời gian so sánh không phụ thuộc vào vị trí byte khác nhau).
    /// </summary>
    /// <param name="password">Mật khẩu cần kiểm tra.</param>
    /// <param name="storedHash">Chuỗi đã băm lưu trong database.</param>
    /// <returns>True nếu mật khẩu đúng.</returns>
    public static bool Verify(string password, string? storedHash)
    {
        if (password is null || string.IsNullOrWhiteSpace(storedHash))
        {
            return false;
        }

        var parts = storedHash.Split('$');
        if (parts.Length != 4 || !string.Equals(parts[0], Algorithm, StringComparison.Ordinal))
        {
            return false;
        }

        if (!int.TryParse(parts[1], System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out var iterations)
            || iterations <= 0)
        {
            return false;
        }

        byte[] salt;
        byte[] expected;
        try
        {
            salt = Convert.FromBase64String(parts[2]);
            expected = Convert.FromBase64String(parts[3]);
        }
        catch (FormatException)
        {
            // Chuỗi lưu trữ hỏng -> coi như sai mật khẩu.
            return false;
        }

        if (expected.Length == 0)
        {
            return false;
        }

        var actual = Derive(password, salt, iterations);

        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    /// <summary>
    /// Kiểm tra mật khẩu có đủ mạnh không.
    /// </summary>
    /// <param name="password">Mật khẩu.</param>
    /// <param name="error">Thông báo lỗi nếu không đạt.</param>
    /// <returns>True nếu đạt yêu cầu.</returns>
    public static bool ValidateStrength(string? password, out string error)
    {
        if (string.IsNullOrWhiteSpace(password))
        {
            error = "Mật khẩu không được để trống.";
            return false;
        }

        if (password.Length < MinPasswordLength)
        {
            error = $"Mật khẩu phải có ít nhất {MinPasswordLength} ký tự.";
            return false;
        }

        error = string.Empty;
        return true;
    }

    /// <summary>
    /// Chạy PBKDF2 để sinh hash từ mật khẩu + salt.
    /// </summary>
    private static byte[] Derive(string password, byte[] salt, int iterations) =>
        Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password),
            salt,
            iterations,
            HashAlgorithmName.SHA256,
            HashSize);
}
