using System.Text.Json.Serialization;

namespace ClassLibraryMT.Licensing;

[JsonSerializable(typeof(LicensePayload))]
[JsonSerializable(typeof(LicenseFile))]
public sealed partial class LicenseJsonContext : JsonSerializerContext
{
}
