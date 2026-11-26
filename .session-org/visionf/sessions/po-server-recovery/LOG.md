# Nhật ký PO-01-B khôi phục backend

- Parent: `/root`; CEO được phân công: `01a2175d-8c03-7cb0-8b6c-0fd6c8e9dcf9`.
- Agent thực: `/root/po_server_recovery`; thread xác minh qua `CODEX_THREAD_ID`: `01a21774-bc13-7b52-89bb-79c84af98582`.
- Phạm vi ghi: `ClassLibraryMT.PoServer/**`, `tools/PoServerTests/**`, `docs/MES-PO-API.md` và nhật ký này. Không commit/push/deploy; không sửa tám plan deleted hoặc `.playwright-cli/`.
- Đã đọc `session-worker`, `session-ops`, giao thức, review acceptance và DECISIONS. Budget đầu: normal 3/100 exact=true (2026-11-26T17:44:48+07:00), không ghi budget ngoài phạm vi được giao.

## Nhận việc và triển khai đầu

- Nhận quyền server sau khi PO-01 đã dừng; giữ source dở làm đầu vào.
- Thêm route exact GET `/po-demo/`, `/po-demo/validation.js`, embedded assembly, không disk fallback. Chỉ listener bind loopback được bỏ API key cho đúng hai GET này; API giữ key. Đường dẫn RawUrl exact không resolve traversal.
- Receive dùng GTIN normalized padded14, planned OrderQty từ request, giữ raw ordinal; kiểm metadata/file count trước lọc. Guard legacy: thiếu GTIN chỉ cho append nếu toàn bộ mã hiện hữu parse và khớp GTIN request; legacy không chứng minh được bị 422 nguyên tử.
- Chưa build: chờ core worker báo source sẵn sàng; đang cập nhật fixtures, route/auth và tests contract.

## 2026-11-26 — Build và kiểm chứng backend

- Core A xác nhận source compile sẵn sàng; đã chạy `dotnet build ClassLibraryMT.PoServer/ClassLibraryMT.PoServer.csproj --no-restore`: PASS, 0 warning/0 error.
- `dotnet run --project tools/PoServerTests/PoServerTests.csproj --no-restore`: PASS 116 checks. Có phát hiện HEAD ghi JSON body dẫn lỗi listener; đã sửa HEAD chỉ trả status 404/401 và không body, chạy lại PASS.
- `dotnet run --project tools/PoServerTests/e2e/PoServerE2E.csproj --no-restore`: PASS 31 checks qua HTTP listener thật, CSV/JSON partial giữ raw rejection, XLSX raw hợp lệ, multipart boundary/schema/atomic append/metadata/cleanup.
- Static tests đã kiểm đúng embedded content, key thiếu/sai, chỉ static exact GET loopback bypass; listener wildcard `+` bắt buộc key cả khi request loopback; raw TCP path traversal/percent dot/slash/backslash/alias; HEAD/PUT/POST không serve.
- Theo chỉ đạo CEO: LocalPoInfo.Gtin rỗng bị từ chối append. Core có thể infer GTIN legacy khi toàn bộ mã hợp lệ cùng GTIN; handler còn kiểm mọi mã cũ ngay cả khi metadata đã có GTIN, tránh metadata che mã trộn/sai.
- Đã báo CEO route built ready và báo C nhả build slot. C sau đó xác nhận MES28/console35 PASS, TanTienQR build 0 error/1 warning AVLN3001.
- Đã viết lại `docs/MES-PO-API.md` theo contract thực, alias, planned max/actual, raw/filter, legacy policy, auth/static và ví dụ checksum đúng. Đã dọn whitespace dòng58 của test.
- Còn kiểm sau batch docs: run final tests nếu thêm assertion; chờ UI successor hoàn tất để rebuild embedded source cuối. Chưa xác nhận browser/publish/AOT; CEO nghiệm thu browser riêng.
- Budget checkpoint trước docs: normal 21/100 exact=true tại 2026-11-26T17:53:09+07:00.

## Host browser nghiệm thu

- Theo CEO, thêm mode `--serve [--port <1..65535>]` vào `tools/PoServerTests`: dùng đúng `PoHttpServer`, listener chỉ `127.0.0.1`, DB/upload mới dưới temp; không đọc cấu hình hoặc key production. Key công khai dành riêng cho test: `po-demo-local-test-key`.
- Lệnh: `dotnet run --project tools/PoServerTests/PoServerTests.csproj --no-restore -- --serve --port 18085`; URL `http://127.0.0.1:18085/po-demo/`. Bỏ port để chọn cổng trống. Ctrl+C dừng, giữ store tạm để kiểm chứng.
- Chưa khởi động host: UI successor còn sửa nguồn embedded; cần nhận UI final qua CEO rồi rebuild/start để không smoke assembly UI cũ.

