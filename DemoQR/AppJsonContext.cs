using System.Text.Json.Serialization;
using QRDemoCodeBackend;

[JsonSerializable(typeof(ApiResponse<TokenData>))]
[JsonSerializable(typeof(ApiResponse<ActiveData>))]
[JsonSerializable(typeof(ActiveRequest))]
internal sealed partial class AppJsonContext : JsonSerializerContext
{
}
