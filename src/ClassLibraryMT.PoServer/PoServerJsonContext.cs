using System.Text.Json.Serialization;

namespace ClassLibraryMT.PoServer;

/// <summary>
/// JSON context dùng cho server nhận PO (AOT-safe — không dùng reflection).
///
/// <para>
/// Mọi request / response đều serialize / deserialize qua context này nên
/// ứng dụng vẫn publish Native AOT được (không cần <c>JsonSerializer</c>
/// dạng reflection).
/// </para>
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(PoPushRequest))]
[JsonSerializable(typeof(PoPushResponse))]
[JsonSerializable(typeof(PoHealthResponse))]
[JsonSerializable(typeof(PoRejectedCode))]
[JsonSerializable(typeof(List<PoRejectedCode>))]
public sealed partial class PoServerJsonContext : JsonSerializerContext
{
}
