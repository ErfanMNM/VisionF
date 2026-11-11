namespace ClassLibraryMT.Licensing;

// Danh muc PUBLIC key duoc app tin dung. PRIVATE key khong bao gio nam trong code.
//
// Khi can doi key (bi lo, hoac muon gia han theo chu ky so):
//   1. Sinh cap key moi bang LicenseTool (tab "Quan ly key").
//   2. Them public key moi vao dau danh sach duoi day, kem kId.
//   3. Build lai app va phat lai cho khach.
//   4. Cac license da cap bang key cu van chay (nhieu key song song).
//      Sau khi het han, bo key cu de nho hon.
//
// kid = 8 ky tu hex dau cu SHA256(public key SPKI) — dung de gan nhanh key trong file license.
public static class LicenseKeys
{
    // Key hien hanh (dung de cap license moi).
    public const string CurrentKeyId = "4D088A7B";

    // Key cu (chi con giu de license da cap truoc do van hieu luc).
    public const string LegacyKeyId = "62790857";

    // Key cap nhat truoc do — license da cap bang key nay van phai chay.
    public const string PreviousKeyId = "703C8A5E";

    // Key hien hanh — public key SPKI (base64). KHONG bao gio commit private key.
    // Khoa nay sinh boi Cloudflare License Portal (license-portal/scripts/setup.mjs).
    public const string CurrentPublicKey =
        "MIIBIjANBgkqhkiG9w0BAQEFAAOCAQ8AMIIBCgKCAQEAwG0vc9bil8mhzxx9ZLQ8myeEyLoHatE1n+MWucsyBw/hNzcWGsOxQx/7TOnDTGILxTi40ii7DfLFfZcsqq+yqiZahP3VMFE32vvgvGHDjuxFayCj69ySfI3GIpkQm1qMmYD/MerwtZH959qtQYZMGSKee/YW5j6o0m0N0LWFKkkA1T42ZSV0UFU7p68wJ2GmM91Cm8Qck5qfbvPsFA2XouKVJdrK0va7ettb3OCOVsI4aELd3pBCkDcqLug8BbnkGeLAUQsMXaVLovhYAAdWIeJ1TXCzB+SiheLdiLGXU/Bq3WP3fIWQdEV1eYaoITyQJKIQcLf813OQPL//FWf/2QIDAQAB";

    // Key cu (public key SPKI base64) — giu lai de license cap bang key nay van chay.
    public const string LegacyPublicKey =
        "MIIBIjANBgkqhkiG9w0BAQEFAAOCAQ8AMIIBCgKCAQEAwEuTChqbYttSI7TYdrOpyGpGHz/r0Dd15YOj/rRH/y6mQ+6jDp7BaYA9E/Cjh6LoOF6bRXmcFQ/6vITt3ytH5hJwtIq2ABvhvqNCo0cibsHuceopVnLB7KwN8VI/Nesa/JBSAe05oFdm8o6vxI/GJ1rGGxpSoxNY9aB/NUg01wMwKg7ZL+PODpOFXH8w5jF/v87jmbbIINYKHDZdJbDMzLMEBLQ7D9noZbzQ0DehW5cgoc84WPROOR89IiiG6lBKV5yz7b1hsK7WFm8k86A4VC/w+suFHTIqJQjgMiReV1Q3iVePxO9UKdCl87sEWYZn/G5v0I4epZPJvggozQ3J7QIDAQAB";

    // Key truoc do (public key SPKI base64) — giu lai de license da cap van hieu luc.
    public const string PreviousPublicKey =
        "MIIBIjANBgkqhkiG9w0BAQEFAAOCAQ8AMIIBCgKCAQEA49bE6nA/Xhog8nKyxw5ZI1HAx4lyCthJ7gAOCd5cgl2pFSz8fzONwwkVMZa/NhKnvtihCg9G+rME//Cachw9EffYkcK3p4cTNrJPP9IoGS66+vcgWoYtGkZJA/V9YUSM0YsmauptLuATXue7KvILdXqVwiB8s0qHH4wA0C6gNDucn0xsFRBLfMWhXQ4DGAFOfN5bi8LGK9p9/jKMAkJ/LOHnX0NpsaxnDNeSIh3/PNFzfClZb2Vq9wGJbDGz24KCT645K4YV3HY62J0YPiGiQQ/wDxZZtpWFdbmcusyNZUEUtCmiXaf5R1dn8t0mj/gsRc7HW46lNqYZWURgR+jXVQIDAQAB";

    // Tat ca public key app biet, kem key id tuong ung.
    public static IReadOnlyDictionary<string, string> All { get; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [CurrentKeyId] = CurrentPublicKey,
            [PreviousKeyId] = PreviousPublicKey,
            [LegacyKeyId] = LegacyPublicKey,
        };

    // Tra ve public key theo id, hoac null neu khong biet key id nay.
    public static string? Find(string? keyId) =>
        keyId is not null && All.TryGetValue(keyId, out var key) ? key : null;

    // Tinh kid tu public key (de kiem tra nhat quan khi them key moi).
    public static string ComputeKeyId(string publicKeyBase64)
    {
        var bytes = Convert.FromBase64String(publicKeyBase64.Trim());
        var hash = System.Security.Cryptography.SHA256.HashData(bytes);
        return Convert.ToHexString(hash)[..8];
    }
}
