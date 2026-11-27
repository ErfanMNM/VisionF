# Đây là trang giao việc cho văn phòng AI

## Nhiệm vụ 1
### Trạng thái :
Hoàn thành triển khai và kiểm thử mô phỏng ngày 26/11/2026.
### Nội dung :
Thêm vào cấu hình chọn các ô nhớ D cho hệ thống số đếm gồm Total, Pass, Fail, Timeout, vị trí D reset counter và D Clear (Xóa lỗi).
Cho phép tùy chọn hiển thị giữa số của app và số của PLC

### Liên kết:
[Cấu hình bộ đếm và lệnh PLC](../plc-counters.md), [Báo cáo kiểm chứng](nhiemvu-ketqua-2026-11-26.md).
### Kết quả:
Đã thêm địa chỉ riêng Total/Pass/Fail/Timeout, Reset counter và Clear lỗi; giữ tương thích CounterAddress cũ. Chọn nguồn App/PLC và nhãn nguồn tương ứng, không trộn số PO. Lệnh reset/Clear kiểm quyền trước khi ghi. ConfigTests 208/0, PlcHotApplyTests 142/0; kiểm chứng UI tích hợp đạt. Chưa xác nhận mapping/ladder PLC thật.

## Nhiệm vụ 2
### Trạng thái :
Hoàn thành triển khai và kiểm thử mô phỏng ngày 26/11/2026.
### Nội dung :
Chỗ bảng tra thông tin mã bên dưới có bảng hiện danh sách lỗi gửi checkee => sửa thành bảng lịch sử gửi checkee (60 lần gần nhất gồm số mã ok/fail) => nhấn vào hiện bảng danh sách mã và kết quả

### Liên kết:
[Lịch sử gửi Checkee](../checkee-codecontent.md), [Báo cáo kiểm chứng](nhiemvu-ketqua-2026-11-26.md).
### Kết quả:
Đã thay bảng lỗi bằng 60 lần gửi batch Checkee gần nhất, lưu SQLite bền vững, có số OK/Fail/chưa xác định và mở chi tiết mã/PO/HTTP/thông điệp. Retry giữ lịch sử cũ, không gửi lặp mã vừa thành công khi thao tác xếp hàng. Chi tiết đang mở cập nhật khi có kết quả. CheckeeSendTests 74/0, UpdateSendTests 18/0; kiểm chứng UI tích hợp đạt. Chưa gọi Checkee production.

## Nhiệm vụ 3 — đã gỡ trang thử nghiệm
### Trạng thái :
Trang thử nghiệm đã gỡ khỏi ứng dụng theo yêu cầu ngày 27/11/2026. Dùng tab **Cấu hình** để chỉnh thông số ứng dụng.
### Nội dung :
Trước đây, thêm thử giao diện cài đặt vẽ theo sơ đồ; trang này hiện không còn trong ứng dụng.

### Liên kết:
[Báo cáo kết quả trước đây](nhiemvu-ketqua-2026-11-26.md).
### Kết quả:
Các mục trong báo cáo ngày 26/11/2026 ghi nhận phiên bản trước khi trang thử nghiệm được gỡ.
