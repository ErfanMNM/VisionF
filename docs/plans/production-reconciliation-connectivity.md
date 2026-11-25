# Kế hoạch đối soát dữ liệu và quản lý trạng thái sản xuất

Ngày: 25/11/2026. Phạm vi: lập kế hoạch, chưa triển khai thay đổi nghiệp vụ.

## 1. Mục tiêu và cách hiểu thống nhất

1. Thêm nút **Đối soát lại dữ liệu**: đưa mã `Sent` nhưng `PoCodes.ActivatedUtc IS NULL` về `Pending` để có thể gửi lại máy in. Giữ nguyên mã đã active và không tự đưa mã `Printed` trở lại hàng đợi.
2. Trạng thái vận hành hiển thị gồm **Chưa Chọn PO**, **Đang sản xuất**, **Tạm dừng**, **Bị lỗi**.
3. Kiểm tra và hiển thị tình trạng Internet độc lập với kết nối camera, PLC và máy in.
4. Khi máy in mặc định kết nối lại, so số mã trong buffer thực với hàng đợi app; nếu lệch thì xóa toàn bộ buffer máy in, đối soát lại mã và nạp lại.
5. Khi buffer thấp hơn 10, nạp bù **đến 10**, không nạp lên 100 theo cấu hình lô hiện tại. Nếu không còn đủ mã hợp lệ, nạp toàn bộ phần còn lại và hiển thị số thiếu.

Phạm vi đối soát là dữ liệu thuộc máy in mặc định; có thể rà các DB PO liên quan nhưng chỉ gửi mã của PO đang sản xuất. Không sửa cờ active, không tạo mã mới, không gửi mã của PO khác vào chuyền đang chạy.

## 2. Hiện trạng đã rà soát

- `PrintQueueStore.ClaimNextAsync` chọn mã chưa active có trạng thái `Pending`, ghi `Sent` trước khi truyền TCP.
- `MFD|OK` chỉ xác nhận máy nhận mã; `BACK` được ghép FIFO với mã đã gửi, không mang định danh mã.
- `PrintService.SendNextBatchAsync` hiện gửi thủ công, tính chỗ trống theo `BatchSize` và `BufferCapacity`.
- `PrinterMonitorService` thăm dò máy in mỗi 3 giây; chưa có điều phối nạp bù hoặc đối soát sau reconnect.
- `ReconcileActivatedAcrossOrdersAsync` hiện đổi mã `Sent` đã active sang `NoResponse`; phải giữ chức năng này riêng, không thay bằng tác vụ reset mã chưa active chạy định kỳ.
- `ProductionOrderService` chưa có pause/resume. Trạng thái toàn cục hiện phản ánh license/camera/PLC, chưa phản ánh phiên sản xuất.
- Chưa có dịch vụ kiểm tra Internet riêng.

## 3. Thiết kế đối soát thủ công

1. Khóa gửi mã và tự nạp bù bằng một khóa điều phối theo máy in.
2. Đưa phiên sản xuất về tạm dừng; gửi `STOP` nếu máy in đang chạy và chờ xác nhận dừng.
3. Xử lý hết xác nhận `BACK` cũ theo quy tắc giao thức đã kiểm chứng trên máy thật.
4. Gửi `CLR`; chỉ tiếp tục sau ACK hợp lệ và đọc lại `MFDLEN = 0`.
5. Trong transaction của từng DB PO, đổi đúng nhóm `Sent` chưa active thành `Pending`; xóa dữ liệu gắn với lần gửi cũ để FIFO mới không dùng lại ACK cũ. Giữ số lần thử và lưu nhật ký trước/sau.
6. Hiển thị số mã đã đặt lại và số mã chờ gửi; chỉ nạp cho PO hiện tại khi phiên được phép chạy. Không tự gửi `START` khi kết thúc đối soát.

Nếu `STOP`, `CLR` hoặc truy vấn buffer thất bại: giữ trạng thái tạm dừng/đối soát chưa hoàn tất, không vừa reset DB vừa giữ mã cũ trong máy in. Ghi dấu phục hồi bền vững để mở lại app giữa quy trình vẫn tiếp tục đối soát trước khi gửi.

## 4. Trạng thái sản xuất và khóa thiết bị

