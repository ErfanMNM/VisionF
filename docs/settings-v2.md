# Cài đặt V2

Mở **Thêm → Cài đặt V2** bằng tài khoản có quyền chỉnh cấu hình. Trang **Cấu hình** cũ vẫn hoạt động.

Giao diện dựa trên mẫu sơ đồ luồng được cung cấp: khối QRBox màu xanh ở giữa, các thiết bị/dịch vụ hai bên và bảng thuộc tính bên phải. Chọn một khối để sửa thông số thực của nhóm đó:

| Khối | Nhóm cấu hình |
| --- | --- |
| QRBox | PLC Omron: kết nối, vùng D, bộ đếm, reset và Clear |
| Camera | Đầu đọc mã TCP |
| Scanner | Máy quét cầm tay |
| MultiConnect | Gateway chuyển giao thức |
| Printer | Máy in G10 |
| Checkee | Gửi mã kích hoạt |
| Nhận PO từ MES | HTTP server nhận lệnh sản xuất |
| Production Order | Server quản lý PO |
| Web sản xuất | Trang Checkee nhúng |

**Cài đặt khác** gom các nhóm còn lại như bảo mật/tài khoản, ghi log, lịch sử, điều khiển máy in từ xa, giả lập MES và license. Không tạo bản sao cấu hình: hai trang dùng cùng `ConfigEditorViewModel`, cùng các field, quy tắc kiểm tra, lệnh lưu/hoàn tác và cơ chế xác nhận/áp dụng Camera/PLC. Chuyển giữa hai trang giữ thay đổi chưa lưu; nút **Lưu cấu hình** lưu mọi nhóm đã sửa trong bản nháp chung.

Camera, PLC và Printer hiển thị trạng thái thiết bị thực tế. Các khối dịch vụ chỉ báo bật/tắt cấu hình, không giả lập kết nối. Địa chỉ trên sơ đồ là giá trị của bản nháp; thiết bị chỉ đổi sau khi lưu/xác nhận thành công. Những nhóm cần khởi động lại vẫn hiển thị thông báo và nút khởi động lại. Trường mật khẩu giữ che ký tự.

Ở màn hình nhỏ, sơ đồ giữ chữ và vùng chạm ở kích thước dễ dùng, có thanh cuộn ngang/dọc. Khi mở sơ đồ hoặc chọn một khối, khối đang chọn được đưa vào vùng nhìn thấy; bảng thuộc tính giữ rộng 350 px và cuộn độc lập.

## Kiểm chứng

Chạy `dotnet run --project tools/SettingsV2Tests -c Release -p:PublishAot=false -p:SkipPrinterRemoteHelper=true -- <thư-mục-ảnh>`.

Harness dùng Avalonia.Headless/Skia, cấu hình và SQLite trong thư mục tạm, bộ áp dụng thiết bị giả và không khởi động hosted workers. Nó kiểm quyền điều hướng, toàn bộ nhóm cấu hình có đường truy cập, bản nháp chung, xác nhận/hủy/validation/lưu, giữ key mở rộng và che mật khẩu. Ảnh được tạo ở 1048×540 và 1280×720 cho sơ đồ/cài đặt khác; vị trí QRBox và khối được chọn được kiểm so với viewport thực tế. Không cần thiết bị hoặc dịch vụ production.
