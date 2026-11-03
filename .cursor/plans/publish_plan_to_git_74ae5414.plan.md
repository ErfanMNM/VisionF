---
name: Publish plan to git
overview: Chuyển plan Omron FINS UDP client đang nằm trong thư mục plan cục bộ vào docs trong repo để commit lên git cho người khác cùng code.
todos:
  - id: copy-plan-docs
    content: Copy nội dung plan sang docs/plans/omron-fins-udp-client.md
    status: completed
  - id: verify-git-track
    content: Xác nhận docs không bị gitignore và git add được
    status: completed
  - id: commit-push-plan
    content: Commit và push để người khác cùng dùng
    status: completed
isProject: false
---

# Chuyển plan Omron FINS sang vị trí commit được lên git

- Plan hiện tại nằm ở `[.cursor/plans/omron_fins_udp_client_78115e4f.plan.md](D:/DuAn/VisionF/.cursor/plans/omron_fins_udp_client_78115e4f.plan.md)`, đã nằm trong repo nhưng thư mục `.cursor` thường chỉ dùng cho agent, người khác khó tìm.
- Vị trí đích đề xuất là `[docs/plans/omron-fins-udp-client.md](D:/DuAn/VisionF/docs/plans/omron-fins-udp-client.md)` để ai clone repo cũng đọc được, không phụ thuộc Cursor.
- File `.gitignore` hiện tại trong `[.gitignore](D:/DuAn/VisionF/.gitignore)` không ignore `docs` hay `*.md`, nên file đích commit được ngay, không cần sửa ignore.
- Nội dung giữ nguyên từ plan cũ, gồm các phần:
  - Vì sao viết lại thay vì copy file `OmronPLC.cs` đính kèm WinForms Component thiếu designer, dùng `BackgroundWorker` và `HslCommunication`.
  - Khung FINS/UDP với header 10 byte, lệnh `01 01` read và `01 02` write vùng D `0x82`, kiểm tra `SID` và end code.
  - API đồng bộ kiểu `new OmronPlc(ip, port)` rồi `WriteInt32("D12", 1)` và `ReadInt32("D12")`, result có `IsSuccess`, `Message`, `ErrorCode`, `Value`, `ProcessTime`.
  - Đủ kiểu `bool` qua `D12.05`, `Int16`, `UInt16`, `Int32`, `UInt32`, `Int64`, `UInt64`, `Float`, `Double`, `String`, cộng mảng kiểu Hsl và `ReadWords`/`WriteWords`.
  - Checklist 15 mục từ tạo project `ClassLibraryMT.Plc` tới test PLC thật.
- Sau khi copy, giữ hoặc xóa file plan cũ trong `.cursor/plans` để tránh hai nguồn sự thật, và commit file mới để người khác checkout thấy ngay.