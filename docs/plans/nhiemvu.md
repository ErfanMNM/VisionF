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

## Nhiệm vụ 3
### Trạng thái :
Hoàn thành bản V2 thử nghiệm và kiểm thử giao diện ngày 26/11/2026.
### Nội dung :
Bạn thêm thử cho tôi một trang cài đặt v2 với giao diện kiểu: C:\Users\TH\Downloads\Mẫu 1 · Sơ đồ luồng-html ,
Cài PLC PLC thì là cục QRBox còn các cài đặt khác thì cho vào một chỗ khác.

### Liên kết:
[Cài đặt V2](../settings-v2.md), [Báo cáo kiểm chứng](nhiemvu-ketqua-2026-11-26.md).
### Kết quả:
Mở Thêm → Cài đặt V2 với quyền chỉnh cấu hình. QRBox chỉnh PLC, các thiết bị/dịch vụ có khối chọn và nhóm còn lại nằm ở Cài đặt khác. Dùng chung field/validation/lưu/xác nhận/hotapply với trang cũ; giữ bản nháp khi chuyển trang. Tự cuộn tới khối đang chọn ở màn hình nhỏ. SettingsV2Tests 28/0; đã kiểm ảnh 1048×540 và 1280×720, giữ trang Cấu hình cũ.
