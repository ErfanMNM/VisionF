using System.Text.Json.Serialization;

namespace ClassLibraryMT.Checkee;

/// <summary>
/// JSON context dùng chung cho client Checkee (AOT-safe — không dùng reflection).
/// </summary>
[JsonSourceGenerationOptions(
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(CheckeeResponse<CheckeeTokenData>))]
[JsonSerializable(typeof(CheckeeResponse<CheckeeActiveData>))]
[JsonSerializable(typeof(CheckeeTokenData))]
[JsonSerializable(typeof(CheckeeActiveData))]
[JsonSerializable(typeof(CheckeeActivateRequest))]
public sealed partial class CheckeeJsonContext : JsonSerializerContext
{
}
