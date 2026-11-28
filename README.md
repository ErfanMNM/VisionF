# VisionF

TanTienQR hỗ trợ kiểm tra và cài bản cập nhật từ Cloudflare với xác thực license, gói ký số và tự phục hồi phiên bản trước khi cài đặt hoặc khởi động thất bại.

Xem [hướng dẫn phát hành, bootstrap và rollback](docs/cloudflare-updates.md).

Quy trình PO hiện dùng file một cột `CodeContent`, đối chiếu toàn bộ nội dung mã theo DB riêng từng PO. Xem [hợp đồng nhập PO](docs/contract.md), [mẫu CSV](docs/examples/po-codecontent.csv), [mẫu JSON](docs/examples/po-codecontent.json) và [hướng dẫn màn sản xuất / bộ đếm PLC](docs/application-codecontent.md).

Kết quả tích hợp và giới hạn kiểm thử: [CodeContent validation](docs/codecontent-validation.md).

Thêm mã bằng COM, nhập tay, PDA/POST và monitor gửi Checkee theo PO: [hướng dẫn vận hành](docs/manual-activation.md).

## Cấu trúc repository

- Thư viện nghiệp vụ và tích hợp dùng chung nằm trong src/.
- Ứng dụng desktop TanTienQR nằm trong TanTienQR/; launcher và test/harness độc lập nằm trong tools/.
- Cloudflare Worker nằm trong license-portal/; script phát hành nằm trong deploy/; hướng dẫn và hợp đồng nằm trong docs/.

Chi tiết project graph, thư mục cục bộ cần giữ và hướng dẫn build sau khi sắp xếp: [cấu trúc dự án](docs/project-layout.md).
