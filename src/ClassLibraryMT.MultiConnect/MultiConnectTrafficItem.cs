namespace ClassLibraryMT.MultiConnect;

/// <summary>Một dòng dữ liệu vào/ra đi qua gateway (dùng để hiển thị trên UI).</summary>
public sealed class MultiConnectTrafficItem
{
    public long Id { get; init; }
    public DateTimeOffset Time { get; init; }
    public string Channel { get; init; } = "";
    public bool IsOutput { get; init; }
    public string Payload { get; init; } = "";
    public int Clients { get; init; }
}
