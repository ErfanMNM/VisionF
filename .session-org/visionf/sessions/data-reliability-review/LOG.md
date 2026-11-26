# DATA-02 — data-reliability-review

- Agent collaboration ID: `/root/data_reliability_review` (chưa có thread UUID xác minh; không chạy bộ đếm).
- Vai trò: reviewer chỉ đọc; owner: parent `/root`.
- Phạm vi: đọc flow `CameraSignalWorker`, `ScanStore`/channel, PO cache/store, intake gates và tests; không sửa code, tests hoặc hồ sơ cấp dự án.
- Mục tiêu: xác định mất record khi queue đầy; PLC offline lúc nhận mã; thứ tự đánh dấu dictionary/PO trước phát PLC; tính bền vững Scan DB/Checkee và các race/error/restart; đề xuất test nghiệm thu.
- Trạng thái đầu vào: `git status` có nhiều thay đổi chưa commit do các tác vụ khác; giữ nguyên.

## Nhật ký

### 2026-11-26 — bắt đầu
Đã đọc `session-worker/SKILL.md`, `session-ops/SKILL.md` và `session-ops/references/protocol.md`. Sẽ chỉ ghi trong thư mục log riêng này, không sửa file sản phẩm/tests hoặc `BRIEF.md`, `REGISTRY.md`, `TASKS.md`, `DECISIONS.md`.

## Kết quả khảo sát DATA-02 (chỉ đọc)

Workspace: `C:\Users\THUC\source\repos\ErfanMNM\VisionF`, branch `master` tại lúc đọc; `git status` đã có nhiều thay đổi chưa commit ở scanner/PO/scan tests do tác vụ khác. Không đọc/sửa lẫn các thay đổi đó. Nội dung dưới đây mô tả code hiện có trong working tree lúc khảo sát, không phải thẩm định diff của agent triển khai.

### Luồng hiện tại

- `TanTienQR/Services/CameraSignalWorker.cs`: Camera `Received` đi qua `ScanIntakeGate.Dispatch`. Nếu gate suspended, callback bị bỏ lặng lẽ. Nếu nhận, `_inFlight` chỉ cho tối đa một request đang xử lý/chờ; request đến khi đang bận thành `Overload`, không vào `_scanQueue`/không ghi PLC, nhưng được `Publish` (UI + Scan DB enqueue). Vì thế channel capacity 512 / `DropOldest` không phải đường kiểm soát tải thực tế; `Overload` là từ chối rõ ràng thay queue FIFO.
- Khi không bị từ chối, worker kiểm tra `_po.IsHalted`, NoRead và GS1 structure; mã đúng cấu trúc vào `ProductionOrderService.ProcessCodeAsync`. Service giữ mutation lock qua validate → durable pending intent → PLC callback → commit PO. Không có PO hoặc mã invalid/duplicate thì không bắn Pass; Duplicate/InvalidForOrder phát Fail.
- `ClassLibraryMT.Po/PoStore.cs`: `BeginPendingPassAsync` ghi `PendingPassUtc` trước PLC. Cache cũng có PendingPass. PLC trả thành công thì `CommitPendingPassAsync` đổi atomic `PendingPassUtc` thành `ScannedUtc`/`Status='Scanned'`, sau đó cache đánh dấu scanned. Nếu PLC trả false rõ ràng, service xóa PendingPass để mã còn available. Nếu lỗi/ACK không rõ, giữ PendingPass/halt; restart load marker và không phát lại.
- Sau khi quyết định outcome, `Publish` đẩy `ScanRecord` vào `ScanFeed` cho UI (bounded 1000, DropOldest) và gọi `IScanRecordWriter.Enqueue` sang SQLite Scan DB. Đây là hai lịch sử/outbox độc lập với DB PO và không synchronous với PLC/PO commit.
- `ClassLibraryMT.Scans/ScanRecordWriter.cs`: queue SQLite bounded, DropOldest. Overflow callback latch `_writeFailure`; thao tác `Enqueue` không báo caller rằng một record cũ vừa rơi. Batch INSERT lỗi bị giữ latch và log, nhưng batch đã bị clear khỏi pending, không retry. `FlushAsync` chỉ barrier, lỗi ghi bị nuốt; `VerifyHealthyAsync` ném lỗi sau khi flush/latch kiểm tra. Tìm thấy VerifyHealthy dùng ở preflight/cập nhật; không thấy `CameraSignalWorker` khóa intake theo health writer.
- `CheckeeSendWorker` flush Scan DB rồi query các row theo điều kiện Pass (mặc định OnlyPassCodes); payload lấy CodeContent đầy đủ. HTTP/Checkee reject chỉ đổi `SendStatus`, không thay `Status` scan; network/metadata thiếu thì Pending. Vì vậy worker không tự biến row Fail thành Pass, nhưng một row không có trong Scan DB thì không thể được Checkee gửi.
- `PlcWriter.WriteResultAsync(pass:true)` phát Pass một lần, không retry lệnh vật lý; ACK/timeout lỗi không xác định ném để PO quarantine. PLC status chỉ được cập nhật theo kết quả write/read/heartbeat/config. Trong callback/`HandleAsync` không có gate yêu cầu `GlobalVariables.PLCStatus == Connected` trước khi nhận/xử lý mã; `ProductionOrderService.IsProductionRunning` không xét PLC status. PLC tắt (`Enabled=false`) làm callback PLC trả false (không attempt), sau đó PO pending bị xóa và camera ghi outcome `PlcError`; PLC offline/timeout sau send có thể thành trạng thái không xác định/halt.

