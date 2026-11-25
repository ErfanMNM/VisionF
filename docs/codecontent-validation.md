# CodeContent integration validation — 2026-11-26

The production path uses complete ordinal, case-sensitive CodeContent, strict one-column CSV/JSON/XLSX files, and durable state in a separate SQLite database for each PO. Cross-PO code reuse remains independent. Duplicate time-window configuration is removed.

PLC Pass is sent once. A confirmed rejection leaves the code eligible; an uncertain ACK or post-ACK scan persistence fault leaves a durable quarantine. A committed Pass remains Pass if later PO completion fails. Activation drains scanned rows from completed/cancelled POs after failures and restart. Imported activation state contributes once to PO progress without fabricating a scan timestamp; fully processed POs complete at start/restore. Repeated server snapshots retain the actual per-PO quantity.

## Verification

Tests run sequentially against temporary SQLite databases, fake services, and local TCP/UDP/HTTP endpoints. Counts below describe the latest successful run of each suite, not accumulated reruns.

**Result: 1,044 passing checks across 19 harnesses. Final solution build succeeded with 0 warnings and 0 errors.** The 694-check application checkpoint was completed by its owning department; after the final core changes, CameraHotApplyTests (61) and PrintTests (32) were rerun successfully, together with the new core PoTests (112) and PoFlowTests (43) coverage. The final solution build compiles all integrated sources.

| Suite | Passing checks |
|---|---:|
| ConfigTests | 143 |
| ConfigUiTests | 117 |
| DeviceConfigurationTests | 24 |
| LoginOverlayTests | 79 |
| FullscreenNumpadTests | 28 |
| CameraHotApplyTests | 61 |
| PlcHotApplyTests | 117 |
| ApplicationUiTests | 48 |
| UpdateSendTests | 18 |
| ScanTests | 21 |
| CheckeeSendTests | 31 |
| UpdateSafetyTests | 7 |
| MesSimulatorTests | 22 |
| PoConsoleTests | 25 |
| PoServerTests | 86 |
| PoServerE2E | 30 |
| PrintTests | 32 |
| PoTests | 112 |
| PoFlowTests | 43 |

The application checkpoint includes seven rendered UI images at 1048×540, 1280×720, and 1440×900. Long full-code text remains scrollable and its result/time/PLC/PO metadata remains visible. The build also compiles newly added scan, Checkee, application UI, simulator, and console harnesses through `VisionF.slnx`.

The release build command is:

```powershell
dotnet build VisionF.slnx -c Debug -m:1 -p:PublishAot=false -p:SkipPrinterRemoteHelper=true
```

Core regressions explicitly exercise case/Unicode/whitespace identity, same code in independent POs, confirmed rejection, cancellation after ACK, ambiguous Pass and restart, SQLite-triggered scan/completion faults, activation retry/restart/cache isolation, noncurrent cancellation, completion after a PO switch, and startup cancellation after durable Running state. The camera rerun also verifies these outcomes through TCP camera input and real UDP FINS acknowledgements from a local fake PLC.

## Operational limits

No real PLC, camera, printer, production MES, or NativeAOT publication was exercised. PLC large counters use the configured five-Int32 block (slots 0 Total, 1 ReadFail, 2 Pass, 4 Timeout; Fail is an Int64 sum). The default counter base D100 is a reference test-rig value. Configure and validate the machine's actual reset word; its default remains empty. Reset writes Int16=1 and the displayed result follows subsequent PLC polling.

The original legacy CSV is preserved unchanged. New import examples and the canonical API contract are in `docs/examples` and `docs/contract.md`.
