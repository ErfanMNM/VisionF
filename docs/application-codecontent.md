# Ứng dụng sản xuất với CodeContent

CameraSignalWorker coi nguyên frame camera là CodeContent và kiểm tra cấu trúc GS1 trước khi đối chiếu nội dung với PO. Không chuẩn hóa chữ hoa/thường, không bỏ khoảng trắng hoặc thay CodeContent bằng GTIN/serial. Transport chỉ loại dấu kết thúc frame CR/LF. NoRead và FAIL là tín hiệu lỗi đọc riêng, không phân biệt hoa thường.

Các dạng AI 01/21 được nhận: Digital Link HTTP/HTTPS hoặc đường dẫn `/01/{GTIN}/21/{serial}`, dạng `(01){GTIN14}(21){serial}` và element string `01{GTIN14}21{serial}`. Element string cho phép symbology identifier `]C1`, `]d2`, `]Q3` cùng dấu GS. Serial có 1–20 ký tự thuộc GS1 character set 82; serial trong URL được giải mã percent escape chỉ để kiểm tra, còn CodeContent luôn giữ nguyên. Kiểm tra neo toàn chuỗi, loại mã plain, thiếu AI/GTIN/serial, GTIN không phải số ASCII, escape hỏng, khoảng trắng và ký tự thừa không hợp lệ.