### Mất record và phân biệt trạng thái xử lý / lịch sử

1. **Mất scan history khi queue Scan DB đầy:** `DropOldest` làm rơi bản ghi cũ; chỉ health latch cho cập nhật tương lai, không cảnh báo/halt/reject camera tại thời điểm enqueue. `CameraSignalWorker` vẫn tiếp tục xử lý PLC/PO.
2. **Mất batch khi DB INSERT lỗi:** writer bắt lỗi và bỏ batch khỏi list (không retry), còn `FlushAsync` không ném. `CheckeeSendWorker` có thể tiếp tục query mà không thấy các row vừa mất.
3. **Ngoại lệ trong xử lý camera:** catch tổng ở `ProcessQueueAsync` chỉ log rồi giảm `_inFlight`, không gọi `Publish`. Lỗi PO DB trước khi `passAttempted` (ví dụ `BeginPendingPassAsync` lỗi), lỗi validation/lifecycle hoặc cancellation/error ở các đường gửi Fail có thể làm mất luôn history; catch quarantine riêng chỉ hoạt động sau khi đã thử Pass hoặc PO đã halted.
4. **Restart/crash giữa PO commit và `Publish`/Scan DB insert:** PO đã ghi Scanned nhưng event Scan DB chưa tồn tại. Hai DB không có transaction/outbox chung; worker Checkee chỉ đọc Scan DB nên không thể tự phục hồi row còn thiếu. `Dispose` writer có cố flush khi graceful shutdown, nhưng crash/force kill không đảm bảo.
5. **Gate suspended:** `ScanIntakeGate.Dispatch` không chạy callback khi suspended, không phát `Overload`/`Halted` record; nếu yêu cầu “mọi mã camera đã nhận phải có lịch sử” bao gồm cả thời điểm gate đóng, đây là thêm đường mất record. `ScanFeed` cũng cố ý drop UI history riêng; UI không phải nguồn bền vững.

### Bất biến cần CEO chốt/giữ

- Một frame camera phải có một kết quả có thể đối soát (accepted/explicitly rejected/reason) và Scan DB bền vững; queue/backpressure không được drop im lặng. Với capacity giới hạn, chính sách phải là backpressure, queue bền vững, hoặc explicit fail-closed admission có bằng chứng bền vững; tăng capacity một mình không chứng minh không mất.
- PLC đang offline lúc intake theo yêu cầu thì không gọi PO/PLC xử lý mã đó; vẫn cần record chính xác `PLC unavailable` và không Pass, nếu định nghĩa “mọi record camera” tính cả frame khi offline. Cần xét race PLC rớt sau admission.
- Với code hợp lệ, cần phân biệt marker pending trước phát lệnh và trạng thái scanned/consumed đã commit. Code hiện có marker pending trước PLC; kết quả reject chắc chắn xóa marker, còn timeout/unknown giữ quarantine. Câu yêu cầu “đánh dấu dictionary/PO trước khi phát PLC, không hoàn tác do PLC send fail” đọc theo nghĩa literal là khác hành vi hiện tại (không commit Scanned trước send và có clear pending khi PLC trả false); CEO cần xác định chính sách cho reject chắc chắn vs ACK mơ hồ, để không tạo trạng thái PO/Scan DB mâu thuẫn.
- `ScanStatuses.Pass` chỉ được ghi khi PLC Pass ACK thành công **và** trạng thái PO bền vững xác nhận committed. ACK timeout + commit lỗi không được thành Pass/Checkee send; giữ durable pending/halt cho đối soát. Checkee chỉ được chọn row Pass có bằng chứng này; lỗi gửi là thuộc tính gửi, không đổi scan outcome.
- DB lỗi trước PLC: không gửi Pass khi chưa thể ghi intent bền vững. DB lỗi sau PLC ACK: không retry lệnh, quarantine/halt và không ghi Pass sai; cần đường phục hồi để Scan DB/Checkee không mất record.

