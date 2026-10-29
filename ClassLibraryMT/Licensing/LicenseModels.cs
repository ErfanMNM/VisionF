using System.Text.Json.Serialization;

namespace ClassLibraryMT.Licensing;

// Du lieu license (payload duoc ky so RSA). Gio UTC, dinh dang ISO 8601.
public sealed class LicensePayload
{
    [JsonPropertyName("hwid")] public string Hwid { get; set; } = "";
    [JsonPropertyName("customer")] public string Customer { get; set; } = "";
    [JsonPropertyName("issued")] public DateTime IssuedUtc { get; set; }
    [JsonPropertyName("expires")] public DateTime ExpiresUtc { get; set; }
    [JsonPropertyName("product")] public string Product { get; set; } = "DemoQR";
}

// File license.lic: {"v":1,"data":"<base64 json payload>","sig":"<base64 chu ky RSA>"}
public sealed class LicenseFile
{
    [JsonPropertyName("v")] public int Version { get; set; }
    [JsonPropertyName("data")] public string Data { get; set; } = "";
    [JsonPropertyName("sig")] public string Signature { get; set; } = "";
}

[JsonSerializable(typeof(LicensePayload))]
[JsonSerializable(typeof(LicenseFile))]
public sealed partial class LicenseJsonContext : JsonSerializerContext
{
}