Đây là kiểm tra cấu trúc AI 01/21 dùng cho mã sản phẩm của dự án, không phải bộ kiểm định toàn bộ Application Identifier GS1. Digital Link nhận GTIN 8/12/13/14 chữ số để tương thích dữ liệu eTrace hiện có, gồm mẫu UAT `https://beta-elink.checkee.vn/01/8931234567892/21/BN8joxxKwx`; không tự thêm số 0 hoặc áp checksum GTIN ở bước này. Quy tắc độ dài serial và element string tham chiếu [GS1 Application Identifiers](https://ref.gs1.org/ai/), cú pháp URL tham chiếu [GS1 Digital Link URI Syntax](https://ref.gs1.org/standards/digital-link/uri-syntax/1.7.0/). GTIN ngắn là tương thích riêng của dự án, khác dạng canonical GTIN 14 chữ số của chuẩn.

Sau guard halt và nhận diện NoRead, mã không đúng cấu trúc được kiểm tra ngay khi vào xử lý, kể cả khi chưa chạy PO; có kết quả `InvalidStructure`, hiển thị đúng **Sai cấu trúc** trên bảng quét, sự kiện và tra cứu. Khi đang chạy PO, gửi Fail=0 xuống PLC; khi chưa chạy PO thì bỏ qua PLC. Cả hai trường hợp đều tăng bộ đếm lỗi local và lưu nguyên mã/trạng thái vào scan DB; không gọi xử lý mã PO, không tạo pending Pass hoặc tăng số đã quét PO. Mã sai vẫn bị loại kể cả khi tồn tại trong dữ liệu PO. Bộ lọc Fail tổng hợp bao gồm InvalidStructure.

Đối chiếu mã qua được kiểm tra cấu trúc chạy qua Dictionary của PO theo contract.md. Mã hợp lệ chỉ được ghi trạng thái quét bền vững sau khi PLC xác nhận Pass=1; trùng mã hoặc không tồn tại gửi Fail=0. NoRead/FAIL cũng gửi Fail=0. Khi chưa chạy PO, đã halt hoặc đang xử lý mã trước, kết quả được ghi nhận nhưng không ghi PLC. PLC từ chối Pass chắc chắn thì hiển thị Lỗi PLC, lưu Fail và giữ mã PO chưa tiêu thụ. Riêng Pass chỉ phát một lần, kể cả khi cấu hình PLC có retry; mất ACK, SID sai hoặc phản hồi không hợp lệ là kết quả không xác định, không tự gửi lại hay mở mã để quét lại. Các lệnh Ready/Fail/thủ công và đọc PLC giữ chính sách retry hiện có. Nếu lỗi DB sau PLC Pass hoặc kết quả gửi PLC không xác định, worker giữ cổng intake đóng, ghi Halted với CodeContent/PO và yêu cầu đối soát, Ready=0. Backend lưu pending intent/quarantine bền vững để khởi động lại không tự quét Pass lần nữa; banner halt hướng dẫn đối soát mã, PLC và PO thay vì khẳng định PO đã huỷ. Nếu Pass đã commit nhưng bước hoàn tất PO lỗi, lịch sử vẫn giữ Pass đúng thực tế; ứng dụng đóng intake, Ready=0 và ghi lỗi hoàn tất riêng, không đổi Pass thành Fail. Scan DB lưu riêng Pass, Duplicate/Trùng, InvalidForOrder/Không tồn tại, InvalidStructure/Sai cấu trúc, NoRead; Fail/Overload cũ vẫn đọc được.

Không có cửa sổ chống trùng theo giây. Cấu hình camera chỉ có IP/cổng; tính trùng phụ thuộc trạng thái bền vững của chính CodeContent trong PO. Key cấu hình cũ bị bỏ khỏi form/raw config và bị xóa khi lưu merged; các key không liên quan được giữ.

## Số liệu và xóa số PLC

Các số lớn của màn Thông tin chọn nguồn theo `Plc:CounterDisplaySource`: `PLC` đọc thiết bị qua heartbeat nền, `App` đọc snapshot bộ đếm kết quả xử lý camera từ lúc mở ứng dụng. Nhãn nguồn đổi cùng số; không dùng tiến độ PO hoặc truy vấn lịch sử để thay thế bộ đếm. Refresh và đổi nguồn không tự tăng số. Tỷ lệ Pass được tính từ Total/Pass của nguồn đang chọn. Ở nguồn PLC, không có snapshot hoặc đọc lỗi hiển thị dấu gạch ngang.

`Plc:CounterAddress` mặc định D100 theo test rig TApp, đọc một khối năm Int32 (10 word): Total ở offset 0, ReadFail offset 2, Pass offset 4, word offset 6 dự phòng, Timeout offset 8. Có thể cấu hình riêng `TotalCounterAddress`, `PassCounterAddress`, `FailCounterAddress`, `TimeoutCounterAddress`; trường trống kế thừa offset của khối cũ. Mặc định Fail = ReadFail + Timeout theo TApp; bật `FailIncludesTimeout` nếu thanh ghi Fail đã gồm Timeout để tránh cộng hai lần. Cần xác nhận mapping thực tế của từng máy. Xem [cấu hình bộ đếm](plc-counters.md).

`Plc:ResetCounterAddress` là word reset thực tế của máy, mặc định trống vì reference không xác nhận địa chỉ production. Nút Xóa số PLC bị khóa khi chưa cấu hình địa chỉ, PLC tắt, đang gửi hoặc phiên không có quyền EditConfig. Khi thực thi, ghi Int16=1 tới word đó, ghi user action, và chờ lần polling PLC kế tiếp. ACK reset không tự đặt số local về 0.

`Plc:ClearErrorAddress` là word xóa lỗi riêng, cũng mặc định trống. Nút Xóa lỗi PLC chỉ ghi Int16=1 khi người có quyền EditConfig chủ động bấm; heartbeat, refresh và đổi nguồn không phát lệnh Clear. Xóa số PLC không xóa bộ đếm App.

Polling và reset dùng cùng gate/endpoint snapshot với PlcWriter và hotapply thiết bị. Đổi cấu hình đợi request cũ xong; request xếp hàng chọn cả endpoint và địa chỉ sau gate. Disable PLC ngăn phát UDP. Bộ đếm được poll trong hosted heartbeat như kiến trúc thiết bị hiện có; UI chỉ đọc snapshot ở timer 500ms khi phiên đang hiển thị.

Vùng thông tin PO tiếp tục đọc ProductionOrderService và dữ liệu trong SQLite từng PO; các bộ đếm này độc lập với bộ đếm PLC. Readiness vẫn do AppStateManager/GlobalVariables và cổng bảo trì ScanIntakeGate điều khiển; việc xem số không tự cấp quyền thao tác máy.

## Tra cứu và tích hợp gửi

Tra cứu dùng CodeContent nguyên văn và so sánh ordinal chính xác, không tìm theo serial hoặc LIKE substring. Nội dung mã/Raw được wrap đầy đủ trên màn quét và tra cứu; không lấy serial thay thế hoặc ellipsis mất phần mã. Nhãn banner kết quả local không khẳng định server đã kích hoạt chỉ vì scan Pass. Vùng tóm tắt và lịch sử tra cứu chia sẻ chiều cao hữu hạn; mã dài cuộn dọc trong từng vùng, danh sách gửi lỗi không chiếm hết vùng lịch sử.

Checkee là một tích hợp gửi riêng: xem checkee-codecontent.md. GTIN cấu hình chỉ làm metadata ngoài; thiếu metadata giữ Pending. Simulator chỉ upload file CodeContent theo contract backend; không có GTIN mẫu trên form cấu hình.

## Kiểm thử

Các harness chỉ dùng thư mục tạm và thiết bị localhost/fake. Chạy ScanTests, CheckeeSendTests, CameraHotApplyTests, PlcHotApplyTests, ConfigTests, ConfigUiTests, DeviceConfigurationTests, LoginOverlayTests, FullscreenNumpadTests, UpdateSafetyTests, UpdateSendTests và ApplicationUiTests sau khi backend source ổn định. Các harness UI có thể render headless để kiểm tra vùng mã dài mà không điều khiển thiết bị thật.
