# Async scan persistence — implementation and verification

Implemented locally on `master`, November 27, 2026. No commit, push, deployment or production application run was performed.

## Processing contract

1. Camera callback reserves bounded persistence capacity in RAM, including for rejected outcomes. Pressure suspends intake before the queue fills; the current admitted event retains its slot.
2. The existing single-flight camera worker validates GS1 and atomically validates/consumes the complete code in the active PO dictionary. Keys remain ordinal, case-sensitive full `CodeContent`; duplicate scope remains the active PO.
3. PLC Pass/Fail is attempted before SQLite persistence. An accepted code remains consumed after PLC timeout or lost ACK; PLC outcome is recorded separately.
4. The reservation publishes a private snapshot with a unique attempt EventId, captured canonical PO identity and CreatedUtc, original scan UTC, outcome and separate PLC-call/ACK timings.
5. One app-level `ScanPersistenceWorker` batches on the existing 100 ms target interval, configured batch size, or explicit barrier. Its bounded reservation semaphore includes local batches and unpublished PLC results. There is no task or independent database timer per scan.
6. A transaction in `scans.db` inserts history plus accepted PO outbox intent. The worker applies an idempotent batch per captured PO database, then marks outbox completion and Checkee eligibility. These are separate SQLite commits, not an atomic transaction spanning two files.

## Recovery, lifecycle and compatibility

- Startup replays durable outbox before restoring the running PO dictionary and before camera/Ready starts. Missing PO database/code or mismatched identity fails startup closed; no empty PO database is created to acknowledge zero updates.
- A failed batch stays in RAM and retries every 500 ms with the same EventIds. Intake is suspended and barrier callers receive the failure promptly. Recovery opens intake only after PO and history synchronize. Existing `Sent`, `Failed` and send-attempt state are preserved.
- Near-full pressure recovers correctly even with capacity one. Intake leases are never acquired or released while holding the coordinator state lock, avoiding the reverse order of camera Dispatch.
- Final-code auto-completion waits for the durable batch. Manual completion/cancel, device changes, updater and shutdown use the common barrier, including reservations not yet published by PLC callbacks. Background completion never waits for the lifecycle mutex held by a barrier caller.
- Shutdown cancels the exit on failed/timed-out drain and reports the error. Writer shutdown follows camera/Checkee producers. Singleton disposal is idempotent.
- Retention deletes detailed history and applied outbox together, retains pending intent, and retains compact EventId/hash receipts for post-retention idempotency. A pruning failure logs/backoffs without terminating scan persistence.
- Legacy databases migrate additively. `ActivatedUtc` still prevents reopening previously consumed codes. Checkee ACK remains `ScanRecords.SendStatus`; the app never writes Checkee success into per-PO activation flags.
- Production `/api/activate` client/worker/policy/VM command and AutoActivate/ActivationIntervalMs configuration were removed. Download/receive PO, startup restore, MES push and Checkee remain. The standalone demo server keeps its own separate activation DTOs for demo compatibility.

## Verification

All executed data tests use unique temporary sandbox directories and explicit store paths. New PO fixtures use `TEST-ASYNC-PO-20261127-*`. Real application JSON/environment configuration, production databases, PLC/camera/Checkee, credentials, Bruno UAT and UI harnesses that copy production databases were not used.

| Harness | Passed checks |
| --- | ---: |
| PoTests | 135 |
| AsyncCoordinatorTests | 56 |
| ScanOutboxTests | 25 |
| ScanTests | 25 |
| CheckeeSendTests | 74 |
| AsyncPersistenceTests | 21 |
| CameraHotApplyTests | 121 |
| PoFlowTests | 64 |
| PoAcceptanceTests | 154 |
| DeviceConfigurationTests | 24 |
| UpdateSafetyTests | 7 |
| UpdateSendTests | 18 |

Coordinator coverage includes unpublished reservation barriers, immutable snapshot, capacity one/near-full, retry after PO commit before outbox completion, restart/replay, existing Sent ACK, deleted synthetic PO code, prompt current/queued barrier faults, cancel/complete/switch with pending accepted mutation, final auto-completion, shutdown timeout/retry, repeated disposal and housekeeping failure while an accepted event is queued. Four concurrent producers execute 1,600 callback attempts across fault, retry and pressure recovery; all admitted outcomes are accounted for.

The app was built in Debug and Release; additional affected ConfigTests, ConfigUiTests, PoServerTests and standalone demo PoConsoleTests builds succeeded. No publish operation was run. Avalonia AVLN3001 remains; a clean PO compilation can also report unused compatibility event HistoryConflict (CS0067).

Latest warmed loopback measurement from `AsyncPersistenceTests`, using the real camera processing queue, real coordinator and a private PO SQLite write lock held **310.389 ms**:

