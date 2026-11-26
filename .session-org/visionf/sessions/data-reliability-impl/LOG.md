# Nhật ký triển khai DATA-02

- Thời điểm: 2026-11-26 (Asia/Saigon)
- Task: DATA-02 — độ tin cậy camera intake / ScanStore / PO state.
- Agent collaboration: `/root/data_reliability_impl`; chưa có thread UUID xác minh, dùng journal-key `data-reliability-impl`; budget chưa xác định.
- Phạm vi được giao: `ClassLibraryMT.Scans/ScanStore.cs`, camera intake (`TanTienQR/Services/CameraSignalWorker.cs` và vùng liên quan), PO cache/store và test liên quan; bảo toàn thay đổi GS1 hiện có; không chạm UI/eTrace, không commit/deploy/thiết bị thật.
- Tiêu chí: lịch sử camera bền vững trong `logs/scans.db` không mất khi queue đầy; PLC mất kết nối thì không tiêu thụ/gửi mã; mã hợp lệ đủ điều kiện cập nhật dictionary/PO trước gửi PLC và gửi lỗi không rollback; giữ Pass/Checkee và nguyên `CodeContent`.
- Trạng thái đầu kỳ: repo `master...origin/master`, nhiều thay đổi chưa commit do các task khác; chỉ chỉnh vùng DATA-02.
