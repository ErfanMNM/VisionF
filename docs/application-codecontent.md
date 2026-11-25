# Ứng dụng sản xuất với CodeContent

CameraSignalWorker coi nguyên frame camera là CodeContent, không parse GS1, GTIN hoặc serial và không chuẩn hóa chữ hoa/thường hay khoảng trắng. Transport chỉ loại dấu kết thúc frame CR/LF. NoRead và FAIL là tín hiệu lỗi đọc, không phân biệt hoa thường.

Đối chiếu mã chạy qua Dictionary của PO theo contract.md. Mã hợp lệ chỉ được ghi trạng thái quét bền vững sau khi PLC xác nhận Pass=1; trùng mã hoặc không tồn tại gửi Fail=0. NoRead/FAIL cũng gửi Fail=0. Khi chưa chạy PO, đã halt hoặc đang xử lý mã trước, kết quả được ghi nhận nhưng không ghi PLC. PLC từ chối Pass chắc chắn thì hiển thị Lỗi PLC, lưu Fail và giữ mã PO chưa tiêu thụ. Riêng Pass chỉ phát một lần, kể cả khi cấu hình PLC có retry; mất ACK, SID sai hoặc phản hồi không hợp lệ là kết quả không xác định, không tự gửi lại hay mở mã để quét lại. Các lệnh Ready/Fail/thủ công và đọc PLC giữ chính sách retry hiện có. Nếu lỗi DB sau PLC Pass hoặc kết quả gửi PLC không xác định, worker giữ cổng intake đóng, ghi Halted với CodeContent/PO và yêu cầu đối soát, Ready=0. Backend lưu pending intent/quarantine bền vững để khởi động lại không tự quét Pass lần nữa; banner halt hướng dẫn đối soát mã, PLC và PO thay vì khẳng định PO đã huỷ. Nếu Pass đã commit nhưng bước hoàn tất PO lỗi, lịch sử vẫn giữ Pass đúng thực tế; ứng dụng đóng intake, Ready=0 và ghi lỗi hoàn tất riêng, không đổi Pass thành Fail. Scan DB lưu riêng Pass, Duplicate/Trùng, InvalidForOrder/Không tồn tại, NoRead; Fail/Overload cũ vẫn đọc được.

Không có cửa sổ chống trùng theo giây. Cấu hình camera chỉ có IP/cổng; tính trùng phụ thuộc trạng thái bền vững của chính CodeContent trong PO. Key cấu hình cũ bị bỏ khỏi form/raw config và bị xóa khi lưu merged; các key không liên quan được giữ.

## Số liệu và xóa số PLC

Các số lớn của màn Thông tin chỉ đọc PLC qua heartbeat nền hiện có. Ứng dụng không cộng số lớn khi nhận ScanFeed, không dùng số mã PO hoặc số bản ghi scan để thay thế PLC. Tỷ lệ Pass được tính từ Total/Pass đọc PLC. Không có snapshot hoặc đọc lỗi hiển thị dấu gạch ngang.

`Plc:CounterAddress` mặc định D100 theo test rig TApp, đọc một khối năm Int32 (10 word): Total ở offset 0, ReadFail offset 2, Pass offset 4, word offset 6 dự phòng, Timeout offset 8. Fail = ReadFail + Timeout theo TApp. Cần xác nhận địa chỉ bắt đầu với mapping thực tế của từng máy.

`Plc:ResetCounterAddress` là word reset thực tế của máy, mặc định trống vì reference không xác nhận địa chỉ production. Nút Xóa số PLC bị khóa khi chưa cấu hình địa chỉ, PLC tắt, đang gửi hoặc phiên không có quyền EditConfig. Khi thực thi, ghi Int16=1 tới word đó, ghi user action, và chờ lần polling PLC kế tiếp. ACK reset không tự đặt số local về 0.

Polling và reset dùng cùng gate/endpoint snapshot với PlcWriter và hotapply thiết bị. Đổi cấu hình đợi request cũ xong; request xếp hàng chọn cả endpoint và địa chỉ sau gate. Disable PLC ngăn phát UDP. Bộ đếm được poll trong hosted heartbeat như kiến trúc thiết bị hiện có; UI chỉ đọc snapshot ở timer 500ms khi phiên đang hiển thị.

Vùng thông tin PO tiếp tục đọc ProductionOrderService và dữ liệu trong SQLite từng PO; các bộ đếm này độc lập với bộ đếm PLC. Readiness vẫn do AppStateManager/GlobalVariables và cổng bảo trì ScanIntakeGate điều khiển; việc xem số không tự cấp quyền thao tác máy.

## Tra cứu và tích hợp gửi

Tra cứu dùng CodeContent nguyên văn và so sánh ordinal chính xác, không tìm theo serial hoặc LIKE substring. Nội dung mã/Raw được wrap đầy đủ trên màn quét và tra cứu; không lấy serial thay thế hoặc ellipsis mất phần mã. Nhãn banner kết quả local không khẳng định server đã kích hoạt chỉ vì scan Pass. Vùng tóm tắt và lịch sử tra cứu chia sẻ chiều cao hữu hạn; mã dài cuộn dọc trong từng vùng, danh sách gửi lỗi không chiếm hết vùng lịch sử.

Checkee là một tích hợp gửi riêng: xem checkee-codecontent.md. GTIN cấu hình chỉ làm metadata ngoài; thiếu metadata giữ Pending. Simulator chỉ upload file CodeContent theo contract backend; không có GTIN mẫu trên form cấu hình.

## Kiểm thử

Các harness chỉ dùng thư mục tạm và thiết bị localhost/fake. Chạy ScanTests, CheckeeSendTests, CameraHotApplyTests, PlcHotApplyTests, ConfigTests, ConfigUiTests, DeviceConfigurationTests, LoginOverlayTests, FullscreenNumpadTests, UpdateSafetyTests, UpdateSendTests và ApplicationUiTests sau khi backend source ổn định. Các harness UI có thể render headless để kiểm tra vùng mã dài mà không điều khiển thiết bị thật.
