# Nhật ký GS1-03 — rà soát độc lập

- Nhân viên: `/root/gs1_audit`; thread xác minh từ `CODEX_THREAD_ID`: `01a2162f-0ff6-7b90-950a-e3c4ac3a41d5`.
- Parent: `/root`; dự án `C:\Users\THUC\source\repos\ErfanMNM\VisionF`; journal-key `gs1-audit` do parent cấp.
- Phạm vi: chỉ đọc code/diff, không build hoặc chạy harness, không sửa sản phẩm/harness, không API hay thiết bị. Quyền ghi chỉ file nhật ký này.
- Đã đọc BRIEF, REGISTRY, TASKS và skill session-worker/session-ops/protocol. Budget riêng xác minh được: 8 request hoàn tất, `normal` tại 11:50 ngày 2026-11-26; không dùng bộ đếm parent.
- Nền Git: nhánh `master`, HEAD `92ba3ca09bdc5ba7706cdc430b6a94805f0d3c37`; working tree chứa các thay đổi chưa commit của nhiều task.

## Phát hiện cần xử lý

1. **[P2] Percent escape hỏng ngoài serial vẫn qua kiểm tra cấu trúc.** `TanTienQR/Services/QrParser.cs:44` chỉ duyệt `%` trong nhóm `serial`, còn regex dòng 17 cho phép mọi ký tự không trắng trong query/fragment và prefix URL. Ví dụ `/01/08934567890123/21/ABC?batch=%ZZ` hoặc `/01/08934567890123/21/ABC#%2`: regex khớp, không có `origin` nên bỏ qua cả `Uri.TryCreate`, serial `ABC` hợp lệ nên trả `true`. Nếu mã nguyên văn này đã có trong PO, nó sẽ đi tới PLC Pass thay vì “Sai cấu trúc”. Đây là suy luận tĩnh trực tiếp từ luồng mã; không chạy parser hoặc harness theo phân công. Harness của `/root/scan_review` đã có ca query `%ZZ` với URL tuyệt đối, nhưng chưa có ca path-only và fragment. Đề xuất kiểm tra cú pháp percent escape trên toàn bộ Digital Link sau khi regex khớp, rồi chỉ decode nhóm serial để kiểm tra set82; giữ nguyên CodeContent. Bổ sung ca path-only query và fragment hỏng.

## Các điểm đã đối chiếu

- Guard halt và chưa chạy PO đứng trước NoRead/GS1; cấu trúc đứng trước `ProcessCodeAsync` và callback PLC Pass.
- Nhánh sai cấu trúc chỉ gửi Fail=0; không gọi lookup, không tạo pending Pass/commit PO.
- Mapping `InvalidStructure` từ outcome → SQLite → UI/tra cứu và enum → storage đầy đủ; nhãn đúng “Sai cấu trúc”, badge đỏ.
- Bộ lọc Fail tổng hợp đã thêm `InvalidStructure`; lọc trạng thái riêng đi qua `ToStorage`; bộ đếm trạng thái nhóm trực tiếp theo chuỗi nên không cần sửa thêm.
- Luồng valid vẫn giữ CodeContent nguyên văn, ACK/commit/quarantine như trước. Không thấy thay đổi enum làm đổi giá trị cũ.
- Resolver path prefix trong URL tuyệt đối đã được parent chốt hợp lệ và harness đã chuyển sang accepted; không coi đó là lỗi.
- Chưa phát hiện hồi quy khác có đủ bằng chứng trong nhãn, DB filter hoặc nhánh PLC Pass. Đây là kết quả review tĩnh, không thay thế các harness do parent điều phối.
- `git diff --check` chạy chỉ đọc, không có lỗi whitespace; có cảnh báo LF/CRLF ở các file đang được task khác sửa.

## Bước tiếp theo

- Parent sửa kiểm tra escape toàn Digital Link, giao chủ sở hữu harness thêm hai ca path-only/fragment, rồi chạy build/harness tuần tự.
- Không có tiến trình nền hay thiết bị/API đang được nhân viên này giữ.

## Rà soát lại sau sửa — 12:07 ngày 2026-11-26

- Budget riêng: 17 request hoàn tất, `normal`; tiếp tục chỉ đọc code, không build hoặc chạy harness.
- **P2 đã được xử lý về mặt mã nguồn.** Vòng lặp `QrParser.cs:45` hiện quét toàn bộ `raw` trong nhánh Digital Link thay vì chỉ `serial`; phát hiện escape hỏng trong query, fragment và resolver prefix trước khi trả kết quả hợp lệ. Nhánh GS1 có ngoặc/element string vẫn cho phép `%` là ký tự set82 nguyên văn, đúng phạm vi đã chốt.
- Harness đã thêm ca query path-only thiếu hex, fragment hỏng ở URL tuyệt đối/path-only, resolver prefix hỏng và một URL hợp lệ có escape ở prefix/query/fragment. Chỉ decode `serial` để kiểm độ dài/set82; CodeContent không bị biến đổi.
- Không phát hiện lỗi mới do bản sửa này. Kết luận này là review tĩnh; kết quả thực thi CameraHotApplyTests/ApplicationUiTests do `/root/scan_review` và CEO nghiệm thu riêng.
