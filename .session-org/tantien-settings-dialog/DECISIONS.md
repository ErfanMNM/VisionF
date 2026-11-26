# Quyết định
## 2026-11-26T10:15:00+07:00 — Phạm vi và phân công
- Tập trung hộp thoại xác nhận Camera/PLC đúng ảnh người dùng; dùng màu và style có sẵn trong App.axaml.
- Chỉ tạo một phòng giao diện, phòng này tuyển nhân viên triển khai. CEO tuyển một nhân viên review độc lập, không sửa chồng code.
- Ưu tiên render Avalonia.Headless, không mở app sản xuất hoặc kết nối thiết bị.
- Xác minh caller ConfigEditorViewModel.SaveAsync chỉ áp dụng thiết bị khi dialog trả true; có ghi chú Development tùy môi trường. Mọi nội dung này phải còn đọc được trong giao diện mới.
## 2026-11-26T10:20:00+07:00 — Kết quả khảo sát QA độc lập
- ConfigUiTests hiện dùng callback giả, chưa tạo hộp thoại thật; cần harness/render trực tiếp ConnectionConfigurationConfirmWindow.
- Nghiệm thu thêm: Enter ngay khi mở không áp dụng; Hủy/Esc/đóng X không thành true; Tab tới nút vẫn thao tác được; giá trị dài wrap và cuộn hết trong khi footer còn thấy.
- Giữ fallback bảo toàn mọi nội dung message không khớp định dạng; phần Development không được mất. Chi tiết ở sessions/ceo-dialog-review/LOG.md.
## 2026-11-26T10:30:00+07:00 — Review ảnh thật của CEO
- Đã xem short, long-top và long-bottom: bố cục rõ, card/cảnh báo đọc đủ, footer luôn thấy.
- Yêu cầu phòng UI chỉnh căn giữa dọc chữ nút Hủy; bỏ khung focus đen chữ nhật khi vừa mở nhưng giữ chỉ báo focus bằng bàn phím.
- Yêu cầu thêm ảnh một thay đổi Port TCP 9000 → 9001 không Development giống đầu vào người dùng; thêm bằng chứng đóng cửa sổ mặc định false nếu thuận tiện.
