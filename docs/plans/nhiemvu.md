# Đây là trang giao việc cho văn phòng AI

## Nhiệm vụ 1
### Trạng thái :
Hoàn tất triển khai và nghiệm thu — task PO-01. Chờ người dùng duyệt nhánh riêng trước khi gộp.
### Nội dung :
Dây giờ nhận thông tin PO sẽ gồm có orderNo tức LOT name, orderQty và GTIN, một PO không được chứa nhiều GTIN, khi nhận file mã tự loại bỏ các mã sai cấu trúc và mã trùng trả về cho Client. Sửa cả luồn nhận và luồng down.

### Liên kết:
`.session-org/visionf/TASKS.md`; `.session-org/visionf/departments/po-integrations/STATE.md`
### Kết quả:
Đã triển khai `orderNo`/LOT (giữ alias `poName`), planned `orderQty` và đúng một GTIN có checksum, chuẩn hóa riêng GTIN về 14 chữ số. Receive/download giữ nguyên raw `CodeContent`, loại mã sai cấu trúc, lệch GTIN và trùng ordinal; trả số nhận/kế hoạch cùng chi tiết mã loại. Planned đã lưu giữ giá trị lớn nhất, kể cả upload toàn mã trùng; actual và lịch sử quét được giữ độc lập. Receive yêu cầu số record gốc bằng planned; download chấp nhận snapshot accepted-only không vượt planned, phân biệt validation với lỗi vận chuyển.

Kiểm tra cuối ngày 26/11/2026: PoTests **142**, PoFlowTests **63**, PoServerTests **121**, HTTP e2e **31**, MesSimulatorTests **31** (có DI/config thật), console **40** đều đạt. CEO kế nhiệm chạy lại `PoAcceptanceTests` cấu hình Release sau source B/C/UI cuối: **154 assertion, 0 nhóm lỗi, exit 0**. TanTienQR build đạt, còn một cảnh báo `AVLN3001` có sẵn ngoài phạm vi. Hướng dẫn: `docs/MES-PO-API.md`, `docs/MES-SIMULATOR.md`; chứng cứ và giới hạn trong `.session-org/visionf/TASKS.md` và các LOG được liên kết.

## Nhiệm vụ 2
### Trạng thái :
Hoàn tất triển khai và nghiệm thu — task PO-02. Chờ người dùng duyệt nhánh riêng trước khi gộp.
### Nội dung :
Để trực quan hoá luồn nhận, làm cho tôi một trang html để tôi có thể tạo PO và gửi cho TanTienApp.

### Liên kết:
`.session-org/visionf/TASKS.md`; `.session-org/visionf/departments/po-demo-ui/STATE.md`
### Kết quả:
Trang `docs/po-demo/` đã gửi FormData thật tới relative `POST /api/po`, chuyển nguyên File, hiển thị counts và raw mã loại bằng `textContent`. Host nhúng đúng hai asset qua `GET /po-demo/` và `GET /po-demo/validation.js`; chỉ listener thực sự bind loopback được bỏ key cho hai GET này, API vẫn giữ `x-api-key`. Key password chỉ dùng tạm trong header, không vào URL/body/summary/storage và được xóa sau thao tác.

Node **18/18** đạt, CEO chạy lại sau source cuối. Browser trên receiver thật đã đạt nhận đủ/partial/toàn trùng, 400/401/409/422, mất mạng, chặn gửi kép, key/storage/pagehide và bố cục desktop/mobile 390px. Sau rebuild backend delta cuối, browser kiểm planned **1→4→1**, SQLite giữ **planned4/actual1**, raw URL nguyên vẹn và GTIN chuẩn hóa; tài nguyên embedded khớp source theo SHA-256. Host thử nghiệm đã dừng, không còn listener 18085. Hướng dẫn mở trang: `docs/po-demo/README.md`; hồ sơ phòng: `.session-org/visionf/departments/po-demo-ui/STATE.md`.

Phạm vi nghiệm thu là local/temp DB; chưa kiểm định gói publish single-file/Native AOT hoặc thiết bị PLC thật. Nhánh bàn giao: `codex/po-gtin-demo`; chỉ commit/push để người dùng duyệt, không tự merge hoặc deploy.
