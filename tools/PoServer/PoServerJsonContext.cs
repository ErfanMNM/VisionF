using System.Text.Json.Serialization;
using ClassLibraryMT.Po;

namespace PoServer;

/// <summary>
/// JSON context của server (AOT-safe).
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(List<ProductionOrder>))]
[JsonSerializable(typeof(List<PoCode>))]
[JsonSerializable(typeof(PoCreateRequest))]
[JsonSerializable(typeof(PoActivateRequest))]
[JsonSerializable(typeof(PoActivateResult))]
[JsonSerializable(typeof(ProductionOrder))]
public sealed partial class PoServerJsonContext : JsonSerializerContext
{
}
