# Bộ đếm hiển thị và lệnh PLC

Màn hình quét mã cho phép chọn **Nguồn số đếm hiển thị** trong phần cấu hình PLC. Thay đổi được lưu và áp dụng ngay. Nhãn tổng số chuyển giữa `TỔNG SỐ · PLC` và `TỔNG SỐ · APP`.

## Địa chỉ bộ đếm PLC

Mỗi giá trị là Int32 có dấu, chiếm hai word D liên tiếp, word thấp trước (CDAB). Địa chỉ riêng phải nằm từ D0 đến D32766; chấp nhận tiền tố D/DM.

| Khóa trong `Plc` | Ý nghĩa | Nếu để trống, kế thừa `CounterAddress` |
| --- | --- | --- |
| `TotalCounterAddress` | Tổng số | +0 word, mặc định D100–D101 |
| `PassCounterAddress` | Pass | +4 word, mặc định D104–D105 |
| `FailCounterAddress` | Số lỗi gốc | +2 word, mặc định D102–D103 |
| `TimeoutCounterAddress` | Timeout | +8 word, mặc định D108–D109 |

`CounterAddress` vẫn được giữ để đọc cấu hình cũ. Bốn địa chỉ mới mặc định để trống, vì vậy một máy trước đây dùng khối D200 tiếp tục dùng D200/D204/D202/D208. Có thể chỉ ghi đè một vài địa chỉ. Nếu cả bốn để trống, ứng dụng tiếp tục đọc chung khối năm Int32 như trước; Int32 tại offset +6 vẫn là dự phòng. Nếu có địa chỉ riêng, ứng dụng đọc đủ bốn giá trị rồi mới công bố snapshot; bất kỳ lần đọc lỗi nào cũng loại cả snapshot, hiển thị `—` thay vì trộn số cũ với số mới.

`FailIncludesTimeout` mặc định `false`: **Fail hiển thị = số tại D Fail + số tại D Timeout**, giữ nguyên cách tính cũ của TApp. Bật **Bộ đếm Fail đã gồm Timeout** (`true`) chỉ khi ladder đã tính tổng NG tại D Fail; lúc đó hiển thị nguyên D Fail để tránh cộng Timeout hai lần. Tỷ lệ đạt = Pass / Total × 100, làm tròn một chữ số thập phân; Total bằng 0 thì tỷ lệ 0%.

Chu kỳ đọc theo `RefreshIntervalMs` (mặc định 1000 ms). Cần đối chiếu từng địa chỉ và cách tính Fail với ladder thực tế của máy.

## Nguồn App

`CounterDisplaySource` nhận `PLC` (mặc định) hoặc `App`. Nguồn App lấy snapshot từ `RuntimeCounters`, được camera worker tăng khi xử lý kết quả. Tổng/Pass/Fail sử dụng Int64. Các kết quả không Pass được tính vào Fail, gồm NoRead, sai cấu trúc, trùng, không thuộc PO, chưa có PO, lỗi PLC, halted hoặc quá tải. Đây là số từ lúc mở ứng dụng, không phải số mã của PO hay lịch sử toàn bộ DB. Khởi động lại app bắt đầu số App từ 0.

Đọc snapshot, refresh UI, đổi PO hoặc đổi nguồn hiển thị không tự tăng/xóa số. Số App vẫn dùng được khi PLC tắt hoặc mất kết nối. Bảng lịch sử nhận sự kiện để hiển thị, không tự cộng lại bộ đếm. Đổi nguồn chỉ thay số hiển thị; luồng xử lý camera và gửi kết quả PLC vẫn hoạt động theo cấu hình hiện có.

## Reset counter và Clear lỗi

| Khóa | Nút | Lệnh |
| --- | --- | --- |
| `ResetCounterAddress` | Xóa số PLC | Ghi Int16 = 1 tới một word D đã cấu hình |
| `ClearErrorAddress` | Xóa lỗi PLC | Ghi Int16 = 1 tới một word D riêng do ladder quy định |

Cả hai địa chỉ mặc định trống, khóa nút tương ứng. Người bấm phải đăng nhập và có quyền `EditConfig`; kiểm tra quyền lại ngay trước khi gửi sau khi chờ gate. Khi PLC tắt, intake tạm dừng hoặc lệnh đang gửi, nút tương ứng không thực thi. Các thao tác được ghi user action log.

Lệnh Clear chỉ được gửi khi chủ động bấm nút; heartbeat, đọc số, đổi nguồn và đổi cấu hình không tự gửi Clear/reset. ACK chỉ xác nhận PLC nhận lệnh, không tự xóa snapshot, lỗi local, PO halted hoặc bộ đếm App. Sau reset, số PLC đợi lần polling tiếp theo. Ladder chịu trách nhiệm xử lý/reset word lệnh theo quy ước của máy; ứng dụng không đoán địa chỉ, không tự ghi 0 sau ACK.

## Kiểm chứng

- `tools/ConfigTests`: địa chỉ tùy chọn/biên hai word, nguồn hiển thị, hot-apply/diff, round-trip JSON và cấu hình cũ.
- `tools/PlcHotApplyTests`: UDP localhost cho khối cũ, địa chỉ riêng, fallback từng giá trị, snapshot lỗi, contract Fail, startup binding, snapshot App, quyền reset/Clear và heartbeat không gửi lệnh chủ động.
- `tools/ApplicationUiTests`: kiểm chứng binding và nguồn số qua ViewModel/UI headless.

Các bộ kiểm thử dùng thiết bị giả hoặc UI headless; không xác nhận ladder, địa chỉ reset/Clear hay kết nối PLC production.
