using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ClassLibraryMT.Licensing;

// Ket qua kiem tra license.
public sealed record LicenseCheck(bool Ok, string Message, LicensePayload? Payload)
{
    public int DaysLeft => Payload is null
        ? 0
        : Math.Max(0, (int)Math.Ceiling((Payload.ExpiresUtc - DateTime.UtcNow).TotalDays));

    public bool Expired => Payload is not null && Payload.ExpiresUtc <= DateTime.UtcNow;
}

// Kiem tra license trong app (chi giu PUBLIC key trong app, PRIVATE key giu kin).
public static class LicenseValidator
{
    public static string DefaultLicensePath =>
        Path.Combine(AppContext.BaseDirectory, "license.lic");

    public static LicenseCheck Validate(string? licenseFilePath = null, string product = "DemoQR")
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

        // Doc payload truoc de biet key nao da ky (payload da duoc ky nen an toan khi doc).
        LicensePayload? payload;
        try
        {
            payload = JsonSerializer.Deserialize(Encoding.UTF8.GetString(data), LicenseJsonContext.Default.LicensePayload);
        }
        catch
        {
            return new LicenseCheck(false, "Noi dung license loi.", null);
        }

        if (payload is null)
            return new LicenseCheck(false, "Noi dung license loi.", null);

        // Chon public key theo key id (ho tro xoay vong key).
        var publicKey = LicenseKeys.Find(payload.KeyId);
        if (publicKey is null)
            return new LicenseCheck(false,
                $"Key ky khong con trong app (kid={payload.KeyId}). Hay cap lai license hoac update app.", null);

        try
        {
            using var rsa = RSA.Create();
            rsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKey), out _);
            if (!rsa.VerifyData(data, sig, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1))
                return new LicenseCheck(false, "Chu ky license khong hop le (file gia/hoac dung cap key).", null);
        }
        catch
        {
            return new LicenseCheck(false, "Chu ky license khong hop le.", null);
        }

        if (!string.Equals(payload.Product, product, StringComparison.OrdinalIgnoreCase))
            return new LicenseCheck(false, "License khong dung san pham.", null);

        if (!string.Equals(HardwareId.Normalize(payload.Hwid), HardwareId.Get(), StringComparison.Ordinal))
            return new LicenseCheck(false, "License khong dung cho may nay.", null);

        var now = DateTime.UtcNow;
        if (now < payload.IssuedUtc.AddDays(-1))
            return new LicenseCheck(false, "Dong ho he thong khong hop le (bi chinh nguoc).", null);
        if (now > payload.ExpiresUtc)
            return new LicenseCheck(false, $"License het han tu {payload.ExpiresUtc:yyyy-MM-dd}.", null);

        return new LicenseCheck(true, "OK", payload);
    }
}
