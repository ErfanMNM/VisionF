# Quyết định
- Nhiệm vụ mới thay cho thao tác mở app đang bị ngắt; chưa mở app. Không chạy worker tự động để tránh gửi ngoài PO A002.
- Một phòng tích hợp tuyển worker thực thi, một QA độc lập do CEO giao; một executor duy nhất được tạo PO/gửi eTrace.
- Không đưa credential/token vào output, report hoặc nhật ký. Dùng nguồn credential cục bộ hiện hữu, chỉ báo có/không và môi trường.
- CEO đọc PDF trang 92-93: POST /middleware-api/CTEs/active, GTIN/batchCode/type bắt buộc, chỉ serialNumbers hoặc fromIndex+toIndex. Chưa có quy định batch-size được chứng minh ở các trang này.
- CSV đủ 1.000 mã duy nhất trong cột Mã serial, raw GTIN duy nhất có khoảng trắng cuối `8931234567892 `, loại NGẪU NHIÊN. Chọn cột Mã serial nguyên trạng làm CodeContent, không cắt URL; GTIN metadata Trim thành `8931234567892`, type RANDOM. Hai cột URL không dùng làm định danh vì có khoảng trắng sau GTIN.
- Gửi thăm dò 1 mã trước, xác nhận phản hồi rồi gửi phần còn lại theo lô hợp lý; không lặp mutation khi kết quả mơ hồ. Cần tra batch A002 trước vì test hôm qua đã dùng mã này nhưng trả 400.
## Chỉ đạo người dùng mới — Link tra cứu 1
- Người dùng chỉ định toàn bộ Link tra cứu 1 làm content, đưa mẫu không có khoảng trắng sau GTIN. Quyết định serial-only ở trên bị thay thế.
- CodeContent/payload dùng https://beta-elink.checkee.vn/01/8931234567892/21/<serial>, chuẩn hóa khoảng trắng thừa của GTIN trong Link tra cứu 1 và trim ngoài theo mẫu, không thay phần khác.
- Đã yêu cầu phòng chuyển worker ngay, báo nếu đã mutation serial thuần, không tự fallback serial nếu server từ chối URL.
- QA độc lập phát hiện client hiện có thể coi 2xx rỗng/malformed là thành công; executor cần kiểm envelope và đọc sau ghi, báo mức bằng chứng thực tế. Không sửa sản phẩm trong nhiệm vụ kiểm thử này.
- Đã nghiệm thu local-preflight.json: A002 local có sẵn Running từ 25/11, đủ 1.000 URL1 chuẩn hóa, missing/extra=0, 0 Scanned/Activated và 1.000 Pending. Không import/merge lại.
- SHA256 tập URL1 theo thứ tự CSV (join newline, không newline cuối) khớp QA độc lập: 28d0a35253255c3bbf144cac214db0ff89e91bbe591227be81029fa818dfb226. DB giữ nguyên ef3318263c40282f0cf9a501d294b00459d018f376d12142f4c3260c663665d5.
- Chặn thực tế: không có credential Checkee nonempty trong cấu hình/env đã rà; đã hỏi người dùng đường dẫn nguồn credential qua request_user_input_async. Trong khi chờ hoàn tất runner/payload/báo cáo trung thực; không gọi HTTP bằng dummy hoặc thử dò credential ngoài scope.
- Credential đã được người dùng cấp trực tiếp và đọc qua file Temp, chặn nguồn credential đã giải quyết. Remote preflight: token200/status=true nhưng profile/product/batch/serial reads401 NOT_VERIFIED_USER; không được báo hệ thống đã sửa hoàn toàn.
- CEO cho phép đúng MỘT POST pilot URL1 được người dùng chỉ định (GTIN8931234567892,batchA002,typeRANDOM,serialNumbers chỉ URL1 BN8joxxKwx), không retry/bulk/fallback. Quyền thử này đến từ yêu cầu trực tiếp của người dùng; GET401 không tự tạo yêu cầu hỏi lại quyền đã cấp.
- Sau pilot dù 2xx cũng không gửi phần còn lại khi chưa đối soát quyền đọc/membership. Giữ DB local Pending/không ghi Sent; báo riêng phản hồi endpoint và mức đối soát.
