# Nhật ký GS1-02

- Nhân viên: `/root/scan_review`; thread đã xác minh từ `CODEX_THREAD_ID`: `01a2162b-c579-7093-89c8-476c98e09a31`.
- Parent giao quyền sửa ba harness `tools/CameraHotApplyTests/CameraCodeContentChecks.cs`, `tools/ScanTests/Program.cs`, `tools/ApplicationUiTests/Program.cs`; không sửa code sản phẩm hoặc chạy API ngoài.
- Đã đọc BRIEF, session-worker/session-ops/protocol và khảo sát pipeline/DB/UI. Budget riêng: 12 request hoàn tất, `normal` lúc bắt đầu triển khai.
- Kế hoạch: đổi fixture an toàn PLC/DB sang GS1; kiểm tra parser và cấu trúc trước lookup; xác minh persistence/filter/UI. Chưa build theo yêu cầu parent để tránh build đồng thời khi code sản phẩm đang sửa.
- Đã cập nhật cả ba harness: 14 ca parser hợp lệ và 27 ca sai cấu trúc; PO riêng chứa mã sai cấu trúc hợp lệ theo lookup và mã đã kích hoạt để chứng minh thứ tự trước lookup/trùng. Kiểm Fail=0, Raw nguyên văn, PLC từ chối Fail, không thay Scanned/Activated/PendingPass. Đổi fixture PLC ACK/commit lỗi/quarantine sang GS1 hợp lệ, giữ nguyên các kiểm tra an toàn cũ.
- ScanTests thêm persistence riêng InvalidStructure, bộ lọc Fail tổng hợp và lọc chính xác; ApplicationUiTests thêm nhãn “Sai cấu trúc”/badge đỏ/sự kiện/tra cứu.
- Kiểm tra tĩnh `git diff --check` trên ba file pass; chưa build/chạy harness, chờ parent điều phối. Không sửa code sản phẩm.
- Parent chốt resolver prefix URL hợp lệ; đã chuyển ca `https://example.test/prefix/01/.../21/ABC` sang accepted, plain prefix vẫn bị loại. Budget riêng tại checkpoint: 23/100, normal.
- Parent cập nhật thứ tự: mã sai cấu trúc phải hiện cả khi chưa chạy PO; thêm trường hợp chưa PO hợp lệ trả NoProductionOrder, malformed trả InvalidStructure/Raw nguyên văn/PlcSkipped=true/PLC không ghi. UI test thêm row PLC “Bỏ qua” và xác nhận không phát sự kiện lỗi PLC giả.
- Parser chỉ kiểm định percent triplet trên toàn raw Digital Link; chỉ decode serial trước kiểm set82. Thêm escape sai/thiếu trong query, fragment, resolver prefix và escape hợp lệ ở cả ba vùng.
- Kiểm thử tuần tự hoàn tất:
  - `dotnet run --project tools/CameraHotApplyTests/CameraHotApplyTests.csproj -c Debug -p:SkipPrinterRemoteHelper=true -p:UseSharedCompilation=false` — exit 0, **119 checks**. Log: `artifacts/camera-tests.log`.
  - `dotnet run --project tools/ApplicationUiTests/ApplicationUiTests.csproj -c Debug -p:SkipPrinterRemoteHelper=true -p:UseSharedCompilation=false -- .session-org/camera-gs1-validation/artifacts/application-ui` — exit 0, **52 checks**, 7 ảnh PNG và `results.json`. Log: `artifacts/application-ui-tests.log`.
- Camera harness dùng TCP camera/UDP PLC loopback và DB tạm; không kết nối thiết bị/API thật. UI chạy Avalonia headless với host/workers chưa khởi động và sandbox tạm.
- Không chạy build tổng thể hoặc `git diff --check` cuối theo chỉ đạo parent; chờ CEO chạy build cuối/diff-check. Budget CEO snapshot lúc này 65/100, normal.
