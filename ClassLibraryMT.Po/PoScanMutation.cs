namespace ClassLibraryMT.Po;

/// <summary>
/// Immutable accepted scan captured before PLC I/O. The persistence coordinator
/// owns durability; this identity must never be inferred from the current PO.
/// </summary>
public sealed record PoScanMutation(
    string EventId,
    string OrderNo,
    DateTime OrderCreatedUtc,
    string CodeContent,
    DateTime ScannedUtc);
