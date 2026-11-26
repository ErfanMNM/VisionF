# Bàn giao PO-02 — preview xong, chờ route để tích hợp

- Ngày: 2026-11-26
- Tiền nhiệm: `01a21732-6a7f-7a53-8673-def42dc3ebd6`
- CEO hiện tại: `01a2175d-8c03-7cb0-8b6c-0fd6c8e9dcf9`
- Repo: `C:\Users\THUC\source\repos\ErfanMNM\VisionF`
- Branch: `master`
- STATE_ROOT: `C:\Users\THUC\source\repos\ErfanMNM\VisionF\.session-org\visionf`
- Scope tiếp tục: PO-02, chỉ sửa `docs/po-demo/` và hồ sơ phòng/session khi owner. Không sửa backend PO-01. Theo chỉ dẫn mới, chỉ CEO commit/push; không tự commit.

## Preview đã hoàn thành

- `docs/po-demo/index.html`: form orderNo/LOT, planned orderQty, GTIN, file CodeContent raw, API key password transient; summary và placeholder kết quả accepted/invalid/GTIN mismatch/duplicate; cảnh báo nếu accepted khác planned; nút gửi đang disabled.
- `docs/po-demo/validation.js`: helper checksum GTIN classic JS, được nạp bằng relative `<script src="./validation.js">`.
- `docs/po-demo/validation.test.mjs`: kiểm tra GTIN-8/12/13/14, checksum và đầu vào lỗi.
- `docs/po-demo/README.md`: chạy preview/test, URL tích hợp dự kiến, không gửi từ origin riêng.
- Key không có form `name`, không đi vào summary/FormData tự động, không dùng browser storage/log. Chưa có `fetch` hay request `/api/po`; CodeContent chưa đọc/biến đổi.

## Kiểm tra

- `node --test docs/po-demo/validation.test.mjs`: 2 passed, 0 failed.
- Playwright session `po02-final`, URL `http://127.0.0.1:8765/`: GTIN `4006381333932` -> invalid, summary hidden; `4006381333931` -> valid, summary visible; send disabled. Console 0 errors/warnings.
- `Invoke-WebRequest -Method Head http://127.0.0.1:8765/validation.js`: `application/javascript`.
- `.playwright-cli/` là snapshots/log do session tiền nhiệm tạo; output tạm kiểm thử, owner là thread PO-02 tiền nhiệm, không phải asset app.

## Chặn và tiếp theo

- PO-01 đề xuất `GET /po-demo/` (alias tùy chọn `/po-demo/index.html`) trên cùng origin loopback với `/api/po`; route/asset chưa được ghi và test trong STATE PO-01 tại thời điểm checkpoint. CEO báo worker B `/root/po_server_recovery` đang làm server/static route.
- Đã báo PO-01 dependency `validation.js`: cần embedded exact allowlist `/po-demo/validation.js` và MIME JavaScript; không copy từ `docs` trên disk khi app chạy.
- Contract gần nhất theo handover: `POST /api/po`; `orderNo` (legacy alias `poName`), planned `orderQty`, một GTIN đúng checksum, file CSV/JSON/XLSX cột CodeContent giữ raw; response `acceptedCount`, `plannedCount`, `invalidCodes`, `duplicateCodes`. Đọc STATE PO-01 mới nhất trước khi implement vì cần schema/reason exact và route test.
- Khi route/test xác nhận: viết submit bằng `FormData` tường minh cho metadata/file; key riêng qua header `x-api-key`, optional nếu key rỗng; giữ trong memory, không storage/log/summary và xóa sau response; map 401, transport errors, partial groups và warning accepted vs planned. Browser-test route/JS MIME/submit/response trên local, không production.
- Repo hiện có thay đổi ngoài phạm vi; đọc `git status --short` và bảo toàn. Không commit/push/deploy.
- Budget tiền nhiệm tại checkpoint: 93/100 (`rotate_now`); không tiếp tục source work ở session cũ.
