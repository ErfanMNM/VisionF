# Nhật ký CEO

- 2026-11-26: nhận nhiệm vụ GS1; đọc chat CEO cũ và xác minh điểm dừng. Budget snapshot ban đầu 4 request đã hoàn tất, normal.
- Đã khảo sát CameraSignalWorker, parser cũ/mới và fixtures PLC. Chốt kiểm tra cấu trúc theo AI 01/21, giữ link eTrace GTIN 13 và không áp checksum UAT. Không tách CodeContent khi lookup/lưu/gửi.
- Đã giao /root/scan_review khảo sát độc lập; chuyển sang sở hữu ba harness kiểm thử. CEO sở hữu code sản phẩm và tài liệu.
- Build sản phẩm đã thành công: 0 lỗi, 1 warning AVLN3001 có sẵn ở hộp thoại cấu hình thuộc task cũ. Lần chạy ScanTests --no-restore bị thiếu project.assets.json; đã chạy lại với restore bình thường, tiến trình 28009.
- Tiếp tục sau gián đoạn; đọc lại session-ceo vừa cập nhật. Phần code sản phẩm đã xong trước khi skill đổi; giao phần sửa review và thực thi kiểm thử còn lại cho nhân viên, CEO tập trung nghiệm thu.
- Review phát hiện percent escape hỏng ngoài serial vẫn qua. Chốt sửa toàn Digital Link, giữ CodeContent nguyên văn. Resolver path prefix của URL tuyệt đối được chấp nhận theo cấu trúc GS1; prefix plain vẫn bị loại. Giao scan_review sửa phạm vi hẹp này và chạy camera/UI tuần tự, ghi chứng cứ.
- Chỉnh semantics theo “kiểm tra ngay khi camera nhận”: sau guard halt/NoRead, mã sai cấu trúc được báo dù PO chưa chạy; PLC bị bỏ qua ở trạng thái đó và `PlcSkipped` ngăn UI/DB báo nhầm lỗi PLC. Mã hợp lệ chưa PO giữ outcome NoProductionOrder.
- Nghiệm thu cuối: build TanTienQR PASS (0 warning/0 error); CameraHotApplyTests 119 PASS; ScanTests 23 PASS; ApplicationUiTests 52 PASS; `git diff --check` không có lỗi whitespace. `scan_review` xác nhận camera/UI chạy tuần tự trên code cuối; review độc lập không còn phát hiện lỗi.
- Kiểm tra chỉ dùng loopback, harness Avalonia headless và SQLite tạm; không thử scanner/PLC thật, không gọi endpoint bên ngoài. Không commit/deploy. Các chỉnh sửa sẵn có ngoài task GS1 được giữ nguyên.
- 2026-11-26: nhận task song song DATA-01 theo yêu cầu người dùng. Giao hai review chỉ đọc cho lifecycle PO và Checkee; CEO đối chiếu trực tiếp code/tests. Không sửa code cho DATA-01.
- DATA-01 đã chạy `CheckeeSendTests`: 31 pass; `PoTests`: 112 pass, 0 fail. Kết luận sơ bộ: Checkee đọc `ScanRecords` từ SQLite sau `FlushAsync`, không đọc `PoCodeCache` RAM; payload dùng nguyên `CodeContent` từ DB.
