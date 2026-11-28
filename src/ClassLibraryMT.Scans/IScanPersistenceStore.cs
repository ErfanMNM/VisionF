namespace ClassLibraryMT.Scans;

/// <summary>
/// Snapshot of one camera attempt. EventId identifies the attempt, never the code.
/// Accepted attempts carry the exact PO database identity captured during acceptance.
/// Callers must supply a private snapshot of Record and not mutate it after publication.
/// </summary>
public sealed record ScanPersistenceEnvelope(
    string EventId,
    ScanRecordEntity Record,
    DateTime? OrderCreatedUtc,
    bool Accepted);

/// <summary>
/// Durable half of the app persistence coordinator; deliberately independent of the PO library.
/// A scan and its PO outbox intent commit together in scans.db. The app applies PO batches,
/// then completes these intents before Checkee is allowed to send the corresponding records.
/// </summary>
public interface IScanPersistenceStore
{
    Task PersistBatchAsync(IReadOnlyList<ScanPersistenceEnvelope> batch, CancellationToken ct = default);
    Task<IReadOnlyList<ScanPersistenceEnvelope>> GetPendingAsync(CancellationToken ct = default);
    Task CompleteAsync(IReadOnlyList<string> eventIds, CancellationToken ct = default);
    Task VerifyHealthyAsync(CancellationToken ct = default);
    Task<int> PruneAsync(DateTime cutoff, CancellationToken ct = default);
}