## 2026-11-26T18:01:37+07:00 — Checkpoint cuối, dừng source

- Theo CEO, PO-02 successor tự build embed/start host thật sau UI final. B không giữ tiến trình host nền, không chờ để sửa tiếp UI; build token đã nhả cho CEO/PO-02.
- Tự review phát hiện điều kiện loopback cũ tính lại `Prefix` từ options mutable: đã chốt `_boundToLoopback` từ prefix THỰC sau `_listener.Start()` thành công. Thay `BindAddress` sau Start không thể mở bypass cho wildcard listener. Thêm hai test đổi options cả hai hướng.
- Suite cuối trên core A đã chốt source: `dotnet run --project tools/PoServerTests/PoServerTests.csproj --no-restore` **PASS 119 checks**, gồm body static bằng đúng manifest resources, guard metadata GTIN không che legacy mixed codes, planned4/actual5 độc lập, wildcard/loopback mutable config.
- `dotnet run --project tools/PoServerTests/e2e/PoServerE2E.csproj --no-restore` **PASS 31 checks** lần cuối. Không còn lỗi biên dịch/test; không chạy publish/Native AOT/browser trong worker B.
- `git diff --check -- ClassLibraryMT.PoServer tools/PoServerTests docs/MES-PO-API.md`: PASS, chỉ cảnh báo Git LF→CRLF ở tài liệu, không whitespace error.
- File bàn giao: `ClassLibraryMT.PoServer/{ClassLibraryMT.PoServer.csproj,PoHttpServer.cs,PoServerContracts.cs,PoServerJsonContext.cs,PoUploadHandler.cs}`; `tools/PoServerTests/{PoServerTests.csproj,Program.cs,PoContractChecks.cs,PoDemoTestHost.cs,e2e/Program.cs}`; `docs/MES-PO-API.md`; nhật ký này. Có source dở kế thừa từ PO-01 trước đó trong cùng diff; không ghi đè vùng owner khác.
- HEAD/branch không commit bởi B; CEO giữ quyền commit/push. Tám file plan deleted có sẵn và `.playwright-cli/` giữ nguyên. Không gọi production/deploy.
- Budget cuối: normal **34/100**, exact=true, còn66. Mọi thay đổi chưa commit trong workspace chung. Chỉ nhận delta hẹp do CEO giao nếu nghiệm thu phát hiện.

## Delta cuối — planned max cho toàn duplicate

- CEO review yêu cầu parity: tạo planned1/raw A rồi gửi `[A,A,A,A]`, planned4 phải trả200 accepted0 và persist planned4. Đã sửa nhánh all-duplicate: chỉ khi planned tăng thì `EnsurePoDbAsync` với danh sách mã rỗng, giữ `Additional` cũ, identity/GTIN/status/codes/scans. Nếu planned không tăng, trả200 không mutation. Metadata/GTIN/running guards vẫn trước nhánh này. Event UI phát khi target tăng; health `received` vẫn đếm upload có code mới.
- Thêm regression planned1→4→1, actual1, `productName` cũ còn nguyên. Hai SQLite trigger cấm INSERT và UPDATE `PoCodes` chứng minh merge metadata không đụng mã/scan.
- `docs/MES-PO-API.md` đã sửa cả hai đoạn từng ghi duplicate không cập nhật target: giờ nói rõ planned max áp dụng cả toàn duplicate. Không sửa metadata mô tả của replay duplicate.
- Lượt build default bị MSB3027/MSB3021 do host UI PID24236 đang giữ `tools/PoServerTests/bin/Debug/net10.0/ClassLibraryMT.PoServer.dll`. Không dừng host của owner khác. Đổi output vào temp riêng, build **PASS 0 warning/0 error** và chạy DLL **PASS121**.
- Output suite cuối: `C:/Users/THUC/AppData/Local/Temp/visionf-po-server-final-8d8240e9a39a4287887e49df758689c3/PoServerTests.dll`.
- Output e2e cuối: `C:/Users/THUC/AppData/Local/Temp/visionf-po-e2e-final-1830194702c54cbfba1fae35db1b2462/PoServerE2E.dll`, build **PASS0warning/0error**, **PASS31** qua listener thật. `git diff --check` vùng B PASS (chỉ cảnh báo LF→CRLF docs).
- Đã báo CEO cần PO-02 restart/rebuild host default sau delta backend này, nếu chưa restart thì host đang chạy vẫn là assembly trước planned-delta. Không dùng kết quả browser từ host cũ để khẳng định delta target.
- B đã nhả build token, không tiến trình B nền; dừng source. CEO/PO-02 giữ phần restart/browser/nghiệm thu cuối. Không commit/push/deploy hoặc thay đổi tám plan deleted/artifact khác.