| Measurement | Milliseconds |
| --- | ---: |
| Camera receipt → PLC call start | 0.138 |
| Camera receipt → PLC ACK | 0.411 |
| Accepted scan plus immediate duplicate | 0.802 |

These are measured sandbox observations, not production throughput guarantees. The duplicate is rejected before the 100 ms flush; lifecycle drain remains pending until the SQLite lock releases. The separate timings are persisted as ProcessingTimeMs and PlcAckTimeMs.

## Remaining limits

RAM acceptance and PLC precede the first durable commit: process crash or power loss in that window can lose the tail. The outbox protects recovery only after the first history/outbox transaction commits. The 100 ms interval is a target, not a maximum while SQLite is slow or locked. Persistent database faults stop intake and require repair/recovery; they do not silently discard a batch. Compact EventId receipts intentionally grow with attempts when detailed retention is enabled.

No real PO A001 or production data was read, copied, used, modified or deleted. Sandbox artifacts were retained where useful for review; any test cleanup is limited to its own verified unique temporary path.

## Changed files

The following list is generated from the final local diff (including new source/test files).

<!-- changed-files -->
- `ClassLibraryMT.Po/IPoClient.cs`
- `ClassLibraryMT.Po/PoActivationPolicy.cs`
- `ClassLibraryMT.Po/PoClient.cs`
- `ClassLibraryMT.Po/PoCodeCache.cs`
- `ClassLibraryMT.Po/PoContracts.cs`
- `ClassLibraryMT.Po/PoJsonContext.cs`
- `ClassLibraryMT.Po/PoOptions.cs`
- `ClassLibraryMT.Po/PoScanMutation.cs`
- `ClassLibraryMT.Po/PoServiceCollectionExtensions.cs`
- `ClassLibraryMT.Po/PoStore.cs`
- `ClassLibraryMT.Po/ProductionOrderService.cs`
- `ClassLibraryMT.Scans/AssemblyInfo.cs`
- `ClassLibraryMT.Scans/IScanPersistenceStore.cs`
- `ClassLibraryMT.Scans/ScanPersistenceIdentity.cs`
- `ClassLibraryMT.Scans/ScanPersistenceStore.cs`
- `ClassLibraryMT.Scans/ScanRecordEntity.cs`
- `ClassLibraryMT.Scans/ScanSendStatusStore.History.cs`
- `ClassLibraryMT.Scans/ScanSendStatusStore.cs`
- `ClassLibraryMT.Scans/ScanServiceCollectionExtensions.cs`
- `ClassLibraryMT.Scans/ScanStore.cs`
- `TanTienQR/App.axaml.cs`
- `TanTienQR/AssemblyInfo.cs`
- `TanTienQR/Services/AppConfigModels.cs`
- `TanTienQR/Services/CameraSignalWorker.cs`
- `TanTienQR/Services/PlcHeartbeatService.cs`
- `TanTienQR/Services/PoActivationWorker.cs`
- `TanTienQR/Services/ScanPersistenceWorker.cs`
- `TanTienQR/Services/TanTienAppServices.cs`
- `TanTienQR/ViewModels/ProductionOrdersViewModel.cs`
- `TanTienQR/appsettings.json`
- `docs/async-scan-persistence-20261127.md`
- `tools/AsyncCoordinatorTests/AsyncCoordinatorTests.csproj`
- `tools/AsyncCoordinatorTests/Program.cs`
- `tools/AsyncPersistenceTests/AsyncPersistenceTests.csproj`
- `tools/AsyncPersistenceTests/Program.cs`
- `tools/CameraHotApplyTests/CameraCodeContentChecks.cs`
- `tools/CheckeeSendTests/Program.cs`
- `tools/ConfigTests/Program.cs`
- `tools/ConfigUiTests/Program.cs`
- `tools/FullscreenNumpadTests/Program.cs`
- `tools/LoginOverlayTests/Program.cs`
- `tools/PoAcceptanceTests/Program.cs`
- `tools/PoFlowTests/PoDownloadContractChecks.cs`
- `tools/PoFlowTests/Program.cs`
- `tools/PoServer/PoActivationContracts.cs`
- `tools/PoServer/Tests/Program.cs`
- `tools/PoServerTests/Program.cs`
- `tools/PoTests/CodeContentLifecycleChecks.cs`
- `tools/PoTests/PoMetadataContractChecks.cs`
- `tools/PoTests/Program.cs`
- `tools/ScanOutboxTests/Program.cs`
- `tools/ScanOutboxTests/ScanOutboxTests.csproj`
- `tools/UiAuditHarness-VF/Sandbox.cs`
- `tools/UiAuditHarnessRunner/Sandbox.cs`
- `tools/sandbox/appsettings.json`
