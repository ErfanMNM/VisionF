# Công việc phòng tích hợp
| Task ID | Kết quả và tiêu chí nghiệm thu | Owner | Vùng ghi | Trạng thái | Bằng chứng/bước tiếp theo |
| --- | --- | --- | --- | --- | --- |
| ETRACE-01-W1 | Đọc spec/CSV, xác minh PO/GTIN/type, tạo/import A002 nếu an toàn, gửi thật đúng giới hạn, đối soát và báo cáo có số thực | /root/integration_worker | .tmp-etrace-po-a002; artifacts; docs/etrace-po-a002-report-2026-11-26.md; journal riêng | done/report_ready | Local1000khớp/no-op; gửi1/từ chối400/chấp nhận0/chưa gửi999; remote chưa đối soát401; credential tạm đã xóa |
| ETRACE-01-R1 | Kiểm tra báo cáo, evidence, phạm vi mutation và số đếm; cập nhật CEO | 01a21616-70a0-7d80-9ccc-73bb2ba6c4f0 | Hồ sơ phòng, log riêng | done | Report/raw/count/hash/secret scan nghiệm thu; CEO và QA PASS báo cáo/API FAIL; không còn mutation được phép |
