# Công việc

| Task | Owner | Phạm vi | Tiêu chí | Trạng thái |
| --- | --- | --- | --- | --- |
| GS1-01 | CEO | Code sản phẩm và tài liệu camera | Kiểm tra cấu trúc trước PO, lỗi riêng xuyên suốt UI/DB, giữ CodeContent | done |
| GS1-02 | /root/scan_review | Ba harness camera/scan/UI đã phân công | Mã không GS1 dù thuộc PO vẫn Fail, đúng GS1 vẫn Pass, giữ kiểm thử an toàn PLC | done |
| GS1-03 | /root/gs1_audit | Chỉ đọc diff | Review độc lập hành vi và ca biên | done |
| DATA-01 | CEO + /root/po_commit_review + /root/checkee_source_review | Truy vết lifecycle PLC → PO DB/cache → Scan DB → Checkee | Xác định thứ tự commit, nguồn payload và hành vi lỗi/restart; không sửa code | done |
| DATA-02 | /root/data_reliability_impl | Scan-history persistence và thứ tự camera/dictionary/PLC | Không loại bản ghi khi queue đầy; PLC offline thì không xử lý; cập nhật dictionary trước khi gửi PLC; kiểm thử DB/PLC | in_progress |

## DATA-02 — tiêu chí chi tiết

- Mỗi record lịch sử camera được ghi bền vững vào `logs/scans.db`; backpressure hoặc lỗi DB phải được báo/giữ retry, không âm thầm bỏ record.
- Trước khi nhận xử lý mã cần kiểm tra PLC đang kết nối; khi mất kết nối, không tra cứu/tiêu thụ mã PO và không phát PLC lệnh. Record camera vẫn được lưu với trạng thái phù hợp để điều tra, nhưng không được gửi Checkee như Pass.
- Với mã đủ điều kiện xử lý, dictionary/PO DB chuyển sang trạng thái đã nhận/quét trước khi phát PLC; kết quả PLC không đảo ngược việc tiêu thụ mã. `ScanStatus=Pass` phản ánh PO đã commit; `PlcResult` ghi độc lập kết quả write. Pass đã commit được gửi Checkee dù PLC write thất bại, theo yêu cầu user không phụ thuộc kết quả PLC.
- Khi PLC offline trước intake, không tiêu thụ mã và không phát lệnh PLC; vẫn lưu lịch sử bền vững với trạng thái skipped/offline, không gửi Checkee như Pass.
- Không xem bounded in-memory channel là lưu DB bền vững: nếu Scan DB chưa xác nhận insert, giữ retry/backpressure và không âm thầm loại record.
- Bảo toàn các thay đổi chưa commit có sẵn (GS1, UI xác nhận cấu hình, eTrace artifacts); không commit, deploy, gọi API hoặc thiết bị thật.
- Chủ sở hữu triển khai kiểm thử hẹp liên quan; reviewer độc lập đọc diff và chứng cứ trước khi CEO nghiệm thu.

## Nghiệm thu

- `dotnet build TanTienQR\TanTienQR.csproj --no-restore -p:SkipPrinterRemoteHelper=true -p:UseSharedCompilation=false --verbosity quiet`: PASS, 0 warning / 0 error.
- `dotnet run --project tools\CameraHotApplyTests\CameraHotApplyTests.csproj -p:SkipPrinterRemoteHelper=true -p:UseSharedCompilation=false`: PASS, 119 checks, TCP/UDP loopback.
- `dotnet run --project tools\ScanTests\ScanTests.csproj --no-restore --verbosity quiet`: PASS, 23 checks, SQLite tạm.
- `dotnet run --project tools\ApplicationUiTests\ApplicationUiTests.csproj -c Debug -p:SkipPrinterRemoteHelper=true -p:UseSharedCompilation=false -- .session-org/camera-gs1-validation/artifacts/application-ui`: PASS, 52 checks; ảnh UI ở `artifacts/application-ui/`.
- `git diff --check`: PASS, không có whitespace error; Git chỉ cảnh báo chuyển LF/CRLF trên file harness/Bruno đang sửa.
- Các bài kiểm tra dùng harness giả lập, SQLite/host tạm; không kết nối thiết bị thật hoặc gọi API eTrace.

