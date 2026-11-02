using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ClassLibraryMT.Licensing;

// Dung trong LicenseTool (may chu cua ban) de ky license. KHONG goi tu app.
public static class LicenseSigner
{
    public static string Create(
        string privateKeyBase64, string hwidRaw, string customer, int daysValid,
        string product = "DemoQR", string? keyId = null)
    {
        var hwid = HardwareId.Normalize(hwidRaw);
        if (hwid.Length != 32)
            throw new ArgumentException("HWID phai la 32 ky tu hex (dang XXXX-XXXX-... van duoc).", nameof(hwidRaw));
        if (daysValid <= 0)
            throw new ArgumentException("So ngay hieu luc phai > 0.", nameof(daysValid));

        byte[] privateKey;
        try
        {
            privateKey = Convert.FromBase64String(privateKeyBase64.Trim());
        }
        catch (Exception ex) when (ex is FormatException or ArgumentNullException)
        {
            throw new ArgumentException("Private key khong phai chuoi base64 hop le.", nameof(privateKeyBase64));
        }

        using var rsa = RSA.Create();
        try
        {
            rsa.ImportPkcs8PrivateKey(privateKey, out _);
        }
        catch (CryptographicException)
        {
            throw new ArgumentException("Private key khong hop le (phai la PKCS#8 RSA).", nameof(privateKeyBase64));
        }

        // Tu dong gan kid tu public key suy ra, neu nguoi dung khong chi dinh.
        var publicKey = rsa.ExportSubjectPublicKeyInfo();
        var kid = keyId ?? LicenseKeys.ComputeKeyId(Convert.ToBase64String(publicKey));

        var now = DateTime.UtcNow;
        var payload = new LicensePayload
        {
            Hwid = hwid,
            Customer = customer,
            IssuedUtc = now,
            ExpiresUtc = now.AddDays(daysValid),
            Product = product,
            KeyId = kid,
        };
        var json = JsonSerializer.Serialize(payload, LicenseJsonContext.Default.LicensePayload);
        var data = Encoding.UTF8.GetBytes(json);

        var sig = rsa.SignData(data, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        var file = new LicenseFile
        {
            Version = 1,
            Data = Convert.ToBase64String(data),
            Signature = Convert.ToBase64String(sig),
        };
        return JsonSerializer.Serialize(file, LicenseJsonContext.Default.LicenseFile);
    }
}
