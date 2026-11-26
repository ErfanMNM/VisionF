# Thêm mã bằng handheld, nhập tay và PDA

Tab **Thêm mã** dùng cùng quy tắc PO và bộ ghi lịch sử của camera. Nhập tay vào ô rồi bấm **Thêm mã**. Mã nhận từ cổng COM được gửi tự động khi thiết bị kết thúc khung bằng CR hoặc LF; CRLF chỉ tạo một lần thêm. Bảng hiển thị kết quả hiện tại và 10 lần thêm gần nhất từ cơ sở dữ liệu riêng.

## Vòng đời handheld

`Scanner:Mode = Serial` dùng `Scanner:ComPort` và `Scanner:BaudRate`. Giữ hai chế độ TCP để kiểm thử/tích hợp đã có. Cổng chỉ được mở khi tab Tra cứu mã hoặc Thêm mã đang hoạt động. Mỗi lần vào tab chỉ thử kết nối một lần; nếu không có thiết bị thì hiện **Không có tay quét**. Rời tab hoặc đăng xuất đóng kết nối. Mở lại tab cho phép một lần thử mới. Dấu phân cách GS1 được giữ nguyên; khung quá dài hoặc chưa kết thúc bị bỏ.

## PDA và POST

Listener PDA chạy cùng ứng dụng, độc lập với việc đã mở tab Thêm mã hay chưa:

- Trang nhập: `http://<IP-máy-chạy-app>:18080/manual-activation`.
- Thêm mã: `POST /api/manual-activation`, JSON `{"codeContent":"<toàn-bộ-mã>","source":"PDA"}` hoặc form `codeContent=...`.
- Tra cứu 10 kết quả mới nhất: `GET /api/manual-activation/status`.

Trang PDA tự focus ô mã khi mở và sau mỗi lần gửi, hiển thị kết quả, đồng thời nạp lại lịch sử. Khi mất kết nối, kiểm tra lịch sử trước khi gửi lại. Mỗi yêu cầu trả `id`, `codeContent`, `source`, `status`, `message`, `orderNo`, `receivedUtc`, `success` và `auditPersisted`.

Cấu hình listener ở mục `ManualActivation` trong `appsettings.json`: `HttpPrefix`, `QueueCapacity`, `HistoryPath`, `MaxCodeLength`, `MaxBodyBytes`, `MaxConcurrentRequests`, `RequestTimeoutSeconds`. Mặc định prefix là `http://+:18080/`. Windows có thể yêu cầu URL ACL cho tài khoản chạy ứng dụng và firewall cho phép cổng này. Nếu listener không mở được, xem trạng thái PDA và log kỹ thuật; không coi thao tác desktop thành công là bằng chứng PDA đã kết nối được. Chỉ mở listener trên mạng vận hành tin cậy theo cùng mô hình triển khai PO local.

## Xử lý và lưu dữ liệu

Nguồn nhập tay, COM và POST cùng vào hàng đợi Manual Active hữu hạn. Consumer của `CameraSignalWorker` được đánh thức khi có việc và xử lý luân phiên với camera; không có worker polling riêng. Nhánh thêm thủ công hoạt động khi camera/PLC mất kết nối và không phát lệnh PLC.

Mã phải có cấu trúc GS1 hợp lệ, GTIN đúng checksum và khớp PO đang chạy, toàn bộ `CodeContent` phải tồn tại chính xác trong dictionary của PO và chưa được quét/kích hoạt. Đối chiếu mã vẫn phân biệt hoa thường và giữ nguyên nội dung. Hệ thống dành chỗ trong hàng đợi persistence trước khi tiêu thụ mã nguyên tử trong RAM, nên nhiều nguồn gửi cùng mã chỉ có một lần Pass.

`success=true` nghĩa là mã đã được chấp nhận trong RAM và sự kiện ghi bất biến đã vào hàng đợi ghi chung; thông báo nêu rõ đang lưu và đồng bộ PO. Đây chưa phải xác nhận giao dịch của cả hai file SQLite. Bộ ghi hiện có ghi `scans.db` và outbox, cập nhật đúng PO theo danh tính thời điểm tạo, rồi mới cho bản ghi đủ điều kiện gửi Checkee. Bảo trì và shutdown dùng cùng barrier để chờ các sự kiện đang xử lý.

Nhật ký riêng nằm tại `logs/manual-activation.db` theo mặc định. Nó lưu cả lần thành công, trùng, sai mã, bị từ chối và trạng thái gián đoạn; dùng ID lần thêm để cập nhật kết quả. `auditPersisted=false` báo lỗi lưu nhật ký riêng, không biến một mã đã được nhận thành một mã có thể thử lại an toàn. Sau restart, bản ghi Pending chưa chốt được đánh dấu Interrupted để đối chiếu với lịch sử quét.

## Gửi Checkee

Có thể bật/tắt gửi ngay trên giao diện monitor hoặc lưu `Checkee:Enabled` trong Cấu hình. Gate áp dụng cho chu kỳ nền, gửi thủ công và gửi lại lỗi. GTIN lấy từ PO; lô lấy từ metadata PO (`lot`, `lotNo`, `batchCode`, `batchNo`, không phân biệt hoa thường), nếu thiếu thì dùng số PO. Không còn trường nhập GTIN/lô hoặc cho phép gửi mã không Pass trong Cấu hình.

Phiên gửi chỉ chọn Pass đã được persistence xác nhận của đúng PO được quan sát đang Running, đối chiếu cả số PO và thời điểm tạo. Khi PO tự hoàn tất, snapshot đó được giữ để gửi hết các mã đã nhận, kể cả mã cuối; khi PO khác chạy thì chuyển sang phiên mới. Không tự chọn các PO lịch sử đã hoàn tất khi mở ứng dụng. Hủy PO chặn gửi tiếp phiên đó. Mã sai GTIN không được gộp vào payload hoặc đánh dấu đã gửi.

Monitor có ba thẻ **Mã đang chờ gửi**, **Mã đã gửi**, **Mã gửi lỗi**, cùng lịch sử từng lần gửi. Ngoài snapshot SQLite hiện có, audit gửi ghi riêng tại `logs/checkee-send-audit.jsonl` theo mặc định; không ghi API key hoặc client secret. Mục Giả lập MES gửi PO test đã được bỏ khỏi ứng dụng.

## Nút xóa PLC

Xóa lỗi và Xóa số PLC cần quyền chỉnh cấu hình, PLC sẵn sàng và địa chỉ D thực tế. `Plc:ResetCounterAddress` và `Plc:ClearErrorAddress` mặc định trống vì ladder chưa cung cấp mapping chung. Giao diện hiển thị nguyên nhân chưa thể thao tác. Chỉ nhập địa chỉ được xác nhận cho máy; ACK thao tác không tự xóa các bộ đếm hiển thị trong ứng dụng.
