# Nhật ký PO-01-A — Khôi phục core PO

- Agent collaboration thực: `/root/po_core_recovery`; parent `/root`.
- Thread riêng xác minh qua `CODEX_THREAD_ID` và rollout budget: `01a21773-5bcd-7320-8eb7-27552d8c2c70`.
- CEO đang active theo phân công: `01a2175d-8c03-7cb0-8b6c-0fd6c8e9dcf9`.
- Project root: `C:\Users\THUC\source\repos\ErfanMNM\VisionF`.
- Journal-key được cấp: `po-core-recovery`; chỉ ghi `LOG.md` này, không tạo ACK/budget hoặc sửa bảng tổng ngoài phạm vi.
- Phạm vi ghi: `ClassLibraryMT.Po/**`, `tools/PoTests/**`, `tools/PoFlowTests/**`, nhật ký này. PO-01 đã được xác nhận dừng trước khi nhận quyền; backend/callers/UI thuộc owner khác.
- Đã đọc `session-worker`, `session-ops`, giao thức, checkpoint review và `DECISIONS.md` trước khi sửa.
- Budget đầu: `normal`, 5/100, exact=true. Budget checkpoint 2026-11-26T17:54:44+07:00: `normal`, 33/100, exact=true; đọc bằng script chung không ghi file ngoài quyền.

## 2026-11-26 — Kết quả core hoàn tất

- Branch hiện tại do CEO tạo: `codex/po-gtin-demo`; HEAD khi checkpoint: `b66dce690674bebbbe395f47cd052cbc2777fe47`.
- Mọi source vẫn chưa commit. CEO đã truyền yêu cầu mới commit/push nhánh riêng để chờ duyệt; worker không chạy commit/push.
- Đã phối hợp API trước khi sửa với CEO và hai owner `/root/po_server_recovery`, `/root/po_callers_recovery`.

### File và hành vi

1. `ClassLibraryMT.Po/PoCodeParser.cs`: giữ file parser mới có sẵn, thêm `TryNormalizeGtin(string?, out string)` xác thực checksum GTIN 8/12/13/14 rồi padded14; `TryGetGtin` trả cùng định danh chuẩn. Không sửa raw `CodeContent`. URL không nuốt AI numeric trong prefix, chặn repeated `/01`/`/21`, AI lặp trong serial ngoặc và khóa query `01`/`21` kể cả escape. Giữ grammar URL, `(01)...(21)...`, element string có GS.
2. `ClassLibraryMT.Po/PoStore.cs`: thêm `MetaGtin`, round-trip `LocalPoInfo.Gtin`, lưu GTIN chuẩn và chặn thay đổi GTIN đã lưu trong cùng transaction. Planned `MetaOrderQty` merge `max(existing, incoming)` tại SQLite, không cộng actual/batch và không hạ target khi replay. `ListLocalOrdersAsync.OrderQty` đọc planned; `TotalCodes` vẫn actual. Legacy đọc `Additional.gtin` case-insensitive hoặc chỉ suy từ codes khi toàn bộ parse được và cùng một GTIN; không xác định duy nhất trả chuỗi rỗng. Không sửa/xóa mã cũ, scan, activation hoặc trạng thái sản xuất để migrate.
3. `ClassLibraryMT.Po/ProductionOrderService.cs`: download chuẩn hóa GTIN, phân loại theo thứ tự `invalidStructure`, `gtinMismatch`, duplicate exact ordinal (trong snapshot và đã có trong PO đích); raw reject/duplicate giữ nguyên. Chặn metadata GTIN sai/thiếu, raw count sai planned, GTIN khác PO hoặc legacy codes sai/mixed trước mutation; all-invalid không tạo PO rỗng. Không sửa DTO của `IPoClient`. Bảo toàn metadata legacy, planned, GTIN và Additional trong summary/start/restore/cancel; cache/progress/completion vẫn dùng số mã thực.
4. `ClassLibraryMT.Po/ProductionOrder.cs`: chú thích `OrderQty` rõ planned độc lập actual, GTIN chuẩn sau xác thực; giữ thay đổi GTIN đã có từ predecessor. `PoContracts.cs` có `PoCreateRequest.Gtin` đã có trước khi worker nhận, được bảo toàn.
5. `tools/PoTests/Program.cs`, `tools/PoFlowTests/Program.cs`: sửa fixtures GS1 cũ không checksum/`G` thành GTIN hợp lệ ở các fixture hợp đồng; expected sync planned tách actual. Bộ `CodeContentLifecycleChecks.cs` giữ nguyên fixture opaque để kiểm tra tương thích dữ liệu local cũ và identity.
6. Thêm `tools/PoTests/PoMetadataContractChecks.cs`, `tools/PoFlowTests/PoDownloadContractChecks.cs` bao phủ normalization, parser lặp AI, persistence, replay/append, raw identity, state, migration, download partial, validation và transport.

