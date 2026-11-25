# Checkee Middleware UAT - Bruno

Collection kiểm tra theo Checkee Middleware API Integration Specification v1.1.1. Import/mở thư mục này trong Bruno rồi chọn environment `UAT`.

## Cấu hình bảo mật

Trong Environment `UAT`, tạo hai secret variable:

- `clientId`: Client ID do Checkee cấp.
- `apiKey`: API key do Checkee cấp.

Giá trị secret được lưu riêng trong Bruno trên máy của bạn. Không ghi credential hoặc access token vào file collection hay gửi kèm cho người khác.

## Thứ tự chạy

1. `01 - Lấy access token`
2. `02 - Hồ sơ doanh nghiệp`
3. `03 - Tổng quan hạn mức`
4. `04 - Tra serial theo GTIN`
5. `05 - Tra serial theo serial`
6. `06 - Kích hoạt một serial (serialNumbers)`

Request kích hoạt có thể thay đổi dữ liệu từ xa. Chỉ chạy sau khi Checkee xác nhận serial thử, trạng thái lô và quyền doanh nghiệp. Collection chính chỉ chứa một request kích hoạt.

Biến thể `fromIndex/toIndex` được tách thành collection riêng tại `../Checkee-UAT-Index-Variant` để tránh vô tình chạy hai kiểu kích hoạt trong một lượt. Chỉ dùng biến thể này khi Checkee yêu cầu kiểm tra theo index.

Serial mẫu trong collection là `BN8joxxKwx`, GTIN `8931234567892`, lô `A002`, loại `RANDOM`.
