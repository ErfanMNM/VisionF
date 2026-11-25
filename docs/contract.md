# CodeContent PO contract

This is the canonical backend/application contract for the PO redesign. Code identity is the complete `CodeContent` string exactly as supplied by MES. The backend does not parse QR formats, GTIN, serials, or other parts of the value. Equality is ordinal and case-sensitive.

## Import shape

An import is a file/table containing exactly one data column/property named `CodeContent`:

- CSV requires a `CodeContent` header and one value per row.
- JSON requires an array of objects such as `[{"CodeContent":"raw-code"}]`.
- XLSX may be accepted when its first sheet has the same single-column table shape.
- A bare JSON string array, an inline PO envelope containing `codes` or `csv`, a missing/empty `CodeContent`, or any additional data column/property is invalid.

PO metadata (`poName`, `orderQty`, and optional descriptive metadata) travels outside the table: query parameters for raw-file uploads, or separate multipart fields alongside the file. `orderQty` must match the imported non-empty row count. GTIN/serial fields and metadata are not part of code identity.

## Application API

The application-facing methods are:

```csharp
PoCodeValidation ValidateCode(string codeContent);
Task<PoScanResult> ProcessCodeAsync(
    string codeContent,
    Func<CancellationToken, Task<bool>> sendPass,
    CancellationToken ct = default);
```

`PoCode.CodeContent` is the canonical model property. A running PO loads a dictionary keyed by complete `CodeContent`; no QR parser or identity projection is involved.

`ValidateCode` returns `NoProductionOrder` when no PO is running, `Halted` when the production guard is active, `InvalidForOrder` when the exact code is absent from that PO, `Duplicate` when that PO's durable scanned or activated state already contains it, and `Valid` otherwise. This includes a code imported with an existing activation timestamp, even when it has no scan timestamp. Production scanning uses `ProcessCodeAsync`, which holds the lifecycle mutation lock across validation, a durable pending-Pass intent, the awaited PLC Pass callback, and the scan commit. It invokes the callback only for `Valid`. A callback result of `false` means a confirmed failure: the pending intent is cleared, the code remains available, and PO counts do not change. An exception or cancellation after invoking the callback is ambiguous: the service halts and leaves the intent in the PO database to quarantine a possible accepted Pass across restart. After a successful callback, the scan commit ignores caller cancellation so the method cannot lose a confirmed Pass.

`PoScanResult` exposes `Validation`, nullable `PlcWritten`, `Committed`, `OrderNo`, and optional `ProductionHalted` / `Message`. A normal Pass returns `Valid`, `PlcWritten=true`, and `Committed=true`. If scan persistence fails after PLC Pass, `ProcessCodeAsync` throws `PoScanProcessingException` with `Result.Validation=Halted`, `PlcWritten=true`, and `Committed=false`. If the scan is committed but automatic PO completion fails, it still returns the committed Pass, with `ProductionHalted=true`; the service durably records the halt and reports it through `IsHalted`. `MarkCodeScannedAsync` is obsolete compatibility only; production camera code uses `ProcessCodeAsync`.

The background activation worker drains scanned-but-not-yet-activated values from every local PO database, including completed and cancelled POs. It retries failures on later cycles and after restart; successful acknowledgements are persisted only in the originating PO database. The active RAM cache is updated only if it still belongs to that PO after the network request. Any code already carrying an activation timestamp, including an imported one, validates as `Duplicate`.

Matching and activation are isolated per PO. The same complete `CodeContent` can be imported and processed in two different POs; a scan in one PO does not alter the other's validation state. No global scan-history query participates in normal camera decisions or activation.

## Lifecycle and persistence

Each PO retains its own SQLite database. For compatibility with existing local databases and print readers, the database keeps its `PoCodes.Code`, `Gtin`, and `Serial` columns: `Code` stores the complete new `CodeContent`, and new rows leave `Gtin` and `Serial` empty. Imported code, scanned/seen state, activation acknowledgement, and an unresolved pending-Pass marker survive restart independently of other POs. PO completion status and timestamp are committed in one database transaction. Existing start, restore, cancel, update-maintenance, and cancellation behavior remains in force. PO counters come from committed rows in that PO database, not PLC counters or session events.

The processed PO count is the union of rows with `ScannedUtc` or `ActivatedUtc`; a row carrying both timestamps counts once. Importing an activation acknowledgement does not fabricate a scan timestamp. Starting or restoring a PO whose entire code set is already processed completes that PO immediately without sending PLC Pass. An unresolved pending-Pass or halt marker always prevents this automatic completion. Repeated server snapshots keep the local quantity equal to the actual set of rows in that PO.

The production ingestion endpoint accepts the file/table shape above. It does not accept the legacy inline `{ codes: [...] }` or `{ csv: "..." }` PO envelope. Test simulator orders use explicit CodeContent rows; they do not synthesize production sample payloads.
