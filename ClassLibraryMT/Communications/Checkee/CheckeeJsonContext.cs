using System.Text.Json.Serialization;

namespace ClassLibraryMT.Communications.Checkee;

[JsonSerializable(typeof(ApiResponse<TokenData>))]
[JsonSerializable(typeof(ApiResponse<ActiveData>))]
[JsonSerializable(typeof(ActiveRequest))]
public sealed partial class CheckeeJsonContext : JsonSerializerContext
{
}
