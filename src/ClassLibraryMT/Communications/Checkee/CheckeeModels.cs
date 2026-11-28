using System.Text.Json.Serialization;

namespace ClassLibraryMT.Communications.Checkee;

// ---------------------------------------------------------------- Model
public sealed class ApiResponse<T>
{
    [JsonPropertyName("code")] public int Code { get; set; }
    [JsonPropertyName("status")] public bool Status { get; set; }
    [JsonPropertyName("message")] public string? Message { get; set; }
    [JsonPropertyName("data")] public T? Data { get; set; }
}

public sealed class TokenData
{
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("token")] public string Token { get; set; } = ""; // "accessToken=<JWT>"
}

public sealed class ActiveData
{
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("GTIN")] public string? Gtin { get; set; }
    [JsonPropertyName("batch")] public string? Batch { get; set; }
    [JsonPropertyName("quantityInBatch")] public int QuantityInBatch { get; set; }
    [JsonPropertyName("activeSerials")] public int ActiveSerials { get; set; }
}

public sealed class ActiveRequest
{
    [JsonPropertyName("GTIN")] public string Gtin { get; set; } = "";
    [JsonPropertyName("batchCode")] public string BatchCode { get; set; } = "";
    [JsonPropertyName("type")] public string Type { get; set; } = "";
    [JsonPropertyName("serialNumbers")] public string[] SerialNumbers { get; set; } = [];
}

// Kết quả gọi API kích hoạt mã.
public sealed record ActivateResult(bool Success, string Message, ActiveData? Data);