### Tình huống lỗi/race cần bao phủ

- Camera tăng tốc/PLC chậm → Overload; stress Scan DB channel thật đầy chứ không chỉ Camera channel.
- PLC disconnect đã có trước `Received`; disconnect xảy ra giữa `Received` và `WriteResultAsync`; PLC disabled; known PLC rejection; timeout/mất ACK sau khi PLC có thể đã thực thi.
- Lỗi PO DB khi ghi pending (trước PLC); lỗi xóa pending sau rejection; lỗi commit Scanned sau ACK; lỗi ghi halt metadata; lỗi ghi Scan DB độc lập sau commit.
- Graceful shutdown/drain và force restart ở từng biên pending → PLC send → ack → commit PO → Scan DB enqueue/commit; restart không replay Pass mơ hồ.
- Reject Pass có biết chắc: quyết định mark/rollback đúng yêu cầu nghiệp vụ; Checkee không lấy row không-Pass.
- UI consumer chậm/ScanFeed overflow không làm nhầm rằng đã lưu bền vững.
- Checkee HTTP reject/timeout/retry giữ nguyên scan `Pass` duy nhất khi Pass thật, và không gửi record Fail/Overload/Halted như Pass.

### Tests đang có và phần còn thiếu

Có kiểm tra PO exact match, mark pending trước send, confirmed PLC rejection giữ mã available, ACK mất thì quarantine, lỗi commit sau ACK không giả Pass, restart không replay và completion lỗi không đổi Pass thật (`tools/PoTests/CodeContentLifecycleChecks.cs`, `tools/CameraHotApplyTests/CameraCodeContentChecks.cs`). Checkee test xác nhận HTTP reject chỉ đổi SendStatus, transient giữ Pending, payload full content (`tools/CheckeeSendTests/Program.cs`). Scan DB update-safety test xác nhận write lỗi/queue capacity=1 overflow được health latch để chặn update (`tools/UpdateSafetyTests/Program.cs`).

Thiếu tối thiểu để nghiệm thu DATA-02: (a) camera integration stress với PLC bị chặn và writer DB capacity nhỏ/DB lock; assert mọi received có outcome/history, không row bị drop hoặc mất im lặng; (b) test xác nhận writer overflow/write failure làm camera fail-closed/báo durable evidence theo chính sách; (c) PLC status offline trước nhận + disconnect sau nhận, assert không phát Pass hoặc xử lý code sai yêu cầu; (d) kiểm tra durable PO marker tồn tại tại thời điểm PLC callback, và test policy confirmed reject/lost ACK riêng; (e) fault injection vào BeginPendingPass, PO commit và Scan DB insert sau PO commit; restart rồi xác nhận không false Pass, không replay, và history/Checkee recover hoặc trạng thái đối soát hiển thị; (f) test gate suspended/worker shutdown để xác nhận frame nào bị bỏ nhận có dấu vết hay không theo scope; (g) test tổng hợp row Pass đã persist → Checkee eligible, mọi status khác bị loại.

### Đường dẫn đã đọc

- `TanTienQR/Services/CameraSignalWorker.cs`
- `TanTienQR/Services/ScanFeed.cs`
- `TanTienQR/Services/ScanIntakeGate.cs`
- `TanTienQR/Services/PlcWriter.cs`
- `TanTienQR/Services/CheckeeSendWorker.cs`
- `ClassLibraryMT.Scans/ScanRecordWriter.cs`, `ScanStore.cs`, `IScanRecordWriter.cs`
- `ClassLibraryMT.Po/ProductionOrderService.cs`, `PoStore.cs`, `PoCodeCache.cs`, `PoContracts.cs`
- `tools/CameraHotApplyTests/CameraCodeContentChecks.cs`, `tools/PoTests/CodeContentLifecycleChecks.cs`, `tools/ScanTests/Program.cs`, `tools/UpdateSafetyTests/Program.cs`, `tools/CheckeeSendTests/Program.cs`

Không chạy test trong lượt review chỉ đọc. Không sửa code/tests hoặc hồ sơ cấp dự án.
