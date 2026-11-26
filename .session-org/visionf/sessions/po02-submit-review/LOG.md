# Nhật ký PO-02-REVIEW

## 2026-11-26 17:57 (Asia/Saigon) — Nhận việc và đọc contract
- Agent collaboration: `/root/po02_submit_review`; parent `/root` thuộc phòng UI, owner thread `01a21779-c0fb-76a1-9c92-75207202dbd9`.
- Thread thật riêng đã xác minh qua `CODEX_THREAD_ID`: `01a2177e-f514-7ca3-bcc7-2ea98a8a31dc`. `CODEX_SESSION_ID` là thread parent và không dùng cho budget.
- Budget riêng bằng `request_budget.py --thread-id 01a2177e-f514-7ca3-bcc7-2ea98a8a31dc`: `3/100`, `normal`, `exact=true` lúc 17:56. Không ghi budget.json vì chỉ được ghi file nhật ký này.
- Đã đọc `session-worker/SKILL.md`, `session-ops/SKILL.md`, `session-ops/references/protocol.md`, trạng thái/công việc phòng UI và trạng thái PO-01.
- Quyền: read-only `docs/po-demo/` và contract backend. Chỉ ghi nhật ký này; không Git mutation, HTTP POST, host hay source/test/STATE tổng.
- Backend exact: `PoPushResponse.invalidCodes` là `{code,reason}[]`; reasons hiện có `invalidStructure`, `gtinMismatch` (`PoHttpServer.cs:739-747`); `duplicateCodes` là raw string[], đếm theo record bị loại (`749-753`).
- HTTP 200 vẫn có `acceptedCount=0`, `success=true` khi toàn mã trùng (`757-775`). Partial 200 lưu mã mới và trả nhóm loại; `plannedCount` giữ request, không được tự chuyển thành accepted.
- 422 gồm GTIN metadata sai (`662-671`), GTIN xung đột PO cũ (`719-729`) và không còn mã hợp lệ (`757-775`); không phải ca nào cũng có danh sách invalid.
- 409 gồm PO đang chạy (có thể response orderNo rỗng), PO kết thúc, trùng tên DB sau chuẩn hoá. 401 chỉ nên hướng dẫn kiểm tra/nhập lại key.
- File record count phải bằng `orderQty` trước lọc (`681-685`). File đầu vào phải được truyền nguyên File; UI không đọc/biến đổi dữ liệu.
- Đã gửi parent đề xuất pure helper/test response: 200 full/partial/all-duplicates; 401; 422 metadata/all-invalid; 409; 408/500/non-JSON/transport, không tự retry; key transient, explicit FormData.
- Source lúc đọc vẫn ở preview; chờ parent báo source ready để review tích hợp.

## 2026-11-26 18:03 (Asia/Saigon) — Review tích hợp và unit test
- Parent mở rộng quyền rõ ràng: được tạo riêng `docs/po-demo/response.test.mjs`; vẫn không sửa helper/HTML/backend hay state tổng.
- Đã thêm 16 test response/upload/location, dùng fetch và FormData dependency injection; không request mạng. Cộng 2 test GTIN hiện có là 18 ca.
- Bao phủ: route exact loopback HTTP(S), full/partial/all duplicate 200, 401/409 không có orderNo, 422 metadata và mixed invalid, 400/413/415, 408/5xx uncertain, null/malformed response, raw code/reason chưa biết, File identity/multipart metadata exact, key header-only và cleanup, redaction key chứa quote/backslash, non-JSON, TypeError/AbortError và không retry.
- Hai phát hiện đã báo parent và được sửa trong source sở hữu của parent: HTTP408 cần uncertain=true; `valid` cần Boolean(...) để payload null không làm ok/showCounts/differs trả null.
- Source review: explicit FormData chỉ orderNo/orderQty/gtin/file; relative `/api/po`; mode same-origin, credentials omit, redirect error; password không name; key input xóa trước await, biến/header cleanup; không storage/log; hiển thị bằng textContent; detail giới hạn 200 record và giữ reason lạ; file không bị đọc/biến đổi.
- Kết quả sau sửa: `node --test docs/po-demo/validation.test.mjs docs/po-demo/response.test.mjs` đạt 18/18; `git diff --check -- docs/po-demo` không báo lỗi. Đây là kiểm tra thuần Node, không thay browser smoke host thật do parent phụ trách.
- Budget riêng gần nhất: 17/100 normal exact lúc 18:01. Không có nguồn lỗi nghiêm trọng còn mở từ vòng review này.

- Rà thêm nhánh reason lạ: đã báo parent dùng Object.hasOwn cho map label, vì object reasons hiện có thể nhận inherited property như toString. textContent khiến không có XSS; không phải blocker contract hiện tại. Giữ source helper/HTML do parent sửa. Test bổ sung xác nhận showCounts/differs luôn boolean cho null/malformed; kết quả vẫn 18/18.
