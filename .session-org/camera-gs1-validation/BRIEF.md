# Kiểm tra cấu trúc GS1 tại đầu vào camera

- Project root: C:\Users\THUC\source\repos\ErfanMNM\VisionF.
- CEO hiện tại: 01a21681-6ac4-7ca1-895d-55a3c8e93537; tiếp quản thủ công theo chỉ định trực tiếp của người dùng ngày 2026-11-26 từ CEO tiền nhiệm 01a2162b-3ae9-76a2-976a-055cba157b1b, đã dừng do giới hạn lượt. CEO trước đó 01a215d7-5953-74c2-90de-dd1799a93c39 cũng đã dừng do giới hạn lượt.
- Yêu cầu: khi camera nhận mã, kiểm tra GS1 trước khi đối chiếu mã PO; sai thì hiển thị đúng “Sai cấu trúc”.
- Nghiệm thu: trạng thái riêng trên UI, tra cứu và SQLite; Fail=0 khi đang sản xuất, không tiêu thụ mã PO; mã GS1 hợp lệ vẫn chạy quy trình PLC ACK/commit, chống trùng và quarantine; NoRead/FAIL vẫn riêng.
- Phạm vi: parser/worker camera, enum/status, nhãn UI, bộ lọc Fail, tài liệu và kiểm thử liên quan. Giữ nguyên CodeContent. Không API, thiết bị thật, commit hoặc deploy.
- Định dạng trong phạm vi dự án: Digital Link /01/{GTIN}/21/{serial}, GS1 có ngoặc, GS1 element string AI 01/21. GTIN Digital Link chấp nhận độ dài 8/12/13/14 để tương thích eTrace UAT, không kiểm tra checksum trong bước kiểm tra cấu trúc; AI 01 element string dùng 14 số. Serial 1–20 ký tự GS1. Không coi đây là thư viện toàn bộ AI GS1.
- Guard intake/quá tải/halt và NoRead giữ ưu tiên; kiểm tra cấu trúc trước lookup/PLC Pass/commit PO, kể cả khi chưa có PO. Sai cấu trúc không phát PLC Fail khi PO chưa chạy.
- Phần mở rộng DATA-02: mọi bản ghi lịch sử camera phải được lưu bền vững vào Scan DB, không mất khi hàng đợi đầy; đánh dấu mã PO trong dictionary trước khi phát PLC; khi PLC mất kết nối lúc nhận mã thì không xử lý mã đó. Duy trì nguyên tắc CodeContent gốc và trạng thái Pass gửi Checkee chỉ sau điều kiện nghiệp vụ hợp lệ.

