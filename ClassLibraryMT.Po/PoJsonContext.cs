using System.Text.Json.Serialization;

namespace ClassLibraryMT.Po;

/// <summary>
/// JSON context dùng chung cho client và server (AOT-safe).
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
[JsonSerializable(typeof(PoCode))]
public sealed partial class PoJsonContext : JsonSerializerContext
{
}