| Trạng thái | Điều kiện | Hành vi |
|---|---|---|
| Chưa Chọn PO | Chưa có PO được đưa vào phiên sản xuất | Không nhận mã cho sản xuất, không gửi lô in |
| Đang sản xuất | Có PO đang chạy và các thiết bị bắt buộc sẵn sàng | Cho xử lý mã và nạp buffer |
| Tạm dừng | Người dùng tạm dừng, hoặc đang phục hồi sau lỗi/đối soát | Giữ PO và tiến độ, chặn xử lý sản xuất và gửi mới |
| Bị lỗi | Camera/PLC mất kết nối, license không hợp lệ, hoặc sự cố máy in khi module in được sử dụng | Khóa Bắt đầu/Tiếp tục/Bật in, đưa tín hiệu cho phép chạy về 0, thử STOP máy in nếu còn kết nối |

- `Bị lỗi` ưu tiên hiển thị cao nhất; phần chi tiết phải nêu thiết bị/lý do cụ thể.
- Chọn PO ở dropdown chỉ là lựa chọn; chỉ nút Bắt đầu mới mở phiên sản xuất.
- Khi thiết bị hồi phục, giữ PO ở Tạm dừng để người vận hành chủ động Tiếp tục.
- Lưu Paused trong DB PO và lịch sử lần chạy; mở lại app vẫn giữ pause, không tạo lần chạy mới khi resume.
- Kiểm tra điều kiện tại service trước Start/Resume, không chỉ disable nút UI; kiểm tra lại sau khi chờ I/O/khóa.
- Camera chặn cả mã hợp lệ, NoRead và sai định dạng khi không được phép sản xuất. Thao tác ghi nhận mã trả kết quả thành công/thất bại để tránh báo Pass nếu mã đã bị chặn.
- Giữ riêng trạng thái sức khỏe thiết bị và trạng thái vận hành. Nếu D16 là tín hiệu cho phép chạy, giá trị gửi xuống PLC cần bằng 1 chỉ khi thiết bị sẵn sàng **và** PO đang sản xuất; xác nhận ý nghĩa này với chương trình PLC trước nghiệm thu.

## 5. Kiểm tra Internet

- Worker bất đồng bộ chạy khi khởi động và định kỳ khoảng 30 giây; timeout khoảng 5 giây, hủy được khi đóng app.
- Dùng endpoint kiểm tra kết nối có phản hồi mong đợi rõ ràng; xác minh nội dung, không chỉ HTTP 200 để tránh báo nhầm ở captive portal. Endpoint và chu kỳ có thể cấu hình.
- Hiển thị Đang kiểm tra / Có Internet / Mất Internet / Chưa xác định và thời điểm kiểm tra gần nhất.
- Mặc định mất Internet chỉ cảnh báo vì app dùng dữ liệu PO local; không đồng nhất Internet mất với camera/PLC mất. Nếu nghiệp vụ bắt buộc online mới được sản xuất, bổ sung điều kiện này vào cùng cổng kiểm soát vận hành.
- Trạng thái Internet không thay thế việc xử lý lỗi riêng của server PO/Checkee.

## 6. Tự phục hồi khi kết nối lại máy in

1. Theo dõi thế hệ kết nối TCP trong client, tăng sau mỗi lần connect thành công. Không chỉ dựa vào kết quả poll vì UI có thể đã reconnect trước worker.
2. Khóa gửi và đưa máy in về dừng trước khi chụp số buffer để so sánh ổn định.
3. `B_printer` lấy từ `MFDLEN` hợp lệ; `B_app` là số mã được máy in mặc định nhận vào hàng đợi, chưa có xác nhận in. Tách riêng mã đã claim nhưng chưa rõ ACK để không coi là chắc chắn nằm trong buffer.
4. Nếu lệch: thực hiện chu trình `STOP → xác nhận dừng → CLR → MFDLEN=0 → reset Sent chưa active → nạp lại`.
5. Nếu bằng nhau: giữ hàng đợi hiện tại, không CLR; vẫn kiểm tra các lần gửi mơ hồ trước khi cho nạp tiếp.
6. Nếu không đọc được buffer hoặc phục hồi thất bại, giữ pause và hiển thị lỗi; không coi dữ liệu thiếu/sai định dạng là 0.

