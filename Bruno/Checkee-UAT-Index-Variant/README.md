# Checkee UAT - Kích hoạt theo index

Collection riêng để kiểm tra lựa chọn `fromIndex` / `toIndex` của `POST /middleware-api/CTEs/active`. Mở thư mục này riêng trong Bruno, chọn environment `UAT`, nhập secret `clientId` và `apiKey`, rồi chạy collection.

Collection lấy access token và chỉ gửi một request kích hoạt với `fromIndex=1`, `toIndex=1`, GTIN `8931234567892`, loại `RANDOM`, batch `A002`. Request này có thể thay đổi dữ liệu từ xa; chỉ chạy khi Checkee xác nhận mã/index thử và trạng thái batch phù hợp. Không chạy cùng lượt với request `serialNumbers` nếu muốn tránh kích hoạt trùng.
