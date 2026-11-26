# Quyết định dự án
## 2026-11-26 — Chia hai nhiệm vụ PO thành hai phòng chạy song song
- Bối cảnh và yêu cầu liên quan: file kế hoạch có một thay đổi hợp đồng/luồng xử lý PO và một trang HTML tạo PO; người dùng yêu cầu tạo chat phụ để tăng tốc.
- Quyết định, người chịu trách nhiệm và lý do: CEO giữ hồ sơ tổng; giao luồng dữ liệu PO cho phòng Tích hợp PO và trang HTML cho phòng Giao diện tạo PO. Cho phép UI khảo sát/khung độc lập ngay; chốt tích hợp sau khi API contract được PO-01 xác nhận.
- Ảnh hưởng đến task/phòng: PO-02 phụ thuộc contract của PO-01 trước khi hoàn thiện luồng gửi.

## 2026-11-26 — Xử lý phụ thuộc origin giữa trang demo và API PO
- Bối cảnh và yêu cầu liên quan: khảo sát PO-00 xác nhận `tools/PoServer/www/index.html` dùng mock server; `PoHttpServer` thật chưa phục vụ file tĩnh hoặc CORS/preflight nên trang độc lập không đọc được JSON response.
- Quyết định, người chịu trách nhiệm và lý do: ưu tiên phục vụ trang PO cùng origin loopback với API. Nếu không khả thi, dùng allowlist đúng origin local và preflight; không dùng wildcard. PO-01 sở hữu endpoint/hosting và contract; PO-02 sở hữu UI và chỉ bật gửi sau khi contract được xác nhận.
- Ảnh hưởng đến task/phòng: PO-02 đang chạy phần UI có thể tách; gửi tới TanTienApp phụ thuộc PO-01. CEO theo dõi quyết định trong STATE phòng và nghiệm thu khả năng đọc success/error.

## 2026-11-26 — Giữ nguyên định danh mã và báo partial receive minh bạch
- Bối cảnh và yêu cầu liên quan: hợp đồng hiện dùng toàn bộ `CodeContent` làm identity; yêu cầu mới lọc cấu trúc sai, mã trùng và giới hạn một GTIN trên mỗi PO.
- Quyết định, người chịu trách nhiệm và lý do: không cắt/chuẩn hóa nội dung code; xác minh GTIN của code với GTIN duy nhất của PO; báo riêng số/lý do loại (cấu trúc, GTIN không khớp, duplicate) và số accepted. Giữ `orderQty` là lượng kế hoạch do caller gửi, không âm thầm thay bằng accepted count; báo chênh lệch để Client thấy partial receive.
- Ảnh hưởng đến task/phòng: PO-01 triển khai contract/filter/response và test; PO-02 hiển thị cùng thông tin, cảnh báo khi accepted count thấp hơn orderQty.

## 2026-11-26 — Chốt contract PO cho receive/download
- Bối cảnh và yêu cầu liên quan: PO-00 cho thấy API hiện dùng `poName`, PO model chưa có GTIN, receive lọc duplicate nhưng không validate; Client cần phản hồi rõ.
- Quyết định, người chịu trách nhiệm và lý do: giữ `POST /api/po`; dùng `orderNo` (hỗ trợ alias `poName`), `orderQty` planned hiện hành, một `gtin` bắt buộc; file một cột `CodeContent` giữ nguyên raw. Receive lọc từng record theo invalidStructure/gtinMismatch/duplicate, trả `acceptedCount` + `rejectedCodes`; download lọc cùng luật và trả counts qua `PoSyncResult`. PO-01 sở hữu contract/tests, CEO nghiệm thu backward compatibility.
- Ảnh hưởng đến task/phòng: PO-02 tích hợp theo contract trên sau khi PO-01 ghi route/hosting cùng-origin. Static route cùng-origin loopback vẫn là phần bắt buộc đang được xác nhận để browser có thể đọc response.

## 2026-11-26 — Bảo toàn API key khi phục vụ trang demo
- Bối cảnh và yêu cầu liên quan: API hiện kiểm tra `x-api-key` trước mọi route; browser navigation không tự gửi header này khi mở trang HTML.
- Quyết định, người chịu trách nhiệm và lý do: PO-01 được giao phục vụ chỉ tài nguyên GET tĩnh từ root cố định, chỉ nếu listener được xác nhận bind loopback và route ngăn path traversal; API `/api/po` giữ nguyên yêu cầu key. PO-02 nhập key qua trường password và chỉ gửi trong header, không lưu browser storage, không render/log key. Không nhúng cấu hình bí mật vào HTML.
- Ảnh hưởng đến task/phòng: static route/auth boundary cần test; UI phải xử lý 401 và không giữ key sau phiên.

