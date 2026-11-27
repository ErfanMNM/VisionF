# Kết quả Thêm mã, PDA và Checkee — 27/11/2026

Đã triển khai trên checkout `master`, nền `30c43205192682a062978734e5db9fdbe13a505b`. Chưa commit, push hoặc deploy. Hướng dẫn vận hành: [Thêm mã và Checkee](../manual-activation.md).

## Chức năng hoàn tất

- Tab **Thêm mã** có ô nhập, nút thêm cho nhập tay, nhận COM tự gửi khi đủ khung CR/LF, kết quả gần nhất và 10 lần nhận gần nhất đọc từ SQLite riêng.
- COM chỉ kết nối khi tab Tra cứu/Thêm mã hoạt động; mỗi lần mở tab thử một lần, lỗi báo Không có tay quét; rời tab/đăng xuất đóng kết nối. Mở lại có phiên kết nối mới. Giữ GS1 separator, bỏ cả khung quá dài/chưa kết thúc và ngăn callback cũ gửi sang tab mới.
- Trang PDA `http://<IP-máy>:18080/manual-activation`, POST `/api/manual-activation`, trạng thái `/api/manual-activation/status`. Chạy độc lập với tab desktop; autofocus/refocus, trạng thái và lịch sử. Listener lỗi URL ACL/port có trạng thái rõ, không làm ứng dụng sập hoặc âm thầm fallback localhost.
- Hàng đợi Manual Active hữu hạn được consumer camera đánh thức và xử lý luân phiên; không worker polling riêng, không gửi PLC, vẫn xử lý khi camera/PLC offline. Kiểm tra GS1/checksum/GTIN PO, toàn bộ CodeContent chính xác trong dictionary và trạng thái chưa quét/kích hoạt. Dành chỗ persistence trước khi tiêu thụ RAM nguyên tử, dùng outbox ghi scans.db/đúng PO. Nhật ký thêm riêng ở `logs/manual-activation.db`.
- Checkee bật/tắt runtime, chỉ gửi Pass đã đồng bộ đúng danh tính PO và GTIN. GTIN/lô lấy từ PO; phiên đã quan sát Running được drain sau auto-complete để không sót mã cuối, hết hiệu lực khi PO khác bắt đầu hoặc bị hủy; startup không tự chọn lịch sử đã hoàn tất. Đường dẫn GS1 tương đối, URL và element string gửi serial đúng, giữ nguyên CodeContent tại máy.
- Audit gửi riêng `logs/checkee-send-audit.jsonl` chứa PO/GTIN/lô, thời gian, attempt, CodeContent/serial, HTTP và phản hồi đã che thông tin xác thực; độc lập retention 60 snapshot SQLite. Monitor có ba thẻ chờ gửi/đã gửi/gửi lỗi. Bố cục có trang cuộn và overlay riêng cho cửa sổ thấp.
- Bỏ simulator MES khỏi cấu hình, đăng ký runtime và tham chiếu ứng dụng. Bỏ cấu hình GTIN/lô/cho phép gửi non-Pass. Căn lại hàng nút/badge màn chính, sửa thông báo và cập nhật CanExecute theo quyền/PLC/mapping/bảo trì/busy.
- Sửa race khởi tạo/migrate schema SQLite: BEGIN IMMEDIATE trước đọc metadata, toàn bộ ALTER/rebuild/index/version trong một transaction; bảo toàn dữ liệu legacy khi nhiều caller mở cùng lúc.

## Kiểm chứng tại thời điểm báo cáo 27/11/2026

Các số liệu và danh sách suite dưới đây ghi nhận phiên bản trước lần gỡ trang Cài đặt V2 và sửa cảnh báo build; `SettingsV2Tests` là kết quả lịch sử.

Lần thành công mới nhất của **17 suite: 1.268 kiểm tra, không lỗi**. Rerun thay thế kết quả cũ, không cộng dồn.

| Suite | Đạt |
| --- | ---: |
| ScanTests | 32 |
| CameraHotApplyTests | 121 |
| ManualActivationTests | 57 |
| ManualActivationUiTests | 28 |
| AsyncCoordinatorTests | 56 |
| AsyncPersistenceTests | 21 |
| ScanOutboxTests | 27 |
| CheckeeSendTests | 112 |
| UpdateSendTests | 18 |
| ConfigTests | 216 |
| SettingsV2Tests | 35 |
| PoFlowTests | 64 |
| PoAcceptanceTests | 154 |
| DeviceConfigurationTests | 24 |
| UpdateSafetyTests | 7 |
| ConfigUiTests | 138 |
| ApplicationUiTests | 158 |

