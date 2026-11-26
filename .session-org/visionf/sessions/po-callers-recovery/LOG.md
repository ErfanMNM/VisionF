# Nhật ký khôi phục callers/fixtures PO

- Task: `PO-01-C`; parent: `/root`; agent: `/root/po_callers_recovery`.
- Thread thực xác minh qua `CODEX_THREAD_ID`: `01a21775-037e-7131-ae18-5734d2d25b36`; budget đầu: `normal`, 6/100, exact=true.
- Project root: `C:\Users\THUC\source\repos\ErfanMNM\VisionF`; CEO đang active: `01a2175d-8c03-7cb0-8b6c-0fd6c8e9dcf9`.
- Quyền ghi: `ClassLibraryMT.MesSimulator/**`, `tools/MesSimulatorTests/**`, source/test trong `tools/PoServer/**` (không HTML), `TanTienQR/ViewModels/ProductionOrdersViewModel.cs` và nhật ký này. Không sửa core, receiver, `tools/PoServerTests`, hồ sơ tổng hoặc file của worker khác.
- Đã đọc skill `session-worker`, `session-ops`, giao thức, review nghiệm thu và `DECISIONS.md`. Giữ nguyên tám plan deleted và `.playwright-cli/`; không commit/push/deploy/production.

## 2026-11-26 — Nhận việc

- MES chưa có đường truyền GTIN, còn gửi `poName`; fixture simulator/console dùng opaque code nên không còn khớp contract GS1.
- Mock store đang lưu quantity theo actual, chưa lưu/đọc GTIN và chưa lọc cấu trúc/GTIN; VM download hiện chỉ báo thành công tổng.
- Đã phối hợp A về shape kết quả: `InvalidCodes:IReadOnlyList<PoCodeRejection>`, `DuplicateCodes:IReadOnlyList<string>`, `ValidationError:string?`; chờ A/B source ready trước build/test phụ thuộc.
- Trang HTML mock vẫn thiếu GTIN; đã báo parent vì HTML nằm ngoài quyền ghi explicit.

## 2026-11-26T17:56:47+07:00 — Hoàn thành source và kiểm tra callers

- Parent cấp thêm quyền sửa form tối thiểu `tools/PoServer/www/index.html`; không sửa `docs/po-demo/`.
- Nhánh hiện tại do CEO tạo: `codex/po-gtin-demo`; HEAD nền `b66dce690674bebbbe395f47cd052cbc2777fe47`; mọi thay đổi dưới đây chưa commit. Chỉ CEO được commit/push.
- `ClassLibraryMT.MesSimulator/MesSimulatorOptions.cs`: thêm `Gtin`, `CustomOrderNo`; giữ `CustomPoName` fallback để cấu hình cũ tương thích. `OrderQty` là planned và bằng record input trước lọc.
- `MesSimulatorClient.cs`: gửi multipart `orderNo`, planned `orderQty`, GTIN checksum chuẩn hóa 14 chữ số. Thêm tham số `string? gtin = null` sau `CancellationToken` để chữ ký cũ còn compile, mặc định lấy `MesSimulator:Gtin`. Chỉ chuẩn hóa metadata, không thay raw file/CodeContent. Thiếu/sai GTIN bị chặn trước HTTP. `MesSimulatorWorker.cs` dùng cấu hình GTIN, ưu tiên `CustomOrderNo` rồi alias cũ/tên file.
- `tools/PoServer/PoServerStore.cs`: thêm migration cột Orders.Gtin; lưu/trả normalized GTIN, planned merge `MAX(existing,incoming)`, actual vẫn từ Codes. Lọc GS1/cùng GTIN/duplicate ordinal, raw response và DB giữ nguyên. Không mutation khi all-invalid, metadata GTIN sai/mâu thuẫn hay planned mismatch. Legacy chỉ suy GTIN khi toàn bộ raw chứng minh cùng định danh; raw opaque cũ vẫn đọc được nhưng không cho append âm thầm. Activation/migration/transaction được giữ.
- `PoConsoleServer.cs`: truyền GTIN metadata sang store, all-invalid/GTIN sai trả 422. Form mock thêm GTIN/orderNo/planned và hiển thị raw rejected bằng `textContent`, giữ layout.
- `TanTienQR/ViewModels/ProductionOrdersViewModel.cs`: dùng `PoSyncResult.Orders` và aggregates hiển thị accepted/planned/invalid/duplicate, PO validation lỗi và snapshot nguồn partial. Lỗi transport vẫn qua catch/DescribeDownloadError. Log không còn luôn ghi success cho validation lỗi. Cột planned giữ `OrderQty`; `ProgressText` chia cho `_summary.TotalCodes` actual để không hiển thị PO partial hoàn tất thành `1 / 4`.
- Fixtures MES/console chuyển sang GS1 có checksum đúng, giữ các dạng phân biệt hoa thường/host/quote/comma và test raw Unicode/CRLF khi bị loại. Không đổi expected status để che fixture opaque cũ.

### Chứng cứ thực thi

