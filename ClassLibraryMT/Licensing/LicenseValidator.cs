using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ClassLibraryMT.Licensing;

public sealed record LicenseCheck(bool Ok, string Message, LicensePayload? Payload)
{
    public int DaysLeft => Payload is null
        ? 0
        : Math.Max(0, (int)Math.Ceiling((Payload.ExpiresUtc - DateTime.UtcNow).TotalDays));
}

// Kiem tra license trong app (chi giu PUBLIC key trong app, PRIVATE key giu kin).
public static class LicenseValidator
{
    // Public key RSA-2048 (SPKI DER base64). Thay bang key that khi dua vao san xuat.
    // Cap phat sinh cap key that: chay LicenseTool mot lan o may chu, roi thay chuoi nay.
    private const string PublicKeyBase64 =
        "MIIBIjANBgkqhkiG9w0BAQEFAAOCAQ8AMIIBCgKCAQEAwEuTChqbYttTI7TYdrOpyGpGHz/r0Dd15YOj/rRH/y6mQ+6jDp7BaYA9E/Cjh6LoOF6bRXmcFQ/6vITt3ytH5hJwtIq2ABvhvqNCo0cibsHuceopVnLB7KwN8VI/Nesa/JBSAe05oFdm8o6vxI/GJ1rGGxpSoxNY9aB/NUg01wMwKg7ZL+PODpOFXH8w5jF/v87jmbbIINYKHDZdJbDMzLMEBLQ7D9noZbzQ0DehW5cgoc84WPROOR89IiiG6lBKV5yz7b1hsK7WFm8k86A4VC/w+suFHTIqJQjgMiReV1Q3iVePxO9UKdCl87sEWYZn/G5v0I4epZPJvggozQ3J7QIDAQAB";

    private const string ProductName = "DemoQR";

    public static string DefaultLicensePath =>
        Path.Combine(AppContext.BaseDirectory, "license.lic");

    public static LicenseCheck Validate(string? licenseFilePath = null)
    {
        var path = licenseFilePath ?? DefaultLicensePath;

        if (!File.Exists(path))
            return new LicenseCheck(false, "Khong tim thay file license.lic.", null);

        LicenseFile? file;
        try
        {
            var text = File.ReadAllText(path, Encoding.UTF8);
            file = JsonSerializer.Deserialize(text, LicenseJsonContext.Default.LicenseFile);
        }
        catch
        {
            return new LicenseCheck(false, "File license.lic loi dinh dang.", null);
        }

        if (file is null || file.Version != 1 ||
            string.IsNullOrEmpty(file.Data) || string.IsNullOrEmpty(file.Signature))
            return new LicenseCheck(false, "File license.lic loi dinh dang.", null);

        byte[] data, sig;
        try
        {
            data = Convert.FromBase64String(file.Data);
            sig = Convert.FromBase64String(file.Signature);
        }
        catch
        {
            return new LicenseCheck(false, "File license.lic loi dinh dang.", null);
        }

        try
        {
            using var rsa = RSA.Create();
            rsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(PublicKeyBase64), out _);
            if (!rsa.VerifyData(data, sig, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1))
                return new LicenseCheck(false, "Chu ky license khong hop le (file gia/khong dung cap key).", null);
        }
        catch
        {
            return new LicenseCheck(false, "Chu ky license khong hop le.", null);
        }

        LicensePayload? p;
        try
        {
            p = JsonSerializer.Deserialize(Encoding.UTF8.GetString(data), LicenseJsonContext.Default.LicensePayload);
        }
        catch
        {
            return new LicenseCheck(false, "Noi dung license loi.", null);
        }

        if (p is null)
            return new LicenseCheck(false, "Noi dung license loi.", null);
        if (!string.Equals(p.Product, ProductName, StringComparison.OrdinalIgnoreCase))
            return new LicenseCheck(false, "License khong dung san pham.", null);
        if (!string.Equals(HardwareId.Normalize(p.Hwid), HardwareId.Get(), StringComparison.Ordinal))
            return new LicenseCheck(false, "License khong dung cho may nay.", null);

        var now = DateTime.UtcNow;
        if (now < p.IssuedUtc.AddDays(-1))
            return new LicenseCheck(false, "Dong ho he thong khong hop le (bi chinh nguoc).", null);
        if (now > p.ExpiresUtc)
            return new LicenseCheck(false, $"License het han tu {p.ExpiresUtc:yyyy-MM-dd}.", null);

        return new LicenseCheck(true, "OK", p);
    }
}

// Dung trong LicenseTool (may chu cua ban) de ky license. KHONG goi tu app.
public static class LicenseSigner
{
    public static string Create(
        string privateKeyBase64, string hwidRaw, string customer, int daysValid, string product = "DemoQR")
    {
        var hwid = HardwareId.Normalize(hwidRaw);
        if (hwid.Length != 32)
            throw new ArgumentException("HWID phai la 32 ky tu hex (dang XXXX-XXXX-... van duoc).", nameof(hwidRaw));
        if (daysValid <= 0)
            throw new ArgumentException("So ngay hieu luc phai > 0.", nameof(daysValid));

        var now = DateTime.UtcNow;
        var payload = new LicensePayload
        {
            Hwid = hwid,
            Customer = customer,
            IssuedUtc = now,
            ExpiresUtc = now.AddDays(daysValid),
            Product = product
        };

        var json = JsonSerializer.Serialize(payload, LicenseJsonContext.Default.LicensePayload);
        var data = Encoding.UTF8.GetBytes(json);

        using var rsa = RSA.Create();
        rsa.ImportPkcs8PrivateKey(Convert.FromBase64String(privateKeyBase64), out _);
        var sig = rsa.SignData(data, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        var file = new LicenseFile
        {
            Version = 1,
            Data = Convert.ToBase64String(data),
            Signature = Convert.ToBase64String(sig)
        };
        return JsonSerializer.Serialize(file, LicenseJsonContext.Default.LicenseFile);
    }
}
