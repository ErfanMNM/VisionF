using System.Security.Cryptography;
using System.Text;

namespace ClassLibraryMT.Scans;

/// <summary>Compact durable identity receipt retained after detailed history retention.</summary>
internal static class ScanPersistenceIdentity
{
    internal static byte[] Hash(ScanPersistenceEnvelope envelope) => Hash(
        envelope.Record.OrderNo ?? string.Empty,
        envelope.OrderCreatedUtc is { } created ? ScanStore.FormatUtc(created) : null,
        envelope.Record.CodeContent, ScanStore.FormatUtc(envelope.Record.ReceivedUtc), envelope.Accepted);

    internal static byte[] Hash(string orderNo, string? createdUtc, string code, string scannedUtc, bool accepted)
    {
        // Length prefixes preserve arbitrary separators, whitespace and Unicode.
        var identity = new StringBuilder();
        foreach (var value in new[] { orderNo, createdUtc, code, scannedUtc, accepted ? "1" : "0" })
            identity.Append(value?.Length ?? -1).Append(':').Append(value);
        return SHA256.HashData(Encoding.UTF8.GetBytes(identity.ToString()));
    }
}