1. `dotnet run --project tools/MesSimulatorTests/MesSimulatorTests.csproj --no-restore`: PASS **28** checks, exit 0.
2. `dotnet run --project tools/PoServer/Tests/PoConsoleTests.csproj --no-restore`: PASS **37** checks, exit 0 sau bổ sung legacy. Bao gồm HTTP receive → PoClient → core download accepted-only partial planned=4/actual=1, GTIN/planned/actual reload, raw rejection, no-GTIN/all-invalid/GTIN conflict, activation và rollback trigger. Dòng `SQLite Error 19: 'test reject'.` là lỗi cố ý của bài rollback, suite vẫn exit 0.
3. `dotnet build TanTienQR/TanTienQR.csproj --no-restore -v:minimal`: PASS, **0 error / 1 warning** có sẵn `AVLN3001` ở `ConnectionConfigurationConfirmWindow.axaml` (ngoài scope).
4. `git diff --check -- ClassLibraryMT.MesSimulator tools/MesSimulatorTests tools/PoServer TanTienQR/ViewModels/ProductionOrdersViewModel.cs`: PASS, không output. Không sửa cảnh báo whitespace ở file worker khác.

### Giới hạn và bàn giao

- Không chạy browser cho form mock hoặc UI TanTienQR; browser demo thật và review cuối do parent/PO-02 nghiệm thu. Chưa xác minh rendering status message qua giao diện native; build xác nhận binding/type compile.
- Không sửa docs API ngoài quyền; đã gửi B yêu cầu ghi cấu hình MES mới. Không sửa bảng tổng/outcomes; parent quyết định đóng task sau nghiệm thu chung.
- Đã nhả build token. Không còn tiến trình server/test của C chạy nền; các suite dùng listener loopback tạm và tự dọn dữ liệu temp của mình.
- Budget checkpoint: `normal`, **30/100**, exact=true, còn 70 (thread riêng, không cộng vào parent). Chỉ ghi LOG theo quyền, không tạo budget/ACK ngoài phạm vi.
- Tiếp theo cho CEO: review diff chín file kể trên, ghép kết quả acceptance/browser với A/B; chỉ yêu cầu C sửa tiếp nếu có finding trong phạm vi. C dừng mutation tại checkpoint này.

## 2026-11-26T18:06:40+07:00 — Sửa delta review trong phạm vi C

- Parent giao lại hai finding: P1 đường đăng ký DI chưa đọc `Gtin`/`CustomOrderNo`, P2 console all-duplicate trả sớm làm mất planned lớn hơn. Parent mở thêm quyền **chỉ** `docs/MES-SIMULATOR.md` để bỏ hướng dẫn không gửi GTIN đã lỗi thời. Không mở phạm vi khác.
- `ClassLibraryMT.MesSimulator/MesSimulatorServiceCollectionExtensions.cs`: thêm mapping `MesSimulator:Gtin` và `MesSimulator:CustomOrderNo` trong Configure thủ công.
- `tools/MesSimulatorTests/Program.cs`: thêm `IConfiguration` từ `ConfigurationBuilder.AddInMemoryCollection`, đăng ký `AddMesSimulator` vào `ServiceCollection`, resolve worker thật qua DI và gửi HTTP loopback. Kiểm options có hai field mới, upload accepted=2/planned=3, PO dùng `CustomOrderNo` thay vì alias và DB giữ GTIN.
- `tools/PoServer/PoServerStore.cs`: khi không có mã mới nhưng toàn duplicate hợp lệ, transaction chỉ cập nhật `Orders.OrderQty=MAX(existing,incoming)` rồi commit; không động raw codes, timestamp kích hoạt hay metadata khác. Validation/all-invalid vẫn không mutation.
- `tools/PoServer/Tests/Program.cs`: HTTP tạo PO planned=1 với raw A, rồi gửi bốn bản A/planned=4; assert accepted=0, duplicate=4, planned response=4 và sau mở store lại DB planned=4, actual=1, raw/GTIN/CreatedUtc/ActivatedUtc không đổi.
- `docs/MES-SIMULATOR.md`: đồng bộ metadata `orderNo`+GTIN bắt buộc, cấu hình Gtin/CustomOrderNo và alias, tham số client bổ sung sau CancellationToken, ví dụ CSV/C#/curl/activation đều GS1 checksum hợp lệ, giải thích partial snapshot accepted-only và planned max cả all-duplicate. Không sửa docs khác.

### Chứng cứ delta

1. Sau khi B nhả build token, `dotnet run --project tools/MesSimulatorTests/MesSimulatorTests.csproj --no-restore`: PASS **31** checks, exit 0 (thay mốc 28 trước).
2. `dotnet run --project tools/PoServer/Tests/PoConsoleTests.csproj --no-restore`: PASS **40** checks, exit 0 (thay mốc 37 trước). Dòng trigger SQLite cố ý vẫn thuộc bài rollback pass.
3. `git diff --check -- ClassLibraryMT.MesSimulator tools/MesSimulatorTests tools/PoServer TanTienQR/ViewModels/ProductionOrdersViewModel.cs docs/MES-SIMULATOR.md`: PASS, không output.
4. Đã nhả build token cho B sau hai suite; B tiếp tục receiver planned all-duplicate và guard/static suite riêng. Không chạy lại TanTienQR vì delta library đã compile qua suite MES; VM không đổi ở batch này.

- Budget riêng: `normal`, **42/100**, exact=true, còn 58. Không ghi/commit hồ sơ tổng; không commit/push/deploy; không còn tiến trình C nền. Bản source C hiện gồm mười một file tracked thay đổi (chín file cũ + DI registration + hướng dẫn MES).
- C dừng mutation sau checkpoint delta; CEO hiện tại/kế nhiệm có thể nghiệm thu từ LOG này. Việc còn ngoài C: review chung, browser demo thật và quyết định commit/push do CEO đảm nhiệm.
