# Hồ sơ dự án VisionF
- Project root: `C:\Users\THUC\source\repos\ErfanMNM\VisionF`
- STATE_ROOT: `C:\Users\THUC\source\repos\ErfanMNM\VisionF\.session-org\visionf`
- Mục tiêu người dùng: hoàn thành các đầu việc trong `docs/plans/nhiemvu.md`, cập nhật kết quả và để lại hồ sơ bàn giao.
- Tiêu chí hoàn tất: (1) hợp đồng PO gồm orderNo/LOT, orderQty, một GTIN; luồng nhận/tải xuống xử lý mã sai cấu trúc và trùng, có kết quả rõ cho Client; (2) trang HTML tạo PO và gửi sang TanTienApp; (3) kiểm tra phù hợp đã chạy và file kế hoạch được cập nhật kết quả, liên kết artifact.
- Phạm vi được sửa: các file cần thiết cho hai đầu việc trên, bài kiểm tra liên quan, `docs/plans/nhiemvu.md` và hồ sơ trong `STATE_ROOT`.
- Quyền đã được người dùng cấp: điều phối và tạo chat phụ; triển khai hai nhiệm vụ được ghi trong file kế hoạch; ghi kết quả vào Markdown để bàn giao.
- Quyền chưa được cấp / thao tác cần hỏi: không có thao tác ngoài phạm vi workspace và không tự gửi dữ liệu tới dịch vụ production.
- Ngân sách mỗi session: 100; chuẩn bị: 80; chuyển khi trên 90.
- Ràng buộc kỹ thuật và lệnh kiểm tra: giữ raw `CodeContent`; `orderQty` là planned quantity; filter/response phải báo accepted/rejected theo lý do. Ưu tiên UI cùng origin loopback; static GET-only chỉ từ root cố định, không auth chỉ khi bind loopback và có bảo vệ path traversal; `/api/po` luôn giữ `x-api-key`. UI nhập key tạm kiểu password, không lưu/inject/log. Kiểm tra contract, receive, download và trang HTML; bảo toàn thay đổi Git có sẵn.
- Trạng thái cập nhật lúc: 2026-11-26T16:41:45.000+07:00

- Mục tiêu bổ sung do user trực tiếp giao: sau khi hoàn tất và kiểm tra, commit + push lên nhánh riêng; chờ user duyệt mới gộp. Chỉ CEO thực hiện Git publish; không merge/deploy/production.

- Kết quả nghiệm thu ngày26/11/2026: hai nhiệm vụ PO đã đạt, xem docs/plans/nhiemvu.md. Đang chuẩn bị commit/push nhánh riêng codex/po-gtin-demo; việc gộp cần user duyệt. CEO hiện tại:01a21787-5ed2-7ac2-a73d-80c07b4996d0.