Do `BACK` không có mã, số buffer bằng nhau không chứng minh nội dung giống nhau. Cần test trên G10 thứ tự BACK/STOP/CLR và khả năng BACK muộn; không giả định ACK STOP/CLR tự chứng minh mọi mã cũ đã được xử lý.

## 7. Tự nạp bù đến 10

- Một bộ điều phối dùng chung cho gửi tay, nạp tự động, CLR và đối soát.
- Sau mỗi BACK, phát tín hiệu đánh thức worker nạp bù; không gửi lệnh trực tiếp trong callback đang đọc socket vì sẽ chặn việc nhận ACK.
- Poll định kỳ làm phương án dự phòng khi không có sự kiện.
- Công thức mỗi lần: `cần gửi = max(0, 10 - buffer thực tế)`; dùng cùng snapshot mới nhất để tính và gửi, tránh tính trên số cũ rồi đọc lại số mới.
- Chỉ lấy mã Pending chưa active của PO hiện tại; dừng lô nếu PO đổi, bị pause, lỗi thiết bị hoặc mất kết nối.
- Phân biệt mã chưa hề truyền với mã đã truyền nhưng mất ACK: chỉ mã chắc chắn chưa truyền mới trả Pending ngay; mã mơ hồ đi qua đối soát.
- Chặn cấu hình sức chứa/lô gửi thấp hơn 10 hoặc báo rõ không thể đạt yêu cầu.

Ví dụ nghiệm thu: buffer 9 gửi 1; buffer 3 gửi 7; buffer 10 hoặc 11 không gửi; còn 4 mã Pending và buffer 3 thì chỉ gửi 4, báo còn 7 trong buffer và hết mã chờ.

## 8. Thứ tự thực hiện và vùng mã dự kiến

1. **Trạng thái + interlock:** `ProductionOrderService`, `ProductionOrderStatus`, `PoHistoryStore`, `AppStateManager`, `CameraSignalWorker`, `PlcHeartbeatService`; thêm pause/resume và cổng điều kiện thiết bị.
2. **Đối soát + persistence:** `PrintQueueStore`, `PrintService`; transaction reset đúng dữ liệu, nhật ký phục hồi và khóa gửi chung.
3. **Reconnect + nạp 10:** `G10PrinterClient`, `PrinterMonitorService`, `PrintService`; thế hệ kết nối, xác nhận buffer, tín hiệu BACK và cơ chế tự bù.
4. **Internet:** dịch vụ worker/options và đăng ký DI trong `TanTienAppServices`.
5. **Giao diện:** `MainViewModel`, `ProductionOrdersViewModel`, `ProductionInfoViewModel`, `PrintViewModel`, `MainWindow.axaml`, `InfoView.axaml`, `PrintView.axaml`; nút đối soát, pause/resume, bốn trạng thái và chỉ báo Internet.
6. **Kiểm thử và nghiệm thu:** test mô phỏng/SQLite/TCP, build và test giao diện, sau đó thử có giám sát trên camera/PLC/G10 thật.

## 9. Tiêu chí nghiệm thu

- Đối soát chỉ đưa Sent chưa active về Pending; không sửa mã active, Printed hoặc dữ liệu PO khác khi gửi.
- Reconnect với buffer bằng/khác/không đọc được đi đúng nhánh, kể cả UI đã tự reconnect trước worker.
- CLR bị từ chối, timeout, app tắt giữa CLR và reset không làm nạp chồng mã cũ.
- Gửi tay và tự nạp đồng thời không claim/gửi trùng hoặc vượt mức nạp tự động.
- Buffer 0/3/9/10/11, hết mã và lô nhỏ hơn 10 có kết quả đúng.
- Camera/PLC rớt trước Start, giữa batch và khi đang xử lý mã đều chặn vận hành; phục hồi không tự chạy lại.
- Tạm dừng và mở lại app giữ đúng PO/tiến độ/trạng thái; resume không tạo run mới.
- Mã cuối của PO vẫn nhận xác nhận đúng khi auto-complete; không có Pass cho mã bị từ chối.
- Internet mất, timeout và trang đăng nhập mạng không làm treo UI hoặc báo nhầm online.

Tài liệu này cập nhật yêu cầu mới: mức nạp đích là 10. Kế hoạch buffer cũ trong cùng thư mục chỉ dùng làm tham khảo hiện trạng; không áp dụng quy tắc nạp lên BatchSize/BufferCapacity của bản cũ.