### API additive đã chốt

```text
PoCodeParser.TryNormalizeGtin(string?, out string) -> bool; canonical 14 digits
PoCodeParser.TryGetGtin(string?, out string) -> bool; canonical 14 digits
PoStore.MetaGtin = "gtin"
LocalPoInfo.Gtin: string
ProductionOrderSummary.TotalCodes: int { get; init; } // actual
PoSyncResult(int PoCount, int NewPoCount, int NewCodeCount) // constructor cũ giữ nguyên
  Orders: IReadOnlyList<PoSyncOrderResult>
  AcceptedCount, PlannedCount: int
  InvalidCodes: IReadOnlyList<PoCodeRejection>
  DuplicateCodes: IReadOnlyList<string>
  HasValidationErrors: bool
PoCodeRejection(string CodeContent, string Reason)
PoSyncOrderResult(string OrderNo, int PlannedCount, int InputCount, int AcceptedCount,
  IReadOnlyList<PoCodeRejection> InvalidCodes, IReadOnlyList<string> DuplicateCodes,
  string? ValidationError = null)
```

`AcceptedCount` là mã mới thực lưu; replay có thể accepted=0 và duplicate>0. `ValidationError` là lỗi toàn snapshot, `InvalidCodes` mang chi tiết lỗi code. Lỗi network/HTTP/JSON từ client vẫn throw, không biến thành validation/sync thành công rỗng.

### Quyết định phối hợp của CEO

- Planned merge `Math.Max(existing.OrderQty, incoming.OrderQty)`; replay không tăng target và append không âm thầm hạ planned. Không biến `orderQty` thành accepted/actual.
- Receive multipart vẫn phải `planned == raw input count` trước filter. Download là snapshot có thể accepted-only: chấp nhận `0 < rawCount <= planned`, không giả lập các record server đã loại. `rawCount > planned`, planned không hợp lệ hoặc snapshot rỗng -> `ValidationError`, không mutation PO đó.
- Ví dụ đã test: download planned4/raw4 lọc còn1 giữ planned4; planned4/raw1 accepted-only vẫn giữ planned4; planned1/raw2 bị từ chối.
- Legacy metadata GTIN đã có nhưng codes trộn/sai vẫn phải bị chặn khi bổ sung. Đã thông báo backend kiểm tập `existingCodes` thực tế, không chỉ dựa property metadata.

### Kiểm tra đã chạy

- `dotnet build ClassLibraryMT.Po\ClassLibraryMT.Po.csproj --no-restore`: **pass, 0 warnings, 0 errors**.
- `dotnet run --project tools\PoTests\PoTests.csproj --no-restore`: lần cuối **142 pass, 0 fail**. Gồm rollback planned/scan khi đổi GTIN đã lưu; persistence planned4/actual1/GTIN14; legacy additional/missing/mixed; parser GTIN ngắn, checksum, repeated AI path/query/ngoặc; toàn bộ lifecycle scan/PLC/activation cũ.
- `dotnet run --project tools\PoFlowTests\PoFlowTests.csproj --no-restore`: lần cuối **63 pass, 0 fail**. Gồm download4→1 counts/raw, replay/append exact case, planned không hạ, start/restore actual khác planned, accepted-only, restart khác GTIN, metadata count/GTIN lỗi, all-invalid, legacy mixed và exception vận chuyển.
- `git diff --check -- ClassLibraryMT.Po tools/PoTests tools/PoFlowTests`: **pass**, không whitespace errors.

### Giới hạn và bước tiếp theo

- Chưa chạy HTTP/server/browser/publish/AOT hoặc suite owner khác; CEO/PO-01-B/PO-01-C nghiệm thu tích hợp theo quyền.
- Sync giữ semantics merge theo từng PO như trước; lỗi transport ở PO sau vẫn throw và không rollback PO trước đã commit. Không hứa transaction toàn danh sách.
- `LocalPoInfo.Gtin` chỉ infer legacy trên lượt đọc; lần nhận/download hợp lệ sau đó persist MetaGtin. Không tự sửa các PO opaque/mixed đã lưu; chúng vẫn xem/scan theo identity cũ, nhưng cần xử lý dữ liệu trước khi append tự động.
- Đã giữ nguyên tám file `docs/plans` deleted có sẵn và `.playwright-cli/`; không dọn artifact, không gọi production, không deploy.
- Core đã sẵn sàng cho owner server/callers build và CEO chạy acceptance. Worker dừng mutation sau checkpoint này, sẵn sàng nhận delta nhỏ nếu nghiệm thu phát hiện.
