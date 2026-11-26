# Làm đẹp hộp thoại xác nhận cấu hình TanTienApp
- Project root: C:\Users\THUC\source\repos\ErfanMNM\VisionF
- STATE_ROOT: C:\Users\THUC\source\repos\ErfanMNM\VisionF\.session-org\tantien-settings-dialog
- Yêu cầu: dùng session-ceo tuyển nhân sự chỉnh hộp thoại trong ảnh đính kèm cho đẹp và dễ đọc hơn.
- Phạm vi: TanTienQR/Views/ConnectionConfigurationConfirmWindow.cs, có thể thêm XAML/code hỗ trợ riêng cho hộp thoại; kiểm tra liên quan trong tools/ConfigUiTests hoặc harness riêng.
- Tiêu chí nghiệm thu: phân cấp tiêu đề/nội dung rõ, danh sách thay đổi đọc thuận tiện, cảnh báo kết nối tách riêng, nút chính xanh/nút phụ nhẹ đồng bộ App.axaml; cuộn được với nội dung dài; giữ nguyên kết quả Hủy=false/Áp dụng=true và không tự áp dụng khi Enter ngoài ý muốn; build và kiểm tra off-screen đạt; có ảnh thật từ render.
- Quyền: sửa code, chạy kiểm tra cục bộ, tạo phòng ban và giao sub-agent theo skill người dùng gọi. Không commit/deploy hoặc thao tác thiết bị thật.
- Ngân sách mỗi session: 100; chuẩn bị 80; chuyển khi trên 90.
- Ban đầu git status sạch. .NET SDK 10.0.403; Avalonia 12.1.4.
- Cập nhật: 2026-11-26T10:15:00+07:00.