Các suite chạy Release JIT, DB temp riêng, transport giả hoặc TCP/HTTP loopback. Camera tests không gọi PLC thật. Kiểm thử mới bao gồm gửi trùng nhiều nguồn, queue capacity 1, audit SQLite bị khóa không giữ consumer camera, drain/shutdown, gián đoạn nhật ký/restart, gửi Checkee theo PO và mã cuối, audit còn truy vết sau retention, mở schema đồng thời 12 caller trên DB mới/legacy và trạng thái Sent được giữ nguyên.

Build đã ghi nhận trước lần sửa gần đây: `dotnet build TanTienQR/TanTienQR.csproj -c Release -p:PublishAot=false` thành công, gồm helper PrinterRemoteHost: **0 error, 2 warning** (event `HistoryConflict` chưa sử dụng và constructor runtime loader của `ConnectionConfigurationConfirmWindow`). Cả hai cảnh báo đã được khắc phục ở cập nhật sau.

## Xác minh các cập nhật sau báo cáo

Các lệnh dưới đây chỉ dùng build/test headless, không mở ứng dụng trên desktop:

- `dotnet build TanTienQR/TanTienQR.csproj -c Debug -p:PublishAot=false` — **0 cảnh báo, 0 lỗi**.
- `dotnet run --project tools/PoTests/PoTests.csproj -c Debug -p:PublishAot=false -p:SkipPrinterRemoteHelper=true --no-launch-profile` — **134 đạt, 0 lỗi**.
- `dotnet run --project tools/ConfigUiTests/ConfigUiTests.csproj -c Debug -p:PublishAot=false -p:SkipPrinterRemoteHelper=true --no-launch-profile -- --confirmation-artifacts <thư-mục-tạm>` — **140 đạt, 0 lỗi**.
- `dotnet run --project tools/FullscreenNumpadTests/FullscreenNumpadTests.csproj -c Debug -p:PublishAot=false -p:SkipPrinterRemoteHelper=true --no-launch-profile` — **31 đạt, 0 lỗi**; xác nhận trang camera dùng IP trực tiếp, tự cập nhật địa chỉ và giữ cổng HTTP tùy chỉnh mà không nối thêm đường dẫn.

Các suite dùng môi trường Avalonia headless và SQLite trong thư mục tạm; không kết nối thiết bị thật.

Đã xem ảnh Thêm mã ở 1100×760 và 1366×768, màn chính, Tra cứu ở vùng 1048×540, monitor Pending/Failed cùng ảnh Sent. Suite UI tạo đủ ảnh ba tab monitor ở 1048×540 và 1280×720, giữ nguyên assertion vùng bảng quét tối thiểu 24px. PDA được kiểm tra trên trình duyệt 390×844: ô mã tự focus, POST mã sai trả InvalidStructure, lịch sử tăng, focus quay lại và không tràn ngang.

QA độc lập đã kiểm source và xác nhận đóng các finding về pressure lease, audit chặn camera, khả năng truy vết audit, serial GS1 tương đối, PO tự hoàn tất trước sự kiện Running và migration đồng thời. Log, SHA256 checkpoint nguồn và registry điều phối nằm ngoài repo tại `D:\DuAn\VisionF-coordination\manual-activation-20261127`.

## Điều kiện vận hành còn cần xác nhận tại máy

- **Hai lệnh xóa PLC cần mapping D thực tế.** `Plc:ResetCounterAddress` và `Plc:ClearErrorAddress` đang mặc định trống có chủ đích. Giữ guard an toàn và hiện đúng lý do khóa; không tự đoán địa chỉ ladder. Khi có mapping đúng, quyền phù hợp và PLC sẵn sàng, các nút được bật.
- Chưa kiểm handheld COM vật lý, PDA qua LAN/firewall/Windows URL ACL, PLC/camera thật hoặc Checkee production. Kết quả loopback/fake không thay thế nghiệm thu thiết bị.
- Success thêm mã xác nhận đã chấp nhận RAM và publish sự kiện persistence; bộ ghi chung hoàn tất scans/PO bất đồng bộ. Audit riêng lỗi được báo bằng AuditPersisted=false, không biến mã đã nhận thành mã có thể thử lại an toàn.