## 2026-11-26 — Đề xuất route cùng origin cho trang PO demo
- Bối cảnh và yêu cầu liên quan: khảo sát `po_hosting_review` xác nhận listener chưa có static/CORS; updater không đóng gói thư mục `docs/po-demo` thành runtime subdirectory.
- Quyết định, người chịu trách nhiệm và lý do: hướng dùng `GET /po-demo/` phục vụ HTML embedded trong assembly; UI gọi relative `/api/po`. Không mở static file server trên disk và không cần CORS. PO-01 xác nhận/triển khai route và wiring package; chỉ nghiệm thu sau test local.
- Ảnh hưởng đến task/phòng: PO-02 tiếp tục UI độc lập; gửi thật vẫn khóa đến khi route exact chạy cùng origin và `x-api-key` đi đúng header. Nhật ký: `.session-org/visionf/sessions/po-hosting-review/LOG.md`.
## 2026-11-26 — So sánh GTIN theo định danh chuẩn, giữ nguyên CodeContent
- Bối cảnh: metadata/UI chấp nhận GTIN có checksum dài 8/12/13/14; mã GS1 AI (01) thường biểu diễn 14 chữ số. So sánh literal sẽ loại nhầm GTIN tương đương được thêm số 0 đầu.
- Quyết định của CEO kế nhiệm: xác thực checksum trước, chuẩn hóa riêng GTIN về 14 chữ số để so sánh/lưu định danh PO xuyên suốt receive/download. Không sửa chuỗi raw CodeContent và không dùng chuẩn hóa GTIN để dedupe CodeContent.
- Nghiệm thu: metadata `4006381333931` phải khớp mã có AI (01) `04006381333931`; GTIN khác thật vẫn bị loại. Append/reload dùng cùng quy tắc.
## 2026-11-26 — User yêu cầu commit và push nhánh riêng
- Quyền mới do user trực tiếp cấp trong chat CEO: hoàn tất nhiệm vụ, commit và push nhánh riêng, chờ user duyệt mới gộp.
- CEO thực hiện Git theo phạm vi PO đã nghiệm thu; worker không tự commit/push. Dùng nhánh `codex/po-gtin-demo` nếu chưa tồn tại. Không merge hoặc deploy; tám plan deleted có sẵn và output `.playwright-cli/` không được đưa vào commit nhiệm vụ này.

## 2026-11-26 — Planned khi merge và snapshot download
- Merge cùng PO giữ planned lớn nhất giữa giá trị hiện có và incoming; replay không tự tăng/hạ target. Actual vẫn là số code raw unique đã chấp nhận.
- Receive multipart: planned phải bằng số record file trước lọc để bắt lỗi metadata/file.
- Download: nguồn có thể chỉ trả snapshot accepted-only sau khi lọc ở server; cho phép raw count không lớn hơn planned và giữ planned, hiển thị partial qua counts. Raw lớn hơn planned hoặc metadata không hợp lệ trả validation outcome không mutation. Không giả lập danh sách mã đã bị nguồn bỏ đi.
- Worker core sở hữu triển khai/test; caller hiển thị kết quả additive và phân biệt lỗi transport.

## 2026-11-26 — Nghiệm thu cuối và khóa phạm vi publish
- CEO kế nhiệm đã nhận quyền hợp lệ, giữ hai phòng hiện có và owner thực thi; không lập lại tổ chức.
- Planned max áp dụng cả upload toàn duplicate; B/C đã sửa và kiểm planned1→4→1, giữ actual/raw/scan/metadata mô tả. Không còn exception target như một snapshot review lịch sử.
- Core142/flow63, server121/e2e31, MES31/console40, Node18 và AcceptanceRelease154/0lỗi đạt. Browser sau rebuild cuối xác nhận planned4/actual1 và hai asset embedded khớp source; host test đã dừng.
- Publish chỉ nhánh codex/po-gtin-demo. Loại tám deletion có sẵn, .playwright-cli, budget JSON, output/temp/script/ảnh/JSON evidence; chỉ chọn source/test/docs và Markdown PO liên quan. Sau push phải đối chiếu SHA local/remote, chờ user duyệt mới gộp.
