# VisionF

TanTienQR hỗ trợ kiểm tra và cài bản cập nhật từ Cloudflare với xác thực license, gói ký số và tự phục hồi phiên bản trước khi cài đặt hoặc khởi động thất bại.

Xem [hướng dẫn phát hành, bootstrap và rollback](docs/cloudflare-updates.md).

Quy trình PO hiện dùng file một cột `CodeContent`, đối chiếu toàn bộ nội dung mã theo DB riêng từng PO. Xem [hợp đồng nhập PO](docs/contract.md), [mẫu CSV](docs/examples/po-codecontent.csv), [mẫu JSON](docs/examples/po-codecontent.json) và [hướng dẫn màn sản xuất / bộ đếm PLC](docs/application-codecontent.md).

Kết quả tích hợp và giới hạn kiểm thử: [CodeContent validation](docs/codecontent-validation.md).
