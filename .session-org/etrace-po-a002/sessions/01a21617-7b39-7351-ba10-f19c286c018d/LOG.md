# Nhật ký ETRACE-01-W1
- Worker thread UUID: 01a21617-7b39-7351-ba10-f19c286c018d; agent canonical path: /root/integration_worker.
- Parent: 01a21616-70a0-7d80-9ccc-73bb2ba6c4f0; project root: C:\Users\THUC\source\repos\ErfanMNM\VisionF.
- Vai trò: executor duy nhất tạo/import PO A002 và gửi CSV được cấp; không bật app/worker/backlog; không sửa sản phẩm/UI/config.
- Được ghi: .tmp-etrace-po-a002, artifacts đã che bí mật, báo cáo docs/etrace-po-a002-report-2026-11-26.md và journal riêng.
- Đã đọc session-worker, session-ops/protocol, pdf và BRIEF/DECISIONS/STATE/TASKS phòng. Budget đầu 3/100 normal.
- Repo master @ 92ba3ca09bdc5ba7706cdc430b6a94805f0d3c37; thay đổi UI có sẵn được giữ nguyên.
- Đang khảo sát; chưa có mutation PO/API.

## Checkpoint 11:28 +07, 26/11/2026
- Chỉ đạo mới người dùng qua CEO/parent: dùng toàn bộ Link tra cứu 1, chỉ trim ngoài và bỏ dấu cách sau GTIN trước /21/ theo mẫu; supersede lựa chọn serial ban đầu. Chưa có mutation theo lựa chọn cũ.
- A002 local đã tồn tại Running với 1.000 URL; tập URL1 chuẩn hóa khớp tuyệt đối, 0 thiếu/thừa, hash sorted d57a33e706ffd058103298bae8dfe1d3e6536ed457a080c4aa5f44c4cd2e36f1. 0 Scanned, 0 Activated, 1000 Pending. Parent đã review độc lập/no-op import được chấp nhận.
- local-preflight.json và A002-CodeContent.csv đã tạo; không sửa DB app.
- PDF 115 trang: đọc/extract hợp đồng và render/kiểm hình trang 93; mục13.1 không nêu max batch hoặc idempotency. Chỉ pilot1 sau preflight; lỗi chắc chắn dừng; wrapper sản phẩm tự retry401 và fallback HTTP200 không dùng cho lần này.
- Harness .tmp-etrace-po-a002/etrace_a002.py có token/GET preflight, pilot guard1, marker create-only, không retry/redirect, redaction và GET hậu kiểm. py_compile/source exact match/redaction synthetic đều pass. Chưa chạy mạng do chưa có nguồn credential cấu hình.
- Đã rà appsettings*.json cả repo/bin/artifacts và env checkee/etrace: không có ApiKey/ClientId nonempty. Parent/CEO đang xác định nguồn cục bộ test25/11; không in hay lưu bí mật.

## Remote preflight thực tế sau khi nhận credential cục bộ
- Credential chỉ đọc trong bộ nhớ từ đường dẫn tạm cụ thể do CEO/parent cấp; không in/copy bí mật. Đã dừng tìm nguồn khác.
- token-preflight: HTTP200/status=true. Profile, product, batch-list A002, find full URL và serial-list: HTTP401 NOT_VERIFIED_USER. Find GTIN+serialNumber đúng tên spec trả400 Either GTIN and Serial Number or Serial is required. Chưa POST; không thể xác minh remote batch/membership/type.
- Đã báo parent checkpoint trước pilot; pilot-authorized vẫn reviewed=false. Auth shape diagnostic GET riêng chỉ ghi boolean prefix/header/whitespace, không token. DB cũ hash nguyên.

## Hoàn tất kiểm tra thực tế và chốt báo cáo
- CEO chỉ định đúng một POST URL1 dù preflight401; lưu authorization_basis/remoteReadUnverified=true, các cờ remote verified giữ false. Không tự nới quyền cho999 mã còn lại.
- POST bắt đầu 2026-11-26T11:32:15.643239+07:00, kết thúc 11:32:15.729950+07:00: HTTP400/status=false/data=null, message `Provide either serialNumbers or fromIndex and toIndex, not both`. Body chỉ có GTIN,batchCode,type,serialNumbers[fullURL1], không fromIndex/toIndex.
- Dừng mutation sau lỗi chắc chắn; không retry. GET hậu kiểm URL/batch401 NOT_VERIFIED_USER; metadata400 validation. Không đối soát remote được.
- Tổng HTTP13: GET12,POST1;200×3,401×7,400×3. CSV1000/localExact1000/importedNew0/sent1/acceptedConfirmed0/rejected1/notSent999/remoteStateUnknown1000.
- DB local SHA-256 trước/sau ef3318263c40282f0cf9a501d294b00459d018f376d12142f4c3260c663665d5; không cập nhật Running/Pending/Scanned/Activated/Sent. Không bật app/worker/backlog.
- Đầu ra: docs/etrace-po-a002-report-2026-11-26.md; artifacts pilot-response/final-summary và từng HTTP đã che bí mật; harness/README và CSV một cột ở .tmp-etrace-po-a002.
- Đã kiểm cú pháp, guard SHA và local exact match, mô phỏng token malformed/nonobject/valid để kiểm redaction không HTTP. Secret literal audit23 file PASS. File credential tạm đúng đích được xóa lúc 2026-11-26T11:35:01.128669+07:00; không đổi config sản phẩm.
- Không commit/deploy/đổi nhánh; giữ nguyên sửa UI có sẵn. Trạng thái report_ready/blocked_external_etrace; không tiếp tục mutation trừ chỉ đạo mới và đối soát.
