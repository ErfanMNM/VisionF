# PO-02 — Báo cáo nghiệm thu giao diện

Ngày 26/11/2026, múi giờ Asia/Saigon. Trưởng phòng: `01a21779-c0fb-76a1-9c92-75207202dbd9`. CEO nhận kết quả: `01a21787-5ed2-7ac2-a73d-80c07b4996d0`.

## Source final

Root: `C:\Users\THUC\source\repos\ErfanMNM\VisionF`; nhánh `codex/po-gtin-demo`. Chỉ sửa source trong `docs/po-demo/`: `index.html`, `validation.js`, `README.md` và thêm `response.test.mjs`; giữ `validation.test.mjs` có sẵn. Không sửa backend, commit, push, merge hoặc deploy. Giữ các thay đổi khác và output `.playwright-cli/`.

Trang dùng relative `POST /api/po`, explicit FormData gồm orderNo/orderQty/gtin/nguyên File. Chỉ cho gửi tại `/po-demo/` trên origin loopback và sau khi kiểm tra thông tin. Map số nhận mới/planned, sai cấu trúc/GTIN khác/trùng, chi tiết raw qua textContent và cảnh báo partial. Có nhánh riêng cho 401/409/422; xử lý 400/413/415 và kết quả chưa xác định với 408/5xx/non-JSON 2xx/transport. Không retry tự động. Khóa form trong request và bắt kiểm tra lại trước gửi tiếp.

API key dùng password không có form name; không đưa vào FormData/URL/summary/storage/log UI. Key chỉ ở header x-api-key, xóa ô nhập khi bắt đầu request, khi hoàn tất và khi pagehide; xóa header/biến tạm sau request. Phản hồi phản chiếu key được che trước hiển thị, kể cả key chứa ký tự escape.

## Kiểm tra source

- `node --test docs/po-demo/*.test.mjs`: **18 passed, 0 failed**. Bao gồm 16 ca mới do worker `/root/po02_submit_review`, thread `01a2177e-f514-7ca3-bcc7-2ea98a8a31dc`, viết và parent kiểm tra lại.
- `node --check docs/po-demo/validation.js` và kiểm cú pháp inline script: PASS.
- `git diff --check -- docs/po-demo`: sạch.
- Review độc lập đã sửa và nghiệm thu: HTTP 408 phải báo chưa xác định, payload null phải cho boolean false, reason lạ phải dùng fallback không lấy inherited property.

## Browser trên snapshot trước delta backend planned-max

Host thật được build bằng `dotnet run --project tools/PoServerTests/PoServerTests.csproj --no-restore -- --serve --port 18085`; phiên host `12967`, URL `http://127.0.0.1:18085/po-demo/`, kho thử nghiệm `C:\Users\THUC\AppData\Local\Temp\visionf-po-demo-0f1b621f76b9417f81f7591abc44226d`. Host dùng key thử nghiệm công khai của fixture, không đọc key/config/DB production.

Đạt: GTIN checksum sai/đúng; sửa input khóa gửi và ẩn summary; 401; nhận đủ 1/1; partial 1/4 với mỗi nhóm loại có đúng một record; toàn mã trùng không báo nhận mới; 422 mã sai và xung đột GTIN metadata; 400 count không khớp; 409 sau khi đặt riêng PO fixture thành Completed; mất mạng; form khóa và bấm lần hai chỉ có một POST; key chỉ header, xóa ngay khi request đang chờ, storage rỗng, pagehide xóa key; file preview vẫn khóa. Phase B có **17 assertion PASS**. Script tái hiện: `browser-phase-a.js`, `browser-phase-b.js` cùng thư mục.

SQLite xác nhận partial giữ planned=4/actual=1, GTIN canonical14 và URL raw gồm query/fragment đúng nguyên văn; JSON upload cũng giữ raw. Các request 401/422/400/offline không tạo DB. Bằng chứng: `persistence.json`, `final-evidence.json`.

Giới hạn telemetry: Chromium trả null cho `request.postDataBuffer()` của multipart dùng Blob/File ở lượt thử đầu. Đã điều chỉnh harness, không tuyên bố đọc được toàn bộ multipart bytes qua Playwright. Việc không đọc/biến đổi file được kiểm bằng unit test File identity/explicit fields, kiểm raw response và SQLite sau nhận thật.

Ảnh `desktop-partial.png` và `mobile-partial.png` đã mở xem: bố cục rõ, viewport390 không tràn ngang, mã HTML bị loại hiện như text và không trở thành element. `network-error.png` ghi trạng thái kết quả chưa xác định.

Console snapshot này có 7 resource error dự kiến do các ca âm (401 hai lượt, 422 hai lượt, 400, 409 và offline). **0 JavaScript pageerror, 0 warning**; không gọi đó là console sạch 0 error. UI không ghi key. Nhật ký browser: `.playwright-cli/console-2026-11-26T11-01-41-355Z.log` dưới repo.

## Browser trên binary FINAL sau delta B server121

Theo yêu cầu CEO, đã Ctrl+C host cũ, rebuild/restart sau source delta planned-max; không chạy lại tràn lan các ca UI bất biến. Host cuối là phiên **13422**, dotnet PID **30556** bắt đầu **18:08:14**, PoServerTests PID **4968** bắt đầu **18:08:16**, ngày 26/11/2026. Kho thử nghiệm mới: `C:\Users\THUC\AppData\Local\Temp\visionf-po-demo-4ad0d637a8e449169d1b2ffe81b9c117`.

- Browser targeted **18 assertion PASS**, script `browser-final-delta.js`: planned gửi **1 → 4 → 1**, accepted mới **1 → 0 → 0**, duplicate **0 → 4 → 1**; UI đúng trạng thái không có mã mới, có cảnh báo và key/storage sạch.
- Sau request cuối, SQLite `PO-BROWSER-FINAL-MAX.db` giữ **planned=4, actual=1**, GTIN canonical và CodeContent raw query/fragment nguyên vẹn. Bằng chứng `final-delta-db.json`.
- DLL PoServer trong thư mục host và class library có cùng SHA256 `4B7B69053613D6FD798F69DB61633DA7D691E5D7142840BB943EE2B3280629C3`; modified **18:08:15+07:00**. PID/time/path/hash tại `host-final.json`.
- HTML/JS embedded bằng đúng bytes source final; HTML SHA256 `8648e4ec89a2d14558154ff996e6ee9a82763a169bc7e8eb06f31925c93c2175`, JS SHA256 `27923fd1d71b51be5e731fe8fb50e8b9d81abb063359aa8afd16094ab5bdbb65`.
- Screenshot `final-all-duplicates.png`; snapshot cuối không JavaScript pageerror hoặc resource error chủ động vì cả ba request đều 200.

## Trạng thái bàn giao

Đã Ctrl+C host cuối, xác minh **remainingHostProcesses=0, listeners18085=0, hostStopped=true**; xem `host-stopped.json`. Browser riêng `po02-integrated` đã đóng. **Build token FREE**, đã báo trực tiếp CEO kế nhiệm để chạy AcceptanceRelease và thực hiện Git trong quyền được giao.

STATE/TASKS phòng và ACK accepted/log đã cập nhật. PO-02 không còn gate source/browser. Ngân sách riêng tại checkpoint: **40/100, normal, exact=true**, lúc 18:10:02+07:00. CEO tiếp tục nghiệm thu tổng, commit/push nhánh riêng và chờ người dùng duyệt; không merge/deploy.
